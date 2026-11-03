using System;
using System.Collections.Generic;

namespace AdaptiveNet
{
    /// <summary>Identidade de um ZDO (usuario criador + id), sem depender do tipo ZDOID do jogo.</summary>
    internal readonly struct ZdoKey : IEquatable<ZdoKey>
    {
        public ZdoKey(long user, uint id)
        {
            User = user;
            Id = id;
        }

        public long User { get; }
        public uint Id { get; }

        public bool Equals(ZdoKey other) => User == other.User && Id == other.Id;
        public override bool Equals(object obj) => obj is ZdoKey other && Equals(other);
        public override int GetHashCode() => unchecked((int)(User * 397L) ^ (int)Id);
    }

    /// <summary>
    /// O que um cliente diz sobre as IAs que ele instanciou (BaseAI.Instances): quantas existem,
    /// quantas ele mesmo simula (dono) e quantas pertencem a outro. Measured=false quando o
    /// relatorio e do protocolo 2 ou a leitura falhou.
    /// </summary>
    internal readonly struct ClientAiSample
    {
        public ClientAiSample(int instances, int owned, int other)
        {
            Measured = true;
            Instances = Math.Max(0, instances);
            Owned = Math.Max(0, Math.Min(Instances, owned));
            Other = Math.Max(0, other);
        }

        public bool Measured { get; }
        public int Instances { get; }
        public int Owned { get; }
        public int Other { get; }
    }

    /// <summary>Campos baratos da maquina do cliente. -1 = nao medido (por exemplo, sem GetSystemTimes).</summary>
    internal readonly struct ClientMachineSample
    {
        public ClientMachineSample(double processCpuMsPerSecond, double systemCpuPercent, int processMemoryMiB)
        {
            Measured = true;
            ProcessCpuMsPerSecond = processCpuMsPerSecond < 0d || double.IsNaN(processCpuMsPerSecond) ? -1d : processCpuMsPerSecond;
            SystemCpuPercent = systemCpuPercent < 0d || double.IsNaN(systemCpuPercent) ? -1d : Math.Min(100d, systemCpuPercent);
            ProcessMemoryMiB = Math.Max(0, processMemoryMiB);
        }

        public bool Measured { get; }
        public double ProcessCpuMsPerSecond { get; }
        public double SystemCpuPercent { get; }
        public int ProcessMemoryMiB { get; }
    }

    /// <summary>Contas de CPU sem depender de Process/P-Invoke (testaveis).</summary>
    internal static class CpuUsageMath
    {
        public static double ProcessMsPerSecond(double cpuMillisecondsDelta, double elapsedSeconds)
        {
            if (elapsedSeconds <= 0d || cpuMillisecondsDelta < 0d) return -1d;
            return cpuMillisecondsDelta / elapsedSeconds;
        }

        /// <summary>
        /// GetSystemTimes: o tempo de kernel inclui o ocioso. Uso = (kernel + user - idle) / (kernel + user).
        /// </summary>
        public static double SystemPercent(ulong idleDelta, ulong kernelDelta, ulong userDelta)
        {
            ulong total = kernelDelta + userDelta;
            if (total == 0UL || idleDelta > total) return -1d;
            return 100d * (total - idleDelta) / total;
        }
    }

    /// <summary>O que o censo por setor viu ao redor de UM jogador na sua ultima varredura.</summary>
    internal readonly struct PeerCensusSample
    {
        public PeerCensusSample(
            int creatures,
            int ownedByPeer,
            int ownedByServer,
            int unowned,
            int zdosVisited,
            double scanMilliseconds,
            double ageSeconds,
            bool truncated)
        {
            Measured = true;
            Creatures = Math.Max(0, creatures);
            OwnedByPeer = Math.Max(0, ownedByPeer);
            OwnedByServer = Math.Max(0, ownedByServer);
            Unowned = Math.Max(0, unowned);
            ZdosVisited = Math.Max(0, zdosVisited);
            ScanMilliseconds = Math.Max(0d, scanMilliseconds);
            AgeSeconds = Math.Max(0d, ageSeconds);
            Truncated = truncated;
        }

        public bool Measured { get; }
        public int Creatures { get; }
        public int OwnedByPeer { get; }
        public int OwnedByServer { get; }
        public int Unowned { get; }
        public int OwnedByOthers => Math.Max(0, Creatures - OwnedByPeer - OwnedByServer - Unowned);
        public int ZdosVisited { get; }
        public double ScanMilliseconds { get; }
        public double AgeSeconds { get; }
        public bool Truncated { get; }

        public PeerCensusSample WithAge(double ageSeconds) =>
            new PeerCensusSample(Creatures, OwnedByPeer, OwnedByServer, Unowned, ZdosVisited, ScanMilliseconds, ageSeconds, Truncated);
    }

