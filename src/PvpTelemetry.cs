using System;
using System.Collections.Generic;
using System.Diagnostics;
using AdaptiveNet.Core;
using BepInEx.Logging;
using UnityEngine;

namespace AdaptiveNet
{
    /// <summary>What the PvP probes saw during one sample window.</summary>
    internal sealed class PvpWindow
    {
        public static readonly PvpWindow Empty = new PvpWindow(
            new Dictionary<long, PvpPeerStats>(), default, default, 0d, 0, 0d, 0);

        private readonly Dictionary<long, PvpPeerStats> _peers;

        public PvpWindow(
            Dictionary<long, PvpPeerStats> peers,
            LatencySummary relayHold,
            LatencySummary hitForward,
            double serviceGapMaximumMilliseconds,
            int queueRefusals,
            double worldSaveMilliseconds,
            int worldSaves)
        {
            _peers = peers ?? new Dictionary<long, PvpPeerStats>();
            RelayHold = relayHold;
            HitForward = hitForward;
            ServiceGapMaximumMilliseconds = serviceGapMaximumMilliseconds;
            QueueRefusals = queueRefusals;
            WorldSaveMilliseconds = worldSaveMilliseconds;
            WorldSaves = worldSaves;
        }

        public LatencySummary RelayHold { get; }
        public LatencySummary HitForward { get; }
        public double ServiceGapMaximumMilliseconds { get; }
        public int QueueRefusals { get; }
        /// <summary>Longest main-thread world save in the window; zero when none ran.</summary>
        public double WorldSaveMilliseconds { get; }
        public int WorldSaves { get; }

        public PvpPeerStats Get(long uid) => _peers.TryGetValue(uid, out PvpPeerStats stats) ? stats : default;
    }

    /// <summary>
    /// Server-side measurements of what makes Valheim PvP feel laggy, gathered from Valheim's own
    /// send path so they read the same with the adaptive scheduler on or off (Mode=ObserveOnly):
    ///
    ///   relay hold    — how long a newer position of a nearby player sat on the server before
    ///                   each other player was sent it (cadence of the ZDO scheduler);
    ///   hit forward   — how long a hit RPC (damage, stagger) routed to a player waited in that
    ///                   player's send queue, behind ZDO data on the same ordered lane. Block and
    ///                   parry are decided by the target's client when the RPC lands;
    ///   service gap   — longest wait between two ZDO send attempts for a player;
    ///   queue refusal — attempts the ZDO queue guard turned away because the connection was full;
    ///   world save    — main-thread time of ZNet.SaveWorld, which freezes every player at once.
    ///
    /// The client measures its own upload wait for the hit RPCs it sends and reports it to the
    /// server (ClientTelemetryReport.HitUpload).
    ///
    /// Observation only: nothing here changes what or when Valheim sends. A failure disables the
    /// probes and leaves networking as it was.
    /// </summary>
    internal static class PvpTelemetry
    {
        private sealed class PeerWindow
        {
            public readonly LatencyAccumulator RelayHold = new LatencyAccumulator();
            public readonly LatencyAccumulator HitForward = new LatencyAccumulator();
            public int ServiceCalls;
            public int QueueRefusals;
            public double ServiceGapMaximumMilliseconds;
            public double LastServiceTime = double.NaN;
        }

        // Valheim's ZDO send guard refuses a send when less than this much room is left.
        private const int MinimumZdoPackageBytes = 2048;

        private static readonly int[] HitRpcHashes =
        {
            "RPC_Damage".GetStableHashCode(),
            "RPC_Stagger".GetStableHashCode(),
            "RPC_HitWhileDodging".GetStableHashCode()
        };

