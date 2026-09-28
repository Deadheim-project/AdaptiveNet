using System;
using System.Collections.Generic;

namespace AdaptiveNet.Core
{
    internal readonly struct RuntimeHealthSample
    {
        public RuntimeHealthSample(
            double intervalSeconds,
            int frameCount,
            double averageFrameMilliseconds,
            double p95FrameMilliseconds,
            double maximumFrameMilliseconds,
            int stallFrames,
            int severeStallFrames,
            int gen0Collections,
            int gen1Collections,
            int gen2Collections,
            double gcCorrelatedStallMilliseconds,
            long managedMemoryBytes)
        {
            IntervalSeconds = Math.Max(0d, intervalSeconds);
            FrameCount = Math.Max(0, frameCount);
            AverageFrameMilliseconds = SanitizeNonNegative(averageFrameMilliseconds);
            P95FrameMilliseconds = SanitizeNonNegative(p95FrameMilliseconds);
            MaximumFrameMilliseconds = SanitizeNonNegative(maximumFrameMilliseconds);
            StallFrames = Math.Max(0, stallFrames);
            SevereStallFrames = Math.Max(0, severeStallFrames);
            Gen0Collections = Math.Max(0, gen0Collections);
            Gen1Collections = Math.Max(0, gen1Collections);
            Gen2Collections = Math.Max(0, gen2Collections);
            GcCorrelatedStallMilliseconds = SanitizeNonNegative(gcCorrelatedStallMilliseconds);
            ManagedMemoryBytes = Math.Max(0L, managedMemoryBytes);
        }

        public double IntervalSeconds { get; }
        public int FrameCount { get; }
        public double AverageFrameMilliseconds { get; }
        public double P95FrameMilliseconds { get; }
        public double MaximumFrameMilliseconds { get; }
        public int StallFrames { get; }
        public int SevereStallFrames { get; }
        public int Gen0Collections { get; }
        public int Gen1Collections { get; }
        public int Gen2Collections { get; }
        public double GcCorrelatedStallMilliseconds { get; }
        public long ManagedMemoryBytes { get; }
        public bool Valid => FrameCount > 0;
        public double AverageFps => AverageFrameMilliseconds > 0d
            ? 1000d / AverageFrameMilliseconds
            : 0d;

        public static RuntimeHealthSample Merge(RuntimeHealthSample older, RuntimeHealthSample newer)
        {
            if (!older.Valid) return newer;
            if (!newer.Valid) return older;

            int frames = older.FrameCount + newer.FrameCount;
            double weightedAverage = frames > 0
                ? (older.AverageFrameMilliseconds * older.FrameCount +
                   newer.AverageFrameMilliseconds * newer.FrameCount) / frames
                : 0d;
            return new RuntimeHealthSample(
                older.IntervalSeconds + newer.IntervalSeconds,
                frames,
                weightedAverage,
                Math.Max(older.P95FrameMilliseconds, newer.P95FrameMilliseconds),
                Math.Max(older.MaximumFrameMilliseconds, newer.MaximumFrameMilliseconds),
                older.StallFrames + newer.StallFrames,
                older.SevereStallFrames + newer.SevereStallFrames,
                older.Gen0Collections + newer.Gen0Collections,
                older.Gen1Collections + newer.Gen1Collections,
                older.Gen2Collections + newer.Gen2Collections,
                Math.Max(older.GcCorrelatedStallMilliseconds, newer.GcCorrelatedStallMilliseconds),
                newer.ManagedMemoryBytes);
        }

        private static double SanitizeNonNegative(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value) ? 0d : Math.Max(0d, value);
        }
    }

    internal sealed class CircularHistory<T>
    {
        private readonly T[] _items;
        private int _next;

        public CircularHistory(int capacity)
        {
            _items = new T[Math.Max(1, capacity)];
        }

        public int Count { get; private set; }
        public int Capacity => _items.Length;

        public void Add(T item)
        {
            _items[_next] = item;
            _next = (_next + 1) % _items.Length;
            if (Count < _items.Length) Count++;
        }

        public void CopyOrderedTo(List<T> destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            int first = Count == _items.Length ? _next : 0;
            for (int offset = 0; offset < Count; offset++)
            {
                destination.Add(_items[(first + offset) % _items.Length]);
            }
        }

        public void Clear()
        {
            Array.Clear(_items, 0, _items.Length);
            _next = 0;
            Count = 0;
        }

        /// <summary>A copy holding up to <paramref name="capacity"/> of the newest items, for a
        /// history window resized while connections are open. The original is left as it was,
        /// so a reader already holding it is unaffected.</summary>
        public CircularHistory<T> WithCapacity(int capacity)
        {
            var ordered = new List<T>(Count);
            CopyOrderedTo(ordered);
            var resized = new CircularHistory<T>(capacity);
            foreach (T item in ordered) resized.Add(item);
            return resized;
        }
    }

    internal sealed class DeliveryDelayEstimator
    {
        // Allows ordinary monotonic-clock drift (500 ppm) to move the baseline without
        // absorbing a sudden queued report. Typical PC clock drift is much smaller.
        private const double MaximumClockDriftSecondsPerSecond = 0.0005d;
        private double _minimumClockOffset = double.MaxValue;
        private long _lastSequence = -1;
        private double _lastClientTime;
        private double _lastServerTime;

        public double Observe(long sequence, double clientMonotonicSeconds, double serverArrivalSeconds)
        {
            if (!IsFinite(clientMonotonicSeconds) || !IsFinite(serverArrivalSeconds)) return 0d;

            if (_lastSequence >= 0 && (sequence <= _lastSequence || clientMonotonicSeconds < _lastClientTime))
            {
                Reset();
            }

            _lastSequence = sequence;
            _lastClientTime = clientMonotonicSeconds;
            double offset = serverArrivalSeconds - clientMonotonicSeconds;
            if (!IsFinite(offset)) return 0d;
            if (_minimumClockOffset == double.MaxValue)
            {
                _minimumClockOffset = offset;
            }
            else if (offset < _minimumClockOffset)
            {
                _minimumClockOffset = offset;
            }
            else
            {
                double elapsedServerSeconds = Math.Max(0d, serverArrivalSeconds - _lastServerTime);
                double allowedBaselineRise = elapsedServerSeconds * MaximumClockDriftSecondsPerSecond;
                _minimumClockOffset = Math.Min(offset, _minimumClockOffset + allowedBaselineRise);
            }
            _lastServerTime = serverArrivalSeconds;
            double delay = (offset - _minimumClockOffset) * 1000d;
            return IsFinite(delay) ? Math.Max(0d, delay) : 0d;
        }

        public void Reset()
        {
            _minimumClockOffset = double.MaxValue;
            _lastSequence = -1;
            _lastClientTime = 0d;
            _lastServerTime = 0d;
        }

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }

    internal static class DiagnosticAnomalyClassifier
    {
        public static string ClassifyPeer(
            bool networkCongested,
            string networkReason,
            bool clientReportAvailable,
            bool clientFocused,
            bool clientPlayerReady,
            bool clientTeleporting,
            double clientDeliveryDelayMilliseconds,
            RuntimeHealthSample clientHealth,
            double frameStallThresholdMilliseconds,
            double severeFrameStallThresholdMilliseconds,
            double deliveryDelayThresholdMilliseconds)
        {
            string result = string.Empty;
            if (networkCongested)
            {
                Append(ref result, "network-" + (string.IsNullOrEmpty(networkReason) ? "congested" : networkReason));
            }
            if (!clientReportAvailable) return result;

            if (clientDeliveryDelayMilliseconds >= deliveryDelayThresholdMilliseconds)
            {
                Append(ref result, "telemetry-delayed");
            }
            if (!clientFocused || !clientPlayerReady || clientTeleporting)
            {
                return result;
            }
            if (clientHealth.GcCorrelatedStallMilliseconds >= frameStallThresholdMilliseconds)
            {
                Append(ref result, "client-gc-correlated-stall");
            }
            else if (clientHealth.MaximumFrameMilliseconds >= severeFrameStallThresholdMilliseconds)
            {
                Append(ref result, "client-frame-stall");
            }
            return result;
        }

        public static string ClassifyServer(
            RuntimeHealthSample serverHealth,
            double frameStallThresholdMilliseconds,
            double severeFrameStallThresholdMilliseconds)
        {
            if (serverHealth.GcCorrelatedStallMilliseconds >= frameStallThresholdMilliseconds)
            {
                return "server-gc-correlated-stall";
            }
            return serverHealth.MaximumFrameMilliseconds >= severeFrameStallThresholdMilliseconds
                ? "server-frame-stall"
                : string.Empty;
        }

        private static void Append(ref string target, string value)
        {
            target = string.IsNullOrEmpty(target) ? value : target + "+" + value;
        }
    }
}