    /// <summary>
    /// Agrega, por dono, os ZDOs de criaturas vistos durante um ciclo de varreduras (um jogador por
    /// varredura). Cada ZDO conta uma vez por ciclo, mesmo que os setores de dois jogadores se
    /// sobreponham. Ao fechar o ciclo calcula mudanca de contagem e transferencias contra o anterior.
    /// </summary>
    internal sealed class OwnershipTally
    {
        private Dictionary<ZdoKey, long> _building = new Dictionary<ZdoKey, long>();
        private Dictionary<ZdoKey, long> _previous = new Dictionary<ZdoKey, long>();
        private readonly Dictionary<long, int> _current = new Dictionary<long, int>();
        private readonly Dictionary<long, int> _changes = new Dictionary<long, int>();
        private readonly Dictionary<long, int> _transfers = new Dictionary<long, int>();
        private readonly Dictionary<long, int> _previousCounts = new Dictionary<long, int>();
        private bool _hasPrevious;

        public long CyclesCompleted { get; private set; }
        public int Active { get; private set; }
        public int Unowned { get; private set; }
        public int ServerOwned { get; private set; }

        public void BeginCycle() => _building.Clear();

        /// <summary>Devolve true se o ZDO ainda nao tinha sido contado neste ciclo.</summary>
        public bool Add(ZdoKey key, long owner)
        {
            if (_building.ContainsKey(key)) return false;
            _building[key] = owner;
            return true;
        }

        public void EndCycle(long serverUid)
        {
            _previousCounts.Clear();
            foreach (KeyValuePair<long, int> pair in _current) _previousCounts[pair.Key] = pair.Value;
            _current.Clear();
            _changes.Clear();
            _transfers.Clear();
            Active = _building.Count;
            Unowned = 0;
            ServerOwned = 0;
            foreach (KeyValuePair<ZdoKey, long> pair in _building)
            {
                long owner = pair.Value;
                if (owner == 0L)
                {
                    Unowned++;
                    continue;
                }
                if (owner == serverUid) ServerOwned++;
                _current.TryGetValue(owner, out int count);
                _current[owner] = count + 1;
            }

            if (_hasPrevious)
            {
                foreach (KeyValuePair<long, int> pair in _current)
                {
                    _previousCounts.TryGetValue(pair.Key, out int old);
                    _changes[pair.Key] = Math.Abs(pair.Value - old);
                }
                foreach (KeyValuePair<long, int> pair in _previousCounts)
                {
                    if (!_current.ContainsKey(pair.Key)) _changes[pair.Key] = pair.Value;
                }
                // Transferencia = o mesmo ZDO, visto nos dois ciclos, mudou de dono.
                foreach (KeyValuePair<ZdoKey, long> pair in _building)
                {
                    if (!_previous.TryGetValue(pair.Key, out long before) || before == pair.Value) continue;
                    if (before != 0L) Increment(_transfers, before);
                    if (pair.Value != 0L) Increment(_transfers, pair.Value);
                }
            }

            Dictionary<ZdoKey, long> swap = _previous;
            _previous = _building;
            _building = swap;
            _building.Clear();
            _hasPrevious = true;
            CyclesCompleted++;
        }

        public CharacterOwnershipSnapshot Get(long owner)
        {
            _current.TryGetValue(owner, out int count);
            _changes.TryGetValue(owner, out int change);
            _transfers.TryGetValue(owner, out int transfers);
            return new CharacterOwnershipSnapshot(count, change, transfers, CyclesCompleted);
        }

