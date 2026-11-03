using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using HarmonyLib;

namespace AdaptiveNet
{
    /// <summary>
    /// Servidor: conta quem e dono de cada criatura SEM instanciar nada e SEM varrer o mundo todo.
    /// Por varredura, olha so os setores ao redor de UM jogador (m_refPos) no indice de setores do
    /// ZDOMan (m_objectsBySector) e, para ZDOs cujo prefab e uma criatura, conta zdo.GetOwner().
    /// Um jogador por varredura, espacadas por intervalo e por orcamento de CPU. A tecnica (setores
    /// ao redor do jogador + prefab de criatura em cache) e nossa; nada do DiagnoseServerLag foi copiado.
    /// </summary>
    internal static class ServerOwnershipCensus
    {
        private const int ConsecutiveSlowScansToDisable = 3;

        private static readonly Dictionary<int, bool> CreaturePrefabs = new Dictionary<int, bool>();
        private static readonly Dictionary<long, PeerCensusSample> Samples = new Dictionary<long, PeerCensusSample>();
        private static readonly Dictionary<long, double> SampleTimes = new Dictionary<long, double>();
        private static readonly CensusPlanner Planner = new CensusPlanner();
        private static readonly Stopwatch Clock = new Stopwatch();
        private static readonly Dictionary<long, int> ScanOwners = new Dictionary<long, int>();
        private static FieldInfo _sectorField;
        private static ManualLogSource _log;
        private static bool _disabled;
        private static int _slowScans;
        private static double _nextLogTime;
        private static int _logScans;
        private static double _logCostTotalMs;
        private static double _logCostMaxMs;
        private static long _logZdosVisited;
        private static int _logTruncated;

        public static bool Disabled => _disabled;
        public static bool Measured => CharacterOwnershipTelemetry.Tally.CyclesCompleted > 0L;

        public static void Initialize(ManualLogSource log)
        {
            _log = log;
            Reset();
            // Campo privado: conferido contra o assembly original (List<ZDO>[] indexado por SectorIndex.Sector).
            FieldInfo field = AccessTools.Field(typeof(ZDOMan), "m_objectsBySector");
            if (field == null || field.FieldType != typeof(List<ZDO>[]))
            {
                _disabled = true;
                _log?.LogWarning(
                    "Ownership census disabled safely: ZDOMan.m_objectsBySector not found or changed shape. Networking is unaffected.");
                return;
            }
            _sectorField = field;
        }

        public static void Reset()
        {
            CreaturePrefabs.Clear();
            Samples.Clear();
            SampleTimes.Clear();
            Planner.Reset();
            CharacterOwnershipTelemetry.Reset();
            _disabled = false;
            _slowScans = 0;
            _nextLogTime = 0d;
            ResetLog();
        }

        public static PeerCensusSample Get(long peerUid, double now)
        {
            if (!Samples.TryGetValue(peerUid, out PeerCensusSample sample)) return default;
            return sample.WithAge(SampleTimes.TryGetValue(peerUid, out double at) ? now - at : 0d);
        }

        public static void Forget(long peerUid)
        {
            Samples.Remove(peerUid);
            SampleTimes.Remove(peerUid);
        }

        /// <summary>Chamado a cada amostra do servidor; faz no maximo UMA varredura de um jogador.</summary>
        public static void Tick(double now, Settings settings)
        {
            if (_disabled || _sectorField == null || settings == null || !settings.OwnerCensus.Value) return;
            ZNet net = ZNet.instance;
            ZDOMan manager = ZDOMan.instance;
            if (net == null || manager == null || ZNetScene.instance == null) return;
            try
            {
                if (Planner.ShouldStartCycle(now, Math.Max(1f, settings.OwnershipSampleIntervalSeconds.Value)))
                {
                    var peers = new List<long>();
                    foreach (ZNetPeer peer in net.GetConnectedPeers())
                    {
                        if (peer != null && peer.IsReady()) peers.Add(peer.m_uid);
                    }
                    if (peers.Count == 0)
                    {
                        // Sem jogadores nao ha o que contar; fecha um ciclo vazio para zerar os totais.
                        CharacterOwnershipTelemetry.Tally.BeginCycle();
                        CharacterOwnershipTelemetry.Tally.EndCycle(ZDOMan.GetSessionID());
                        Planner.StartCycle(now, peers);
                        return;
                    }
                    CharacterOwnershipTelemetry.Tally.BeginCycle();
                    Planner.StartCycle(now, peers);
                }

                if (!Planner.ScanDue(now)) return;
                if (!Planner.TryNext(out long uid)) return;
                ZNetPeer target = FindPeer(net, uid);
                if (target != null)
                {
                    double cost = Scan(manager, target, now, settings);
                    Planner.ScanFinished(now, cost, Math.Max(0.25f, settings.OwnerCensusPeerIntervalSeconds.Value),
                        Math.Max(0.05f, settings.OwnerCensusBudgetMsPerSecond.Value));
                    if (cost > settings.OwnerCensusMaxScanMs.Value) _slowScans++;
                    else _slowScans = 0;
                    if (_slowScans >= ConsecutiveSlowScansToDisable)
                    {
                        Disable(ConsecutiveSlowScansToDisable + " consecutive scans above " +
                                settings.OwnerCensusMaxScanMs.Value.ToString("F0") + " ms (last " + cost.ToString("F1") +
                                " ms); lower OwnerCensusZoneRadius");
                        return;
                    }
                }
                if (!Planner.CycleOpen) CharacterOwnershipTelemetry.Tally.EndCycle(ZDOMan.GetSessionID());
            }
            catch (Exception exception)
            {
                Disable(exception.GetType().Name + ": " + exception.Message);
            }
        }

