using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Logging;

namespace AdaptiveNet
{
    /// <summary>
    /// Formato do eco: um inteiro de versao e um long de sequencia (12 bytes). O servidor manda e o
    /// cliente devolve o mesmo pacote. Nada de relogio do cliente: a volta e medida so no servidor.
    /// </summary>
    internal static class OwnerEchoWire
    {
        public const string RequestRpcName = "AdaptiveNet.OwnerEcho.1";
        public const string ReplyRpcName = "AdaptiveNet.OwnerEchoReply.1";
        public const int Protocol = 1;
        public const int MaxPackageBytes = 32;

        public static ZPackage Write(long sequence)
        {
            var package = new ZPackage();
            package.Write(Protocol);
            package.Write(sequence);
            return package;
        }

        public static bool TryRead(ZPackage package, out long sequence)
        {
            sequence = 0L;
            if (package == null || package.Size() > MaxPackageBytes) return false;
            try
            {
                package.SetPos(0);
                int protocol = package.ReadInt();
                long value = package.ReadLong();
                if (protocol != Protocol || value <= 0L) return false;
                sequence = value;
                return true;
            }
            catch
            {
                // Pacote diagnostico malformado nunca afeta a rede.
                return false;
            }
        }
    }

    /// <summary>
    /// Latencia ate o dono do mob. Servidor: a cada OwnerEchoIntervalSeconds escolhe ate N donos (os
    /// que mais simulam criaturas perto de jogadores, pelo censo) e manda um eco de 12 bytes pelo
    /// ZRoutedRpc; o cliente com o mod devolve no mesmo frame. Nao varre ZDOs, no maximo 1 pacote por
    /// peer por intervalo. A ideia (eco RPC direto ao dono) vem da analise do DiagnoseServerLag; o
    /// codigo e nosso. Peer sem o mod nao responde e fica "nao medido".
    /// </summary>
    internal static class OwnerEchoRuntime
    {
        private const int RankedOwnersConsidered = 12;
        private const double NearMaxAgeSeconds = 30d;

        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly OwnerEchoTracker EmptyTracker = new OwnerEchoTracker();
        private static readonly OwnerNearMap Near = new OwnerNearMap();
        private static readonly Dictionary<long, string> Names = new Dictionary<long, string>();
        private static OwnerEchoTracker _tracker = EmptyTracker;
        private static ZRoutedRpc _registeredInstance;
        private static ManualLogSource _log;
        private static bool _registrationFailed;
        private static bool _sendFailureLogged;
        private static double _nextSendTime;
        private static double _nextLogTime;
        private static int _logSent;
        private static int _logReplies;
        private static int _logTimeouts;
        private static double _logMaxRound;

        /// <summary>Segundos desde o inicio do processo, relogio de alta resolucao (as voltas sao de poucos ms).</summary>
        private static double Seconds => Clock.Elapsed.TotalSeconds;

        public static void Initialize(ManualLogSource log)
        {
            _log = log;
            _registeredInstance = null;
            _registrationFailed = false;
            Reset();
        }

        public static void Reset()
        {
            _tracker = EmptyTracker;
            _tracker.Reset();
            Near.Reset();
            Names.Clear();
            _sendFailureLogged = false;
            _nextSendTime = 0d;
            _nextLogTime = 0d;
            ResetLog();
        }

        public static void Shutdown()
        {
            _registeredInstance = null;
            _log = null;
            _registrationFailed = false;
        }

        public static void EnsureRegistered()
        {
            ZRoutedRpc routed = ZRoutedRpc.instance;
            if (routed == null || ReferenceEquals(routed, _registeredInstance) || _registrationFailed) return;
            try
            {
                routed.Register<ZPackage>(OwnerEchoWire.RequestRpcName, OnRequest);
                routed.Register<ZPackage>(OwnerEchoWire.ReplyRpcName, OnReply);
                _registeredInstance = routed;
            }
            catch (Exception exception)
            {
                _registrationFailed = true;
                _log?.LogWarning($"Owner echo RPC disabled safely: {exception.GetType().Name}: {exception.Message}");
            }
        }

        public static void Forget(long peerUid)
        {
            _tracker.Forget(peerUid);
            Near.Forget(peerUid);
            Names.Remove(peerUid);
        }

        /// <summary>Chamado pelo censo a cada varredura de um jogador: quantas criaturas cada dono simula perto dele.</summary>
        public static void RecordNear(long owner, long nearPlayer, int mobs, double now) => Near.Record(owner, nearPlayer, mobs, now);

        /// <summary>O que se sabe do peer: mediana das voltas e mobs que ele simula perto de jogadores.</summary>
        public static OwnerEchoSample Get(long peerUid, double now)
        {
            OwnerEchoSample sample = _tracker.Get(peerUid, Seconds);
            List<KeyValuePair<long, int>> near = Near.Get(peerUid, now, NearMaxAgeSeconds, out int total);
            if (near.Count == 0 && !sample.Tracked) return default;
            var names = new List<string>(3);
            for (int index = 0; index < near.Count && names.Count < 3; index++)
            {
                names.Add(near[index].Key == peerUid ? "ele mesmo" : Names.TryGetValue(near[index].Key, out string name) ? name : "jogador " + near[index].Key);
            }
            return sample.WithMobs(total, string.Join(", ", names));
        }

