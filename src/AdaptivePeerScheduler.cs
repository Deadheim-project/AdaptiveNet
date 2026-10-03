using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using AdaptiveNet.Core;
using BepInEx.Logging;
using UnityEngine;

namespace AdaptiveNet
{
    internal readonly struct SchedulerSnapshot
    {
        public SchedulerSnapshot(int groupedPeers, double attemptsPerSecond, int budgetStopsPerSecond)
        {
            GroupedPeers = groupedPeers;
            AttemptsPerSecond = attemptsPerSecond;
            BudgetStopsPerSecond = budgetStopsPerSecond;
        }
        public int GroupedPeers { get; }
        public double AttemptsPerSecond { get; }
        public int BudgetStopsPerSecond { get; }
    }

    internal static class AdaptivePeerScheduler
    {
        private sealed class PeerState
        {
            public double NextDueTime;
            public double LastAttemptTime;
            public bool Grouped;
            public int NearbyPlayers;
        }

        private static readonly Dictionary<ZNetPeer, PeerState> States =
            new Dictionary<ZNetPeer, PeerState>(ReferenceComparer<ZNetPeer>.Instance);
        // Tick runs every frame on the main thread, so its scratch collections are reused
        // rather than allocated: per-frame garbage only brings Unity's stop-the-world GC sooner.
        private static readonly HashSet<ZNetPeer> LivePeers =
            new HashSet<ZNetPeer>(ReferenceComparer<ZNetPeer>.Instance);
        private static readonly List<ZNetPeer> StalePeers = new List<ZNetPeer>();
        private static PeerSchedulerOptions _options;
        private static ManualLogSource _log;
        private static double _nextClusterRefresh;
        private static int _cursor;
        private static bool _faulted;
        private static double _predictedCallTicks;
        private static double _metricsWindowStart;
        private static int _attemptsInWindow;
        private static int _budgetStopsInWindow;
        private static SchedulerSnapshot _snapshot;

        public static SchedulerSnapshot Snapshot => _snapshot;

        public static double GetTransportWeight(ZNetPeer peer)
        {
            return peer != null && States.TryGetValue(peer, out PeerState state) && state.Grouped ? 1.5d : 1d;
        }

        public static int GetNearbyPlayers(ZNetPeer peer)
        {
            return peer != null && States.TryGetValue(peer, out PeerState state)
                ? Math.Max(1, state.NearbyPlayers)
                : 1;
        }

        public static void Initialize(PeerSchedulerOptions options, ManualLogSource log)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _options.Validate();
            _log = log;
            _nextClusterRefresh = 0d;
            _cursor = 0;
            _faulted = false;
            _predictedCallTicks = Stopwatch.Frequency * 0.00005d;
            _metricsWindowStart = 0d;
            _attemptsInWindow = 0;
            _budgetStopsInWindow = 0;
            States.Clear();
        }

        /// <summary>
        /// Swaps in options reloaded from the cfg. Every interval and cap is read from the
        /// options on each pass, so peer state and metrics carry over; only the grouping is
        /// refreshed at once so a new radius or minimum does not wait for the next cycle.
        /// </summary>
        public static void UpdateOptions(PeerSchedulerOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (_options == null) return;
            options.Validate();
            _options = options;
            _nextClusterRefresh = 0d;
        }

        public static bool TryHandle(ZDOMan manager)
        {
            if (_faulted || _options == null || manager == null) return false;
            try
            {
                Tick(manager, Time.realtimeSinceStartupAsDouble);
                return true;
            }
            catch (Exception exception)
            {
                _faulted = true;
                _log?.LogError($"Adaptive scheduler failed; reverting to Valheim scheduler: {exception.GetType().Name}: {exception.Message}");
                return false;
            }
        }

        public static void Reset()
        {
            States.Clear();
            LivePeers.Clear();
            _options = null;
            _faulted = false;
            _snapshot = default;
        }

        private static void Tick(ZDOMan manager, double now)
        {
            IList peers = GameAccess.GetZdoPeers(manager);
            if (peers == null || peers.Count == 0)
            {
                States.Clear();
                UpdateMetrics(now);
                return;
            }

            SynchronizeStates(peers, now);
            if (now >= _nextClusterRefresh)
            {
                RefreshGroups(peers, now);
                _nextClusterRefresh = now + 0.75d;
            }

            long startedAt = Stopwatch.GetTimestamp();
            double budgetTicks = _options.CpuBudgetMilliseconds * Stopwatch.Frequency / 1000d;
            int attempts = 0;
            bool budgetStopped = false;
            while (attempts < _options.MaximumPeersPerFrame)
            {
                int index = FindOldestDuePeer(peers, now);
                if (index < 0) break;
                long elapsed = Stopwatch.GetTimestamp() - startedAt;
                if (attempts > 0 && elapsed + _predictedCallTicks > budgetTicks)
                {
                    budgetStopped = true;
                    break;
                }

                object zdoPeer = peers[index];
                ZNetPeer peer = GameAccess.GetZNetPeer(zdoPeer);
                PeerState state = GetState(peer);
                long callStart = Stopwatch.GetTimestamp();
                GameAccess.SendZdos(manager, zdoPeer, false);
                long callTicks = Math.Max(1L, Stopwatch.GetTimestamp() - callStart);
                double weight = callTicks > _predictedCallTicks ? 0.35d : 0.08d;
                _predictedCallTicks += (callTicks - _predictedCallTicks) * weight;

                attempts++;
                _cursor = (index + 1) % Math.Max(1, peers.Count);
                state.LastAttemptTime = now;
                int interval = PeerCadencePolicy.GetIntervalMilliseconds(
                    ZNet.instance != null && ZNet.instance.IsServer(), state.NearbyPlayers, peers.Count, _options);
                state.NextDueTime = now + interval / 1000d;
            }

            _attemptsInWindow += attempts;
            if (budgetStopped) _budgetStopsInWindow++;
            UpdateMetrics(now);
        }

