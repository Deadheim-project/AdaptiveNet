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

    internal static class ClientReportPolicy
    {
        /// <summary>
        /// Whether a connected client counts as having failed to deliver its diagnostic report.
        /// </summary>
        /// <remarks>
        /// A client that is still loading the world cannot report: its first report arrived 14 s
        /// after it connected in the 2026-10-03 test, and it had no character for 33 s. Counting
        /// from the connection therefore opened an incident on every login. This counts from the
        /// moment the player's character exists, and only for a client that has never reported:
        /// one that reported and then went silent (a teleport, a hang) is the delivery-delay and
        /// frame-stall triggers' business, not a client without the mod.
        /// </remarks>
        /// <param name="everReported">Whether any report has arrived on this connection.</param>
        /// <param name="reportAvailable">Whether a report arrived recently enough to count as live.</param>
        /// <param name="characterSince">When the character first existed; NaN while it has none.</param>
        public static bool IsMissing(
            bool everReported,
            bool reportAvailable,
            double now,
            double characterSince,
            double missingAfterSeconds)
        {
            if (everReported || reportAvailable) return false;
            if (double.IsNaN(characterSince)) return false;
            return now - characterSince >= missingAfterSeconds;
        }
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

            // A client still loading the world, or crossing a teleport, cannot report on time: its
            // main thread is blocked, so the report's estimated delay grows with the freeze
            // (0.5 s to 4.5 s across a 28 s world load, seen 2026-10-03) while the link is fine.
            // Only a client that is in the game can have a late report mean a delivery delay.
            // An unfocused window is throttled the same way: on 2026-10-13, 10 of 11 automatic
            // incidents were a background client at 1-3 FPS, with a 12 ms ping throughout.
            bool inGame = clientPlayerReady && !clientTeleporting;
            if (inGame && clientFocused && clientDeliveryDelayMilliseconds >= deliveryDelayThresholdMilliseconds)
            {
                Append(ref result, "telemetry-delayed");
            }
            if (!clientFocused || !inGame)
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

        /// <summary>
        /// The reason a client is quiet, as a label that enters the verdict but never opens a
        /// capture. <see cref="ClassifyPeer"/> silences a loading or teleporting client (its main
        /// thread is blocked); this names that silence so an incident does not just lose it.
        /// </summary>
        public static string ClassifyPeerContext(bool clientReportAvailable, bool clientPlayerReady, bool clientTeleporting)
        {
            if (!clientReportAvailable) return string.Empty;
            if (!clientPlayerReady) return "client-loading";
            return clientTeleporting ? "client-teleporting" : string.Empty;
        }

        public static string ClassifyServer(
            RuntimeHealthSample serverHealth,
            double frameStallThresholdMilliseconds,
            double severeFrameStallThresholdMilliseconds,
            double worldSaveMilliseconds = 0d,
            bool serverOverloaded = false)
        {
            // Valheim prepares a world save on the main thread (ZDOMan.PrepareSave), freezing every
            // player at once. Naming it keeps a routine save from reading as an unexplained stall,
            // while a long one still opens a capture.
            if (worldSaveMilliseconds >= frameStallThresholdMilliseconds)
            {
                return "server-world-save";
            }
            if (serverHealth.GcCorrelatedStallMilliseconds >= frameStallThresholdMilliseconds)
            {
                return "server-gc-correlated-stall";
            }
            if (serverHealth.MaximumFrameMilliseconds >= severeFrameStallThresholdMilliseconds)
            {
                return "server-frame-stall";
            }
            // Last, so a spike keeps its own name; this one is the slow server with no spike at all.
            return serverOverloaded ? "server-overloaded" : string.Empty;
        }

        private static void Append(ref string target, string value)
        {
            target = string.IsNullOrEmpty(target) ? value : target + "+" + value;
        }
    }

    /// <summary>
    /// Thresholds of the incident verdict. The defaults are deliberately conservative: a verdict
    /// that cries wolf teaches the admin to ignore it. Settings.cs binds each one to the cfg.
    /// </summary>
    internal sealed class VerdictOptions
    {
        public double FrameStallThresholdMs = 250d;
        public double SevereFrameStallThresholdMs = 500d;
        public double DeliveryDelayThresholdMs = 500d;

        // Server load: Valheim's dedicated server settles around 30 FPS (33 ms) in batchmode, so
        // 1.5x the 50 ms target (75 ms, ~13 FPS) is a server that is really behind, not capped.
        public double ServerFrameTargetMs = 50d;
        public double ServerOverloadFactor = 1.5d;
        public int ServerOverloadSeconds = 10;

        // Peers that must show a symptom in the same second for it to count as correlated.
        public int CorrelationMinPeers = 2;
        public double CorrelationMinFraction = 0.5d;

        public double JitterThresholdMs = 40d;
        public double LossThreshold = 0.15d;
        public int QualityMinPingPairs = 5;

        public double SaturationRatio = 0.9d;
        public int SaturationSeconds = 5;
        public int SaturationPendingFloorBytes = 8 * 1024;
        public int PendingGrowthBytes = 32 * 1024;

        public double ChurnFactor = 3d;
        public double ChurnFloorPerSecond = 2000d;
        public int ChurnBaselineSeconds = 60;
        public int ChurnMinBaselineSamples = 10;

        public double ServerOverloadThresholdMs => ServerFrameTargetMs * ServerOverloadFactor;
    }

    /// <summary>
    /// Tells whether the server is slow without needing a spike: the median of the last N
    /// per-second frame averages. A median, not a mean, so one long frame (a save, a GC) cannot
    /// carry the window by itself; that case is the frame-stall trigger's job.
    /// </summary>
    internal static class ServerLoad
    {
        public static bool IsSustained(IReadOnlyList<double> averageFrameMilliseconds, int seconds, double thresholdMilliseconds)
        {
            seconds = Math.Max(1, seconds);
            if (averageFrameMilliseconds == null || averageFrameMilliseconds.Count < seconds) return false;
            return LastMedian(averageFrameMilliseconds, averageFrameMilliseconds.Count, seconds) >= thresholdMilliseconds;
        }

        /// <summary>Median of the <paramref name="seconds"/> values ending at index <paramref name="end"/> (exclusive).</summary>
        public static double LastMedian(IReadOnlyList<double> values, int end, int seconds)
        {
            int start = Math.Max(0, end - seconds);
            var window = new List<double>(end - start);
            for (int index = start; index < end; index++) window.Add(values[index]);
            if (window.Count == 0) return 0d;
            window.Sort();
            int middle = window.Count / 2;
            return window.Count % 2 == 1 ? window[middle] : (window[middle - 1] + window[middle]) / 2d;
        }
    }

    /// <summary>
    /// Keeps the last N per-second frame averages of the server and reports when it enters the
    /// overloaded state, once per episode, so a long slow stretch opens one capture, not one per cooldown.
    /// </summary>
    internal sealed class ServerOverloadTracker
    {
        private readonly List<double> _averages = new List<double>();

        public bool Overloaded { get; private set; }

        /// <returns>True only on the sample that makes the server overloaded.</returns>
        public bool Observe(RuntimeHealthSample interval, int seconds, double thresholdMilliseconds)
        {
            seconds = Math.Max(1, seconds);
            // An interval without frames carries no average; the frame-stall trigger covers a
            // server that produced none.
            if (interval.Valid)
            {
                _averages.Add(interval.AverageFrameMilliseconds);
                if (_averages.Count > seconds) _averages.RemoveRange(0, _averages.Count - seconds);
            }
            bool was = Overloaded;
            Overloaded = ServerLoad.IsSustained(_averages, seconds, thresholdMilliseconds);
            return Overloaded && !was;
        }

        public void Reset()
        {
            _averages.Clear();
            Overloaded = false;
        }
    }

    /// <summary>What the verdict needs of one player's second: a flat copy of the telemetry point,
    /// so the verdict stays a pure function that the test suites compile without the game.</summary>
    internal readonly struct VerdictPeerSample
    {
        public VerdictPeerSample(
            double seconds,
            int pingMilliseconds,
            double localQuality,
            double remoteQuality,
            int pendingBytes,
            int unackedBytes,
            double queueDelayMilliseconds,
            int transportRateBytesPerSecond,
            double outgoingBytesPerSecond,
            bool congested,
            bool clientAvailable,
            bool clientFocused,
            bool clientPlayerReady,
            bool clientTeleporting,
            double clientFrameMaxMilliseconds,
            double clientDeliveryDelayMilliseconds)
        {
            Seconds = seconds;
            PingMilliseconds = pingMilliseconds;
            LocalQuality = localQuality;
            RemoteQuality = remoteQuality;
            PendingBytes = pendingBytes;
            UnackedBytes = unackedBytes;
            QueueDelayMilliseconds = queueDelayMilliseconds;
            TransportRateBytesPerSecond = transportRateBytesPerSecond;
            OutgoingBytesPerSecond = outgoingBytesPerSecond;
            Congested = congested;
            ClientAvailable = clientAvailable;
            ClientFocused = clientFocused;
            ClientPlayerReady = clientPlayerReady;
            ClientTeleporting = clientTeleporting;
            ClientFrameMaxMilliseconds = clientFrameMaxMilliseconds;
            ClientDeliveryDelayMilliseconds = clientDeliveryDelayMilliseconds;
        }

        public double Seconds { get; }
        public int PingMilliseconds { get; }
        public double LocalQuality { get; }
        public double RemoteQuality { get; }
        public int PendingBytes { get; }
        public int UnackedBytes { get; }
        public double QueueDelayMilliseconds { get; }
        public int TransportRateBytesPerSecond { get; }
        public double OutgoingBytesPerSecond { get; }
        public bool Congested { get; }
        public bool ClientAvailable { get; }
        public bool ClientFocused { get; }
        public bool ClientPlayerReady { get; }
        public bool ClientTeleporting { get; }
        public double ClientFrameMaxMilliseconds { get; }
        public double ClientDeliveryDelayMilliseconds { get; }
    }

    internal sealed class VerdictPeerInput
    {
        public VerdictPeerInput(string name, long uid, bool triggered, IReadOnlyList<VerdictPeerSample> samples)
        {
            Name = string.IsNullOrEmpty(name) ? "unknown" : name;
            Uid = uid;
            Triggered = triggered;
            Samples = samples ?? new VerdictPeerSample[0];
        }

        public string Name { get; }
        public long Uid { get; }
        /// <summary>Whether this player's own anomaly opened or joined the incident.</summary>
        public bool Triggered { get; }
        /// <summary>Oldest first, one per second (pre and post windows together).</summary>
        public IReadOnlyList<VerdictPeerSample> Samples { get; }
    }

    internal readonly struct VerdictServerSample
    {
        public VerdictServerSample(
            double seconds,
            RuntimeHealthSample health,
            double worldSaveMilliseconds,
            int zdoObjects = -1,
            int zdoSentLastSecond = -1,
            int zdoReceivedLastSecond = -1,
            int zdoClientChangeQueue = -1)
        {
            Seconds = seconds;
            Health = health;
            WorldSaveMilliseconds = worldSaveMilliseconds;
            ZdoObjects = zdoObjects;
            ZdoSentLastSecond = zdoSentLastSecond;
            ZdoReceivedLastSecond = zdoReceivedLastSecond;
            ZdoClientChangeQueue = zdoClientChangeQueue;
        }

        public double Seconds { get; }
        public RuntimeHealthSample Health { get; }
        public double WorldSaveMilliseconds { get; }
        /// <summary>ZDOMan counters; -1 when they could not be read.</summary>
        public int ZdoObjects { get; }
        public int ZdoSentLastSecond { get; }
        public int ZdoReceivedLastSecond { get; }
        public int ZdoClientChangeQueue { get; }

        /// <summary>ZDOs the server moved in the second, or -1 when unreadable.</summary>
        public double ZdoTraffic => ZdoSentLastSecond < 0 || ZdoReceivedLastSecond < 0
            ? -1d
            : (double)ZdoSentLastSecond + ZdoReceivedLastSecond;
    }

    /// <summary>Counts, second by second, how many players show a symptom together. Eight players
    /// stalling in the same second point at the server, not at eight connections.</summary>
    internal static class PeerCorrelation
    {
        /// <summary>A symptom that does not depend on a player's own machine being idle: the
        /// connection is backed off, or an in-game focused client froze or reported late.</summary>
        public static bool IsAffected(VerdictPeerSample sample, VerdictOptions options)
        {
            if (sample.Congested) return true;
            bool inGame = sample.ClientAvailable && sample.ClientPlayerReady && !sample.ClientTeleporting && sample.ClientFocused;
            return inGame &&
                   (sample.ClientFrameMaxMilliseconds >= options.SevereFrameStallThresholdMs ||
                    sample.ClientDeliveryDelayMilliseconds >= options.DeliveryDelayThresholdMs);
        }

        public static long Key(double seconds) => (long)Math.Round(seconds);

        public static void Count(
            IReadOnlyList<VerdictPeerInput> peers,
            long key,
            VerdictOptions options,
            out int affected,
            out int total)
        {
            affected = 0;
            total = 0;
            for (int peerIndex = 0; peerIndex < peers.Count; peerIndex++)
            {
                IReadOnlyList<VerdictPeerSample> samples = peers[peerIndex].Samples;
                for (int index = 0; index < samples.Count; index++)
                {
                    if (Key(samples[index].Seconds) != key) continue;
                    total++;
                    if (IsAffected(samples[index], options)) affected++;
                    break;
                }
            }
        }

        public static bool IsCorrelated(int affected, int total, VerdictOptions options)
        {
            if (total <= 0) return false;
            int needed = Math.Max(options.CorrelationMinPeers, (int)Math.Ceiling(options.CorrelationMinFraction * total));
            return affected >= needed;
        }
    }

    internal readonly struct LinkQualityStats
    {
        public LinkQualityStats(int pingPairs, double jitterMilliseconds, bool qualityMeasured, double minimumQuality)
        {
            PingPairs = pingPairs;
            JitterMilliseconds = jitterMilliseconds;
            QualityMeasured = qualityMeasured;
            MinimumQuality = minimumQuality;
        }

        /// <summary>Consecutive-second ping pairs where both pings were measured.</summary>
        public int PingPairs { get; }
        /// <summary>Mean absolute change of the ping from one second to the next.</summary>
        public double JitterMilliseconds { get; }
        public bool QualityMeasured { get; }
        /// <summary>Lowest measured connection quality (0-1) in the window; 1 when none was measured.</summary>
        public double MinimumQuality { get; }
        /// <summary>1 - lowest quality: the loss Steam's quality figure lets us infer, not a measured loss.</summary>
        public double ApproximateLoss => QualityMeasured ? Math.Max(0d, 1d - MinimumQuality) : 0d;
    }

    /// <summary>Jitter and approximate loss from a player's per-second history, on the server alone.</summary>
    internal static class LinkQuality
    {
        /// <remarks>A ping of 0 and a quality of 0 mean "not measured" (the link could not be
        /// sampled that second), never a perfect ping or a dead link, so they are skipped.</remarks>
        public static LinkQualityStats Compute(IReadOnlyList<VerdictPeerSample> samples)
        {
            int pairs = 0;
            double jitterSum = 0d;
            bool qualityMeasured = false;
            double minimumQuality = 1d;
            for (int index = 0; index < samples.Count; index++)
            {
                VerdictPeerSample current = samples[index];
                if (current.LocalQuality > 0d)
                {
                    qualityMeasured = true;
                    minimumQuality = Math.Min(minimumQuality, current.LocalQuality);
                }
                if (current.RemoteQuality > 0d)
                {
                    qualityMeasured = true;
                    minimumQuality = Math.Min(minimumQuality, current.RemoteQuality);
                }
                if (index == 0 || current.PingMilliseconds <= 0 || samples[index - 1].PingMilliseconds <= 0) continue;
                pairs++;
                jitterSum += Math.Abs(current.PingMilliseconds - samples[index - 1].PingMilliseconds);
            }
            return new LinkQualityStats(pairs, pairs > 0 ? jitterSum / pairs : 0d, qualityMeasured, minimumQuality);
        }

        public static bool IsBad(LinkQualityStats stats, VerdictOptions options, out bool jitterHigh, out bool lossHigh)
        {
            jitterHigh = stats.PingPairs >= options.QualityMinPingPairs &&
                         stats.JitterMilliseconds >= options.JitterThresholdMs;
            lossHigh = stats.QualityMeasured && stats.ApproximateLoss >= options.LossThreshold;
            return jitterHigh || lossHigh;
        }
    }

    internal readonly struct SaturationResult
    {
        public SaturationResult(bool saturated, bool byRate, int seconds, double peakOutgoingBytesPerSecond, int rateBytesPerSecond, int peakPendingBytes, int pendingFromBytes)
        {
            Saturated = saturated;
            ByRate = byRate;
            Seconds = seconds;
            PeakOutgoingBytesPerSecond = peakOutgoingBytesPerSecond;
            RateBytesPerSecond = rateBytesPerSecond;
            PeakPendingBytes = peakPendingBytes;
            PendingFromBytes = pendingFromBytes;
        }

        public bool Saturated { get; }
        /// <summary>True when the output sat at the Steam rate; false when only the pending bytes kept growing.</summary>
        public bool ByRate { get; }
        public int Seconds { get; }
        public double PeakOutgoingBytesPerSecond { get; }
        public int RateBytesPerSecond { get; }
        public int PeakPendingBytes { get; }
        public int PendingFromBytes { get; }
    }

    /// <summary>
    /// A saturated link is one the server cannot push more bytes into: its output sits at the
    /// Steam rate while bytes wait to go out, or the bytes waiting keep growing. Bytes sent and
    /// not yet acknowledged are NOT a queue: at 225-250 ms ping a healthy link always has a
    /// round trip of data in flight, so only the pending bytes count here.
    /// </summary>
    internal static class LinkSaturation
    {
        public static SaturationResult Analyze(IReadOnlyList<VerdictPeerSample> samples, VerdictOptions options)
        {
            int needed = Math.Max(2, options.SaturationSeconds);

            int run = 0;
            int bestRun = 0;
            double peakOut = 0d;
            int rate = 0;
            int peakPending = 0;
            for (int index = 0; index < samples.Count; index++)
            {
                VerdictPeerSample sample = samples[index];
                bool atRate = sample.TransportRateBytesPerSecond > 0 &&
                              sample.OutgoingBytesPerSecond >= options.SaturationRatio * sample.TransportRateBytesPerSecond &&
                              sample.PendingBytes >= options.SaturationPendingFloorBytes;
                run = atRate ? run + 1 : 0;
                if (run > bestRun)
                {
                    bestRun = run;
                    peakOut = Math.Max(peakOut, sample.OutgoingBytesPerSecond);
                    rate = sample.TransportRateBytesPerSecond;
                    peakPending = Math.Max(peakPending, sample.PendingBytes);
                }
            }
            if (bestRun >= needed)
            {
                return new SaturationResult(true, true, bestRun, peakOut, rate, peakPending, 0);
            }

            // Pending bytes that never drop for N samples and end well above where they began.
            for (int start = 0; start + needed <= samples.Count; start++)
            {
                bool rising = true;
                for (int step = start + 1; step < start + needed && rising; step++)
                {
                    rising = samples[step].PendingBytes >= samples[step - 1].PendingBytes;
                }
                int first = samples[start].PendingBytes;
                int last = samples[start + needed - 1].PendingBytes;
                if (rising &&
                    last >= options.SaturationPendingFloorBytes &&
                    last - first >= options.PendingGrowthBytes)
                {
                    return new SaturationResult(true, false, needed, 0d, 0, last, first);
                }
            }
            return default;
        }
    }

    internal readonly struct ChurnResult
    {
        public ChurnResult(bool triggered, double current, double median, double ratio)
        {
            Triggered = triggered;
            Current = current;
            Median = median;
            Ratio = ratio;
        }

        public bool Triggered { get; }
        public double Current { get; }
        public double Median { get; }
        public double Ratio { get; }
    }

    /// <summary>
    /// ZDO traffic of the second against the median of the previous minute. The counters come
    /// from ZDOMan (sent and received last second); nothing here walks the ZDOs themselves.
    /// A negative value means the counter could not be read and is skipped.
    /// </summary>
    internal static class ZdoChurn
    {
        /// <summary>Evaluates the value at <paramref name="index"/> against the median of the up to
        /// <c>ChurnBaselineSeconds</c> readable values before it.</summary>
        public static ChurnResult EvaluateAt(IReadOnlyList<double> values, int index, VerdictOptions options)
        {
            if (index < 0 || index >= values.Count || values[index] < 0d) return default;

            var baseline = new List<double>();
            for (int back = index - 1; back >= 0 && baseline.Count < options.ChurnBaselineSeconds; back--)
            {
                if (values[back] >= 0d) baseline.Add(values[back]);
            }
            if (baseline.Count < options.ChurnMinBaselineSamples) return default;

            baseline.Sort();
            int middle = baseline.Count / 2;
            double median = baseline.Count % 2 == 1 ? baseline[middle] : (baseline[middle - 1] + baseline[middle]) / 2d;
            double current = values[index];
            double ratio = current / Math.Max(1d, median);
            bool triggered = current >= options.ChurnFloorPerSecond && ratio >= options.ChurnFactor;
            return new ChurnResult(triggered, current, median, ratio);
        }

        /// <summary>The strongest churn second anywhere in the series, or default when none triggers.</summary>
        public static ChurnResult Peak(IReadOnlyList<double> values, VerdictOptions options)
        {
            ChurnResult best = default;
            for (int index = 0; index < values.Count; index++)
            {
                ChurnResult result = EvaluateAt(values, index, options);
                if (result.Triggered && result.Ratio > best.Ratio) best = result;
            }
            return best;
        }
    }
}
