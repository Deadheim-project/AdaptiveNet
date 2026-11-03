using System;
using System.Collections.Generic;

namespace AdaptiveNet
{
    /// <summary>
    /// O que o eco ao dono diz de UM peer: mediana das ultimas voltas (servidor -> dono -> servidor),
    /// contagem de timeouts e quantos mobs perto de jogadores ele simula. Measured=false quando nunca
    /// houve resposta (por exemplo, peer sem o mod) ou a ultima resposta e velha: nunca vira culpado.
    /// </summary>
    internal readonly struct OwnerEchoSample
    {
        private readonly int _samples;
        private readonly int _timeouts;
        private readonly int _mobsPlusOne;

        public OwnerEchoSample(
            bool tracked,
            bool measured,
            double medianMilliseconds,
            double lastMilliseconds,
            int samples,
            int timeouts,
            int mobsSimulated,
            string nearPlayers)
        {
            Tracked = tracked;
            Measured = tracked && measured;
            MedianMilliseconds = Measured ? Math.Max(0d, medianMilliseconds) : -1d;
            LastMilliseconds = Measured ? Math.Max(0d, lastMilliseconds) : -1d;
            _samples = Math.Max(0, samples);
            _timeouts = Math.Max(0, timeouts);
            _mobsPlusOne = mobsSimulated < 0 ? 0 : mobsSimulated + 1;
            NearPlayers = nearPlayers ?? string.Empty;
        }

        /// <summary>O servidor ja enviou eco a este peer.</summary>
        public bool Tracked { get; }
        public bool Measured { get; }
        public double MedianMilliseconds { get; }
        public double LastMilliseconds { get; }
        /// <summary>Respostas (e timeouts contados como volta cheia) na janela da mediana.</summary>
        public int Samples => Tracked ? _samples : -1;
        public int Timeouts => Tracked ? _timeouts : -1;
        /// <summary>Mobs que o peer simula perto de jogadores (censo); -1 = nao medido.</summary>
        public int MobsSimulated => _mobsPlusOne - 1;
        /// <summary>Nomes dos jogadores perto dos mobs que ele simula, separados por ", ".</summary>
        public string NearPlayers { get; }

        public OwnerEchoSample WithMobs(int mobsSimulated, string nearPlayers) =>
            new OwnerEchoSample(Tracked, Measured, MedianMilliseconds, LastMilliseconds, Samples, Timeouts, mobsSimulated, nearPlayers);
    }

    /// <summary>
    /// Guarda, por peer, os ecos pendentes e as ultimas voltas. Funcao pura do tempo recebido: nao
    /// depende do jogo, entao os testes rodam sem Valheim.
    /// </summary>
    internal sealed class OwnerEchoTracker
    {
        // Peer que nunca respondeu (sem o mod): depois de tantos timeouts seguidos, so tenta de novo
        // de vez em quando, para nao encher o log dele de RPC desconhecido.
        public const int SilentTimeoutsBeforeBackoff = 3;
        public const double SilentBackoffSeconds = 60d;

        private sealed class PeerState
        {
            public readonly List<double> Rounds = new List<double>();
            public long PendingSequence;
            public double PendingSentAt = -1d;
            public double LastSentAt = double.NegativeInfinity;
            public double LastReplyAt = double.NegativeInfinity;
            public double LastRound = -1d;
            public int Timeouts;
            public int SilentTimeouts;
            public bool EverReplied;
        }

        private readonly Dictionary<long, PeerState> _peers = new Dictionary<long, PeerState>();
        private long _nextSequence = 1L;

        public OwnerEchoTracker(int window = 8, double timeoutSeconds = 2d, double staleSeconds = 20d)
        {
            Window = Math.Max(1, window);
            TimeoutSeconds = Math.Max(0.1d, timeoutSeconds);
            StaleSeconds = Math.Max(1d, staleSeconds);
        }

        public int Window { get; }
        public double TimeoutSeconds { get; }
        public double StaleSeconds { get; }
        public int TrackedPeers => _peers.Count;

        /// <summary>Pode enviar eco a este peer agora? No maximo um por intervalo e nunca com outro pendente.</summary>
        public bool IsDue(long uid, double now, double intervalSeconds)
        {
            if (!_peers.TryGetValue(uid, out PeerState state)) return true;
            if (state.PendingSentAt >= 0d) return false;
            double interval = Math.Max(0.5d, intervalSeconds);
            if (!state.EverReplied && state.SilentTimeouts >= SilentTimeoutsBeforeBackoff) interval = Math.Max(interval, SilentBackoffSeconds);
            return now - state.LastSentAt >= interval;
        }

        /// <summary>Registra o envio; devolve o numero de sequencia que vai no pacote.</summary>
        public long BeginSend(long uid, double now)
        {
            if (!_peers.TryGetValue(uid, out PeerState state))
            {
                state = new PeerState();
                _peers[uid] = state;
            }
            state.PendingSequence = _nextSequence++;
            state.PendingSentAt = now;
            state.LastSentAt = now;
            return state.PendingSequence;
        }

        /// <summary>Resposta recebida. Falso se nao era a pendente (atrasada demais, duplicada, de quem nao foi sondado).</summary>
        public bool Reply(long uid, long sequence, double now, out double roundMilliseconds)
        {
            roundMilliseconds = 0d;
            if (!_peers.TryGetValue(uid, out PeerState state) || state.PendingSentAt < 0d || state.PendingSequence != sequence) return false;
            roundMilliseconds = Math.Max(0d, (now - state.PendingSentAt) * 1000d);
            state.PendingSentAt = -1d;
            state.EverReplied = true;
            state.SilentTimeouts = 0;
            state.LastReplyAt = now;
            state.LastRound = roundMilliseconds;
            AddRound(state, roundMilliseconds);
            return true;
        }