        private static int FindOldestDuePeer(IList peers, double now)
        {
            int selected = -1;
            double oldestDeadline = double.MaxValue;
            for (int offset = 0; offset < peers.Count; offset++)
            {
                int index = (_cursor + offset) % peers.Count;
                ZNetPeer peer = GameAccess.GetZNetPeer(peers[index]);
                if (peer == null) continue;
                PeerState state = GetState(peer);
                if (state.NextDueTime <= now && state.NextDueTime < oldestDeadline)
                {
                    selected = index;
                    oldestDeadline = state.NextDueTime;
                }
            }
            return selected;
        }

        private static void SynchronizeStates(IList peers, double now)
        {
            LivePeers.Clear();
            int peerCount = Math.Max(1, peers.Count);
            for (int index = 0; index < peers.Count; index++)
            {
                ZNetPeer peer = GameAccess.GetZNetPeer(peers[index]);
                if (peer == null) continue;
                LivePeers.Add(peer);
                if (!States.ContainsKey(peer))
                {
                    States.Add(peer, new PeerState
                    {
                        NextDueTime = now + (index + 1d) / peerCount * 0.1d,
                        LastAttemptTime = now,
                        NearbyPlayers = 1
                    });
                }
            }

            // Every live peer now has a state, so equal counts mean nobody left.
            if (States.Count == LivePeers.Count) return;
            foreach (ZNetPeer peer in States.Keys)
            {
                if (!LivePeers.Contains(peer)) StalePeers.Add(peer);
            }
            for (int index = 0; index < StalePeers.Count; index++) States.Remove(StalePeers[index]);
            StalePeers.Clear();
        }

        private static void RefreshGroups(IList peers, double now)
        {
            bool isServer = ZNet.instance != null && ZNet.instance.IsServer();
            float radiusSquared = _options.GroupRadiusMeters * _options.GroupRadiusMeters;
            bool countHost = isServer && ZNet.instance != null && !ZNet.instance.IsDedicated();
            Vector3 hostPosition = countHost ? ZNet.instance.GetReferencePosition() : Vector3.zero;
            for (int candidateIndex = 0; candidateIndex < peers.Count; candidateIndex++)
            {
                ZNetPeer candidate = GameAccess.GetZNetPeer(peers[candidateIndex]);
                if (candidate == null) continue;
                PeerState state = GetState(candidate);
                if (!isServer)
                {
                    state.NearbyPlayers = 1;
                    state.Grouped = true;
                    continue;
                }

                int nearby = 1;
                Vector3 origin = candidate.GetRefPos();
                for (int otherIndex = 0; otherIndex < peers.Count; otherIndex++)
                {
                    ZNetPeer other = GameAccess.GetZNetPeer(peers[otherIndex]);
                    if (other != null && !ReferenceEquals(other, candidate) &&
                        (other.GetRefPos() - origin).sqrMagnitude <= radiusSquared) nearby++;
                }
                if (countHost && (hostPosition - origin).sqrMagnitude <= radiusSquared) nearby++;

                bool wasGrouped = state.Grouped;
                state.NearbyPlayers = nearby;
                state.Grouped = nearby >= _options.GroupMinimumPlayers;
                if (state.Grouped && !wasGrouped)
                {
                    state.NextDueTime = Math.Min(state.NextDueTime, now + _options.GroupedPeerIntervalMilliseconds / 1000d);
                }
            }
        }

        private static PeerState GetState(ZNetPeer peer)
        {
            if (!States.TryGetValue(peer, out PeerState state))
            {
                state = new PeerState
                {
                    NextDueTime = Time.realtimeSinceStartupAsDouble,
                    LastAttemptTime = Time.realtimeSinceStartupAsDouble,
                    NearbyPlayers = 1
                };
                States.Add(peer, state);
            }
            return state;
        }

        private static void UpdateMetrics(double now)
        {
            if (_metricsWindowStart <= 0d) { _metricsWindowStart = now; return; }
            double elapsed = now - _metricsWindowStart;
            if (elapsed < 1d) return;
            int groupedPeers = 0;
            foreach (PeerState state in States.Values)
            {
                if (state.Grouped) groupedPeers++;
            }
            _snapshot = new SchedulerSnapshot(groupedPeers, _attemptsInWindow / Math.Max(0.001d, elapsed), _budgetStopsInWindow);
            _attemptsInWindow = 0;
            _budgetStopsInWindow = 0;
            _metricsWindowStart = now;
        }

        private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
        {
            public static readonly ReferenceComparer<T> Instance = new ReferenceComparer<T>();
            public bool Equals(T x, T y) => ReferenceEquals(x, y);
            public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
