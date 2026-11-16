using System;

namespace AdaptiveNet
{
    /// <summary>
    /// Keeps a peer's send queue from sitting above ServerCharacters' / ServerSync's 20000-byte wait
    /// (<c>while GetSendQueueSize() &gt; 20000</c>, disconnect after 30 s). If the queue has stayed at or
    /// above <see cref="HighBytes"/> for <see cref="HoldSeconds"/>, ZDO sending is refused until it falls
    /// below <see cref="ReleaseBytes"/>. Pure function of the observed queue and time.
    /// </summary>
    internal sealed class ZdoDrainWindow
    {
        public const int HighBytes = 19 * 1024;
        public const int ReleaseBytes = 15 * 1024;
        public const double HoldSeconds = 5d;

        private double _aboveSince = double.NaN;

        public bool Draining { get; private set; }

        /// <summary>Records the queue at <paramref name="now"/>; returns true while ZDOs must be refused.</summary>
        public bool Observe(int queueBytes, double now)
        {
            if (Draining)
            {
                if (queueBytes < ReleaseBytes)
                {
                    Draining = false;
                    _aboveSince = double.NaN;
                }
                return Draining;
            }

            if (queueBytes < HighBytes)
            {
                _aboveSince = double.NaN;
                return false;
            }

            if (double.IsNaN(_aboveSince)) _aboveSince = now;
            if (now - _aboveSince >= HoldSeconds) Draining = true;
            return Draining;
        }
    }

    internal static class QueueDelayReading
    {
        /// <summary>Longest queue delay believed to be real; Steam reports INT64_MAX-style values when unknown.</summary>
        public const double MaximumPlausibleMilliseconds = 60_000d;

        /// <summary>Microseconds from Steam to milliseconds; negative or implausible readings become 0 (unknown).</summary>
        public static double FromMicroseconds(long microseconds)
        {
            if (microseconds <= 0) return 0d;
            double milliseconds = microseconds / 1000d;
            return milliseconds > MaximumPlausibleMilliseconds ? 0d : milliseconds;
        }
    }
}
