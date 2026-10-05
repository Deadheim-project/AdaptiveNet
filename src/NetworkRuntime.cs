using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using AdaptiveNet.Core;
using BepInEx.Logging;
using UnityEngine;

namespace AdaptiveNet
{
    internal sealed class ConnectionContext
    {
        public ConnectionContext(
            ISocket socket,
            AdaptiveControllerOptions options,
            int historyCapacity,
            double connectedAt)
        {
            Socket = socket;
            Controller = new AdaptiveConnectionController(options);
            History = new CircularHistory<PeerTelemetryPoint>(historyCapacity);
            ConnectedAt = connectedAt;
            Decision = new AdaptiveDecision(
                options.InitialSendRateBytesPerSecond,
                options.MinimumZdoQueueBudgetBytes,
                false,
                false,
                0d,
                "new");
        }

        public ISocket Socket { get; }
        public AdaptiveConnectionController Controller { get; }
        // Replaced, never resized in place, when HistorySeconds changes: an incident already
        // copying the old one keeps a consistent snapshot.
        public CircularHistory<PeerTelemetryPoint> History { get; set; }
        public RemoteClientTelemetryState ClientTelemetry { get; } = new RemoteClientTelemetryState();
        public AdaptiveDecision Decision { get; set; }
        public NetworkSample LastSample { get; set; }
        public RemoteClientTelemetrySnapshot LastClientSnapshot { get; set; }
        public int LastAppliedRate { get; set; }
        public int LastAppliedBuffer { get; set; }
        public bool TuningFailureLogged { get; set; }
        public string PlayerName { get; set; } = "unknown";
        public long PeerUid { get; set; }
        public bool WasCongested { get; set; }
        public double NextIncidentLogTime { get; set; }
        public double ConnectedAt { get; }
        public double NextInvalidTelemetryLogTime { get; set; }
        public double NextManualMarkerTime { get; set; }
        public double LastAnomalyTime { get; set; } = double.NegativeInfinity;
        public bool ClientReportMissingLatched { get; set; }
        public bool TransportSampleMissingLatched { get; set; }
    }

    internal sealed class DiagnosticsSnapshot
    {
        public string EffectiveMode { get; set; } = "disabled";
        public string Conflicts { get; set; } = string.Empty;
        public int PeerCount { get; set; }
        public int NetworkSamplePeers { get; set; }
        public double AveragePingMs { get; set; }
        public double P95PingMs { get; set; }
        public long TotalQueuedBytes { get; set; }
        public double MaximumQueueDelayMs { get; set; }
        // The ceiling AdaptiveNet asked Steam for, not what Steam sends at; see the real rate below.
        public double AverageRateLimitKiB { get; set; }
        // Steam's own per-connection send rate (m_nSendRateBytesPerSecond), the one that paces
        // the wire. Zero when no link reported one.
        public double AverageTransportRateKiB { get; set; }
        public double MinimumTransportRateKiB { get; set; }
        public double AverageZdoBudgetKiB { get; set; }
        public int CongestedPeers { get; set; }
        public int GroupedPeers { get; set; }
        public double SchedulerAttemptsPerSecond { get; set; }
        public int SchedulerBudgetStopsPerSecond { get; set; }
        public bool ZdoPatchApplied { get; set; }
        public string ZdoPatchMessage { get; set; } = "not checked";
        public double LocalAverageFps { get; set; }
        public double LocalP95FrameMs { get; set; }
        public double LocalMaximumFrameMs { get; set; }
        public int LocalGcCollections { get; set; }
        public double LocalGcCorrelatedFrameMs { get; set; }
        public int ClientTelemetryPeers { get; set; }
        public int MissingClientReports { get; set; }
        public double MaximumClientFrameMs { get; set; }
        public double MaximumClientReportDelayMs { get; set; }
        public int ActiveNonPlayerCharacters { get; set; }
        public int UnownedNonPlayerCharacters { get; set; }
        public int ActiveIncidentId { get; set; }
        public int DroppedIncidentJobs { get; set; }
        // Server side: ZNet.m_onlineBackend, "Steamworks" unless started with -crossplay.
        public string OnlineBackend { get; set; } = string.Empty;
        public LatencySummary RelayHold { get; set; }
        public LatencySummary HitForward { get; set; }
        public double ServiceGapMaximumMs { get; set; }
        public int ZdoQueueRefusals { get; set; }
        public double WorldSaveMs { get; set; }
        public double MaximumClientHitUploadMs { get; set; }
        // Client side: this client's own hit upload wait over its last report window.
        public LatencySummary LocalHitUpload { get; set; }
    }

    internal static class NetworkRuntime
    {
        public const int VanillaZdoQueueBudgetBytes = 10 * 1024;
        // ZSteamSocket.RegisterGlobalCallbacks sets SendRateMin = SendRateMax = 153600.
        private const int VanillaSteamSendRateKiB = 150;
        private const double ManualMarkerIntervalSeconds = 30d;
        // Server-wide: in PvP, everyone who loses a fight presses F9. Each manual capture dumps
        // every player's history, so at most one starts per minute; later markers still ride
        // along on a capture that is already open.
        private const double ManualIncidentIntervalSeconds = 60d;

        private static readonly object Gate = new object();
        private static readonly Dictionary<ISocket, ConnectionContext> Connections =
            new Dictionary<ISocket, ConnectionContext>(ReferenceComparer<ISocket>.Instance);

        private static Settings _settings;
        private static AdaptiveControllerOptions _controllerOptions;
        private static ManualLogSource _log;
        private static IReadOnlyList<string> _conflicts = Array.Empty<string>();
        private static bool _conflictsRechecked;
        private static bool _sampleFailureReported;
        private static bool _active;
        private static double _nextSampleTime;
        private static double _nextLogTime;
        private static double _nextClientReportTime;
        private static double _nextOwnershipSampleTime;
        private static int _historyCapacity;
        private static RuntimeHealthSample _pendingLocalHealth;
        private static bool _clientFocusedThroughoutWindow;
        private static bool _clientPlayerReadyThroughoutWindow;
        private static bool _clientTeleportingDuringWindow;
        private static CsvTelemetry _csv;
        private static IncidentTelemetry _incidents;
        private static bool _incidentWriterInitializationFailed;
        private static bool _ownershipFailureLogged;
        private static bool _ownershipTelemetryDisabled;
        private static bool _zdoPatchApplied;
        private static string _zdoPatchMessage = "not checked";
        private static DiagnosticsSnapshot _snapshot = new DiagnosticsSnapshot();
        private static int _nextIncidentId;
        private static int _activeIncidentId;
        private static string _activeIncidentTrigger = string.Empty;
        private static double _activeIncidentEndTime;
        private static double _nextAutomaticIncidentTime;
        private static string _pendingManualTrigger = string.Empty;
        private static double _nextManualIncidentTime;
        private static bool _settingsChanged;
        private static bool _onlineBackendChecked;
        private static string _onlineBackend = string.Empty;
        private static LatencySummary _lastClientHitUpload;
        private static LatencySummary _logRelayHold;
        private static LatencySummary _logHitForward;
        private static double _logHitUploadMaximumMs;
        private static double _logServiceGapMaximumMs;
        private static int _logZdoQueueRefusals;
        private static double _logWorldSaveMaximumMs;

