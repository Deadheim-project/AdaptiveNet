using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using AdaptiveNet.Core;
using BepInEx;
using BepInEx.Logging;

namespace AdaptiveNet
{
    internal readonly struct PeerTelemetryPoint
    {
        public PeerTelemetryPoint(
            long utcTicks,
            double sessionSeconds,
            string playerName,
            long peerUid,
            NetworkSample serverNetwork,
            AdaptiveDecision decision,
            int appliedRateBytesPerSecond,
            int nearbyPlayers,
            CharacterOwnershipSnapshot ownership,
            RemoteClientTelemetrySnapshot client,
            RuntimeHealthSample serverHealth,
            int activeNonPlayerCharacters,
            int unownedNonPlayerCharacters,
            int serverOwnedNonPlayerCharacters,
            PvpPeerStats pvp = default,
            double serverWorldSaveMilliseconds = 0d)
        {
            UtcTicks = utcTicks;
            SessionSeconds = sessionSeconds;
            PlayerName = playerName ?? "unknown";
            PeerUid = peerUid;
            ServerNetwork = serverNetwork;
            Decision = decision;
            AppliedRateBytesPerSecond = Math.Max(0, appliedRateBytesPerSecond);
            NearbyPlayers = Math.Max(0, nearbyPlayers);
            Ownership = ownership;
            Client = client;
            ServerHealth = serverHealth;
            ActiveNonPlayerCharacters = Math.Max(0, activeNonPlayerCharacters);
            UnownedNonPlayerCharacters = Math.Max(0, unownedNonPlayerCharacters);
            ServerOwnedNonPlayerCharacters = Math.Max(0, serverOwnedNonPlayerCharacters);
            Pvp = pvp;
            ServerWorldSaveMilliseconds = Math.Max(0d, serverWorldSaveMilliseconds);
        }

        public long UtcTicks { get; }
        public double SessionSeconds { get; }
        public string PlayerName { get; }
        public long PeerUid { get; }
        public NetworkSample ServerNetwork { get; }
        public AdaptiveDecision Decision { get; }
        public int AppliedRateBytesPerSecond { get; }
        public int NearbyPlayers { get; }
        public CharacterOwnershipSnapshot Ownership { get; }
        public RemoteClientTelemetrySnapshot Client { get; }
        public RuntimeHealthSample ServerHealth { get; }
        public int ActiveNonPlayerCharacters { get; }
        public int UnownedNonPlayerCharacters { get; }
        public int ServerOwnedNonPlayerCharacters { get; }
        public PvpPeerStats Pvp { get; }
        public double ServerWorldSaveMilliseconds { get; }
    }

    internal sealed class IncidentTelemetry : IDisposable
    {
        private const string Header =
            "utc,session_seconds,incident_id,phase,trigger,player,uid," +
            "server_sample_valid,server_ping_ms,server_quality_local,server_quality_remote,server_managed_queue_bytes," +
            "server_pending_reliable_bytes,server_pending_unreliable_bytes,server_unacked_bytes," +
            "server_queue_delay_ms,server_transport_rate_bps,server_out_bps,server_in_bps," +
            "server_out_packets_s,server_in_packets_s,adaptive_rate_limit_bps,zdo_budget_bytes," +
            "congested,decision_reason,nearby_players,owned_nonplayer_characters,owned_nonplayer_count_change," +
            "ownership_transfers_since_previous_scan,ownership_scan_id,client_present,client_fresh,client_reports_merged,client_sequence,client_focused," +
            "client_player_ready,client_teleporting,client_delivery_delay_ms,client_report_age_seconds,client_network_valid,client_ping_ms," +
            "client_quality_local,client_quality_remote,client_managed_queue_bytes," +
            "client_pending_reliable_bytes,client_pending_unreliable_bytes,client_unacked_bytes," +
            "client_queue_delay_ms,client_transport_rate_bps,client_out_bps,client_in_bps," +
            "client_out_packets_s,client_in_packets_s,client_frame_avg_ms,client_frame_p95_ms," +
            "client_frame_max_ms,client_stall_frames,client_severe_stall_frames,client_gc0,client_gc1," +
            "client_gc2,client_gc_correlated_frame_ms,client_managed_memory_bytes,server_frame_avg_ms," +
            "server_frame_p95_ms,server_frame_max_ms,server_stall_frames,server_severe_stall_frames," +
            "server_gc0,server_gc1,server_gc2,server_gc_correlated_frame_ms,server_managed_memory_bytes," +
            "active_nonplayer_characters,unowned_nonplayer_characters,server_owned_nonplayer_characters," +
            // PvP probes, appended so earlier column positions stay put.
            "relay_hold_samples,relay_hold_avg_ms,relay_hold_p95_ms,relay_hold_max_ms," +
            "hit_forward_count,hit_forward_avg_ms,hit_forward_max_ms," +
            "zdo_service_calls,zdo_service_gap_max_ms,zdo_queue_refusals," +
            "client_hit_upload_count,client_hit_upload_avg_ms,client_hit_upload_max_ms,server_world_save_ms";

