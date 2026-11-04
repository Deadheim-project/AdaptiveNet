using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AdaptiveNet.Core;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace AdaptiveNet
{
    /// <summary>
    /// Formato dos tres RPCs da captura de grupo (versionado pelo sufixo do nome e pelo campo
    /// protocol). Request: cliente admin -> servidor. Ask: servidor -> cada cliente com o mod.
    /// Reply: cliente -> servidor, com a serie deflatada.
    /// </summary>
    internal static class GroupCaptureWire
    {
        public const string RequestName = "AdaptiveNet.GroupCapture.Request.1";
        public const string AskName = "AdaptiveNet.GroupCapture.Ask.1";
        public const string ReplyName = "AdaptiveNet.GroupCapture.Reply.1";

        public static ZPackage WriteRequest(int seconds)
        {
            var package = new ZPackage();
            package.Write(GroupCaptureLimits.Protocol);
            package.Write(seconds);
            return package;
        }

        public static bool TryReadRequest(ZPackage package, out int seconds)
        {
            seconds = 0;
            if (package == null || package.Size() <= 0 || package.Size() > 32) return false;
            try
            {
                if (package.ReadInt() != GroupCaptureLimits.Protocol) return false;
                int requested = package.ReadInt();
                seconds = requested <= 0 ? 0 : GroupCaptureLimits.ClampSeconds(requested);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static ZPackage WriteAsk(int captureId, int seconds)
        {
            var package = new ZPackage();
            package.Write(GroupCaptureLimits.Protocol);
            package.Write(captureId);
            package.Write(seconds);
            return package;
        }

        public static bool TryReadAsk(ZPackage package, out int captureId, out int seconds)
        {
            captureId = 0;
            seconds = 0;
            if (package == null || package.Size() <= 0 || package.Size() > 32) return false;
            try
            {
                if (package.ReadInt() != GroupCaptureLimits.Protocol) return false;
                captureId = package.ReadInt();
                seconds = GroupCaptureLimits.ClampSeconds(package.ReadInt());
                return captureId > 0;
            }
            catch
            {
                return false;
            }
        }

        public static ZPackage WriteReply(int captureId, long clientUtcMilliseconds, byte[] block)
        {
            var package = new ZPackage();
            package.Write(GroupCaptureLimits.Protocol);
            package.Write(captureId);
            package.Write(clientUtcMilliseconds);
            package.Write(block ?? new byte[0]);
            return package;
        }

        public static bool TryReadReply(ZPackage package, out int captureId, out long clientUtcMilliseconds, out byte[] block)
        {
            captureId = 0;
            clientUtcMilliseconds = 0L;
            block = null;
            if (package == null || package.Size() <= 0 || package.Size() > GroupCaptureLimits.MaximumReplyPackageBytes) return false;
            try
            {
                if (package.ReadInt() != GroupCaptureLimits.Protocol) return false;
                captureId = package.ReadInt();
                clientUtcMilliseconds = package.ReadLong();
                block = package.ReadByteArray();
                return captureId > 0 && block != null && block.Length <= GroupCaptureLimits.MaximumCompressedBytes;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Captura de grupo sob demanda, so para admin. Nada aqui bloqueia a thread principal: o
    /// servidor so manda tres pacotes pequenos e junta as respostas no Tick; o arquivo e escrito
    /// em thread do pool. Qualquer falha desliga so esta funcao.
    /// </summary>
    internal static class GroupCaptureRpc
    {
        private const double ClientReplyMinimumSpacingSeconds = 20d;
        private const int KeptFiles = 30;

        private static ManualLogSource _log;
        private static Settings _settings;
        private static ZRoutedRpc _registeredInstance;
        private static readonly GroupCaptureSeries ClientSeries = new GroupCaptureSeries();
        private static GroupCaptureCoordinator _coordinator;
        private static double _lastCoordinatorConfigTimeout = -1d;
        private static double _lastCoordinatorConfigInterval = -1d;
        private static double _nextClientReplyTime;
        private static volatile bool _disabled;

        public static void Initialize(Settings settings, ManualLogSource log)
        {
            _settings = settings;
            _log = log;
            _registeredInstance = null;
            _coordinator = null;
            _disabled = false;
            _nextClientReplyTime = 0d;
        }

        public static void Shutdown()
        {
            _registeredInstance = null;
            _coordinator = null;
            _settings = null;
            _log = null;
        }

        /// <summary>Uma vez por quadro: registra os RPCs, fecha capturas vencidas e le o atalho.</summary>
        public static void Tick(double now)
        {
            if (_settings == null || _disabled) return;
            try
            {
                EnsureRegistered();
                ZNet net = ZNet.instance;
                if (net != null && net.IsServer())
                {
                    GroupCaptureResult done = _coordinator?.Poll(now);
                    if (done != null) Finish(done);
                }
                else if (!Application.isBatchMode && _settings.GroupCaptureKey.Value != KeyCode.None &&
                         Input.GetKeyDown(_settings.GroupCaptureKey.Value))
                {
                    RequestGroupCapture(0);
                }
            }
            catch (Exception exception)
            {
                Disable(exception);
            }
        }

        /// <summary>
        /// Chamado por um cliente (atalho ou menu Deadheim > Admin). Quem decide e o servidor, que
        /// confere se o remetente e admin; o cliente comum que chamar isto so e ignorado.
        /// </summary>
        public static void RequestGroupCapture(int seconds)
        {
            if (_settings == null || _disabled || !_settings.GroupCapture.Value) return;
            ZNet net = ZNet.instance;
            if (ZRoutedRpc.instance == null || net == null || net.IsServer()) return;
            try
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(ServerPeerId(), GroupCaptureWire.RequestName, GroupCaptureWire.WriteRequest(seconds));
                _log?.LogInfo("Group capture requested from the server.");
            }
            catch (Exception exception)
            {
                _log?.LogWarning($"Group capture request failed: {exception.GetType().Name}: {exception.Message}");
            }
        }

        /// <summary>Cliente: guarda o que o relatorio deste segundo ja continha (sem nova medicao).</summary>
        public static void RecordClientSample(
            RuntimeHealthSample health, NetworkSample network, bool focused, bool playerReady, bool teleporting,
            ClientAiSample ai, ClientMachineSample machine)
        {
            if (_disabled || _settings == null || !_settings.GroupCapture.Value) return;
            try
            {
                ClientSeries.Add(GroupCaptureSample.FromReport(
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds(), health, network, focused, playerReady, teleporting, ai, machine));
            }
            catch (Exception exception)
            {
                Disable(exception);
            }
        }

        private static void EnsureRegistered()
        {
            ZRoutedRpc routed = ZRoutedRpc.instance;
            if (routed == null || ReferenceEquals(routed, _registeredInstance)) return;
            routed.Register<ZPackage>(GroupCaptureWire.RequestName, OnRequest);
            routed.Register<ZPackage>(GroupCaptureWire.AskName, OnAsk);
            routed.Register<ZPackage>(GroupCaptureWire.ReplyName, OnReply);
            _registeredInstance = routed;
        }

        // ZRoutedRpc.GetServerPeerID e privado; o peer do servidor tem o mesmo uid, e so existe no cliente.
        private static long ServerPeerId()
        {
            return ZNet.instance?.GetServerPeer()?.m_uid ?? 0L;
        }

        private static void Disable(Exception exception)
        {
            if (_disabled) return;
            _disabled = true;
            _log?.LogWarning(
                $"Group capture disabled safely: {exception.GetType().Name}: {exception.Message}. Networking and the incident black box are unaffected.");
        }

        // ---- servidor ----------------------------------------------------------------------

        private static void OnRequest(long sender, ZPackage package)
        {
            ZNet net = ZNet.instance;
            if (_settings == null || _disabled || net == null || !net.IsServer() || !_settings.GroupCapture.Value) return;
            try
            {
                ZNetPeer requester = FindPeer(sender);
                if (requester?.m_socket == null) return;
                if (!IsAdmin(net, requester))
                {
                    _log?.LogWarning($"Group capture refused: {requester.m_playerName} (uid={sender}) is not an admin.");
                    return;
                }
                if (!GroupCaptureWire.TryReadRequest(package, out int seconds)) return;
                BeginCapture(Time.unscaledTimeAsDouble, requester.m_playerName, seconds <= 0 ? _settings.GroupCaptureSeconds.Value : seconds);
            }
            catch (Exception exception)
            {
                Disable(exception);
            }
        }

        private static void BeginCapture(double now, string requestedBy, int seconds)
        {
            ConfigureCoordinator();
            var participants = new List<GroupCaptureParticipant>();
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (peer?.m_socket == null || !peer.m_socket.IsConnected() || peer.m_uid == 0L) continue;
                participants.Add(new GroupCaptureParticipant(peer.m_uid, peer.m_playerName, NetworkRuntime.PeerHasReported(peer)));
            }
            if (!_coordinator.TryBegin(
                    now, requestedBy, seconds, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), participants,
                    out GroupCaptureResult capture, out string refusal))
            {
                _log?.LogInfo($"Group capture by {requestedBy} not started: {refusal}.");
                return;
            }
            foreach (GroupCapturePeer peer in capture.Peers)
            {
                if (peer.State != GroupCapturePeerState.Pending) continue;
                ZRoutedRpc.instance.InvokeRoutedRPC(peer.Uid, GroupCaptureWire.AskName, GroupCaptureWire.WriteAsk(capture.CaptureId, capture.Seconds));
            }
            _log?.LogInfo(
                $"Group capture #{capture.CaptureId} started by {requestedBy}: {capture.Seconds} s, asked {capture.Peers.Count(p => p.State == GroupCapturePeerState.Pending)} client(s).");
        }

        private static void ConfigureCoordinator()
        {
            double timeout = _settings.GroupCaptureTimeoutSeconds.Value;
            double interval = _settings.GroupCaptureMinIntervalSeconds.Value;
            if (_coordinator != null && !_coordinator.Active &&
                (timeout != _lastCoordinatorConfigTimeout || interval != _lastCoordinatorConfigInterval))
            {
                _coordinator = null; // cfg mudou: o limite de taxa recomeca, so quando nao ha captura aberta
            }
            if (_coordinator != null) return;
            _lastCoordinatorConfigTimeout = timeout;
            _lastCoordinatorConfigInterval = interval;
            _coordinator = new GroupCaptureCoordinator(timeout, interval);
        }

        private static void OnReply(long sender, ZPackage package)
        {
            ZNet net = ZNet.instance;
            if (_settings == null || _disabled || net == null || !net.IsServer() || _coordinator == null || !_coordinator.Active) return;
            try
            {
                if (!GroupCaptureWire.TryReadReply(package, out int captureId, out long clientUtcMilliseconds, out byte[] block)) return;
                if (!GroupCaptureCodec.TryDecode(block, out GroupCaptureSample[] samples)) return;
                if (!_coordinator.AcceptReply(
                        sender, captureId, samples, clientUtcMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), block.Length))
                {
                    return;
                }
                GroupCaptureResult done = _coordinator.Poll(Time.unscaledTimeAsDouble);
                if (done != null) Finish(done);
            }
            catch (Exception exception)
            {
                Disable(exception);
            }
        }

        private static void Finish(GroupCaptureResult result)
        {
            string text = GroupCaptureCsv.Format(result);
            string directory = Path.Combine(Paths.BepInExRootPath, "AdaptiveNet", "incidents");
            string path = Path.Combine(directory, $"group-capture-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
            ManualLogSource log = _log;
            int responded = result.Peers.Count(p => p.State == GroupCapturePeerState.Replied);
            int noMod = result.Peers.Count(p => p.State == GroupCapturePeerState.NoMod);
            int timedOut = result.Peers.Count(p => p.State == GroupCapturePeerState.TimedOut);
            int rows = GroupCaptureCsv.RowCount(result);
            log?.LogInfo(
                $"Group capture #{result.CaptureId} done: responded={responded}, no-mod={noMod}, timeout={timedOut}, rows={rows}, file={Path.GetFileName(path)}.");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(path, text);
                    foreach (FileInfo old in new DirectoryInfo(directory).GetFiles("group-capture-*.csv")
                                 .OrderByDescending(file => file.LastWriteTimeUtc).Skip(KeptFiles))
                    {
                        old.Delete();
                    }
                }
                catch (Exception exception)
                {
                    // Falha de escrita costuma ser passageira (disco ocupado): so esta captura se perde.
                    log?.LogWarning($"Group capture file not written ({path}): {exception.GetType().Name}: {exception.Message}");
                }
            });
        }

        private static ZNetPeer FindPeer(long uid)
        {
            if (uid == 0L) return null;
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (peer != null && peer.m_uid == uid && peer.m_socket != null && peer.m_socket.IsConnected()) return peer;
            }
            return null;
        }

        // Mesma lista que o jogo usa para os comandos de admin; nunca algo que o cliente mande.
        private static bool IsAdmin(ZNet net, ZNetPeer peer)
        {
            try
            {
                return net.IsAdmin(peer.m_socket.GetHostName());
            }
            catch
            {
                return false;
            }
        }

        // ---- cliente -----------------------------------------------------------------------

        private static void OnAsk(long sender, ZPackage package)
        {
            ZNet net = ZNet.instance;
            ZRoutedRpc routed = ZRoutedRpc.instance;
            if (_settings == null || _disabled || net == null || routed == null || net.IsServer() || !_settings.GroupCapture.Value) return;
            try
            {
                long serverId = ServerPeerId();
                if (serverId == 0L || sender != serverId) return;
                if (!GroupCaptureWire.TryReadAsk(package, out int captureId, out int seconds)) return;
                // O servidor ja limita a uma captura a cada 2 min; isto protege contra pacote forjado.
                double now = Time.unscaledTimeAsDouble;
                if (now < _nextClientReplyTime) return;
                _nextClientReplyTime = now + ClientReplyMinimumSpacingSeconds;
                long utcNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                GroupCaptureSample[] samples = ClientSeries.Take(utcNow / 1000L, seconds);
                byte[] block = GroupCaptureCodec.Encode(samples, GroupCaptureLimits.MaximumCompressedBytes, out _);
                routed.InvokeRoutedRPC(sender, GroupCaptureWire.ReplyName, GroupCaptureWire.WriteReply(captureId, utcNow, block));
            }
            catch (Exception exception)
            {
                Disable(exception);
            }
        }
    }
}