        public static bool IsActive => _active;
        public static bool SchedulerEnabled => _settings != null && _settings.AdaptivePeerScheduler.Value;
        public static bool PinnedSteamSendEnabled => _settings != null && _settings.PinnedSteamSend.Value;
        public static DiagnosticsSnapshot Snapshot => _snapshot;

        public static void Initialize(Settings settings, ManualLogSource log, IReadOnlyList<string> conflicts)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _conflicts = conflicts ?? Array.Empty<string>();
            _controllerOptions = settings.BuildControllerOptions();
            AdaptivePeerScheduler.Initialize(settings.BuildSchedulerOptions(), log);
            SteamInterface.Initialize(log);
            PinnedSteamSender.Initialize(log);
            ClientTelemetryRpc.Initialize(log);
            PvpTelemetry.Initialize(log);
            CharacterOwnershipTelemetry.Reset();
            RuntimeHealthMonitor.Initialize(Time.realtimeSinceStartupAsDouble);

            _active = ComputeActive();
            _conflictsRechecked = false;
            _nextSampleTime = 0d;
            _nextLogTime = 0d;
            _nextClientReportTime = 0d;
            _nextOwnershipSampleTime = 0d;
            _historyCapacity = ComputeHistoryCapacity(settings);
            _settingsChanged = false;
            _pendingLocalHealth = default;
            ResetClientContextWindow();
            _incidentWriterInitializationFailed = false;
            _ownershipFailureLogged = false;
            _ownershipTelemetryDisabled = false;
            _nextIncidentId = 0;
            _activeIncidentId = 0;
            _activeIncidentTrigger = string.Empty;
            _activeIncidentEndTime = 0d;
            _nextAutomaticIncidentTime = 0d;
            _pendingManualTrigger = string.Empty;
            _nextManualIncidentTime = 0d;
            _onlineBackendChecked = false;
            _onlineBackend = string.Empty;
            _lastClientHitUpload = default;
            _logRelayHold = default;
            _logHitForward = default;
            _logHitUploadMaximumMs = 0d;
            _logServiceGapMaximumMs = 0d;
            _logZdoQueueRefusals = 0;
            _logWorldSaveMaximumMs = 0d;

            SyncCsvTelemetry();

            string conflictText = _conflicts.Count == 0 ? "none" : string.Join(", ", _conflicts);
            _log.LogInfo($"AdaptiveNet mode={EffectiveModeLabel()}, conflicts={conflictText}, diagnosticHistory={settings.HistorySeconds.Value}s");
            if (_conflicts.Count > 0)
            {
                _log.LogWarning("A known network overhaul is installed. AdaptiveNet is observe-only; no transport or ZDO limits will be changed.");
            }
            WarnIfSendRateFloorBelowVanilla();
        }

        /// <summary>
        /// The installer keeps an existing cfg and BepInEx keeps a value already written there,
        /// so a server updated from 0.4.0 or older still carries the old 64 KiB/s floor even
        /// though the default is now 150. Say so where an admin will read it.
        /// </summary>
        private static void WarnIfSendRateFloorBelowVanilla()
        {
            int floor = _settings.MinimumSendRateKiB.Value;
            if (floor >= VanillaSteamSendRateKiB) return;
            _log.LogWarning(
                $"Controller.MinimumSendRateKiB={floor} is below Valheim's own Steam send rate ({VanillaSteamSendRateKiB} KiB/s). " +
                "Steam only clamps each connection's rate, so a lowered ceiling leaves that player below vanilla until they reconnect. " +
                $"Set it to {VanillaSteamSendRateKiB} in the cfg; a live save applies it to open connections too.");
        }

        /// <summary>
        /// A setting changed: the cfg was reloaded, ServerSync applied the server's values (or
        /// restored the local ones on disconnect), or someone edited it in ConfigurationManager.
        /// The rebuild waits for the next Tick so a batch of dozens of entries costs one pass.
        /// </summary>
        public static void NotifySettingsChanged()
        {
            _settingsChanged = true;
        }

        /// <summary>
        /// Rebuilds what Initialize derived from the cfg, keeping per-connection state: each
        /// controller keeps its learned rate and ping baseline, each history keeps its newest
        /// samples. Everything else in the cfg is read where it is used.
        ///
        /// One thing still waits for a reconnect: when AdaptiveNet turns inactive, the Steam
        /// limits it already set on open connections stay, because it does not know the values
        /// Valheim would have used. They are the game's own again when that player reconnects.
        /// </summary>
        private static void ApplySettingChanges()
        {
            _settingsChanged = false;

            _controllerOptions = _settings.BuildControllerOptions();
            AdaptivePeerScheduler.UpdateOptions(_settings.BuildSchedulerOptions());
            _historyCapacity = ComputeHistoryCapacity(_settings);
            lock (Gate)
            {
                foreach (ConnectionContext context in Connections.Values)
                {
                    context.Controller.UpdateOptions(_controllerOptions);
                    if (context.History.Capacity != _historyCapacity)
                    {
                        context.History = context.History.WithCapacity(_historyCapacity);
                    }

                    // ApplySteamLimits only pushes when the rate or buffer moves; a new Nagle
                    // time, rate floor or buffer bound would otherwise wait for the next change.
                    context.LastAppliedRate = 0;
                    context.LastAppliedBuffer = 0;
                }
            }

            bool wasActive = _active;
            _active = ComputeActive();
            SyncCsvTelemetry();
            _incidents?.ApplyLimits(_settings);

            _log.LogInfo(
                $"AdaptiveNet settings applied: mode={EffectiveModeLabel()}, " +
                $"scheduler={(_settings.AdaptivePeerScheduler.Value ? "on" : "off")}, " +
                $"diagnosticHistory={_settings.HistorySeconds.Value}s." +
                (wasActive && !_active
                    ? " Steam limits already set on open connections stay until those players reconnect."
                    : string.Empty));
            WarnIfSendRateFloorBelowVanilla();
        }

        private static int ComputeHistoryCapacity(Settings settings)
        {
            return Math.Max(
                4,
                (int)Math.Ceiling(settings.HistorySeconds.Value /
                                  Math.Max(0.25f, settings.SampleIntervalSeconds.Value)) + 4);
        }