        /// <summary>Fecha os pendentes que passaram do timeout. Devolve quantos.</summary>
        public int Expire(double now)
        {
            int expired = 0;
            foreach (PeerState state in _peers.Values)
            {
                if (state.PendingSentAt < 0d || now - state.PendingSentAt < TimeoutSeconds) continue;
                state.PendingSentAt = -1d;
                state.Timeouts++;
                state.SilentTimeouts++;
                expired++;
                // Quem ja respondeu um dia e agora some deixa de ser "sem o mod": o timeout vale uma
                // volta do tamanho do timeout. Quem nunca respondeu continua nao medido.
                if (state.EverReplied)
                {
                    state.LastRound = TimeoutSeconds * 1000d;
                    state.LastReplyAt = now;
                    AddRound(state, TimeoutSeconds * 1000d);
                }
            }
            return expired;
        }

        public OwnerEchoSample Get(long uid, double now)
        {
            if (!_peers.TryGetValue(uid, out PeerState state)) return default;
            bool fresh = state.EverReplied && now - state.LastReplyAt <= StaleSeconds && state.Rounds.Count > 0;
            return new OwnerEchoSample(true, fresh, Median(state.Rounds), state.LastRound, state.Rounds.Count, state.Timeouts, -1, null);
        }

        public void Forget(long uid) => _peers.Remove(uid);

        public void Reset()
        {
            _peers.Clear();
            _nextSequence = 1L;
        }

        public static double Median(IReadOnlyList<double> values)
        {
            if (values == null || values.Count == 0) return 0d;
            var sorted = new List<double>(values);
            sorted.Sort();
            int middle = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2d;
        }

        private void AddRound(PeerState state, double milliseconds)
        {
            state.Rounds.Add(milliseconds);
            if (state.Rounds.Count > Window) state.Rounds.RemoveRange(0, state.Rounds.Count - Window);
        }
    }

    /// <summary>
    /// Escolhe quem sondar: os donos que mais simulam criaturas perto de jogadores (ranking do censo),
    /// so peers conectados, sem o servidor nem "sem dono", respeitando o intervalo de cada um.
    /// </summary>
    internal static class OwnerEchoPlanner
    {
        public static List<long> Select(
            IEnumerable<KeyValuePair<long, int>> ownersByMobs,
            Func<long, bool> isConnectedPeer,
            OwnerEchoTracker tracker,
            double now,
            double intervalSeconds,
            int maxPeers)
        {
            var chosen = new List<long>();
            if (ownersByMobs == null || tracker == null || maxPeers <= 0) return chosen;
            var ranked = new List<KeyValuePair<long, int>>(ownersByMobs);
            ranked.Sort((left, right) =>
            {
                int byCount = right.Value.CompareTo(left.Value);
                return byCount != 0 ? byCount : left.Key.CompareTo(right.Key);
            });
            foreach (KeyValuePair<long, int> owner in ranked)
            {
                if (chosen.Count >= maxPeers) break;
                if (owner.Key == 0L || owner.Value <= 0) continue;
                if (isConnectedPeer != null && !isConnectedPeer(owner.Key)) continue;
                if (!tracker.IsDue(owner.Key, now, intervalSeconds)) continue;
                chosen.Add(owner.Key);
            }
            return chosen;
        }
    }

    /// <summary>
    /// Quantas criaturas cada dono simula perto de cada jogador, da ultima varredura que olhou esse
    /// jogador. So guarda contagens (sem ZDO); entradas velhas somem na leitura.
    /// </summary>
    internal sealed class OwnerNearMap
    {
        private readonly Dictionary<long, Dictionary<long, KeyValuePair<int, double>>> _byOwner =
            new Dictionary<long, Dictionary<long, KeyValuePair<int, double>>>();

        public void Record(long owner, long nearPlayer, int mobs, double now)
        {
            if (owner == 0L) return;
            if (!_byOwner.TryGetValue(owner, out Dictionary<long, KeyValuePair<int, double>> players))
            {
                players = new Dictionary<long, KeyValuePair<int, double>>();
                _byOwner[owner] = players;
            }
            players[nearPlayer] = new KeyValuePair<int, double>(mobs, now);
        }

        /// <summary>Jogadores perto dos mobs do dono, do que tem mais mobs para o que tem menos, e o total de mobs.</summary>
        public List<KeyValuePair<long, int>> Get(long owner, double now, double maxAgeSeconds, out int total)
        {
            total = 0;
            var result = new List<KeyValuePair<long, int>>();
            if (!_byOwner.TryGetValue(owner, out Dictionary<long, KeyValuePair<int, double>> players)) return result;
            foreach (KeyValuePair<long, KeyValuePair<int, double>> entry in players)
            {
                if (now - entry.Value.Value > maxAgeSeconds || entry.Value.Key <= 0) continue;
                result.Add(new KeyValuePair<long, int>(entry.Key, entry.Value.Key));
                total += entry.Value.Key;
            }
            result.Sort((left, right) => right.Value.CompareTo(left.Value));
            return result;
        }

        public void Forget(long uid)
        {
            _byOwner.Remove(uid);
            foreach (Dictionary<long, KeyValuePair<int, double>> players in _byOwner.Values) players.Remove(uid);
        }

        public void Reset() => _byOwner.Clear();
    }
}