        private static readonly Dictionary<ZRpc, ZNetPeer> PeersByRpc = new Dictionary<ZRpc, ZNetPeer>();
        private static readonly List<ZNetPeer> Players = new List<ZNetPeer>();
        private static readonly Dictionary<long, PeerWindow> Windows = new Dictionary<long, PeerWindow>();
        private static readonly List<long> StaleWindows = new List<long>();
        private static readonly HashSet<ZDOID> LiveSources = new HashSet<ZDOID>();
        private static readonly HashSet<long> LiveReceivers = new HashSet<long>();
        private static readonly RelayHoldTracker<ZDOID, long> Relay = new RelayHoldTracker<ZDOID, long>();
        private static readonly LatencyAccumulator RelayHoldAll = new LatencyAccumulator();
        private static readonly LatencyAccumulator HitForwardAll = new LatencyAccumulator();
        private static readonly LatencyAccumulator ClientHitUpload = new LatencyAccumulator();

        private static ManualLogSource _log;
        private static bool _faulted;
        private static bool _isServer;
        private static float _radiusSquared = 160f * 160f;
        private static long _worldSaveStartedAt;
        private static double _worldSaveMaximumMilliseconds;
        private static int _worldSaves;

        public static void Initialize(ManualLogSource log)
        {
            ClearState();
            _log = log;
            _faulted = false;
            if (!GameAccess.PeerZdoRevisionAvailable)
            {
                _log?.LogWarning(
                    "PvP relay-hold probe unavailable: ZDOMan.ZDOPeer.m_zdos changed shape in this Valheim build. " +
                    "Hit, queue and world-save probes keep running.");
            }
        }

        public static void Reset()
        {
            ClearState();
            _log = null;
            _faulted = false;
        }

        /// <summary>
        /// Called by NetworkRuntime at each sample, after <see cref="TakeWindow"/>: who is connected,
        /// which ZRpc belongs to whom, and the radius inside which two players count as fighting.
        /// </summary>
        public static void Refresh(List<ZNetPeer> connectedPeers, bool isServer, int pvpRadiusMeters)
        {
            _isServer = isServer;
            _radiusSquared = (float)pvpRadiusMeters * pvpRadiusMeters;
            PeersByRpc.Clear();
            Players.Clear();
            LiveSources.Clear();
            LiveReceivers.Clear();
            if (!isServer || connectedPeers == null)
            {
                Windows.Clear();
                Relay.Clear();
                return;
            }

            for (int index = 0; index < connectedPeers.Count; index++)
            {
                ZNetPeer peer = connectedPeers[index];
                if (peer == null) continue;
                Players.Add(peer);
                if (peer.m_rpc != null) PeersByRpc[peer.m_rpc] = peer;
                LiveReceivers.Add(peer.m_uid);
                if (!peer.m_characterID.IsNone()) LiveSources.Add(peer.m_characterID);
                if (!Windows.ContainsKey(peer.m_uid)) Windows.Add(peer.m_uid, new PeerWindow());
            }

            Relay.Retain(LiveSources, LiveReceivers);
            foreach (long uid in Windows.Keys)
            {
                if (!LiveReceivers.Contains(uid)) StaleWindows.Add(uid);
            }
            for (int index = 0; index < StaleWindows.Count; index++) Windows.Remove(StaleWindows[index]);
            StaleWindows.Clear();
        }

        /// <summary>Closes the current window and starts the next.</summary>
        public static PvpWindow TakeWindow()
        {
            if (!_isServer) return PvpWindow.Empty;
            double now = Now();
            var peers = new Dictionary<long, PvpPeerStats>(Windows.Count);
            double serviceGapMaximum = 0d;
            int queueRefusals = 0;
            foreach (KeyValuePair<long, PeerWindow> pair in Windows)
            {
                PeerWindow window = pair.Value;
                // A player who is not being served at all is the worst case, not a missing sample.
                double openGap = double.IsNaN(window.LastServiceTime) ? 0d : (now - window.LastServiceTime) * 1000d;
                double gap = Math.Max(window.ServiceGapMaximumMilliseconds, openGap);
                peers[pair.Key] = new PvpPeerStats(
                    window.RelayHold.TakeSummary(),
                    window.HitForward.TakeSummary(),
                    window.ServiceCalls,
                    gap,
                    window.QueueRefusals);
                serviceGapMaximum = Math.Max(serviceGapMaximum, gap);
                queueRefusals += window.QueueRefusals;
                window.ServiceCalls = 0;
                window.QueueRefusals = 0;
                window.ServiceGapMaximumMilliseconds = 0d;
            }

            var result = new PvpWindow(
                peers,
                RelayHoldAll.TakeSummary(),
                HitForwardAll.TakeSummary(),
                serviceGapMaximum,
                queueRefusals,
                _worldSaveMaximumMilliseconds,
                _worldSaves);
            _worldSaveMaximumMilliseconds = 0d;
            _worldSaves = 0;
            return result;
        }