        internal static string HeaderForTesting => Header;
        internal static string FormatForTesting(
            int incidentId,
            string phase,
            string trigger,
            PeerTelemetryPoint point) => FormatRow(incidentId, phase, trigger, point);

        private sealed class WriteJob
        {
            public int IncidentId;
            public string Phase;
            public string Trigger;
            public PeerTelemetryPoint[] Points;
            public bool Flush;
        }

        private readonly ManualLogSource _log;
        private readonly string _directory;
        // Set by the main thread (constructor and cfg reload), read by the writer thread at its
        // next rotation or prune. Kept as ints so each read is a single volatile load.
        private volatile int _maximumFileMiB;
        private volatile int _retainedFiles;
        private volatile int _retentionDays;
        private readonly BlockingCollection<WriteJob> _jobs = new BlockingCollection<WriteJob>(8);
        private readonly Thread _worker;
        private readonly string _sessionStamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        private StreamWriter _writer;
        private string _currentPath;
        private long _estimatedBytes;
        private int _part;
        private int _droppedJobs;
        private bool _disposed;
        private volatile bool _disabled;

        public IncidentTelemetry(Settings settings, ManualLogSource log)
        {
            _log = log;
            _directory = Path.Combine(Paths.BepInExRootPath, "AdaptiveNet", "incidents");
            ApplyLimits(settings);
            Directory.CreateDirectory(_directory);
            PruneFiles(Math.Max(0, _retainedFiles - 1));
            _worker = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "AdaptiveNet incident writer"
            };
            _worker.Start();
        }

        /// <summary>
        /// File size, file count and age limits, re-read when the cfg changes. They take effect
        /// at the next rotation; restarting the writer instead would block the main thread on
        /// Dispose for up to two seconds.
        /// </summary>
        public void ApplyLimits(Settings settings)
        {
            _maximumFileMiB = Math.Max(1, settings.IncidentFileMaxMiB.Value);
            _retainedFiles = Math.Max(1, settings.RetainedIncidentFiles.Value);
            _retentionDays = Math.Max(1, settings.IncidentRetentionDays.Value);
        }

        private long MaximumFileBytes => _maximumFileMiB * 1024L * 1024L;

        public int DroppedJobs => _droppedJobs;
        public bool Available => !_disabled && !_disposed;

        public void QueueHistory(
            int incidentId,
            string phase,
            string trigger,
            CircularHistory<PeerTelemetryPoint> history,
            bool flush)
        {
            if (!Available || history == null || history.Count == 0) return;
            var points = new List<PeerTelemetryPoint>(history.Count);
            history.CopyOrderedTo(points);
            Queue(new WriteJob
            {
                IncidentId = incidentId,
                Phase = phase,
                Trigger = trigger,
                Points = points.ToArray(),
                Flush = flush
            });
        }

