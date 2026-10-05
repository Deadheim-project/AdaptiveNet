using System;
using System.Collections.Generic;

namespace AdaptiveNet.Core
{
    /// <summary>Count, mean, p95 and maximum of one latency stream over one sample window.</summary>
    internal readonly struct LatencySummary
    {
        public LatencySummary(int count, double averageMilliseconds, double p95Milliseconds, double maximumMilliseconds)
        {
            Count = Math.Max(0, count);
            AverageMilliseconds = Sanitize(averageMilliseconds);
            P95Milliseconds = Sanitize(p95Milliseconds);
            MaximumMilliseconds = Sanitize(maximumMilliseconds);
        }

        public int Count { get; }
        public double AverageMilliseconds { get; }
        public double P95Milliseconds { get; }
        public double MaximumMilliseconds { get; }

        /// <summary>
        /// Combines two windows. Exact for count, mean and maximum; the p95 of the union is not
        /// recoverable from two p95s, so the larger one stands in as a conservative bound.
        /// </summary>
        public static LatencySummary Merge(LatencySummary older, LatencySummary newer)
        {
            if (older.Count == 0) return newer;
            if (newer.Count == 0) return older;
            int count = older.Count + newer.Count;
            return new LatencySummary(
                count,
                (older.AverageMilliseconds * older.Count + newer.AverageMilliseconds * newer.Count) / count,
                Math.Max(older.P95Milliseconds, newer.P95Milliseconds),
                Math.Max(older.MaximumMilliseconds, newer.MaximumMilliseconds));
        }

        private static double Sanitize(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value) ? 0d : Math.Max(0d, value);
        }
    }

    /// <summary>
    /// Accumulates latencies without keeping the samples: a sum, a maximum and a fixed bucket
    /// histogram for the p95. Allocation-free after construction, because the PvP probes feed it
    /// from Valheim's send path several hundred times a second.
    /// </summary>
    internal sealed class LatencyAccumulator
    {
        private static readonly double[] BucketUpperMilliseconds =
            { 5d, 10d, 20d, 30d, 40d, 50d, 75d, 100d, 150d, 200d, 300d, 500d, 1000d, 2000d, 5000d, double.MaxValue };

        private readonly int[] _buckets = new int[BucketUpperMilliseconds.Length];
        private double _sum;
        private double _maximum;

        public int Count { get; private set; }

        public void Add(double milliseconds)
        {
            if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds)) return;
            milliseconds = Math.Max(0d, milliseconds);
            Count++;
            _sum += milliseconds;
            _maximum = Math.Max(_maximum, milliseconds);
            for (int index = 0; index < BucketUpperMilliseconds.Length; index++)
            {
                if (milliseconds <= BucketUpperMilliseconds[index])
                {
                    _buckets[index]++;
                    return;
                }
            }
        }

        /// <summary>The p95 as its bucket's upper bound, never above the observed maximum.</summary>
        public LatencySummary Summarize()
        {
            if (Count == 0) return default;
            int target = Math.Max(1, (int)Math.Ceiling(Count * 0.95d));
            int seen = 0;
            double p95 = _maximum;
            for (int index = 0; index < _buckets.Length; index++)
            {
                seen += _buckets[index];
                if (seen >= target)
                {
                    p95 = Math.Min(_maximum, BucketUpperMilliseconds[index]);
                    break;
                }
            }
            return new LatencySummary(Count, _sum / Count, p95, _maximum);
        }

        public LatencySummary TakeSummary()
        {
            LatencySummary summary = Summarize();
            Reset();
            return summary;
        }

        public void Reset()
        {
            Array.Clear(_buckets, 0, _buckets.Length);
            _sum = 0d;
            _maximum = 0d;
            Count = 0;
        }
    }

    /// <summary>One receiving player's view of the PvP probes over one sample window.</summary>
    internal readonly struct PvpPeerStats
    {
        public PvpPeerStats(
            LatencySummary relayHold,
            LatencySummary hitForward,
            int serviceCalls,
            double serviceGapMaximumMilliseconds,
            int queueRefusals)
        {
            RelayHold = relayHold;
            HitForward = hitForward;
            ServiceCalls = Math.Max(0, serviceCalls);
            ServiceGapMaximumMilliseconds = double.IsNaN(serviceGapMaximumMilliseconds)
                ? 0d
                : Math.Max(0d, serviceGapMaximumMilliseconds);
            QueueRefusals = Math.Max(0, queueRefusals);
        }

        /// <summary>How long newer positions of nearby players sat on the server before this player got them.</summary>
        public LatencySummary RelayHold { get; }
        /// <summary>Estimated wait in this player's send queue of each hit RPC routed to them.</summary>
        public LatencySummary HitForward { get; }
        /// <summary>ZDO send attempts for this player (vanilla or adaptive scheduler alike).</summary>
        public int ServiceCalls { get; }
        /// <summary>Longest time between two ZDO send attempts for this player, including one still open.</summary>
        public double ServiceGapMaximumMilliseconds { get; }
        /// <summary>Attempts the ZDO queue guard turned away because the connection was already full.</summary>
        public int QueueRefusals { get; }
    }

    /// <summary>
    /// Measures how long the server holds a newer revision of a source (a player's character ZDO)
    /// before a receiver (another player) is sent any newer revision of it.
    /// </summary>
    /// <remarks>
    /// Arrivals are recorded when the source's owner uploads a new revision; services when the
    /// server runs a ZDO send for a receiver. The hold for a delivered update is measured from the
    /// arrival of the oldest revision the receiver had not seen yet, because that is how long the
    /// receiver's picture of that player stayed stale while the server already knew better.
    ///
    /// Only pairs within the PvP radius count, and only once the receiver has been following the
    /// source: the first copy after the source walks into range, or after a respawn, is a
    /// visibility change rather than a relay delay, and would otherwise read as a hold of however
    /// long the source had been out of sight.
    /// </remarks>
    internal sealed class RelayHoldTracker<TSource, TReceiver>
    {
        public const int RevisionHistory = 256;

        internal sealed class Link
        {
            internal uint Revision;
            internal bool HasRevision;
            internal double InRangeSince = double.NaN;
        }

        private sealed class SourceHistory
        {
            public readonly uint[] Revisions = new uint[RevisionHistory];
            public readonly double[] Times = new double[RevisionHistory];
            public int Next;
            public int Count;
            public uint Latest;

            public void Clear()
            {
                Next = 0;
                Count = 0;
            }
        }

        private readonly Dictionary<TSource, SourceHistory> _sources = new Dictionary<TSource, SourceHistory>();
        private readonly Dictionary<TReceiver, Dictionary<TSource, Link>> _links =
            new Dictionary<TReceiver, Dictionary<TSource, Link>>();
        private readonly List<TSource> _staleSources = new List<TSource>();
        private readonly List<TReceiver> _staleReceivers = new List<TReceiver>();

        public int SourceCount => _sources.Count;
        public int ReceiverCount => _links.Count;

        public void ObserveArrival(TSource source, uint revision, double now)
        {
            if (!_sources.TryGetValue(source, out SourceHistory history))
            {
                history = new SourceHistory();
                _sources.Add(source, history);
            }
            else if (history.Count > 0)
            {
                if (revision == history.Latest) return;
                // Revisions only grow for a live ZDO; going back means it was recreated.
                if (revision < history.Latest) history.Clear();
            }

            history.Revisions[history.Next] = revision;
            history.Times[history.Next] = now;
            history.Next = (history.Next + 1) % RevisionHistory;
            if (history.Count < RevisionHistory) history.Count++;
            history.Latest = revision;
        }

        public Link GetLink(TReceiver receiver, TSource source)
        {
            if (!_links.TryGetValue(receiver, out Dictionary<TSource, Link> links))
            {
                links = new Dictionary<TSource, Link>();
                _links.Add(receiver, links);
            }
            if (!links.TryGetValue(source, out Link link))
            {
                link = new Link();
                links.Add(source, link);
            }
            return link;
        }

        /// <summary>
        /// Whether the server holds a revision of the source this link has not recorded as sent;
        /// when false, the caller can skip looking up what the receiver was sent.
        /// </summary>
        public bool IsBehind(Link link, TSource source)
        {
            return _sources.TryGetValue(source, out SourceHistory history) &&
                   history.Count > 0 &&
                   (!link.HasRevision || link.Revision != history.Latest);
        }

        /// <param name="inRange">Whether the source is within the PvP radius of the receiver now.</param>
        /// <param name="revisionKnown">Whether <paramref name="revision"/> holds what the receiver has been sent.</param>
        /// <returns>True when this service delivered an update that yields a hold sample.</returns>
        public bool ObserveService(
            Link link,
            TSource source,
            bool inRange,
            bool revisionKnown,
            uint revision,
            double now,
            out double holdMilliseconds)
        {
            holdMilliseconds = 0d;
            bool wasInRange = !double.IsNaN(link.InRangeSince);
            if (!inRange) link.InRangeSince = double.NaN;
            else if (!wasInRange) link.InRangeSince = now;

            if (!revisionKnown || link.HasRevision && revision == link.Revision) return false;
            bool hadRevision = link.HasRevision;
            uint previous = link.Revision;
            link.Revision = revision;
            link.HasRevision = true;
            if (!hadRevision || !inRange || !wasInRange || revision < previous) return false;
            if (!_sources.TryGetValue(source, out SourceHistory history) || history.Count == 0) return false;

            // Walk newest to oldest to the oldest revision the receiver had not been sent. A full
            // ring means the receiver is further behind than the history reaches: the oldest
            // entry still gives a lower bound, which is the honest answer.
            double firstNewerArrival = double.NaN;
            for (int step = 1; step <= history.Count; step++)
            {
                int index = (history.Next - step + RevisionHistory) % RevisionHistory;
                if (history.Revisions[index] <= previous) break;
                firstNewerArrival = history.Times[index];
            }
            if (double.IsNaN(firstNewerArrival)) return false;

            holdMilliseconds = Math.Max(0d, (now - Math.Max(firstNewerArrival, link.InRangeSince)) * 1000d);
            return true;
        }

        /// <summary>Forgets sources and receivers that are no longer connected.</summary>
        public void Retain(ICollection<TSource> liveSources, ICollection<TReceiver> liveReceivers)
        {
            foreach (TSource source in _sources.Keys)
            {
                if (!liveSources.Contains(source)) _staleSources.Add(source);
            }
            foreach (TSource source in _staleSources) _sources.Remove(source);
            _staleSources.Clear();

            foreach (KeyValuePair<TReceiver, Dictionary<TSource, Link>> pair in _links)
            {
                if (!liveReceivers.Contains(pair.Key))
                {
                    _staleReceivers.Add(pair.Key);
                    continue;
                }
                foreach (TSource source in pair.Value.Keys)
                {
                    if (!liveSources.Contains(source)) _staleSources.Add(source);
                }
                foreach (TSource source in _staleSources) pair.Value.Remove(source);
                _staleSources.Clear();
            }
            foreach (TReceiver receiver in _staleReceivers) _links.Remove(receiver);
            _staleReceivers.Clear();
        }

        public void Clear()
        {
            _sources.Clear();
            _links.Clear();
        }
    }
}