        /// <summary>Opens or closes the aggregate CSV to match CsvTelemetry.</summary>
        private static void SyncCsvTelemetry()
        {
            if (!_settings.CsvTelemetry.Value)
            {
                _csv?.Dispose();
                _csv = null;
                return;
            }

            if (_csv != null) return;
            try
            {
                _csv = new CsvTelemetry(_log);
            }
            catch (Exception exception)
            {
                _csv = null;
                _log.LogWarning(
                    $"Aggregate CSV telemetry disabled safely: {exception.GetType().Name}: {exception.Message}");
            }
        }

        public static void RecordFrame(double unscaledTime)
        {
            if (_settings == null) return;
            double stall = Math.Max(1d, _settings.FrameStallThresholdMs.Value);
            double severe = Math.Max(stall, _settings.SevereFrameStallThresholdMs.Value);
            RuntimeHealthMonitor.RecordFrame(unscaledTime, stall, severe);
            if (!Application.isBatchMode)
            {
                if (!Application.isFocused) _clientFocusedThroughoutWindow = false;
                Player localPlayer = Player.m_localPlayer;
                if (localPlayer == null) _clientPlayerReadyThroughoutWindow = false;
                else if (localPlayer.IsTeleporting()) _clientTeleportingDuringWindow = true;
            }
        }

        public static void NotifyApplicationFocus(bool hasFocus)
        {
            if (_settings != null && !hasFocus) _clientFocusedThroughoutWindow = false;
        }

        private static bool ComputeActive()
        {
            return _settings.Enabled.Value
                   && _settings.Mode.Value != OperatingMode.ObserveOnly
                   && _conflicts.Count == 0;
        }

        /// <summary>
        /// BepInEx fills Chainloader.PluginInfos as plugins load, so a conflict detected in
        /// Awake() only sees plugins whose GUID sorts before ours. AdaptiveNet loads well
        /// before VBNetTweaks, which means the startup check reports a clean run and the mod
        /// would activate on top of another network overhaul. The first Update happens after
        /// every plugin has been constructed, so re-check there and stand down if needed.
        /// </summary>
        private static void RecheckConflictsOnce()
        {
            if (_conflictsRechecked) return;
            _conflictsRechecked = true;

            IReadOnlyList<string> detected;
            try
            {
                detected = CompatibilityGuard.Detect();
            }
            catch (Exception exception)
            {
                _log.LogWarning(
                    $"Late conflict re-check skipped: {exception.GetType().Name}: {exception.Message}");
                return;
            }

            if (detected.Count <= _conflicts.Count) return;

            _conflicts = detected;
            bool wasActive = _active;
            _active = ComputeActive();
            _log.LogWarning(
                $"A known network overhaul loaded after AdaptiveNet: {string.Join(", ", _conflicts)}. " +
                $"mode={EffectiveModeLabel()}" +
                (wasActive && !_active
                    ? "; AdaptiveNet stood down to observe-only and will not change transport or ZDO limits."
                    : "; AdaptiveNet was already observing."));
        }

        public static void Tick(double unscaledTime)
        {
            if (_settings == null) return;
            if (_settingsChanged) ApplySettingChanges();
            RecheckConflictsOnce();
            ClientTelemetryRpc.EnsureRegistered();
            if (unscaledTime < _nextSampleTime) return;

            _nextSampleTime = unscaledTime + Math.Max(0.25f, _settings.SampleIntervalSeconds.Value);
            RuntimeHealthSample intervalHealth = RuntimeHealthMonitor.TakeInterval(unscaledTime);

            bool isServer = ZNet.instance != null && ZNet.instance.IsServer();
            bool dedicatedServer = ZNet.instance != null && ZNet.instance.IsDedicated();
            if (isServer)
            {
                CheckOnlineBackendOnce();
                EnsureIncidentWriter();
                RefreshOwnershipIfDue(unscaledTime);
            }
            else
            {
                _pendingLocalHealth = RuntimeHealthSample.Merge(_pendingLocalHealth, intervalHealth);
            }
            PvpWindow pvp = PvpTelemetry.TakeWindow();

            var seen = new HashSet<ISocket>(ReferenceComparer<ISocket>.Instance);
            var samples = new List<ConnectionContext>();
            var currentPoints = new List<PeerTelemetryPoint>();
            string automaticTrigger = string.Empty;

            if (ZNet.instance != null)
            {
                var connectedPeers = new List<ZNetPeer>();
                foreach (ZNetPeer peer in ZNet.instance.GetPeers())
                {
                    if (peer?.m_socket != null && peer.m_socket.IsConnected()) connectedPeers.Add(peer);
                }
                // Players inside the grouping radius are the ones who can fight each other.
                PvpTelemetry.Refresh(connectedPeers, isServer, _settings.GroupRadiusMeters.Value);

                double totalTransportWeight = 0d;
                for (int index = 0; index < connectedPeers.Count; index++)
                {
                    totalTransportWeight += AdaptivePeerScheduler.GetTransportWeight(connectedPeers[index]);
                }

                for (int index = 0; index < connectedPeers.Count; index++)
                {
                    ZNetPeer peer = connectedPeers[index];
                    ISocket socket = peer.m_socket;
                    // Keyed by the socket the peer holds, which GetZdoQueueBudget looks up every
                    // send; sampled and tuned through the real one, which on a crossplay server
                    // sits under ServerSync wrappers for the whole session.
                    ISocket transport = SocketUnwrapper.Unwrap(socket);
                    seen.Add(socket);
                    ConnectionContext context = GetOrCreate(socket, unscaledTime);
                    context.PlayerName = SanitizePlayerName(peer.m_playerName);
                    context.PeerUid = peer.m_uid;
                    bool transportSampleAvailable = TrySample(transport, out NetworkSample sample);
                    context.LastSample = sample;
                    if (transportSampleAvailable)
                    {
                        context.Decision = context.Controller.Observe(sample);
                        LogPeerLinkState(context, unscaledTime, dedicatedServer, intervalHealth);
                    }
                    samples.Add(context);

                    // Only a server actuates send-rate limits. The problem this mod exists to
                    // solve is the server's outbound fan-out to many players; a client uploads
                    // little more than its own position and input, so capping its uplink has no
                    // upside and a measured downside (2026-08-24: a client-side cut to the
                    // 64 KiB/s floor created an 86 ms send-queue delay against a link whose ping
                    // and queue delay were both healthy). Clients still sample and report.
                    if (transportSampleAvailable && _active && isServer && transport is ZSteamSocket steamSocket)
                    {
                        ApplySteamLimits(
                            context,
                            steamSocket,
                            dedicatedServer,
                            AdaptivePeerScheduler.GetTransportWeight(peer),
                            totalTransportWeight);
                    }

                    if (!isServer) continue;
                    RemoteClientTelemetrySnapshot client = context.ClientTelemetry.TakeSnapshot(
                        unscaledTime,
                        EffectiveClientReportIntervalSeconds(),
                        EffectiveClientReportMissingSeconds());
                    context.LastClientSnapshot = client;
                    CharacterOwnershipSnapshot ownership = CharacterOwnershipTelemetry.Get(peer.m_uid);
                    var point = new PeerTelemetryPoint(
                        DateTime.UtcNow.Ticks,
                        unscaledTime,
                        context.PlayerName,
                        context.PeerUid,
                        sample,
                        context.Decision,
                        context.LastAppliedRate > 0
                            ? context.LastAppliedRate
                            : context.Decision.SendRateLimitBytesPerSecond,
                        AdaptivePeerScheduler.GetNearbyPlayers(peer),
                        ownership,
                        client,
                        intervalHealth,
                        CharacterOwnershipTelemetry.ActiveNonPlayerCharacters,
                        CharacterOwnershipTelemetry.UnownedNonPlayerCharacters,
                        CharacterOwnershipTelemetry.ServerOwnedNonPlayerCharacters,
                        pvp.Get(peer.m_uid),
                        pvp.WorldSaveMilliseconds);
                    context.History.Add(point);
                    currentPoints.Add(point);

                    string anomaly = ClassifyPeerAnomaly(context, client, unscaledTime);
                    // Once per outage, like client-report-missing: a link that cannot be sampled
                    // at all used to start a capture every cooldown, all session long.
                    if (transportSampleAvailable)
                    {
                        context.TransportSampleMissingLatched = false;
                    }
                    else if (!context.TransportSampleMissingLatched &&
                             unscaledTime - context.ConnectedAt >= Math.Max(5d, EffectiveClientReportIntervalSeconds() * 3d))
                    {
                        context.TransportSampleMissingLatched = true;
                        AppendTrigger(ref anomaly, "transport-sample-unavailable");
                    }
                    if (!string.IsNullOrEmpty(anomaly))
                    {
                        context.LastAnomalyTime = unscaledTime;
                        AppendTrigger(ref automaticTrigger,
                            $"player={context.PlayerName};uid={context.PeerUid};reason={anomaly}");
                    }
                }
            }

            ConnectionContext[] staleConnections = GetStaleConnections(seen);
            if (isServer)
            {
                double degradedWindowSeconds = Math.Max(20d, _settings.IncidentCooldownSeconds.Value / 2d);
                for (int index = 0; index < staleConnections.Length; index++)
                {
                    ConnectionContext stale = staleConnections[index];
                    bool wasDegraded = stale.WasCongested ||
                                       unscaledTime - stale.LastAnomalyTime <= degradedWindowSeconds;
                    if (!wasDegraded) continue;
                    AppendTrigger(
                        ref automaticTrigger,
                        $"player={stale.PlayerName};uid={stale.PeerUid};reason=peer-disconnected");
                }
            }

            if (isServer)
            {
                string serverAnomaly = DiagnosticAnomalyClassifier.ClassifyServer(
                    intervalHealth,
                    _settings.FrameStallThresholdMs.Value,
                    Math.Max(_settings.FrameStallThresholdMs.Value, _settings.SevereFrameStallThresholdMs.Value),
                    pvp.WorldSaveMilliseconds);
                if (!string.IsNullOrEmpty(serverAnomaly)) AppendTrigger(ref automaticTrigger, serverAnomaly);
                ProcessIncidents(unscaledTime, currentPoints, automaticTrigger);
            }
            else
            {
                SendClientReportIfDue(unscaledTime, samples);
            }
            RemoveStaleConnections(staleConnections);

            _snapshot = BuildSnapshot(samples, intervalHealth, unscaledTime, isServer, pvp);
            AccumulatePvpForLog(_snapshot);
            DiagnosticsOverlay.SetSnapshot(_snapshot);
            _csv?.Write(unscaledTime, _snapshot);
            LogAggregateIfDue(unscaledTime);
        }