        /// <summary>Client side: upload wait of the hit RPCs sent since the last report.</summary>
        public static LatencySummary TakeClientHitUpload() => ClientHitUpload.TakeSummary();

        /// <summary>ZDOMan.RPC_ZDOData postfix: a player uploaded ZDO data, possibly a new revision of their character.</summary>
        public static void OnZdoDataReceived(ZRpc rpc)
        {
            if (!_isServer || _faulted || rpc == null) return;
            try
            {
                if (!PeersByRpc.TryGetValue(rpc, out ZNetPeer peer)) return;
                ZDOID character = peer.m_characterID;
                if (character.IsNone() || ZDOMan.instance == null) return;
                ZDO zdo = ZDOMan.instance.GetZDO(character);
                if (zdo == null) return;
                Relay.ObserveArrival(character, zdo.DataRevision, Now());
            }
            catch (Exception exception)
            {
                Fault("ZDO arrival probe", exception);
            }
        }

        /// <summary>ZDOMan.SendZDOs postfix: one ZDO send attempt for one player has finished.</summary>
        public static void OnSendZdos(object zdoPeer, bool flush, bool sent)
        {
            if (!_isServer || _faulted || zdoPeer == null) return;
            try
            {
                ZNetPeer receiver = GameAccess.GetZNetPeer(zdoPeer);
                if (receiver == null || !Windows.TryGetValue(receiver.m_uid, out PeerWindow window)) return;

                double now = Now();
                window.ServiceCalls++;
                if (!double.IsNaN(window.LastServiceTime))
                {
                    window.ServiceGapMaximumMilliseconds = Math.Max(
                        window.ServiceGapMaximumMilliseconds,
                        (now - window.LastServiceTime) * 1000d);
                }
                window.LastServiceTime = now;

                if (!sent)
                {
                    // Nothing went out: either nothing changed, or the queue guard refused. Only a
                    // refusal leaves the queue this full, since a refused attempt adds nothing to it.
                    if (!flush && receiver.m_socket != null &&
                        NetworkRuntime.GetZdoGuardBudget(zdoPeer) - receiver.m_socket.GetSendQueueSize() < MinimumZdoPackageBytes)
                    {
                        window.QueueRefusals++;
                    }
                    return;
                }

                if (!GameAccess.PeerZdoRevisionAvailable || ZDOMan.instance == null) return;
                Vector3 origin = receiver.GetRefPos();
                for (int index = 0; index < Players.Count; index++)
                {
                    ZNetPeer source = Players[index];
                    if (ReferenceEquals(source, receiver)) continue;
                    ZDOID character = source.m_characterID;
                    if (character.IsNone()) continue;
                    ZDO zdo = ZDOMan.instance.GetZDO(character);
                    if (zdo == null) continue;

                    bool inRange = (zdo.GetPosition() - origin).sqrMagnitude <= _radiusSquared;
                    RelayHoldTracker<ZDOID, long>.Link link = Relay.GetLink(receiver.m_uid, character);
                    uint revision = 0;
                    bool revisionKnown = Relay.IsBehind(link, character) &&
                                         GameAccess.TryGetPeerZdoRevision(zdoPeer, character, out revision);
                    if (Relay.ObserveService(link, character, inRange, revisionKnown, revision, now, out double hold))
                    {
                        window.RelayHold.Add(hold);
                        RelayHoldAll.Add(hold);
                    }
                }
            }
            catch (Exception exception)
            {
                Fault("ZDO send probe", exception);
            }
        }