        public void QueueHistories(
            int incidentId,
            string phase,
            string trigger,
            IEnumerable<CircularHistory<PeerTelemetryPoint>> histories,
            bool flush)
        {
            if (!Available || histories == null) return;
            var points = new List<PeerTelemetryPoint>();
            foreach (CircularHistory<PeerTelemetryPoint> history in histories)
            {
                history?.CopyOrderedTo(points);
            }
            if (points.Count == 0) return;
            points.Sort((left, right) =>
            {
                int byTime = left.SessionSeconds.CompareTo(right.SessionSeconds);
                return byTime != 0 ? byTime : left.PeerUid.CompareTo(right.PeerUid);
            });
            Queue(new WriteJob
            {
                IncidentId = incidentId,
                Phase = phase,
                Trigger = trigger,
                Points = points.ToArray(),
                Flush = flush
            });
        }

        public void QueuePoints(
            int incidentId,
            string phase,
            string trigger,
            PeerTelemetryPoint[] points,
            bool flush)
        {
            if (!Available || points == null || points.Length == 0) return;
            Queue(new WriteJob
            {
                IncidentId = incidentId,
                Phase = phase,
                Trigger = trigger,
                Points = points,
                Flush = flush
            });
        }

        public void QueuePoint(int incidentId, string phase, string trigger, PeerTelemetryPoint point, bool flush)
        {
            if (!Available) return;
            Queue(new WriteJob
            {
                IncidentId = incidentId,
                Phase = phase,
                Trigger = trigger,
                Points = new[] { point },
                Flush = flush
            });
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _jobs.CompleteAdding();
            bool stopped = _worker.Join(2000);
            if (!stopped)
            {
                _log?.LogWarning("Incident writer did not finish within two seconds; shutdown will continue.");
            }
            else
            {
                _jobs.Dispose();
            }
        }

        private void Queue(WriteJob job)
        {
            if (_jobs.TryAdd(job)) return;
            _droppedJobs++;
            if (_droppedJobs == 1 || _droppedJobs % 10 == 0)
            {
                _log?.LogWarning($"Incident writer queue full; dropped diagnostic job count={_droppedJobs}.");
            }
        }

        private void WriterLoop()
        {
            try
            {
                foreach (WriteJob job in _jobs.GetConsumingEnumerable())
                {
                    EnsureWriter();
                    for (int index = 0; index < job.Points.Length; index++)
                    {
                        string row = FormatRow(job.IncidentId, job.Phase, job.Trigger, job.Points[index]);
                        int rowBytes = Encoding.UTF8.GetByteCount(row) + 2;
                        RotateIfNeeded(rowBytes);
                        _writer.WriteLine(row);
                        _estimatedBytes += rowBytes;
                    }
                    if (job.Flush) _writer.Flush();
                }
            }
            catch (ThreadAbortException)
            {
                // Mono aborts background threads when the server shuts down; reporting that as an
                // I/O failure sent the last line of every restart looking for a disk problem.
            }
            catch (Exception exception)
            {
                _disabled = true;
                _log?.LogWarning($"Incident telemetry disabled after I/O failure: {exception.GetType().Name}: {exception.Message}");
            }
            finally
            {
                _writer?.Dispose();
                _writer = null;
            }
        }

        private void EnsureWriter()
        {
            if (_writer != null) return;
            _part++;
            _currentPath = Path.Combine(_directory, $"incidents-{_sessionStamp}-part{_part:D2}.csv");
            _writer = new StreamWriter(_currentPath, false, new UTF8Encoding(false));
            _writer.WriteLine(Header);
            _writer.Flush();
            _estimatedBytes = Encoding.UTF8.GetByteCount(Header) + 2;
            _log?.LogInfo($"Incident telemetry: {_currentPath}");
        }