        public static void RequestManualIncidentMarker(double unscaledTime)
        {
            if (_settings == null || !_settings.IncidentTelemetry.Value) return;
            if (ZNet.instance != null && ZNet.instance.IsServer())
            {
                AppendTrigger(ref _pendingManualTrigger, "manual-marker-server");
            }
            else
            {
                ClientTelemetryRpc.SendManualMarker(unscaledTime);
            }
        }

        public static void ReceiveClientTelemetry(long sender, ClientTelemetryReport report)
        {
            if (_settings == null || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            ZNetPeer peer = FindConnectedPeer(sender);
            if (peer?.m_socket == null) return;
            double now = Time.realtimeSinceStartupAsDouble;
            ConnectionContext context = GetOrCreate(peer.m_socket, now);
            context.PlayerName = SanitizePlayerName(peer.m_playerName);
            context.PeerUid = peer.m_uid;
            context.ClientTelemetry.Accept(report, now);
        }

        public static void ReportInvalidClientTelemetry(long sender, int protocol)
        {
            if (_settings == null || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            ZNetPeer peer = FindConnectedPeer(sender);
            if (peer?.m_socket == null) return;
            double now = Time.realtimeSinceStartupAsDouble;
            ConnectionContext context = GetOrCreate(peer.m_socket, now);
            if (now < context.NextInvalidTelemetryLogTime) return;
            context.NextInvalidTelemetryLogTime = now + 60d;
            _log.LogWarning(
                $"Invalid or incompatible client telemetry: player={SanitizePlayerName(peer.m_playerName)}, uid={sender}, protocol={protocol}.");
        }

        public static void ReceiveManualIncidentMarker(long sender, double clientTime)
        {
            if (_settings == null || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            ZNetPeer peer = FindConnectedPeer(sender);
            if (peer?.m_socket == null) return;
            // A marker skips the automatic cooldown and dumps every player's history, and any
            // client can send it (Valheim does not even check a routed RPC's sender id), so it
            // is rationed per connection: one held F9 cannot keep the black box busy.
            double now = Time.realtimeSinceStartupAsDouble;
            ConnectionContext context = GetOrCreate(peer.m_socket, now);
            if (now < context.NextManualMarkerTime) return;
            context.NextManualMarkerTime = now + ManualMarkerIntervalSeconds;
            AppendTrigger(
                ref _pendingManualTrigger,
                $"manual-marker;player={SanitizePlayerName(peer.m_playerName)};uid={sender};clientTime={clientTime:F3}");
        }

        public static int GetZdoQueueBudget(object peer)
        {
            ZNetPeer netPeer = GameAccess.GetZNetPeer(peer);
            if (!_active || netPeer?.m_socket == null) return VanillaZdoQueueBudgetBytes;

            lock (Gate)
            {
                return Connections.TryGetValue(netPeer.m_socket, out ConnectionContext context)
                    ? Math.Max(VanillaZdoQueueBudgetBytes, context.Decision.ZdoQueueBudgetBytes)
                    : VanillaZdoQueueBudgetBytes;
            }
        }

        /// <summary>
        /// The budget SendZDOs' queue guard compares against: AdaptiveNet's when its transpiler is
        /// in place, Valheim's otherwise. Lets the PvP probe tell a refusal from an idle attempt.
        /// </summary>
        public static int GetZdoGuardBudget(object peer)
        {
            return _zdoPatchApplied ? GetZdoQueueBudget(peer) : VanillaZdoQueueBudgetBytes;
        }

        public static void ReportZdoPatchStatus(bool applied, string message)
        {
            _zdoPatchApplied = applied;
            _zdoPatchMessage = message ?? string.Empty;
            if (_log == null) return;
            if (applied) _log.LogInfo(message);
            else _log.LogWarning(message);
        }

        public static void Shutdown()
        {
            _csv?.Dispose();
            _csv = null;
            _incidents?.Dispose();
            _incidents = null;
            ClientTelemetryRpc.Shutdown();
            PvpTelemetry.Reset();
            CharacterOwnershipTelemetry.Reset();
            AdaptivePeerScheduler.Reset();
            PinnedSteamSender.Reset();
            SteamInterface.Reset();
            lock (Gate)
            {
                Connections.Clear();
            }
            _settings = null;
        }

        private static void SendClientReportIfDue(double now, List<ConnectionContext> samples)
        {
            if (!_settings.IncidentTelemetry.Value || now < _nextClientReportTime) return;
            _nextClientReportTime = now + EffectiveClientReportIntervalSeconds();
            NetworkSample network = samples.Count > 0 ? samples[0].LastSample : default;
            _lastClientHitUpload = PvpTelemetry.TakeClientHitUpload();
            ClientTelemetryRpc.Send(
                _pendingLocalHealth,
                network,
                now,
                _clientFocusedThroughoutWindow,
                _clientPlayerReadyThroughoutWindow,
                _clientTeleportingDuringWindow,
                _lastClientHitUpload);
            _pendingLocalHealth = default;
            ResetClientContextWindow();
        }

        private static string ClassifyPeerAnomaly(
            ConnectionContext context,
            RemoteClientTelemetrySnapshot client,
            double now)
        {
            double connectionGrace = Math.Max(5d, EffectiveClientReportIntervalSeconds() * 3d);
            if (now - context.ConnectedAt < connectionGrace)
            {
                return string.Empty;
            }

            string anomaly = DiagnosticAnomalyClassifier.ClassifyPeer(
                context.Decision.Congested,
                context.Decision.Reason,
                client.Available,
                client.Focused,
                client.PlayerReady,
                client.Teleporting,
                client.DeliveryDelayMilliseconds,
                client.Health,
                _settings.FrameStallThresholdMs.Value,
                Math.Max(_settings.FrameStallThresholdMs.Value, _settings.SevereFrameStallThresholdMs.Value),
                _settings.ClientReportDelayThresholdMs.Value);

            if (!client.Available && now - context.ConnectedAt >= EffectiveClientReportMissingSeconds())
            {
                if (!context.ClientReportMissingLatched)
                {
                    context.ClientReportMissingLatched = true;
                    AppendTrigger(ref anomaly, "client-report-missing");
                }
            }
            else if (client.Available)
            {
                context.ClientReportMissingLatched = false;
            }
            return anomaly;
        }

        private static void ProcessIncidents(
            double now,
            List<PeerTelemetryPoint> currentPoints,
            string automaticTrigger)
        {
            bool hasManualTrigger = !string.IsNullOrEmpty(_pendingManualTrigger);
            string manualTrigger = _pendingManualTrigger;
            _pendingManualTrigger = string.Empty;

            if (_activeIncidentId != 0)
            {
                if (!string.IsNullOrEmpty(automaticTrigger))
                {
                    AppendTrigger(ref _activeIncidentTrigger, automaticTrigger);
                }
                if (hasManualTrigger)
                {
                    AppendTrigger(ref _activeIncidentTrigger, manualTrigger);
                    _incidents?.QueuePoints(
                        _activeIncidentId,
                        "marker",
                        manualTrigger,
                        currentPoints.ToArray(),
                        true);
                    _log.LogWarning($"diagnostic marker added to incident #{_activeIncidentId}: {manualTrigger}");
                }

                bool ending = now >= _activeIncidentEndTime;
                _incidents?.QueuePoints(
                    _activeIncidentId,
                    "post",
                    _activeIncidentTrigger,
                    currentPoints.ToArray(),
                    ending);
                if (ending)
                {
                    _log.LogInfo($"diagnostic incident #{_activeIncidentId} complete; post-window captured.");
                    _activeIncidentId = 0;
                    _activeIncidentTrigger = string.Empty;
                }
                return;
            }

            bool manualCanStart = hasManualTrigger && now >= _nextManualIncidentTime;
            string trigger;
            if (manualCanStart)
            {
                trigger = manualTrigger;
            }
            else
            {
                trigger = now >= _nextAutomaticIncidentTime ? automaticTrigger : string.Empty;
                // A marker over the server-wide limit still names itself on any capture it meets.
                if (hasManualTrigger && !string.IsNullOrEmpty(trigger)) AppendTrigger(ref trigger, manualTrigger);
                else if (hasManualTrigger)
                {
                    _log.LogDebug($"diagnostic marker not captured (one manual capture per {ManualIncidentIntervalSeconds:F0} s): {manualTrigger}");
                }
            }
            // IncidentTelemetry is checked here, not only where the writer is created: turned off
            // with the server running, the writer stays alive but no new capture starts.
            if (string.IsNullOrEmpty(trigger) ||
                !_settings.IncidentTelemetry.Value ||
                _incidents == null ||
                !_incidents.Available)
            {
                return;
            }

            ConnectionContext[] contexts;
            lock (Gate) contexts = Connections.Values.ToArray();
            if (currentPoints.Count == 0 && contexts.All(context => context.History.Count == 0)) return;

            _activeIncidentId = ++_nextIncidentId;
            _activeIncidentTrigger = trigger;
            _activeIncidentEndTime = now + _settings.IncidentPostSeconds.Value;
            _nextAutomaticIncidentTime = now + _settings.IncidentCooldownSeconds.Value;
            if (manualCanStart) _nextManualIncidentTime = now + ManualIncidentIntervalSeconds;
            _incidents.QueueHistories(
                _activeIncidentId,
                "pre",
                trigger,
                contexts.Select(context => context.History),
                true);
            _log.LogWarning(
                $"diagnostic incident #{_activeIncidentId} started: {trigger}; " +
                $"preserved up to {_settings.HistorySeconds.Value}s before and will capture {_settings.IncidentPostSeconds.Value}s after.");
        }

        /// <summary>
        /// The Deadheim PvP server is Steam-only. Started with -crossplay, players can arrive over
        /// PlayFab, which gets none of the Steam transport tuning, pinned send or hit-queue probes,
        /// and ServerSync then wraps every player's socket for the whole session. Say so once, at
        /// warning level, where the admin reads the startup log.
        /// </summary>
        private static void CheckOnlineBackendOnce()
        {
            if (_onlineBackendChecked) return;
            _onlineBackendChecked = true;
            OnlineBackendType backend = ZNet.m_onlineBackend;
            _onlineBackend = backend.ToString();
            if (backend == OnlineBackendType.Steamworks)
            {
                _log.LogInfo("Online backend: Steamworks (Steam-only). Every player gets the Steam transport tuning and the PvP probes.");
                return;
            }

            _log.LogWarning(
                $"Online backend: {backend}. This server was started with -crossplay: players joining over PlayFab " +
                "get no Steam transport tuning and no hit-queue measurements. AdaptiveNet's PvP setup expects a " +
                "Steam-only server; remove -crossplay from the launch line.");
        }

        private static void EnsureIncidentWriter()
        {
            if (!_settings.IncidentTelemetry.Value || _incidents != null || _incidentWriterInitializationFailed) return;
            try
            {
                _incidents = new IncidentTelemetry(_settings, _log);
                _log.LogInfo(
                    $"Incident black box armed: pre={_settings.HistorySeconds.Value}s, " +
                    $"post={_settings.IncidentPostSeconds.Value}s, clientReport={EffectiveClientReportIntervalSeconds():F1}s.");
            }
            catch (Exception exception)
            {
                _incidentWriterInitializationFailed = true;
                _log.LogWarning(
                    $"Incident telemetry could not start; network control remains active: {exception.GetType().Name}: {exception.Message}");
            }
        }

        private static void RefreshOwnershipIfDue(double now)
        {
            if (_ownershipTelemetryDisabled || now < _nextOwnershipSampleTime) return;
            _nextOwnershipSampleTime = now + Math.Max(1f, _settings.OwnershipSampleIntervalSeconds.Value);
            try
            {
                CharacterOwnershipTelemetry.Refresh();
            }
            catch (Exception exception)
            {
                _ownershipTelemetryDisabled = true;
                if (_ownershipFailureLogged) return;
                _ownershipFailureLogged = true;
                _log.LogWarning(
                    $"Character ownership telemetry disabled safely: {exception.GetType().Name}: {exception.Message}");
            }
        }

        private static ConnectionContext GetOrCreate(ISocket socket, double now)
        {
            lock (Gate)
            {
                if (!Connections.TryGetValue(socket, out ConnectionContext context))
                {
                    context = new ConnectionContext(socket, _controllerOptions, _historyCapacity, now);
                    Connections.Add(socket, context);
                }
                return context;
            }
        }

        private static ZNetPeer FindConnectedPeer(long uid)
        {
            if (uid == 0L || ZNet.instance == null) return null;
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (peer != null && peer.m_uid == uid && peer.m_socket != null && peer.m_socket.IsConnected())
                {
                    return peer;
                }
            }
            return null;
        }

        /// <summary>
        /// Adapts Valheim's ISocket onto the transport-free seam in ConnectionSampler so the
        /// fallback arithmetic stays testable outside the game.
        /// </summary>
        private sealed class SocketMetrics : IVanillaConnectionMetrics
        {
            private readonly ISocket _socket;

            public SocketMetrics(ISocket socket)
            {
                _socket = socket;
            }

            public void GetConnectionQuality(
                out float localQuality,
                out float remoteQuality,
                out int pingMilliseconds,
                out float outgoingBytesPerSecond,
                out float incomingBytesPerSecond)
            {
                _socket.GetConnectionQuality(
                    out localQuality,
                    out remoteQuality,
                    out pingMilliseconds,
                    out outgoingBytesPerSecond,
                    out incomingBytesPerSecond);
            }

            public int GetSendQueueSize() => _socket.GetSendQueueSize();

            public int GetCurrentSendRate() => _socket.GetCurrentSendRate();
        }

        private static bool TrySample(ISocket socket, out NetworkSample sample)
        {
            TransportSampler transportSampler = socket is ZSteamSocket steamSocket
                ? (TransportSampler)((out NetworkSample steamSample) =>
                    SteamTransport.TrySample(steamSocket, out steamSample))
                : null;
            return ConnectionSampler.TrySample(
                new SocketMetrics(socket),
                transportSampler,
                LogSampleFailure,
                out sample);
        }

        /// <summary>
        /// Reports the first failure at warning level and the rest at debug. Debug-only
        /// logging let a broken sampler report networkSamples=0 indefinitely with no hint
        /// in the log file, because BepInEx does not persist debug lines by default.
        /// </summary>
        private static void LogSampleFailure(string stage, Exception exception)
        {
            string details = $"{stage} failed: {exception.GetType().Name}: {exception.Message}";
            if (_sampleFailureReported)
            {
                _log.LogDebug(details);
                return;
            }

            _sampleFailureReported = true;
            _log.LogWarning(details + " Telemetry for this link stays empty; later occurrences log at debug level.");
        }

        private static void ApplySteamLimits(
            ConnectionContext context,
            ZSteamSocket socket,
            bool dedicatedServer,
            double peerWeight,
            double totalWeight)
        {
            int rate = context.Decision.SendRateLimitBytesPerSecond;
            if (dedicatedServer && _settings.ServerUploadBudgetMiB.Value > 0 && totalWeight > 0d)
            {
                long aggregateBytes = (long)_settings.ServerUploadBudgetMiB.Value * 1024L * 1024L;
                int fairShare = (int)Math.Min(int.MaxValue, aggregateBytes * peerWeight / totalWeight);
                rate = Math.Min(rate, Math.Max(_controllerOptions.MinimumSendRateBytesPerSecond, fairShare));
            }
            int minimumBuffer = _settings.MinimumSteamSendBufferKiB.Value * 1024;
            int maximumBuffer = Math.Max(minimumBuffer, _settings.MaximumSteamSendBufferKiB.Value * 1024);
            int buffer = Math.Max(minimumBuffer, Math.Min(maximumBuffer, rate / 2));
            if (context.LastAppliedRate == rate && context.LastAppliedBuffer == buffer) return;

            bool ok = SteamTransport.ApplyConnectionLimits(
                socket,
                _controllerOptions.MinimumSendRateBytesPerSecond,
                rate,
                buffer,
                _settings.NagleTimeMicroseconds.Value);
            if (ok)
            {
                context.LastAppliedRate = rate;
                context.LastAppliedBuffer = buffer;
            }
            else if (!context.TuningFailureLogged)
            {
                context.TuningFailureLogged = true;
                _log.LogWarning("Steam rejected one or more per-connection limits. Adaptive ZDO budgeting remains available.");
            }
        }

        private static ConnectionContext[] GetStaleConnections(HashSet<ISocket> seen)
        {
            lock (Gate)
            {
                return Connections
                    .Where(pair => !seen.Contains(pair.Key))
                    .Select(pair => pair.Value)
                    .ToArray();
            }
        }

        private static void RemoveStaleConnections(ConnectionContext[] staleConnections)
        {
            if (staleConnections == null || staleConnections.Length == 0) return;
            lock (Gate)
            {
                for (int index = 0; index < staleConnections.Length; index++)
                {
                    Connections.Remove(staleConnections[index].Socket);
                }
            }
        }

        private static void LogPeerLinkState(
            ConnectionContext context,
            double now,
            bool dedicatedServer,
            RuntimeHealthSample localHealth)
        {
            bool stateChanged = context.Decision.Congested != context.WasCongested;
            bool periodicIncident = context.Decision.Congested && now >= context.NextIncidentLogTime;
            if (!stateChanged && !periodicIncident) return;

            NetworkSample sample = context.LastSample;
            double quality = Math.Min(
                sample.LocalQuality > 0d ? sample.LocalQuality : 1d,
                sample.RemoteQuality > 0d ? sample.RemoteQuality : 1d);
            string subject = dedicatedServer
                ? $"player={context.PlayerName}, uid={context.PeerUid}"
                : "server-connection";
            // Local and remote quality mean opposite directions: local is what we fail to
            // receive, remote is what the other end fails to receive from us. Only the remote
            // side is something our send rate can influence, so collapsing both into one
            // number made it impossible to tell whether a cut could even help. Local frame
            // stalls ride along because a stalled client is the likeliest confounder.
            string details =
                $"{subject}, ping={sample.PingMilliseconds}ms, quality={quality:F2} " +
                $"(local={FormatQuality(sample.LocalQuality)}, remote={FormatQuality(sample.RemoteQuality)}), " +
                $"queue={sample.QueueDelayMilliseconds:F0}ms/{sample.TotalQueuedBytes / 1024d:F1}KiB, " +
                $"localFrameMax={localHealth.MaximumFrameMilliseconds:F0}ms, " +
                $"reason={context.Decision.Reason}, " +
                $"sendRate={sample.TransportSendRateBytesPerSecond / 1024}KiB/s, cap=" +
                $"{(context.LastAppliedRate > 0 ? context.LastAppliedRate : context.Decision.SendRateLimitBytesPerSecond) / 1024}KiB/s";
            if (context.Decision.Congested)
            {
                _log.LogWarning("peer-link degraded: " + details);
                context.NextIncidentLogTime = now + 30d;
            }
            else if (stateChanged)
            {
                _log.LogInfo("peer-link recovered: " + details);
            }
            context.WasCongested = context.Decision.Congested;
        }

        private static DiagnosticsSnapshot BuildSnapshot(
            List<ConnectionContext> contexts,
            RuntimeHealthSample localHealth,
            double now,
            bool isServer,
            PvpWindow pvp)
        {
            var snapshot = new DiagnosticsSnapshot
            {
                EffectiveMode = EffectiveModeLabel(),
                Conflicts = _conflicts.Count == 0 ? string.Empty : string.Join(", ", _conflicts),
                PeerCount = contexts.Count,
                ZdoPatchApplied = _zdoPatchApplied,
                ZdoPatchMessage = _zdoPatchMessage,
                LocalAverageFps = localHealth.AverageFps,
                LocalP95FrameMs = localHealth.P95FrameMilliseconds,
                LocalMaximumFrameMs = localHealth.MaximumFrameMilliseconds,
                LocalGcCollections = localHealth.Gen0Collections + localHealth.Gen1Collections + localHealth.Gen2Collections,
                LocalGcCorrelatedFrameMs = localHealth.GcCorrelatedStallMilliseconds,
                ActiveNonPlayerCharacters = CharacterOwnershipTelemetry.ActiveNonPlayerCharacters,
                UnownedNonPlayerCharacters = CharacterOwnershipTelemetry.UnownedNonPlayerCharacters,
                ActiveIncidentId = _activeIncidentId,
                DroppedIncidentJobs = _incidents?.DroppedJobs ?? 0,
                OnlineBackend = isServer ? _onlineBackend : string.Empty,
                RelayHold = pvp.RelayHold,
                HitForward = pvp.HitForward,
                ServiceGapMaximumMs = pvp.ServiceGapMaximumMilliseconds,
                ZdoQueueRefusals = pvp.QueueRefusals,
                WorldSaveMs = pvp.WorldSaveMilliseconds,
                LocalHitUpload = isServer ? default : _lastClientHitUpload
            };
            SchedulerSnapshot scheduler = AdaptivePeerScheduler.Snapshot;
            snapshot.GroupedPeers = scheduler.GroupedPeers;
            snapshot.SchedulerAttemptsPerSecond = scheduler.AttemptsPerSecond;
            snapshot.SchedulerBudgetStopsPerSecond = scheduler.BudgetStopsPerSecond;
            if (contexts.Count == 0) return snapshot;

            ConnectionContext[] networkContexts = contexts.Where(item => item.LastSample.Valid).ToArray();
            snapshot.NetworkSamplePeers = networkContexts.Length;
            if (networkContexts.Length > 0)
            {
                int[] pings = networkContexts.Select(item => item.LastSample.PingMilliseconds).OrderBy(value => value).ToArray();
                int p95Index = Math.Min(pings.Length - 1, (int)Math.Ceiling(pings.Length * 0.95d) - 1);
                snapshot.AveragePingMs = pings.Average();
                snapshot.P95PingMs = pings[Math.Max(0, p95Index)];
                snapshot.TotalQueuedBytes = networkContexts.Sum(item => (long)item.LastSample.TotalQueuedBytes);
                snapshot.MaximumQueueDelayMs = networkContexts.Max(item => item.LastSample.QueueDelayMilliseconds);
                int[] transportRates = networkContexts
                    .Select(item => item.LastSample.TransportSendRateBytesPerSecond)
                    .Where(value => value > 0)
                    .ToArray();
                if (transportRates.Length > 0)
                {
                    snapshot.AverageTransportRateKiB = transportRates.Average() / 1024d;
                    snapshot.MinimumTransportRateKiB = transportRates.Min() / 1024d;
                }
            }
            snapshot.AverageRateLimitKiB = contexts.Average(item =>
                (item.LastAppliedRate > 0 ? item.LastAppliedRate : item.Decision.SendRateLimitBytesPerSecond) / 1024d);
            snapshot.AverageZdoBudgetKiB = contexts.Average(item => item.Decision.ZdoQueueBudgetBytes / 1024d);
            snapshot.CongestedPeers = contexts.Count(item => item.Decision.Congested);
            if (isServer)
            {
                snapshot.ClientTelemetryPeers = contexts.Count(item => item.LastClientSnapshot.Available);
                snapshot.MissingClientReports = contexts.Count(item =>
                    !item.LastClientSnapshot.Available && now - item.ConnectedAt >= EffectiveClientReportMissingSeconds());
                snapshot.MaximumClientFrameMs = contexts.Max(item => item.LastClientSnapshot.Health.MaximumFrameMilliseconds);
                snapshot.MaximumClientReportDelayMs = contexts.Max(item => item.LastClientSnapshot.DeliveryDelayMilliseconds);
                snapshot.MaximumClientHitUploadMs = contexts.Max(item => item.LastClientSnapshot.HitUpload.MaximumMilliseconds);
            }
            return snapshot;
        }

        private static void LogAggregateIfDue(double now)
        {
            if (_settings.LogIntervalSeconds.Value <= 0f || now < _nextLogTime) return;
            _nextLogTime = now + _settings.LogIntervalSeconds.Value;
            if (_snapshot.PeerCount <= 0)
            {
                FormatPvpSummaryAndReset(_snapshot);
                return;
            }
            _log.LogInfo(
                $"telemetry peers={_snapshot.PeerCount}, networkSamples={_snapshot.NetworkSamplePeers}, ping(avg/p95)={_snapshot.AveragePingMs:F0}/{_snapshot.P95PingMs:F0}ms, " +
                $"queued={_snapshot.TotalQueuedBytes / 1024d:F1}KiB, queueMax={_snapshot.MaximumQueueDelayMs:F1}ms, " +
                $"sendRate(avg/min)={_snapshot.AverageTransportRateKiB:F0}/{_snapshot.MinimumTransportRateKiB:F0}KiB/s, " +
                $"rateCapAvg={_snapshot.AverageRateLimitKiB:F0}KiB/s, zdoAvg={_snapshot.AverageZdoBudgetKiB:F1}KiB, " +
                $"congested={_snapshot.CongestedPeers}, grouped={_snapshot.GroupedPeers}, " +
                $"localFps={_snapshot.LocalAverageFps:F0}, localFrameMax={_snapshot.LocalMaximumFrameMs:F0}ms, " +
                $"clientReports={_snapshot.ClientTelemetryPeers}/{_snapshot.PeerCount}, missing={_snapshot.MissingClientReports}, " +
                $"clientFrameMax={_snapshot.MaximumClientFrameMs:F0}ms, reportDelayMax={_snapshot.MaximumClientReportDelayMs:F0}ms, " +
                $"mobs={_snapshot.ActiveNonPlayerCharacters}, incident={_snapshot.ActiveIncidentId}, " +
                $"sched={_snapshot.SchedulerAttemptsPerSecond:F0}/s, budgetStops={_snapshot.SchedulerBudgetStopsPerSecond}/s" +
                FormatPvpSummaryAndReset(_snapshot));
        }

        /// <summary>
        /// Folds one sample window of PvP probes into the periodic log line, which would otherwise
        /// show only the last second of a 15-second interval and miss the spike a fight produces.
        /// </summary>
        private static void AccumulatePvpForLog(DiagnosticsSnapshot snapshot)
        {
            _logRelayHold = LatencySummary.Merge(_logRelayHold, snapshot.RelayHold);
            _logHitForward = LatencySummary.Merge(_logHitForward, snapshot.HitForward);
            _logHitUploadMaximumMs = Math.Max(_logHitUploadMaximumMs, snapshot.MaximumClientHitUploadMs);
            _logServiceGapMaximumMs = Math.Max(_logServiceGapMaximumMs, snapshot.ServiceGapMaximumMs);
            _logZdoQueueRefusals += snapshot.ZdoQueueRefusals;
            _logWorldSaveMaximumMs = Math.Max(_logWorldSaveMaximumMs, snapshot.WorldSaveMs);
        }

        /// <summary>
        /// The PvP probes since the previous log line: relay hold of nearby players' positions,
        /// hit RPC wait toward the target and from the attacker, the longest gap between ZDO sends
        /// to one player, queue refusals and the longest world save. p95 is the worst one-second p95.
        /// </summary>
        private static string FormatPvpSummaryAndReset(DiagnosticsSnapshot snapshot)
        {
            string summary = string.IsNullOrEmpty(snapshot.OnlineBackend)
                ? string.Empty
                : $", backend={snapshot.OnlineBackend}, " +
                  $"relayHold(n/p95/max)={_logRelayHold.Count}/{_logRelayHold.P95Milliseconds:F0}/{_logRelayHold.MaximumMilliseconds:F0}ms, " +
                  $"hitWait(n/p95/max)={_logHitForward.Count}/{_logHitForward.P95Milliseconds:F0}/{_logHitForward.MaximumMilliseconds:F0}ms, " +
                  $"hitUploadMax={_logHitUploadMaximumMs:F0}ms, " +
                  $"serviceGapMax={_logServiceGapMaximumMs:F0}ms, zdoRefused={_logZdoQueueRefusals}" +
                  (_logWorldSaveMaximumMs > 0d ? $", worldSave={_logWorldSaveMaximumMs:F0}ms" : string.Empty);
            _logRelayHold = default;
            _logHitForward = default;
            _logHitUploadMaximumMs = 0d;
            _logServiceGapMaximumMs = 0d;
            _logZdoQueueRefusals = 0;
            _logWorldSaveMaximumMs = 0d;
            return summary;
        }

        private static string FormatQuality(double value)
        {
            return value > 0d && value <= 1d ? value.ToString("F2") : "n/a";
        }

        private static string SanitizePlayerName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "unknown";
            string sanitized = value.Replace('\r', ' ').Replace('\n', ' ').Replace(',', '_').Trim();
            return sanitized.Length <= 32 ? sanitized : sanitized.Substring(0, 32);
        }

        private static void AppendTrigger(ref string target, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            if (string.IsNullOrEmpty(target)) target = value;
            else if (target.Length < 768) target += " | " + value;
        }

        private static void ResetClientContextWindow()
        {
            _clientFocusedThroughoutWindow = Application.isBatchMode || Application.isFocused;
            Player localPlayer = Application.isBatchMode ? null : Player.m_localPlayer;
            _clientPlayerReadyThroughoutWindow = localPlayer != null;
            _clientTeleportingDuringWindow = localPlayer != null && localPlayer.IsTeleporting();
        }

        private static double EffectiveClientReportMissingSeconds()
        {
            return Math.Max(
                _settings.ClientReportMissingSeconds.Value,
                EffectiveClientReportIntervalSeconds() * 2.5d);
        }

        private static double EffectiveClientReportIntervalSeconds()
        {
            return Math.Max(
                0.5d,
                Math.Max(_settings.ClientReportIntervalSeconds.Value, _settings.SampleIntervalSeconds.Value));
        }

        private static string EffectiveModeLabel()
        {
            if (_settings == null || !_settings.Enabled.Value) return "disabled";
            return _active ? "active" : "observe-only";
        }

        private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
        {
            public static readonly ReferenceComparer<T> Instance = new ReferenceComparer<T>();
            public bool Equals(T x, T y) => ReferenceEquals(x, y);
            public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
