using System;
using BepInEx.Configuration;
using AdaptiveNet.Core;
using ServerSync;
using UnityEngine;

namespace AdaptiveNet
{
    internal enum OperatingMode
    {
        Auto,
        Active,
        ObserveOnly
    }

    internal sealed class Settings
    {
        public ConfigEntry<bool> Enabled { get; private set; }
        public ConfigEntry<OperatingMode> Mode { get; private set; }
        public ConfigEntry<float> SampleIntervalSeconds { get; private set; }
        public ConfigEntry<int> MinimumSendRateKiB { get; private set; }
        public ConfigEntry<int> MaximumSendRateKiB { get; private set; }
        public ConfigEntry<int> InitialSendRateKiB { get; private set; }
        public ConfigEntry<int> ServerUploadBudgetMiB { get; private set; }
        public ConfigEntry<int> AdditiveIncreaseKiB { get; private set; }
        public ConfigEntry<float> MultiplicativeDecrease { get; private set; }
        public ConfigEntry<int> HealthySamplesBeforeIncrease { get; private set; }
        public ConfigEntry<float> TargetQueueDelayMs { get; private set; }
        public ConfigEntry<float> CongestedQueueDelayMs { get; private set; }
        public ConfigEntry<float> MinimumConnectionQuality { get; private set; }
        public ConfigEntry<int> PingInflationThresholdMs { get; private set; }
        public ConfigEntry<int> MinimumZdoQueueBudgetKiB { get; private set; }
        public ConfigEntry<int> MaximumZdoQueueBudgetKiB { get; private set; }
        public ConfigEntry<int> MinimumSteamSendBufferKiB { get; private set; }
        public ConfigEntry<int> MaximumSteamSendBufferKiB { get; private set; }
        public ConfigEntry<int> NagleTimeMicroseconds { get; private set; }
        public ConfigEntry<bool> PinnedSteamSend { get; private set; }
        public ConfigEntry<bool> AdaptivePeerScheduler { get; private set; }
        public ConfigEntry<int> GroupRadiusMeters { get; private set; }
        public ConfigEntry<int> GroupMinimumPlayers { get; private set; }
        public ConfigEntry<int> GroupedPeerIntervalMs { get; private set; }
        public ConfigEntry<int> SoloPeerIntervalMs { get; private set; }
        public ConfigEntry<int> ClientToServerIntervalMs { get; private set; }
        public ConfigEntry<int> MaximumPeersPerFrame { get; private set; }
        public ConfigEntry<float> SchedulerCpuBudgetMs { get; private set; }
        public ConfigEntry<bool> DistantPlayerSync { get; private set; }
        public ConfigEntry<int> DistantPlayerSyncRadiusMeters { get; private set; }
        public ConfigEntry<KeyCode> OverlayKey { get; private set; }
        public ConfigEntry<KeyCode> IncidentMarkerKey { get; private set; }
        public ConfigEntry<float> LogIntervalSeconds { get; private set; }
        public ConfigEntry<bool> GroupCapture { get; private set; }
        public ConfigEntry<int> GroupCaptureSeconds { get; private set; }
        public ConfigEntry<float> GroupCaptureTimeoutSeconds { get; private set; }
        public ConfigEntry<int> GroupCaptureMinIntervalSeconds { get; private set; }
        public ConfigEntry<KeyCode> GroupCaptureKey { get; private set; }
        public ConfigEntry<bool> CsvTelemetry { get; private set; }
        public ConfigEntry<bool> IncidentTelemetry { get; private set; }
        public ConfigEntry<float> ClientReportIntervalSeconds { get; private set; }
        public ConfigEntry<int> HistorySeconds { get; private set; }
        public ConfigEntry<int> IncidentPostSeconds { get; private set; }
        public ConfigEntry<int> IncidentCooldownSeconds { get; private set; }
        public ConfigEntry<float> FrameStallThresholdMs { get; private set; }
        public ConfigEntry<float> SevereFrameStallThresholdMs { get; private set; }
        public ConfigEntry<float> ClientReportDelayThresholdMs { get; private set; }
        public ConfigEntry<float> ClientReportMissingSeconds { get; private set; }
        public ConfigEntry<float> OwnershipSampleIntervalSeconds { get; private set; }
        public ConfigEntry<bool> OwnerCensus { get; private set; }
        public ConfigEntry<float> OwnerCensusPeerIntervalSeconds { get; private set; }
        public ConfigEntry<int> OwnerCensusZoneRadius { get; private set; }
        public ConfigEntry<float> OwnerCensusBudgetMsPerSecond { get; private set; }
        public ConfigEntry<float> OwnerCensusMaxScanMs { get; private set; }
        public ConfigEntry<int> OwnerCensusMaxZdosPerScan { get; private set; }
        public ConfigEntry<int> OwnershipDisagreementMinCreatures { get; private set; }
        public ConfigEntry<bool> OwnerEcho { get; private set; }
        public ConfigEntry<float> OwnerEchoIntervalSeconds { get; private set; }
        public ConfigEntry<int> OwnerEchoMaxPeers { get; private set; }
        public ConfigEntry<float> OwnerEchoTimeoutSeconds { get; private set; }
        public ConfigEntry<int> OwnerEchoWindow { get; private set; }
        public ConfigEntry<float> OwnerEchoLagThresholdMs { get; private set; }
        public ConfigEntry<int> OwnerEchoMinMobs { get; private set; }
        public ConfigEntry<int> IncidentFileMaxMiB { get; private set; }
        public ConfigEntry<int> RetainedIncidentFiles { get; private set; }
        public ConfigEntry<int> IncidentRetentionDays { get; private set; }
        public ConfigEntry<float> ServerFrameTargetMs { get; private set; }
        public ConfigEntry<float> ServerOverloadFactor { get; private set; }
        public ConfigEntry<int> ServerOverloadSeconds { get; private set; }
        public ConfigEntry<float> VerdictJitterThresholdMs { get; private set; }
        public ConfigEntry<float> VerdictLossThreshold { get; private set; }
        public ConfigEntry<int> SaturationSeconds { get; private set; }
        public ConfigEntry<int> SaturationPendingFloorKiB { get; private set; }
        public ConfigEntry<int> PendingGrowthKiB { get; private set; }
        public ConfigEntry<float> ChurnFactor { get; private set; }
        public ConfigEntry<int> ChurnFloorPerSecond { get; private set; }
        public ConfigEntry<float> HostSystemCpuPercent { get; private set; }
        public ConfigEntry<float> HostStealPercent { get; private set; }
        public ConfigEntry<int> HostProcessSaturatedMsPerSecond { get; private set; }
        public ConfigEntry<int> HostMinSeconds { get; private set; }

