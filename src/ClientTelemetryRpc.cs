using System;
using AdaptiveNet.Core;
using BepInEx.Logging;

namespace AdaptiveNet
{
    internal readonly struct ClientTelemetryReport
    {
        // 2 appends the client's hit-RPC upload wait. ServerSync already holds client and server
        // to the same AdaptiveNet version, so only the current protocol is accepted.
        public const int CurrentProtocol = 2;

        public ClientTelemetryReport(
            long sequence,
            double clientMonotonicSeconds,
            bool focused,
            bool playerReady,
            bool teleporting,
            RuntimeHealthSample health,
            NetworkSample uploadNetwork,
            LatencySummary hitUpload = default)
        {
            Sequence = sequence;
            ClientMonotonicSeconds = clientMonotonicSeconds;
            Focused = focused;
            PlayerReady = playerReady;
            Teleporting = teleporting;
            Health = health;
            UploadNetwork = uploadNetwork;
            HitUpload = hitUpload;
        }

        public long Sequence { get; }
        public double ClientMonotonicSeconds { get; }
        public bool Focused { get; }
        public bool PlayerReady { get; }
        public bool Teleporting { get; }
        public RuntimeHealthSample Health { get; }
        public NetworkSample UploadNetwork { get; }
        /// <summary>How long the hit RPCs this client sent waited in its own send queue.</summary>
        public LatencySummary HitUpload { get; }

        public ZPackage Serialize()
        {
            var package = new ZPackage();
            package.Write(CurrentProtocol);
            package.Write(Sequence);
            package.Write(ClientMonotonicSeconds);
            package.Write(Focused);
            package.Write(PlayerReady);
            package.Write(Teleporting);
            package.Write(Health.IntervalSeconds);
            package.Write(Health.FrameCount);
            package.Write((float)Health.AverageFrameMilliseconds);
            package.Write((float)Health.P95FrameMilliseconds);
            package.Write((float)Health.MaximumFrameMilliseconds);
            package.Write(Health.StallFrames);
            package.Write(Health.SevereStallFrames);
            package.Write(Health.Gen0Collections);
            package.Write(Health.Gen1Collections);
            package.Write(Health.Gen2Collections);
            package.Write((float)Health.GcCorrelatedStallMilliseconds);
            package.Write(Health.ManagedMemoryBytes);
            package.Write(UploadNetwork.Valid);
            package.Write(UploadNetwork.PingMilliseconds);
            package.Write((float)UploadNetwork.LocalQuality);
            package.Write((float)UploadNetwork.RemoteQuality);
            package.Write(UploadNetwork.ManagedQueueBytes);
            package.Write(UploadNetwork.PendingReliableBytes);
            package.Write(UploadNetwork.PendingUnreliableBytes);
            package.Write(UploadNetwork.UnackedBytes);
            package.Write((float)UploadNetwork.QueueDelayMilliseconds);
            package.Write(UploadNetwork.TransportSendRateBytesPerSecond);
            package.Write((float)UploadNetwork.OutgoingBytesPerSecond);
            package.Write((float)UploadNetwork.IncomingBytesPerSecond);
            package.Write((float)UploadNetwork.OutgoingPacketsPerSecond);
            package.Write((float)UploadNetwork.IncomingPacketsPerSecond);
            package.Write(HitUpload.Count);
            package.Write((float)HitUpload.AverageMilliseconds);
            package.Write((float)HitUpload.P95Milliseconds);
            package.Write((float)HitUpload.MaximumMilliseconds);
            return package;
        }

        public static bool TryDeserialize(ZPackage package, out ClientTelemetryReport report, out int protocol)
        {
            report = default;
            protocol = 0;
            if (package == null || package.Size() <= 0 || package.Size() > 512) return false;
            try
            {
                protocol = package.ReadInt();
                if (protocol != CurrentProtocol) return false;
                long sequence = package.ReadLong();
                double clientTime = Finite(package.ReadDouble(), 0d, 1000000000d);
                bool focused = package.ReadBool();
                bool playerReady = package.ReadBool();
                bool teleporting = package.ReadBool();
                double interval = Finite(package.ReadDouble(), 0d, 30d);
                int frames = Clamp(package.ReadInt(), 0, 10000);
                double averageFrame = Finite(package.ReadSingle(), 0d, 60000d);
                double p95Frame = Finite(package.ReadSingle(), 0d, 60000d);
                double maximumFrame = Finite(package.ReadSingle(), 0d, 60000d);
                int stalls = Clamp(package.ReadInt(), 0, frames);
                int severeStalls = Clamp(package.ReadInt(), 0, frames);
                int gc0 = Clamp(package.ReadInt(), 0, 100000);
                int gc1 = Clamp(package.ReadInt(), 0, 100000);
                int gc2 = Clamp(package.ReadInt(), 0, 100000);
                double gcStall = Finite(package.ReadSingle(), 0d, 60000d);
                long memory = Math.Max(0L, package.ReadLong());
                var health = new RuntimeHealthSample(
                    interval,
                    frames,
                    averageFrame,
                    p95Frame,
                    maximumFrame,
                    stalls,
                    severeStalls,
                    gc0,
                    gc1,
                    gc2,
                    gcStall,
                    memory);

                bool networkValid = package.ReadBool();
                int ping = Clamp(package.ReadInt(), 0, 60000);
                double localQuality = Finite(package.ReadSingle(), 0d, 1d);
                double remoteQuality = Finite(package.ReadSingle(), 0d, 1d);
                int managed = Clamp(package.ReadInt(), 0, int.MaxValue);
                int pendingReliable = Clamp(package.ReadInt(), 0, int.MaxValue);
                int pendingUnreliable = Clamp(package.ReadInt(), 0, int.MaxValue);
                int unacked = Clamp(package.ReadInt(), 0, int.MaxValue);
                double queueDelay = Finite(package.ReadSingle(), 0d, 60000d);
                int sendRate = Clamp(package.ReadInt(), 0, int.MaxValue);
                double outgoing = Finite(package.ReadSingle(), 0d, int.MaxValue);
                double incoming = Finite(package.ReadSingle(), 0d, int.MaxValue);
                double outgoingPackets = Finite(package.ReadSingle(), 0d, 10000000d);
                double incomingPackets = Finite(package.ReadSingle(), 0d, 10000000d);
                int hitCount = Clamp(package.ReadInt(), 0, 100000);
                double hitAverage = Finite(package.ReadSingle(), 0d, 60000d);
                double hitP95 = Finite(package.ReadSingle(), 0d, 60000d);
                double hitMaximum = Finite(package.ReadSingle(), 0d, 60000d);
                var hitUpload = hitCount > 0
                    ? new LatencySummary(hitCount, hitAverage, hitP95, hitMaximum)
                    : default;
                var network = new NetworkSample(
                    networkValid,
                    ping,
                    localQuality,
                    remoteQuality,
                    managed,
                    pendingReliable,
                    pendingUnreliable,
                    unacked,
                    queueDelay,
                    sendRate,
                    outgoing,
                    incoming,
                    outgoingPackets,
                    incomingPackets);
                report = new ClientTelemetryReport(
                    sequence,
                    clientTime,
                    focused,
                    playerReady,
                    teleporting,
                    health,
                    network,
                    hitUpload);
                return sequence >= 0 && clientTime > 0d;
            }
            catch
            {
                return false;
            }
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static double Finite(double value, double minimum, double maximum)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return minimum;
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }

    internal readonly struct RemoteClientTelemetrySnapshot
    {
        public RemoteClientTelemetrySnapshot(
            bool available,
            bool fresh,
            int reportsMerged,
            long sequence,
            bool focused,
            bool playerReady,
            bool teleporting,
            double deliveryDelayMilliseconds,
            double reportAgeSeconds,
            RuntimeHealthSample health,
            NetworkSample uploadNetwork,
            LatencySummary hitUpload = default)
        {
            Available = available;
            Fresh = fresh;
            ReportsMerged = Math.Max(0, reportsMerged);
            Sequence = sequence;
            Focused = focused;
            PlayerReady = playerReady;
            Teleporting = teleporting;
            DeliveryDelayMilliseconds = Math.Max(0d, deliveryDelayMilliseconds);
            ReportAgeSeconds = Math.Max(0d, reportAgeSeconds);
            Health = health;
            UploadNetwork = uploadNetwork;
            HitUpload = hitUpload;
        }

        public bool Available { get; }
        public bool Fresh { get; }
        public int ReportsMerged { get; }
        public long Sequence { get; }
        public bool Focused { get; }
        public bool PlayerReady { get; }
        public bool Teleporting { get; }
        public double DeliveryDelayMilliseconds { get; }
        public double ReportAgeSeconds { get; }
        public RuntimeHealthSample Health { get; }
        public NetworkSample UploadNetwork { get; }
        /// <summary>Hit RPC upload wait over every report merged into this snapshot.</summary>
        public LatencySummary HitUpload { get; }
    }

    internal sealed class RemoteClientTelemetryState
    {
        private readonly DeliveryDelayEstimator _delay = new DeliveryDelayEstimator();
        private RuntimeHealthSample _mergedHealth;
        private LatencySummary _mergedHitUpload;
        private NetworkSample _latestNetwork;
        private bool _focused;
        private bool _playerReady;
        private bool _teleporting;
        private bool _available;
        private int _reportsMerged;
        private long _lastSequence = -1;
        private double _lastArrivalTime;
        private double _lastExcessDelay;
        private double _maximumExcessDelay;

        public bool Accept(ClientTelemetryReport report, double arrivalTime)
        {
            if (_lastSequence >= 0 && report.Sequence <= _lastSequence) return false;
            _lastSequence = report.Sequence;
            _lastArrivalTime = arrivalTime;
            _lastExcessDelay = _delay.Observe(report.Sequence, report.ClientMonotonicSeconds, arrivalTime);
            _maximumExcessDelay = Math.Max(_maximumExcessDelay, _lastExcessDelay);
            _latestNetwork = report.UploadNetwork;
            if (_reportsMerged == 0)
            {
                _focused = report.Focused;
                _playerReady = report.PlayerReady;
                _teleporting = report.Teleporting;
            }
            else
            {
                _focused &= report.Focused;
                _playerReady &= report.PlayerReady;
                _teleporting |= report.Teleporting;
            }
            _available = true;
            // Every report counts its own hits, so none may be dropped by the 64-report cap below.
            _mergedHitUpload = LatencySummary.Merge(_mergedHitUpload, report.HitUpload);
            if (_reportsMerged < 64)
            {
                _mergedHealth = RuntimeHealthSample.Merge(_mergedHealth, report.Health);
                _reportsMerged++;
            }
            return true;
        }

        public RemoteClientTelemetrySnapshot TakeSnapshot(
            double now,
            double expectedReportIntervalSeconds = 1d,
            double reportMissingAfterSeconds = 4d)
        {
            double silenceSeconds = _available ? Math.Max(0d, now - _lastArrivalTime) : 0d;
            double expectedGapSeconds = Math.Max(0.25d, expectedReportIntervalSeconds) * 1.5d;
            double missingAfterSeconds = Math.Max(
                reportMissingAfterSeconds,
                Math.Max(0.25d, expectedReportIntervalSeconds) * 2.5d);
            bool receiving = _available && silenceSeconds < missingAfterSeconds;
            double excessSilenceMilliseconds = Math.Max(0d, silenceSeconds - expectedGapSeconds) * 1000d;
            double delay = Math.Max(_maximumExcessDelay, _lastExcessDelay + excessSilenceMilliseconds);
            var snapshot = new RemoteClientTelemetrySnapshot(
                receiving,
                _reportsMerged > 0,
                _reportsMerged,
                _lastSequence,
                _focused,
                _playerReady,
                _teleporting,
                delay,
                silenceSeconds,
                _mergedHealth,
                _latestNetwork,
                _mergedHitUpload);
            _mergedHealth = default;
            _mergedHitUpload = default;
            _reportsMerged = 0;
            _maximumExcessDelay = 0d;
            return snapshot;
        }
    }

    internal static class ClientTelemetryRpc
    {
        private const string TelemetryRpcName = "AdaptiveNet.Telemetry.1";
        private const string MarkerRpcName = "AdaptiveNet.IncidentMarker.1";
        private static ZRoutedRpc _registeredInstance;
        private static ManualLogSource _log;
        private static long _sequence;
        private static bool _registrationFailed;

        public static void Initialize(ManualLogSource log)
        {
            _log = log;
            _registeredInstance = null;
            _sequence = 0L;
            _registrationFailed = false;
        }

        public static void EnsureRegistered()
        {
            ZRoutedRpc routed = ZRoutedRpc.instance;
            if (routed == null || ReferenceEquals(routed, _registeredInstance) || _registrationFailed) return;
            try
            {
                routed.Register<ZPackage>(TelemetryRpcName, OnTelemetry);
                routed.Register<ZPackage>(MarkerRpcName, OnMarker);
                _registeredInstance = routed;
            }
            catch (Exception exception)
            {
                _registrationFailed = true;
                _log?.LogWarning($"Client telemetry RPC disabled safely: {exception.GetType().Name}: {exception.Message}");
            }
        }

        public static void Send(
            RuntimeHealthSample health,
            NetworkSample network,
            double now,
            bool focusedThroughoutInterval,
            bool playerReadyThroughoutInterval,
            bool teleportingDuringInterval,
            LatencySummary hitUpload)
        {
            if (ZRoutedRpc.instance == null || ZNet.instance == null || ZNet.instance.IsServer()) return;
            var report = new ClientTelemetryReport(
                _sequence++,
                now,
                focusedThroughoutInterval,
                playerReadyThroughoutInterval,
                teleportingDuringInterval,
                health,
                network,
                hitUpload);
            try
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(TelemetryRpcName, report.Serialize());
            }
            catch (Exception exception)
            {
                _log?.LogDebug($"Client telemetry send failed: {exception.GetType().Name}: {exception.Message}");
            }
        }

        public static void SendManualMarker(double now)
        {
            if (ZRoutedRpc.instance == null || ZNet.instance == null || ZNet.instance.IsServer()) return;
            try
            {
                var package = new ZPackage();
                package.Write(ClientTelemetryReport.CurrentProtocol);
                package.Write(_sequence++);
                package.Write(now);
                ZRoutedRpc.instance.InvokeRoutedRPC(MarkerRpcName, package);
                _log?.LogInfo("Incident marker sent to the server.");
            }
            catch (Exception exception)
            {
                _log?.LogWarning($"Incident marker send failed: {exception.GetType().Name}: {exception.Message}");
            }
        }

        public static void Shutdown()
        {
            _registeredInstance = null;
            _log = null;
            _registrationFailed = false;
        }

        private static void OnTelemetry(long sender, ZPackage package)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (ClientTelemetryReport.TryDeserialize(package, out ClientTelemetryReport report, out int protocol))
            {
                NetworkRuntime.ReceiveClientTelemetry(sender, report);
            }
            else
            {
                NetworkRuntime.ReportInvalidClientTelemetry(sender, protocol);
            }
        }

        private static void OnMarker(long sender, ZPackage package)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || package == null || package.Size() > 64) return;
            try
            {
                int protocol = package.ReadInt();
                long sequence = package.ReadLong();
                double clientTime = package.ReadDouble();
                if (protocol == ClientTelemetryReport.CurrentProtocol && sequence >= 0 &&
                    !double.IsNaN(clientTime) && !double.IsInfinity(clientTime))
                {
                    NetworkRuntime.ReceiveManualIncidentMarker(sender, clientTime);
                }
            }
            catch
            {
                // Malformed diagnostic packets never affect the network controller.
            }
        }
    }
}