        /// <summary>Donos do ultimo ciclo fechado, do que simula mais para o que simula menos.</summary>
        public List<KeyValuePair<long, int>> TopOwners(int limit)
        {
            var list = new List<KeyValuePair<long, int>>(_current);
            list.Sort((left, right) => right.Value.CompareTo(left.Value));
            if (list.Count > limit) list.RemoveRange(limit, list.Count - limit);
            return list;
        }

        public void Reset()
        {
            _building.Clear();
            _previous.Clear();
            _current.Clear();
            _changes.Clear();
            _transfers.Clear();
            _previousCounts.Clear();
            _hasPrevious = false;
            CyclesCompleted = 0L;
            Active = 0;
            Unowned = 0;
            ServerOwned = 0;
        }

        private static void Increment(Dictionary<long, int> values, long key)
        {
            values.TryGetValue(key, out int count);
            values[key] = count + 1;
        }
    }

    /// <summary>Escolhe quem varrer a seguir e quando, dentro do orcamento de CPU.</summary>
    internal sealed class CensusPlanner
    {
        private readonly List<long> _queue = new List<long>();
        private int _next;
        private double _cycleStartedAt = double.NegativeInfinity;
        private double _nextScanAt;

        public bool CycleOpen => _next < _queue.Count;

        public bool ShouldStartCycle(double now, double minimumCycleSeconds) =>
            !CycleOpen && now - _cycleStartedAt >= minimumCycleSeconds;

        public void StartCycle(double now, IEnumerable<long> peers)
        {
            _queue.Clear();
            _queue.AddRange(peers);
            _next = 0;
            _cycleStartedAt = now;
        }

        public bool ScanDue(double now) => CycleOpen && now >= _nextScanAt;

        public bool TryNext(out long peer)
        {
            peer = 0L;
            if (!CycleOpen) return false;
            peer = _queue[_next++];
            return true;
        }

        /// <summary>
        /// Depois de uma varredura que custou costMs, espera o maior entre o intervalo configurado e o
        /// tempo que mantem o custo medio dentro de budgetMsPerSecond.
        /// </summary>
        public void ScanFinished(double now, double costMs, double intervalSeconds, double budgetMsPerSecond)
        {
            double costDelay = budgetMsPerSecond > 0d ? costMs / budgetMsPerSecond : 0d;
            _nextScanAt = now + Math.Max(intervalSeconds, costDelay);
        }

        public void Reset()
        {
            _queue.Clear();
            _next = 0;
            _cycleStartedAt = double.NegativeInfinity;
            _nextScanAt = 0d;
        }
    }

    /// <summary>
    /// Cruza o que o cliente diz simular com o que o servidor ve como dono perto dele. Os dois lados
    /// olham areas diferentes, entao so avisa depois de varias discordancias seguidas e com limite
    /// de frequencia.
    /// </summary>
    internal sealed class OwnershipDisagreementTracker
    {
        private sealed class State
        {
            public int Streak;
            public double LastWarning = double.NegativeInfinity;
        }

        private readonly Dictionary<long, State> _peers = new Dictionary<long, State>();

        public int ConsecutiveRequired { get; set; } = 3;
        public double WarningCooldownSeconds { get; set; } = 120d;
        public double Ratio { get; set; } = 0.5d;

        public static bool Disagree(int clientOwned, int serverOwnedByPeer, int minimumDifference, double ratio)
        {
            int difference = Math.Abs(clientOwned - serverOwnedByPeer);
            int larger = Math.Max(clientOwned, serverOwnedByPeer);
            return difference >= Math.Max(1, minimumDifference) && difference >= ratio * larger;
        }

        /// <summary>true = emitir o aviso agora.</summary>
        public bool Observe(long peer, bool comparable, int clientOwned, int serverOwnedByPeer, int minimumDifference, double now)
        {
            if (!_peers.TryGetValue(peer, out State state))
            {
                state = new State();
                _peers[peer] = state;
            }
            if (!comparable || !Disagree(clientOwned, serverOwnedByPeer, minimumDifference, Ratio))
            {
                state.Streak = 0;
                return false;
            }
            state.Streak++;
            if (state.Streak < ConsecutiveRequired || now - state.LastWarning < WarningCooldownSeconds) return false;
            state.LastWarning = now;
            return true;
        }

        public void Forget(long peer) => _peers.Remove(peer);
        public void Reset() => _peers.Clear();
    }
}