        public static Settings Bind(ConfigFile config, ConfigSync sync)
        {
            // Synced(...) is what the server decides for every process that has AdaptiveNet: the
            // whole network stack, and the diagnostic settings both ends must agree on (a client
            // reporting at another interval than the server expects reads as a late or missing
            // report). Left local: the two keys, the periodic log line and the aggregate CSV,
            // which are each machine's own keyboard, log and disk.
            ConfigEntry<T> Synced<T>(ConfigEntry<T> entry)
            {
                sync.AddConfigEntry(entry).SynchronizedConfig = true;
                return entry;
            }

            var settings = new Settings
            {
                Enabled = Synced(config.Bind("General", "Enabled", true,
                    "Master switch. When false, AdaptiveNet keeps every Valheim limit unchanged.")),
                Mode = Synced(config.Bind("General", "Mode", OperatingMode.Auto,
                    "Auto becomes observe-only when another network overhaul is installed. Active still refuses known conflicts.")),
                SampleIntervalSeconds = Synced(config.Bind("Controller", "SampleIntervalSeconds", 1f,
                    new ConfigDescription("Seconds between connection samples.", new AcceptableValueRange<float>(0.25f, 5f)))),
                MinimumSendRateKiB = Synced(config.Bind("Controller", "MinimumSendRateKiB", 150,
                    new ConfigDescription("Floor of each connection's real Steam send rate. Steam keeps a fixed rate per connection and only clamps it into [minimum, ceiling], so a ceiling cut below this value lowers the rate for good, until the player reconnects. 150 is Valheim's own fixed rate.", new AcceptableValueRange<int>(16, 4096)))),
                MaximumSendRateKiB = Synced(config.Bind("Controller", "MaximumSendRateKiB", 4096,
                    new ConfigDescription("Highest Steam send-rate ceiling.", new AcceptableValueRange<int>(128, 65536)))),
                InitialSendRateKiB = Synced(config.Bind("Controller", "InitialSendRateKiB", 512,
                    new ConfigDescription("Starting send-rate ceiling for each connection.", new AcceptableValueRange<int>(16, 65536)))),
                ServerUploadBudgetMiB = Synced(config.Bind("Controller", "ServerUploadBudgetMiB", 12,
                    new ConfigDescription("Aggregate server upload budget shared fairly by all peers; zero disables this cap.", new AcceptableValueRange<int>(0, 1024)))),
                AdditiveIncreaseKiB = Synced(config.Bind("Controller", "AdditiveIncreaseKiB", 64,
                    new ConfigDescription("Healthy additive increase per probe.", new AcceptableValueRange<int>(1, 4096)))),
                MultiplicativeDecrease = Synced(config.Bind("Controller", "MultiplicativeDecrease", 0.75f,
                    new ConfigDescription("Rate multiplier applied on congestion.", new AcceptableValueRange<float>(0.25f, 0.95f)))),
                HealthySamplesBeforeIncrease = Synced(config.Bind("Controller", "HealthySamplesBeforeIncrease", 3,
                    new ConfigDescription("Consecutive healthy, busy samples before growth.", new AcceptableValueRange<int>(1, 30)))),
                TargetQueueDelayMs = Synced(config.Bind("Latency", "TargetQueueDelayMs", 60f,
                    new ConfigDescription("Desired upper queue-delay window.", new AcceptableValueRange<float>(5f, 500f)))),
                CongestedQueueDelayMs = Synced(config.Bind("Latency", "CongestedQueueDelayMs", 180f,
                    new ConfigDescription("Queue delay that triggers immediate backoff.", new AcceptableValueRange<float>(10f, 2000f)))),
                MinimumConnectionQuality = Synced(config.Bind("Latency", "MinimumConnectionQuality", 0.88f,
                    new ConfigDescription("Steam local quality below this value triggers backoff.", new AcceptableValueRange<float>(0.1f, 1f)))),
                PingInflationThresholdMs = Synced(config.Bind("Latency", "PingInflationThresholdMs", 45,
                    new ConfigDescription("Busy-connection ping increase over baseline that triggers backoff.", new AcceptableValueRange<int>(5, 500)))),
                MinimumZdoQueueBudgetKiB = Synced(config.Bind("ZDO", "MinimumQueueBudgetKiB", 10,
                    new ConfigDescription("Conservative floor; 10 KiB is Valheim's vanilla value.", new AcceptableValueRange<int>(10, 1024)))),
                MaximumZdoQueueBudgetKiB = Synced(config.Bind("ZDO", "MaximumQueueBudgetKiB", 16,
                    new ConfigDescription("Hard upper bound for queued ZDO data per peer.", new AcceptableValueRange<int>(10, 4096)))),
                MinimumSteamSendBufferKiB = Synced(config.Bind("Steam", "MinimumSendBufferKiB", 128,
                    new ConfigDescription("Minimum Steam reliable-send buffer.", new AcceptableValueRange<int>(64, 65536)))),
                MaximumSteamSendBufferKiB = Synced(config.Bind("Steam", "MaximumSendBufferKiB", 2048,
                    new ConfigDescription("Hard cap for the adaptive Steam send buffer.", new AcceptableValueRange<int>(128, 262144)))),
                NagleTimeMicroseconds = Synced(config.Bind("Steam", "NagleTimeMicroseconds", 2500,
                    new ConfigDescription("Small coalescing window. Zero minimizes latency at higher packet overhead.", new AcceptableValueRange<int>(0, 20000)))),
                PinnedSteamSend = Synced(config.Bind("Steam", "PinnedSend", true,
                    "Dedicated Steam server only: send directly from pinned packet memory instead of AllocHGlobal plus Marshal.Copy.")),
                AdaptivePeerScheduler = Synced(config.Bind("Scheduler", "Enabled", true,
                    "Time-slice peer ZDO work and prioritize groups without changing packet formats.")),
                GroupRadiusMeters = Synced(config.Bind("Scheduler", "GroupRadiusMeters", 160,
                    new ConfigDescription("Peers inside this radius count as playing together.", new AcceptableValueRange<int>(20, 1000)))),
                GroupMinimumPlayers = Synced(config.Bind("Scheduler", "GroupMinimumPlayers", 2,
                    new ConfigDescription("Nearby players required for the high-frequency cadence.", new AcceptableValueRange<int>(2, 20)))),
                GroupedPeerIntervalMs = Synced(config.Bind("Scheduler", "GroupedPeerIntervalMs", 100,
                    new ConfigDescription("Target ZDO service interval for grouped peers (100 ms = 10 Hz).", new AcceptableValueRange<int>(25, 1000)))),
                SoloPeerIntervalMs = Synced(config.Bind("Scheduler", "SoloPeerIntervalMs", 250,
                    new ConfigDescription("Target ZDO service interval for dispersed peers.", new AcceptableValueRange<int>(25, 2000)))),
                ClientToServerIntervalMs = Synced(config.Bind("Scheduler", "ClientToServerIntervalMs", 50,
                    new ConfigDescription("Client upload service interval; never use the dispersed-server cadence here.", new AcceptableValueRange<int>(25, 1000)))),
                MaximumPeersPerFrame = Synced(config.Bind("Scheduler", "MaximumPeersPerFrame", 12,
                    new ConfigDescription("Hard cap on peer send attempts in one frame.", new AcceptableValueRange<int>(1, 64)))),
                SchedulerCpuBudgetMs = Synced(config.Bind("Scheduler", "CpuBudgetMs", 0.75f,
                    new ConfigDescription("Main-thread time budget for peer send attempts per frame.", new AcceptableValueRange<float>(0.1f, 20f)))),
                DistantPlayerSync = Synced(config.Bind("Sync", "DistantPlayerSync", true,
                    "Server only: always send the other players' characters inside the radius below, even when the receiver has ten or more nearby objects pending. Vanilla skips the distant zones then, so an enemy 70-150 m away can freeze on screen for many seconds in a busy base.")),
                DistantPlayerSyncRadiusMeters = Synced(config.Bind("Sync", "DistantPlayerSyncRadiusMeters", 160,
                    new ConfigDescription("Players inside this distance from the receiver are always synced.", new AcceptableValueRange<int>(20, 1000)))),
                // 0.5.4: no key by default; Deadheim opens both from ESC > Deadheim > Admin. Renamed
                // (OverlayKey = F8, IncidentMarkerKey = F9) so the keys already in an existing cfg do not come back.
                OverlayKey = config.Bind("Diagnostics", "OverlayShortcut", KeyCode.None,
                    "Toggle the lightweight in-game diagnostics overlay. None = no key (Deadheim: ESC > Deadheim > Admin > Painel de rede)."),
                IncidentMarkerKey = config.Bind("Diagnostics", "IncidentMarkerShortcut", KeyCode.None,
                    "Client key that asks the server to preserve the current diagnostic history. None = no key (Deadheim: ESC > Deadheim > Admin > Marcar incidente de rede)."),
                GroupCapture = Synced(config.Bind("GroupCapture", "GroupCapture", true,
                    "Admin on-demand group capture: the server asks every client with AdaptiveNet for its own last seconds (frame, GC, focus, ping/quality, send rate) and writes them, aligned by UTC second, to group-capture-<date>.csv beside the incident files. Only admins can start one; the server checks the admin list.")),
                GroupCaptureSeconds = Synced(config.Bind("GroupCapture", "Seconds", 60,
                    new ConfigDescription("Seconds of client history requested by default (hard maximum 120).", new AcceptableValueRange<int>(10, 120)))),
                GroupCaptureTimeoutSeconds = Synced(config.Bind("GroupCapture", "TimeoutSeconds", 8f,
                    new ConfigDescription("How long the server waits for the clients' replies; the ones that miss it are listed as timed out.", new AcceptableValueRange<float>(3f, 30f)))),
                GroupCaptureMinIntervalSeconds = Synced(config.Bind("GroupCapture", "MinIntervalSeconds", 120,
                    new ConfigDescription("Minimum seconds between two group captures, server-wide.", new AcceptableValueRange<int>(120, 3600)))),
                GroupCaptureKey = config.Bind("GroupCapture", "Shortcut", KeyCode.None,
                    "Client key that asks the server for a group capture (admins only; the server ignores anyone else). None = no key."),
                LogIntervalSeconds = config.Bind("Diagnostics", "LogIntervalSeconds", 15f,
                    new ConfigDescription("Aggregate telemetry log interval; zero disables periodic lines.", new AcceptableValueRange<float>(0f, 300f))),
                CsvTelemetry = config.Bind("Diagnostics", "CsvTelemetry", false,
                    "Write aggregate samples to BepInEx/AdaptiveNet for A/B comparison."),
                IncidentTelemetry = Synced(config.Bind("Diagnostics", "IncidentTelemetry", true,
                    "Server black box: preserve per-player history when an anomaly or manual marker occurs.")),
                ClientReportIntervalSeconds = Synced(config.Bind("Diagnostics", "ClientReportIntervalSeconds", 1f,
                    new ConfigDescription("Client FPS/GC/upload report interval in seconds.", new AcceptableValueRange<float>(0.5f, 5f)))),
                HistorySeconds = Synced(config.Bind("Diagnostics", "HistorySeconds", 60,
                    new ConfigDescription("Seconds preserved before an incident.", new AcceptableValueRange<int>(15, 180)))),
                IncidentPostSeconds = Synced(config.Bind("Diagnostics", "IncidentPostSeconds", 15,
                    new ConfigDescription("Seconds preserved after an incident.", new AcceptableValueRange<int>(5, 60)))),
                IncidentCooldownSeconds = Synced(config.Bind("Diagnostics", "IncidentCooldownSeconds", 45,
                    new ConfigDescription("Minimum seconds between automatic captures.", new AcceptableValueRange<int>(15, 300)))),
                FrameStallThresholdMs = Synced(config.Bind("Diagnostics", "FrameStallThresholdMs", 250f,
                    new ConfigDescription("Frame duration correlated with GC and reported as a stall.", new AcceptableValueRange<float>(50f, 2000f)))),
                SevereFrameStallThresholdMs = Synced(config.Bind("Diagnostics", "SevereFrameStallThresholdMs", 500f,
                    new ConfigDescription("Frame duration that triggers an incident without a GC collection.", new AcceptableValueRange<float>(100f, 5000f)))),
                ClientReportDelayThresholdMs = Synced(config.Bind("Diagnostics", "ClientReportDelayThresholdMs", 500f,
                    new ConfigDescription("Estimated extra delay of client diagnostic reports that triggers an incident.", new AcceptableValueRange<float>(100f, 10000f)))),
                ClientReportMissingSeconds = Synced(config.Bind("Diagnostics", "ClientReportMissingSeconds", 4f,
                    new ConfigDescription("In-game time (counted from when the player's character exists) without any client report before telemetry is marked missing.", new AcceptableValueRange<float>(2f, 30f)))),
                OwnershipSampleIntervalSeconds = Synced(config.Bind("Diagnostics", "OwnershipSampleIntervalSeconds", 5f,
                    new ConfigDescription("Minimum seconds between the start of two ownership census cycles (one cycle scans every player once).", new AcceptableValueRange<float>(1f, 30f)))),
                OwnerCensus = Synced(config.Bind("Diagnostics", "OwnerCensus", true,
                    "Server only: count who owns (simulates) each creature from the ZDOs in the sectors around every player, without instantiating anything and without scanning the whole world. Feeds the ownership columns of the incident CSV.")),
                OwnerCensusPeerIntervalSeconds = Synced(config.Bind("Diagnostics", "OwnerCensusPeerIntervalSeconds", 1f,
                    new ConfigDescription("Minimum seconds between two single-player scans (one player is scanned per step).", new AcceptableValueRange<float>(0.25f, 30f)))),
                OwnerCensusZoneRadius = Synced(config.Bind("Diagnostics", "OwnerCensusZoneRadius", 1,
                    new ConfigDescription("Zones around each player to scan: 1 = 3x3 zones, 2 = 5x5. Cost grows with the square.", new AcceptableValueRange<int>(0, 3)))),
                OwnerCensusBudgetMsPerSecond = Synced(config.Bind("Diagnostics", "OwnerCensusBudgetMsPerSecond", 1f,
                    new ConfigDescription("Main-thread time the census may use per second; an expensive scan postpones the next one to stay inside it.", new AcceptableValueRange<float>(0.05f, 20f)))),
                OwnerCensusMaxScanMs = Synced(config.Bind("Diagnostics", "OwnerCensusMaxScanMs", 25f,
                    new ConfigDescription("If three scans in a row take longer than this, the census turns itself off with a warning.", new AcceptableValueRange<float>(1f, 500f)))),
                OwnerCensusMaxZdosPerScan = Synced(config.Bind("Diagnostics", "OwnerCensusMaxZdosPerScan", 150000,
                    new ConfigDescription("Hard cap of ZDOs visited by one scan (a scan that hits it is cut and marked truncated).", new AcceptableValueRange<int>(1000, 5000000)))),
                OwnershipDisagreementMinCreatures = Synced(config.Bind("Diagnostics", "OwnershipDisagreementMinCreatures", 4,
                    new ConfigDescription("Minimum difference between the creatures a client says it simulates and what the server counts for it before a (rate-limited) warning is logged.", new AcceptableValueRange<int>(1, 100)))),
                // Owner echo is read on the server only (clients always answer), so none is synced.
                OwnerEcho = config.Bind("Diagnostics", "OwnerEcho", true,
                    "Server only: measure the round trip to the peers that simulate the most creatures near players (a 12-byte RPC echo, at most one per peer per interval, no ZDO scan). Peers without AdaptiveNet do not answer and stay 'not measured'."),
                OwnerEchoIntervalSeconds = config.Bind("Diagnostics", "OwnerEchoIntervalSeconds", 4f,
                    new ConfigDescription("Minimum seconds between two echoes to the same peer.", new AcceptableValueRange<float>(1f, 60f))),
                OwnerEchoMaxPeers = config.Bind("Diagnostics", "OwnerEchoMaxPeers", 4,
                    new ConfigDescription("Peers probed per round: the owners that simulate the most creatures near players.", new AcceptableValueRange<int>(1, 16))),
                OwnerEchoTimeoutSeconds = config.Bind("Diagnostics", "OwnerEchoTimeoutSeconds", 2f,
                    new ConfigDescription("An echo not answered in this time counts as a timeout (a peer that answered before is then counted at this value; one that never answered stays not measured).", new AcceptableValueRange<float>(0.5f, 10f))),
                OwnerEchoWindow = config.Bind("Diagnostics", "OwnerEchoWindow", 8,
                    new ConfigDescription("Last N round trips kept per peer; the median of them is reported.", new AcceptableValueRange<int>(3, 64))),
                IncidentFileMaxMiB = Synced(config.Bind("Diagnostics", "IncidentFileMaxMiB", 16,
                    new ConfigDescription("Rotate the incident CSV at this size.", new AcceptableValueRange<int>(1, 256)))),
                RetainedIncidentFiles = Synced(config.Bind("Diagnostics", "RetainedIncidentFiles", 16,
                    new ConfigDescription("Maximum incident CSV files retained.", new AcceptableValueRange<int>(2, 500)))),
                IncidentRetentionDays = Synced(config.Bind("Diagnostics", "IncidentRetentionDays", 7,
                    new ConfigDescription("Delete AdaptiveNet incident files older than this many days.", new AcceptableValueRange<int>(1, 90)))),
                // Verdict thresholds are read on the server only, so none is synced.
                ServerFrameTargetMs = config.Bind("Verdict", "ServerFrameTargetMs", 50f,
                    new ConfigDescription("Frame time the server should keep. A dedicated server in batchmode settles near 30 FPS (33 ms), well under this.", new AcceptableValueRange<float>(10f, 200f))),
                ServerOverloadFactor = config.Bind("Verdict", "ServerOverloadFactor", 1.5f,
                    new ConfigDescription("The server counts as overloaded (server-overloaded) when its median frame average stays at or above target x this factor.", new AcceptableValueRange<float>(1.1f, 10f))),
                ServerOverloadSeconds = config.Bind("Verdict", "ServerOverloadSeconds", 10,
                    new ConfigDescription("Seconds the median frame average must stay above the overload limit.", new AcceptableValueRange<int>(3, 120))),
                VerdictJitterThresholdMs = config.Bind("Verdict", "JitterThresholdMs", 40f,
                    new ConfigDescription("Mean second-to-second ping change that counts as a bad connection in the verdict.", new AcceptableValueRange<float>(5f, 500f))),
                VerdictLossThreshold = config.Bind("Verdict", "ApproximateLossThreshold", 0.15f,
                    new ConfigDescription("1 - lowest Steam connection quality in the window that counts as a bad connection (an approximation of packet loss).", new AcceptableValueRange<float>(0.02f, 1f))),
                SaturationSeconds = config.Bind("Verdict", "SaturationSeconds", 5,
                    new ConfigDescription("Seconds of output at the Steam rate with bytes pending, or of pending bytes only growing, before link-saturated is named. Unacknowledged bytes never count.", new AcceptableValueRange<int>(2, 60))),
                SaturationPendingFloorKiB = config.Bind("Verdict", "SaturationPendingFloorKiB", 8,
                    new ConfigDescription("Pending KiB (not counting unacknowledged) needed for the link to count as saturated.", new AcceptableValueRange<int>(1, 4096))),
                PendingGrowthKiB = config.Bind("Verdict", "PendingGrowthKiB", 32,
                    new ConfigDescription("KiB the pending bytes must grow by within SaturationSeconds to count as a saturated link.", new AcceptableValueRange<int>(1, 4096))),
                ChurnFactor = config.Bind("Verdict", "ZdoChurnFactor", 3f,
                    new ConfigDescription("ZDOs moved in a second (sent + received) that count as object churn when this many times the median of the previous 60 s.", new AcceptableValueRange<float>(1.5f, 50f))),
                ChurnFloorPerSecond = config.Bind("Verdict", "ZdoChurnFloorPerSecond", 2000,
                    new ConfigDescription("ZDOs per second below which a rise is never called churn, however large the ratio.", new AcceptableValueRange<int>(1, 1000000))),
                OwnerEchoLagThresholdMs = config.Bind("Verdict", "OwnerEchoLagThresholdMs", 200f,
                    new ConfigDescription("mob-owner-lag: median echo round trip to a creature owner at or above this counts as a laggy owner.", new AcceptableValueRange<float>(50f, 2000f))),
                OwnerEchoMinMobs = config.Bind("Verdict", "OwnerEchoMinMobs", 3,
                    new ConfigDescription("mob-owner-lag: creatures the laggy owner must simulate near players for the cause to be named.", new AcceptableValueRange<int>(1, 500))),
                HostSystemCpuPercent = config.Bind("Verdict", "HostSystemCpuPercent", 90f,
                    new ConfigDescription("host-cpu-starved: host CPU use (%) that counts as busy in a second when the server frame was bad and the game process used less than HostProcessSaturatedMsPerSecond.", new AcceptableValueRange<float>(50f, 100f))),
                HostStealPercent = config.Bind("Verdict", "HostStealPercent", 10f,
                    new ConfigDescription("host-cpu-starved: hypervisor steal time (%, Linux) that counts as an oversubscribed host by itself.", new AcceptableValueRange<float>(1f, 100f))),
                HostProcessSaturatedMsPerSecond = config.Bind("Verdict", "HostProcessSaturatedMsPerSecond", 900,
                    new ConfigDescription("Game process CPU (ms per second) from which it counts as saturated (about one core): game-cpu-saturated, and never host-cpu-starved.", new AcceptableValueRange<int>(100, 100000))),
                HostMinSeconds = config.Bind("Verdict", "HostMinSeconds", 2,
                    new ConfigDescription("Seconds of bad server frame with the host condition needed to name host-cpu-starved or game-cpu-saturated.", new AcceptableValueRange<int>(1, 60)))
            };
            return settings;
        }