        /// <summary>
        /// ZRoutedRpc.RouteRPC prefix, before the RPC is queued. On the server this is the hop to
        /// the hit's target; on a client, the upload to the server.
        /// </summary>
        public static void OnRouteRpc(ZRoutedRpc.RoutedRPCData data)
        {
            if (_faulted || data == null || ZNet.instance == null || !IsHitRpc(data.m_methodHash)) return;
            try
            {
                // Hits on players only: that is where block and parry timing is at stake. A hit on
                // a mob some other client owns travels the same way but is not a PvP question.
                if (_isServer)
                {
                    if (data.m_targetPeerID == 0L || !LiveSources.Contains(data.m_targetZDO)) return;
                    ZNetPeer target = ZNet.instance.GetPeer(data.m_targetPeerID);
                    if (target == null || !TryGetQueueWait(target.m_socket, out double wait)) return;
                    if (Windows.TryGetValue(target.m_uid, out PeerWindow window)) window.HitForward.Add(wait);
                    HitForwardAll.Add(wait);
                }
                else
                {
                    if (!IsPlayerCharacter(data.m_targetZDO)) return;
                    ZNetPeer server = ZNet.instance.GetServerPeer();
                    if (server != null && TryGetQueueWait(server.m_socket, out double wait)) ClientHitUpload.Add(wait);
                }
            }
            catch (Exception exception)
            {
                Fault("hit RPC probe", exception);
            }
        }

        public static void OnWorldSaveStarting()
        {
            _worldSaveStartedAt = Stopwatch.GetTimestamp();
        }

        public static void OnWorldSaveFinished(bool synchronous)
        {
            if (_worldSaveStartedAt == 0L) return;
            double milliseconds = (Stopwatch.GetTimestamp() - _worldSaveStartedAt) * 1000d / Stopwatch.Frequency;
            _worldSaveStartedAt = 0L;
            _worldSaveMaximumMilliseconds = Math.Max(_worldSaveMaximumMilliseconds, milliseconds);
            _worldSaves++;
            _log?.LogInfo(
                $"World save held the main thread for {milliseconds:F0} ms" +
                (synchronous ? " (synchronous save, includes the file write)." : "; the file write continues on its own thread."));
        }

        private static bool IsHitRpc(int methodHash)
        {
            for (int index = 0; index < HitRpcHashes.Length; index++)
            {
                if (HitRpcHashes[index] == methodHash) return true;
            }
            return false;
        }

        /// <summary>Client side: whether the ZDO is the character of a player this client can see.</summary>
        private static bool IsPlayerCharacter(ZDOID id)
        {
            if (id.IsNone()) return false;
            List<Player> players = Player.GetAllPlayers();
            for (int index = 0; index < players.Count; index++)
            {
                Player player = players[index];
                if (player != null && player.GetZDOID() == id) return true;
            }
            return false;
        }

        private static bool TryGetQueueWait(ISocket socket, out double waitMilliseconds)
        {
            waitMilliseconds = 0d;
            return SocketUnwrapper.Unwrap(socket) is ZSteamSocket steamSocket &&
                   SteamTransport.TryGetQueueWait(steamSocket, out waitMilliseconds);
        }

        private static double Now() => Time.realtimeSinceStartupAsDouble;

        private static void Fault(string stage, Exception exception)
        {
            _faulted = true;
            ClearState();
            _log?.LogWarning(
                $"PvP probes disabled safely after the {stage} failed: {exception.GetType().Name}: {exception.Message}. " +
                "Networking is unaffected.");
        }

        private static void ClearState()
        {
            PeersByRpc.Clear();
            Players.Clear();
            Windows.Clear();
            LiveSources.Clear();
            LiveReceivers.Clear();
            Relay.Clear();
            RelayHoldAll.Reset();
            HitForwardAll.Reset();
            ClientHitUpload.Reset();
            _isServer = false;
            _worldSaveStartedAt = 0L;
            _worldSaveMaximumMilliseconds = 0d;
            _worldSaves = 0;
        }
    }
}
