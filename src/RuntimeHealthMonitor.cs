using System;
using AdaptiveNet.Core;

namespace AdaptiveNet
{
    internal static class RuntimeHealthMonitor
    {
        private static readonly double[] FrameBucketUpperMilliseconds =
            { 8d, 12d, 16d, 20d, 25d, 33d, 50d, 75d, 100d, 150d, 250d, 500d, 1000d, 2000d, 60000d };
        private static readonly int[] FrameBuckets = new int[FrameBucketUpperMilliseconds.Length];
        private static double _lastFrameTime;
        private static double _windowStartTime;
        private static double _frameMillisecondsTotal;
        private static double _maximumFrameMilliseconds;
        private static double _gcCorrelatedStallMilliseconds;
        private static int _frameCount;
        private static int _stallFrames;
        private static int _severeStallFrames;
        private static int _gen0Collections;
        private static int _gen1Collections;
        private static int _gen2Collections;
        private static int _lastGen0;
        private static int _lastGen1;
        private static int _lastGen2;

        public static void Initialize(double now)
        {
            _lastFrameTime = now;
            _windowStartTime = now;
            _lastGen0 = GC.CollectionCount(0);
            _lastGen1 = GC.CollectionCount(1);
            _lastGen2 = GC.CollectionCount(2);
            ResetCounters();
        }

        public static void RecordFrame(
            double now,
            double stallThresholdMilliseconds,
            double severeStallThresholdMilliseconds)
        {
            if (_lastFrameTime <= 0d || now < _lastFrameTime)
            {
                Initialize(now);
                return;
            }

            double frameMilliseconds = Math.Max(0d, Math.Min(60000d, (now - _lastFrameTime) * 1000d));
            _lastFrameTime = now;
            if (frameMilliseconds > 0d)
            {
                _frameCount++;
                _frameMillisecondsTotal += frameMilliseconds;
                _maximumFrameMilliseconds = Math.Max(_maximumFrameMilliseconds, frameMilliseconds);
                RecordFrameBucket(frameMilliseconds);
                if (frameMilliseconds >= stallThresholdMilliseconds) _stallFrames++;
                if (frameMilliseconds >= severeStallThresholdMilliseconds) _severeStallFrames++;
            }

            int gen0 = GC.CollectionCount(0);
            int gen1 = GC.CollectionCount(1);
            int gen2 = GC.CollectionCount(2);
            int gen0Delta = Math.Max(0, gen0 - _lastGen0);
            int gen1Delta = Math.Max(0, gen1 - _lastGen1);
            int gen2Delta = Math.Max(0, gen2 - _lastGen2);
            _lastGen0 = gen0;
            _lastGen1 = gen1;
            _lastGen2 = gen2;
            _gen0Collections += gen0Delta;
            _gen1Collections += gen1Delta;
            _gen2Collections += gen2Delta;
            if (gen0Delta + gen1Delta + gen2Delta > 0)
            {
                // The runtime does not expose the exact stop-the-world duration here.
                // This records the frame containing a collection, so the CSV labels it
                // as correlated rather than claiming the GC caused the entire frame.
                _gcCorrelatedStallMilliseconds = Math.Max(_gcCorrelatedStallMilliseconds, frameMilliseconds);
            }
        }

        public static RuntimeHealthSample TakeInterval(double now)
        {
            double interval = Math.Max(0d, now - _windowStartTime);
            var sample = new RuntimeHealthSample(
                interval,
                _frameCount,
                _frameCount > 0 ? _frameMillisecondsTotal / _frameCount : 0d,
                GetP95FrameMilliseconds(),
                _maximumFrameMilliseconds,
                _stallFrames,
                _severeStallFrames,
                _gen0Collections,
                _gen1Collections,
                _gen2Collections,
                _gcCorrelatedStallMilliseconds,
                SafeManagedMemory());
            _windowStartTime = now;
            ResetCounters();
            return sample;
        }

        private static long SafeManagedMemory()
        {
            try
            {
                return GC.GetTotalMemory(false);
            }
            catch
            {
                return 0L;
            }
        }

        private static void ResetCounters()
        {
            _frameMillisecondsTotal = 0d;
            _maximumFrameMilliseconds = 0d;
            _gcCorrelatedStallMilliseconds = 0d;
            _frameCount = 0;
            _stallFrames = 0;
            _severeStallFrames = 0;
            _gen0Collections = 0;
            _gen1Collections = 0;
            _gen2Collections = 0;
            Array.Clear(FrameBuckets, 0, FrameBuckets.Length);
        }

        private static void RecordFrameBucket(double frameMilliseconds)
        {
            for (int index = 0; index < FrameBucketUpperMilliseconds.Length; index++)
            {
                if (frameMilliseconds <= FrameBucketUpperMilliseconds[index])
                {
                    FrameBuckets[index]++;
                    return;
                }
            }
            FrameBuckets[FrameBuckets.Length - 1]++;
        }

        private static double GetP95FrameMilliseconds()
        {
            if (_frameCount <= 0) return 0d;
            int target = Math.Max(1, (int)Math.Ceiling(_frameCount * 0.95d));
            int seen = 0;
            for (int index = 0; index < FrameBuckets.Length; index++)
            {
                seen += FrameBuckets[index];
                if (seen >= target) return FrameBucketUpperMilliseconds[index];
            }
            return _maximumFrameMilliseconds;
        }
    }
}