        public VerdictOptions BuildVerdictOptions()
        {
            return new VerdictOptions
            {
                FrameStallThresholdMs = FrameStallThresholdMs.Value,
                SevereFrameStallThresholdMs = Math.Max(FrameStallThresholdMs.Value, SevereFrameStallThresholdMs.Value),
                DeliveryDelayThresholdMs = ClientReportDelayThresholdMs.Value,
                ServerFrameTargetMs = ServerFrameTargetMs.Value,
                ServerOverloadFactor = ServerOverloadFactor.Value,
                ServerOverloadSeconds = ServerOverloadSeconds.Value,
                JitterThresholdMs = VerdictJitterThresholdMs.Value,
                LossThreshold = VerdictLossThreshold.Value,
                SaturationSeconds = SaturationSeconds.Value,
                SaturationPendingFloorBytes = SaturationPendingFloorKiB.Value * 1024,
                PendingGrowthBytes = PendingGrowthKiB.Value * 1024,
                ChurnFactor = ChurnFactor.Value,
                ChurnFloorPerSecond = ChurnFloorPerSecond.Value,
                OwnerEchoLagThresholdMs = OwnerEchoLagThresholdMs.Value,
                OwnerEchoMinMobs = OwnerEchoMinMobs.Value,
                HostSystemCpuPercent = HostSystemCpuPercent.Value,
                HostStealPercent = HostStealPercent.Value,
                HostProcessSaturatedMsPerSecond = HostProcessSaturatedMsPerSecond.Value,
                HostMinSeconds = HostMinSeconds.Value
            };
        }