        private void RotateIfNeeded(int nextRowCharacters)
        {
            if (_writer == null || _estimatedBytes + nextRowCharacters <= MaximumFileBytes) return;
            _writer.Dispose();
            _writer = null;
            PruneFiles(Math.Max(0, _retainedFiles - 1));
            EnsureWriter();
        }

        private void PruneFiles(int maximumExistingFiles)
        {
            try
            {
                DateTime cutoff = DateTime.UtcNow.AddDays(-_retentionDays);
                FileInfo[] files = new DirectoryInfo(_directory)
                    .GetFiles("incidents-*.csv", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .ToArray();
                for (int index = 0; index < files.Length; index++)
                {
                    if (files[index].LastWriteTimeUtc < cutoff || index >= maximumExistingFiles)
                    {
                        files[index].Delete();
                    }
                }
            }
            catch (Exception exception)
            {
                _log?.LogWarning($"Incident retention cleanup skipped: {exception.GetType().Name}: {exception.Message}");
            }
        }

        private static string FormatRow(int incidentId, string phase, string trigger, PeerTelemetryPoint point)
        {
            NetworkSample server = point.ServerNetwork;
            RemoteClientTelemetrySnapshot client = point.Client;
            NetworkSample upload = client.UploadNetwork;
            RuntimeHealthSample clientHealth = client.Health;
            RuntimeHealthSample serverHealth = point.ServerHealth;
            PvpPeerStats pvp = point.Pvp;
            LatencySummary hitUpload = client.HitUpload;
            return string.Join(",",
                new DateTime(point.UtcTicks, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture),
                F(point.SessionSeconds),
                incidentId.ToString(CultureInfo.InvariantCulture),
                Escape(phase),
                Escape(trigger),
                Escape(point.PlayerName),
                point.PeerUid.ToString(CultureInfo.InvariantCulture),
                server.Valid ? "1" : "0",
                server.PingMilliseconds.ToString(CultureInfo.InvariantCulture),
                F(server.LocalQuality), F(server.RemoteQuality),
                server.ManagedQueueBytes.ToString(CultureInfo.InvariantCulture),
                server.PendingReliableBytes.ToString(CultureInfo.InvariantCulture),
                server.PendingUnreliableBytes.ToString(CultureInfo.InvariantCulture),
                server.UnackedBytes.ToString(CultureInfo.InvariantCulture),
                F(server.QueueDelayMilliseconds),
                server.TransportSendRateBytesPerSecond.ToString(CultureInfo.InvariantCulture),
                F(server.OutgoingBytesPerSecond), F(server.IncomingBytesPerSecond),
                F(server.OutgoingPacketsPerSecond), F(server.IncomingPacketsPerSecond),
                point.AppliedRateBytesPerSecond.ToString(CultureInfo.InvariantCulture),
                point.Decision.ZdoQueueBudgetBytes.ToString(CultureInfo.InvariantCulture),
                point.Decision.Congested ? "1" : "0",
                Escape(point.Decision.Reason),
                point.NearbyPlayers.ToString(CultureInfo.InvariantCulture),
                point.Ownership.OwnedNonPlayerCharacters.ToString(CultureInfo.InvariantCulture),
                point.Ownership.ChangeSincePreviousScan.ToString(CultureInfo.InvariantCulture),
                point.Ownership.TransfersSincePreviousScan.ToString(CultureInfo.InvariantCulture),
                point.Ownership.ScanId.ToString(CultureInfo.InvariantCulture),
                client.Available ? "1" : "0",
                client.Fresh ? "1" : "0",
                client.ReportsMerged.ToString(CultureInfo.InvariantCulture),
                client.Sequence.ToString(CultureInfo.InvariantCulture),
                client.Focused ? "1" : "0",
                client.PlayerReady ? "1" : "0",
                client.Teleporting ? "1" : "0",
                F(client.DeliveryDelayMilliseconds),
                F(client.ReportAgeSeconds),
                upload.Valid ? "1" : "0",
                upload.PingMilliseconds.ToString(CultureInfo.InvariantCulture),
                F(upload.LocalQuality), F(upload.RemoteQuality),
                upload.ManagedQueueBytes.ToString(CultureInfo.InvariantCulture),
                upload.PendingReliableBytes.ToString(CultureInfo.InvariantCulture),
                upload.PendingUnreliableBytes.ToString(CultureInfo.InvariantCulture),
                upload.UnackedBytes.ToString(CultureInfo.InvariantCulture),
                F(upload.QueueDelayMilliseconds),
                upload.TransportSendRateBytesPerSecond.ToString(CultureInfo.InvariantCulture),
                F(upload.OutgoingBytesPerSecond), F(upload.IncomingBytesPerSecond),
                F(upload.OutgoingPacketsPerSecond), F(upload.IncomingPacketsPerSecond),
                F(clientHealth.AverageFrameMilliseconds), F(clientHealth.P95FrameMilliseconds),
                F(clientHealth.MaximumFrameMilliseconds),
                clientHealth.StallFrames.ToString(CultureInfo.InvariantCulture),
                clientHealth.SevereStallFrames.ToString(CultureInfo.InvariantCulture),
                clientHealth.Gen0Collections.ToString(CultureInfo.InvariantCulture),
                clientHealth.Gen1Collections.ToString(CultureInfo.InvariantCulture),
                clientHealth.Gen2Collections.ToString(CultureInfo.InvariantCulture),
                F(clientHealth.GcCorrelatedStallMilliseconds),
                clientHealth.ManagedMemoryBytes.ToString(CultureInfo.InvariantCulture),
                F(serverHealth.AverageFrameMilliseconds), F(serverHealth.P95FrameMilliseconds),
                F(serverHealth.MaximumFrameMilliseconds),
                serverHealth.StallFrames.ToString(CultureInfo.InvariantCulture),
                serverHealth.SevereStallFrames.ToString(CultureInfo.InvariantCulture),
                serverHealth.Gen0Collections.ToString(CultureInfo.InvariantCulture),
                serverHealth.Gen1Collections.ToString(CultureInfo.InvariantCulture),
                serverHealth.Gen2Collections.ToString(CultureInfo.InvariantCulture),
                F(serverHealth.GcCorrelatedStallMilliseconds),
                serverHealth.ManagedMemoryBytes.ToString(CultureInfo.InvariantCulture),
                point.ActiveNonPlayerCharacters.ToString(CultureInfo.InvariantCulture),
                point.UnownedNonPlayerCharacters.ToString(CultureInfo.InvariantCulture),
                point.ServerOwnedNonPlayerCharacters.ToString(CultureInfo.InvariantCulture),
                pvp.RelayHold.Count.ToString(CultureInfo.InvariantCulture),
                F(pvp.RelayHold.AverageMilliseconds), F(pvp.RelayHold.P95Milliseconds), F(pvp.RelayHold.MaximumMilliseconds),
                pvp.HitForward.Count.ToString(CultureInfo.InvariantCulture),
                F(pvp.HitForward.AverageMilliseconds), F(pvp.HitForward.MaximumMilliseconds),
                pvp.ServiceCalls.ToString(CultureInfo.InvariantCulture),
                F(pvp.ServiceGapMaximumMilliseconds),
                pvp.QueueRefusals.ToString(CultureInfo.InvariantCulture),
                hitUpload.Count.ToString(CultureInfo.InvariantCulture),
                F(hitUpload.AverageMilliseconds), F(hitUpload.MaximumMilliseconds),
                F(point.ServerWorldSaveMilliseconds));
        }

        private static string F(double value) => value.ToString("F3", CultureInfo.InvariantCulture);
        private static string Escape(string value) => '"' + (value ?? string.Empty).Replace("\"", "\"\"") + '"';
    }
}
