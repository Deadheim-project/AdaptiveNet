using System;
using System.Globalization;
using System.IO;
using BepInEx;
using BepInEx.Logging;

namespace AdaptiveNet
{
    internal sealed class CsvTelemetry : IDisposable
    {
        private const string Header = "utc,session_seconds,mode,peers,network_sample_peers,ping_avg_ms,ping_p95_ms,queued_bytes,queue_max_ms,rate_avg_kib_s,zdo_avg_kib,congested_peers,grouped_peers,scheduler_attempts_s,scheduler_budget_stops_s,local_fps,local_frame_p95_ms,local_frame_max_ms,local_gc_collections,local_gc_correlated_frame_ms,client_telemetry_peers,missing_client_reports,client_frame_max_ms,client_report_delay_max_ms,active_nonplayer_characters,unowned_nonplayer_characters,active_incident_id,dropped_incident_jobs,send_rate_avg_kib_s,send_rate_min_kib_s," +
            "online_backend,relay_hold_samples,relay_hold_p95_ms,relay_hold_max_ms,hit_forward_count,hit_forward_p95_ms,hit_forward_max_ms," +
            "client_hit_upload_max_ms,zdo_service_gap_max_ms,zdo_queue_refusals,world_save_ms";
        private readonly StreamWriter _writer;
        private readonly ManualLogSource _log;
        private int _rowsSinceFlush;
        private bool _disabled;

        internal static string HeaderForTesting => Header;

        public CsvTelemetry(ManualLogSource log)
        {
            _log = log;
            string directory = Path.Combine(Paths.BepInExRootPath, "AdaptiveNet");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"telemetry-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
            _writer = new StreamWriter(path, false);
            _writer.WriteLine(Header);
            _writer.Flush();
            log.LogInfo($"CSV telemetry: {path}");
        }

        public void Write(double sessionSeconds, DiagnosticsSnapshot snapshot)
        {
            if (_writer == null || snapshot == null)
            {
                return;
            }

            if (_disabled) return;
            try
            {
                string row = FormatRow(sessionSeconds, snapshot);
                _writer.WriteLine(row);
                _rowsSinceFlush++;
                if (_rowsSinceFlush >= 10)
                {
                    _writer.Flush();
                    _rowsSinceFlush = 0;
                }
            }
            catch (Exception exception)
            {
                _disabled = true;
                _log?.LogWarning(
                    $"Aggregate CSV write failed and was disabled safely: {exception.GetType().Name}: {exception.Message}");
            }
        }

        internal static string FormatForTesting(double sessionSeconds, DiagnosticsSnapshot snapshot)
        {
            return FormatRow(sessionSeconds, snapshot);
        }

        private static string FormatRow(double sessionSeconds, DiagnosticsSnapshot snapshot)
        {
            return string.Join(",",
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                sessionSeconds.ToString("F3", CultureInfo.InvariantCulture),
                Escape(snapshot.EffectiveMode),
                snapshot.PeerCount.ToString(CultureInfo.InvariantCulture),
                snapshot.NetworkSamplePeers.ToString(CultureInfo.InvariantCulture),
                snapshot.AveragePingMs.ToString("F3", CultureInfo.InvariantCulture),
                snapshot.P95PingMs.ToString("F3", CultureInfo.InvariantCulture),
                snapshot.TotalQueuedBytes.ToString(CultureInfo.InvariantCulture),
                snapshot.MaximumQueueDelayMs.ToString("F3", CultureInfo.InvariantCulture),
                snapshot.AverageRateLimitKiB.ToString("F3", CultureInfo.InvariantCulture),
                snapshot.AverageZdoBudgetKiB.ToString("F3", CultureInfo.InvariantCulture),
                snapshot.CongestedPeers.ToString(CultureInfo.InvariantCulture),
                snapshot.GroupedPeers.ToString(CultureInfo.InvariantCulture),
                snapshot.SchedulerAttemptsPerSecond.ToString("F3", CultureInfo.InvariantCulture),
                snapshot.SchedulerBudgetStopsPerSecond.ToString(CultureInfo.InvariantCulture),
                snapshot.LocalAverageFps.ToString("F3", CultureInfo.InvariantCulture),
                snapshot.LocalP95FrameMs.ToString("F3", CultureInfo.InvariantCulture),
                snapshot.LocalMaximumFrameMs.ToString("F3", CultureInfo.InvariantCulture),
                snapshot.LocalGcCollections.ToString(CultureInfo.InvariantCulture),
                snapshot.LocalGcCorrelatedFrameMs.ToString("F3", CultureInfo.InvariantCulture),
                snapshot.ClientTelemetryPeers.ToString(CultureInfo.InvariantCulture),
                snapshot.MissingClientReports.ToString(CultureInfo.InvariantCulture),
                snapshot.MaximumClientFrameMs.ToString("F3", CultureInfo.InvariantCulture),
                snapshot.MaximumClientReportDelayMs.ToString("F3", CultureInfo.InvariantCulture),
                snapshot.ActiveNonPlayerCharacters.ToString(CultureInfo.InvariantCulture),
                snapshot.UnownedNonPlayerCharacters.ToString(CultureInfo.InvariantCulture),
                snapshot.ActiveIncidentId.ToString(CultureInfo.InvariantCulture),
                snapshot.DroppedIncidentJobs.ToString(CultureInfo.InvariantCulture),
                snapshot.AverageTransportRateKiB.ToString("F3", CultureInfo.InvariantCulture),
                snapshot.MinimumTransportRateKiB.ToString("F3", CultureInfo.InvariantCulture),
                Escape(snapshot.OnlineBackend),
                snapshot.RelayHold.Count.ToString(CultureInfo.InvariantCulture),
                snapshot.RelayHold.P95Milliseconds.ToString("F3", CultureInfo.InvariantCulture),
                snapshot.RelayHold.MaximumMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
                snapshot.HitForward.Count.ToString(CultureInfo.InvariantCulture),
                snapshot.HitForward.P95Milliseconds.ToString("F3", CultureInfo.InvariantCulture),
                snapshot.HitForward.MaximumMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
                snapshot.MaximumClientHitUploadMs.ToString("F3", CultureInfo.InvariantCulture),
                snapshot.ServiceGapMaximumMs.ToString("F3", CultureInfo.InvariantCulture),
                snapshot.ZdoQueueRefusals.ToString(CultureInfo.InvariantCulture),
                snapshot.WorldSaveMs.ToString("F3", CultureInfo.InvariantCulture));
        }

        public void Dispose()
        {
            _writer?.Dispose();
        }

        private static string Escape(string value)
        {
            return '"' + (value ?? string.Empty).Replace("\"", "\"\"") + '"';
        }
    }
}