        public AdaptiveControllerOptions BuildControllerOptions()
        {
            var options = new AdaptiveControllerOptions
            {
                MinimumSendRateBytesPerSecond = MinimumSendRateKiB.Value * 1024,
                MaximumSendRateBytesPerSecond = MaximumSendRateKiB.Value * 1024,
                InitialSendRateBytesPerSecond = InitialSendRateKiB.Value * 1024,
                AdditiveIncreaseBytesPerSecond = AdditiveIncreaseKiB.Value * 1024,
                MultiplicativeDecreaseFactor = MultiplicativeDecrease.Value,
                HealthySamplesBeforeIncrease = HealthySamplesBeforeIncrease.Value,
                TargetQueueDelayMilliseconds = TargetQueueDelayMs.Value,
                CongestedQueueDelayMilliseconds = CongestedQueueDelayMs.Value,
                MinimumConnectionQuality = MinimumConnectionQuality.Value,
                PingInflationThresholdMilliseconds = PingInflationThresholdMs.Value,
                MinimumZdoQueueBudgetBytes = MinimumZdoQueueBudgetKiB.Value * 1024,
                MaximumZdoQueueBudgetBytes = MaximumZdoQueueBudgetKiB.Value * 1024
            };
            options.Validate();
            return options;
        }

        public PeerSchedulerOptions BuildSchedulerOptions()
        {
            var options = new PeerSchedulerOptions
            {
                GroupRadiusMeters = GroupRadiusMeters.Value,
                GroupMinimumPlayers = GroupMinimumPlayers.Value,
                GroupedPeerIntervalMilliseconds = GroupedPeerIntervalMs.Value,
                SoloPeerIntervalMilliseconds = SoloPeerIntervalMs.Value,
                ClientToServerIntervalMilliseconds = ClientToServerIntervalMs.Value,
                MaximumPeersPerFrame = MaximumPeersPerFrame.Value,
                CpuBudgetMilliseconds = SchedulerCpuBudgetMs.Value
            };
            options.Validate();
            return options;
        }
    }
}