        /// <summary>Servidor, a cada amostra: fecha timeouts e envia aos donos que estao na vez.</summary>
        public static void Tick(double now, Settings settings)
        {
            if (settings == null || !settings.OwnerEcho.Value) return;
            ZNet net = ZNet.instance;
            ZRoutedRpc routed = ZRoutedRpc.instance;
            if (net == null || routed == null || !net.IsServer() || _registeredInstance == null) return;
            double clock = Seconds;
            _tracker = EnsureTracker(settings);
            int expired = _tracker.Expire(clock);
            _logTimeouts += expired;
            if (clock < _nextSendTime) return;
            _nextSendTime = clock + 0.5d;

            var connected = new HashSet<long>();
            foreach (ZNetPeer peer in net.GetConnectedPeers())
            {
                if (peer == null || !peer.IsReady()) continue;
                connected.Add(peer.m_uid);
                Names[peer.m_uid] = peer.m_playerName ?? string.Empty;
            }
            if (connected.Count == 0) return;

            List<long> targets = OwnerEchoPlanner.Select(
                CharacterOwnershipTelemetry.Tally.TopOwners(RankedOwnersConsidered),
                connected.Contains,
                _tracker,
                clock,
                Math.Max(1f, settings.OwnerEchoIntervalSeconds.Value),
                Math.Max(1, settings.OwnerEchoMaxPeers.Value));
            foreach (long uid in targets)
            {
                long sequence = _tracker.BeginSend(uid, clock);
                try
                {
                    routed.InvokeRoutedRPC(uid, OwnerEchoWire.RequestRpcName, OwnerEchoWire.Write(sequence));
                    _logSent++;
                }
                catch (Exception exception)
                {
                    if (!_sendFailureLogged)
                    {
                        _sendFailureLogged = true;
                        _log?.LogWarning($"Owner echo send failed: {exception.GetType().Name}: {exception.Message}");
                    }
                }
            }
        }

        /// <summary>Linha agregada a cada LogIntervalSeconds.</summary>
        public static void LogIfDue(double now, Settings settings)
        {
            if (_log == null || settings == null || settings.LogIntervalSeconds.Value <= 0f || now < _nextLogTime) return;
            _nextLogTime = now + settings.LogIntervalSeconds.Value;
            if (!settings.OwnerEcho.Value) return;
            var worst = new System.Text.StringBuilder();
            double clock = Seconds;
            foreach (KeyValuePair<long, string> pair in Names)
            {
                OwnerEchoSample sample = _tracker.Get(pair.Key, clock);
                if (!sample.Tracked) continue;
                if (worst.Length > 0) worst.Append(' ');
                worst.Append(string.IsNullOrEmpty(pair.Value) ? pair.Key.ToString() : pair.Value).Append(':');
                worst.Append(sample.Measured
                    ? sample.MedianMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + "ms"
                    : "nao-medido");
                if (sample.Timeouts > 0) worst.Append("(t").Append(sample.Timeouts).Append(')');
            }
            _log.LogInfo(
                $"owner echo sent={_logSent}, replies={_logReplies}, timeouts={_logTimeouts}, " +
                $"roundMax={_logMaxRound:F1}ms, medians=[{worst}]");
            ResetLog();
        }

        private static void ResetLog()
        {
            _logSent = 0;
            _logReplies = 0;
            _logTimeouts = 0;
            _logMaxRound = 0d;
        }

        private static OwnerEchoTracker EnsureTracker(Settings settings)
        {
            int window = Math.Max(3, settings.OwnerEchoWindow.Value);
            double timeout = Math.Max(0.5d, settings.OwnerEchoTimeoutSeconds.Value);
            if (ReferenceEquals(_tracker, EmptyTracker) || _tracker.Window != window || Math.Abs(_tracker.TimeoutSeconds - timeout) > 1e-6)
            {
                _tracker = new OwnerEchoTracker(window, timeout, Math.Max(20d, settings.OwnerEchoIntervalSeconds.Value * 5d));
            }
            return _tracker;
        }

        /// <summary>Cliente: devolve o pacote no mesmo frame, sem tocar em mais nada.</summary>
        private static void OnRequest(long sender, ZPackage package)
        {
            ZNet net = ZNet.instance;
            ZRoutedRpc routed = ZRoutedRpc.instance;
            if (net == null || routed == null || net.IsServer()) return;
            if (!OwnerEchoWire.TryRead(package, out long sequence)) return;
            try
            {
                routed.InvokeRoutedRPC(sender, OwnerEchoWire.ReplyRpcName, OwnerEchoWire.Write(sequence));
            }
            catch (Exception exception)
            {
                _log?.LogDebug($"Owner echo reply failed: {exception.GetType().Name}: {exception.Message}");
            }
        }

        /// <summary>Servidor: fecha a volta do peer que respondeu.</summary>
        private static void OnReply(long sender, ZPackage package)
        {
            ZNet net = ZNet.instance;
            if (net == null || !net.IsServer()) return;
            if (!OwnerEchoWire.TryRead(package, out long sequence)) return;
            if (_tracker.Reply(sender, sequence, Seconds, out double round))
            {
                _logReplies++;
                if (round > _logMaxRound) _logMaxRound = round;
            }
        }
    }
}