        /// <summary>Linha agregada a cada LogIntervalSeconds.</summary>
        public static void LogIfDue(double now, Settings settings)
        {
            if (_log == null || settings == null || settings.LogIntervalSeconds.Value <= 0f || now < _nextLogTime) return;
            _nextLogTime = now + settings.LogIntervalSeconds.Value;
            if (_disabled || !settings.OwnerCensus.Value) return;
            OwnershipTally tally = CharacterOwnershipTelemetry.Tally;
            var top = new StringBuilder();
            foreach (KeyValuePair<long, int> pair in tally.TopOwners(4))
            {
                if (top.Length > 0) top.Append(' ');
                top.Append(pair.Key).Append(':').Append(pair.Value);
            }
            _log.LogInfo(
                $"ownership census cycles={tally.CyclesCompleted}, creatures={tally.Active}, unowned={tally.Unowned}, " +
                $"serverOwned={tally.ServerOwned}, topOwners=[{top}], scans={_logScans}, " +
                $"cost(avg/max)={(_logScans > 0 ? _logCostTotalMs / _logScans : 0d):F2}/{_logCostMaxMs:F2}ms, " +
                $"zdosVisited(avg)={(_logScans > 0 ? _logZdosVisited / _logScans : 0L)}, truncated={_logTruncated}");
            ResetLog();
        }

        private static void ResetLog()
        {
            _logScans = 0;
            _logCostTotalMs = 0d;
            _logCostMaxMs = 0d;
            _logZdosVisited = 0L;
            _logTruncated = 0;
        }

        private static ZNetPeer FindPeer(ZNet net, long uid)
        {
            foreach (ZNetPeer peer in net.GetConnectedPeers())
            {
                if (peer != null && peer.m_uid == uid) return peer;
            }
            return null;
        }

        private static double Scan(ZDOMan manager, ZNetPeer peer, double now, Settings settings)
        {
            var lists = (List<ZDO>[])_sectorField.GetValue(manager);
            if (lists == null) return 0d;
            int radius = Math.Max(0, settings.OwnerCensusZoneRadius.Value);
            int maxZdos = Math.Max(1000, settings.OwnerCensusMaxZdosPerScan.Value);
            long serverUid = ZDOMan.GetSessionID();
            Vector2s center = ZoneSystem.GetZone(peer.m_refPos);
            OwnershipTally tally = CharacterOwnershipTelemetry.Tally;

            int visited = 0;
            int creatures = 0;
            int byPeer = 0;
            int byServer = 0;
            int unowned = 0;
            bool truncated = false;
            ScanOwners.Clear();
            Clock.Restart();
            for (int dx = -radius; dx <= radius && !truncated; dx++)
            {
                for (int dy = -radius; dy <= radius && !truncated; dy++)
                {
                    uint index = ZoneSystem.SectorToIndex(new Vector2s(center.x + dx, center.y + dy)).Sector;
                    if (index >= (uint)lists.Length) continue;
                    List<ZDO> list = lists[index];
                    if (list == null) continue;
                    for (int item = 0; item < list.Count; item++)
                    {
                        if (++visited > maxZdos)
                        {
                            truncated = true;
                            break;
                        }
                        ZDO zdo = list[item];
                        if (zdo == null || !IsCreature(zdo.GetPrefab())) continue;
                        ZDOID id = zdo.m_uid;
                        long owner = zdo.GetOwner();
                        if (owner != 0L && owner != serverUid)
                        {
                            ScanOwners.TryGetValue(owner, out int already);
                            ScanOwners[owner] = already + 1;
                        }
                        if (!tally.Add(new ZdoKey(id.UserID, id.ID), owner)) continue;
                        creatures++;
                        if (owner == 0L) unowned++;
                        else if (owner == peer.m_uid) byPeer++;
                        else if (owner == serverUid) byServer++;
                    }
                }
            }
            Clock.Stop();
            // Para o eco ao dono: quantas criaturas cada dono simula perto deste jogador.
            foreach (KeyValuePair<long, int> pair in ScanOwners) OwnerEchoRuntime.RecordNear(pair.Key, peer.m_uid, pair.Value, now);
            double costMs = Clock.Elapsed.TotalMilliseconds;
            Samples[peer.m_uid] = new PeerCensusSample(creatures, byPeer, byServer, unowned, visited, costMs, 0d, truncated);
            SampleTimes[peer.m_uid] = now;
            _logScans++;
            _logCostTotalMs += costMs;
            _logCostMaxMs = Math.Max(_logCostMaxMs, costMs);
            _logZdosVisited += visited;
            if (truncated) _logTruncated++;
            return costMs;
        }

        /// <summary>Prefab e criatura (Character que nao e jogador)? Resolvido uma vez por hash.</summary>
        private static bool IsCreature(int prefabHash)
        {
            if (prefabHash == 0) return false;
            if (CreaturePrefabs.TryGetValue(prefabHash, out bool known)) return known;
            ZNetScene scene = ZNetScene.instance;
            if (scene == null) return false;
            UnityEngine.GameObject prefab = scene.GetPrefab(prefabHash);
            bool creature = prefab != null &&
                            prefab.GetComponent<Character>() != null &&
                            prefab.GetComponent<Player>() == null;
            CreaturePrefabs[prefabHash] = creature;
            return creature;
        }

        private static void Disable(string reason)
        {
            _disabled = true;
            _log?.LogWarning("Ownership census disabled safely: " + reason + ". Networking is unaffected.");
        }
    }
}
