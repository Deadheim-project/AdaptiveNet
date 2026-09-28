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
        public ConfigEntry<KeyCode> OverlayKey { get; private set; }
        public ConfigEntry<KeyCode> IncidentMarkerKey { get; private set; }
        public ConfigEntry<float> LogIntervalSeconds { get; private set; }
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
        public ConfigEntry<int> IncidentFileMaxMiB { get; private set; }
        public ConfigEntry<int> RetainedIncidentFiles { get; private set; }
        public ConfigEntry<int> IncidentRetentionDays { get; private set; }

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
                MinimumSendRateKiB = Synced(config.Bind("Controller", "MinimumSendRateKiB", 64,
                    new ConfigDescription("Lowest Steam send-rate ceiling.", new AcceptableValueRange<int>(16, 4096)))),
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
                OverlayKey = config.Bind("Diagnostics", "OverlayKey", KeyCode.F8,
                    "Toggle the lightweight in-game diagnostics overlay."),
                IncidentMarkerKey = config.Bind("Diagnostics", "IncidentMarkerKey", KeyCode.F9,
                    "Client key that asks the server to preserve the current diagnostic history."),
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
                    new ConfigDescription("Connected time without a client report before telemetry is marked missing.", new AcceptableValueRange<float>(2f, 30f)))),
                OwnershipSampleIntervalSeconds = Synced(config.Bind("Diagnostics", "OwnershipSampleIntervalSeconds", 5f,
                    new ConfigDescription("Interval for counting active character/mob ownership.", new AcceptableValueRange<float>(1f, 30f)))),
                IncidentFileMaxMiB = Synced(config.Bind("Diagnostics", "IncidentFileMaxMiB", 16,
                    new ConfigDescription("Rotate the incident CSV at this size.", new AcceptableValueRange<int>(1, 256)))),
                RetainedIncidentFiles = Synced(config.Bind("Diagnostics", "RetainedIncidentFiles", 16,
                    new ConfigDescription("Maximum incident CSV files retained.", new AcceptableValueRange<int>(2, 500)))),
                IncidentRetentionDays = Synced(config.Bind("Diagnostics", "IncidentRetentionDays", 7,
                    new ConfigDescription("Delete AdaptiveNet incident files older than this many days.", new AcceptableValueRange<int>(1, 90))))
            };
            return settings;
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
