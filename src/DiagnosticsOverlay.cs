using UnityEngine;

namespace AdaptiveNet
{
    internal static class DiagnosticsOverlay
    {
        private static DiagnosticsSnapshot _snapshot = new DiagnosticsSnapshot();
        private static bool _visible;

        public static void SetSnapshot(DiagnosticsSnapshot snapshot)
        {
            _snapshot = snapshot ?? new DiagnosticsSnapshot();
        }

        public static void ToggleIfRequested(KeyCode key)
        {
            if (!Application.isBatchMode && Input.GetKeyDown(key))
            {
                _visible = !_visible;
            }
        }

        public static void Draw()
        {
            if (!_visible || Application.isBatchMode)
            {
                return;
            }

            DiagnosticsSnapshot value = _snapshot;
            var bounds = new Rect(18f, 90f, 500f, value.Conflicts.Length > 0 ? 299f : 279f);
            GUI.Box(bounds, "AdaptiveNet");
            float y = bounds.y + 28f;
            DrawLine(ref y, $"Mode: {value.EffectiveMode}   Peers: {value.PeerCount}   Congested: {value.CongestedPeers}");
            DrawLine(ref y, $"Ping avg/p95: {value.AveragePingMs:F0}/{value.P95PingMs:F0} ms");
            DrawLine(ref y, $"Queue: {value.TotalQueuedBytes / 1024d:F1} KiB   max delay: {value.MaximumQueueDelayMs:F1} ms");
            DrawLine(ref y, $"Send rate avg/min: {value.AverageTransportRateKiB:F0}/{value.MinimumTransportRateKiB:F0} KiB/s   cap avg: {value.AverageRateLimitKiB:F0}");
            DrawLine(ref y, $"ZDO budget avg: {value.AverageZdoBudgetKiB:F1} KiB");
            DrawLine(ref y, $"Grouped: {value.GroupedPeers}   scheduler: {value.SchedulerAttemptsPerSecond:F0} attempts/s");
            DrawLine(ref y, $"Local FPS: {value.LocalAverageFps:F0}   frame p95/max: {value.LocalP95FrameMs:F0}/{value.LocalMaximumFrameMs:F0} ms");
            DrawLine(ref y, $"Client reports: {value.ClientTelemetryPeers}/{value.PeerCount}   missing: {value.MissingClientReports}");
            DrawLine(ref y, $"Client frame/report delay max: {value.MaximumClientFrameMs:F0}/{value.MaximumClientReportDelayMs:F0} ms");
            DrawLine(ref y, $"Active mobs: {value.ActiveNonPlayerCharacters}   incident: {value.ActiveIncidentId}");
            DrawLine(ref y, string.IsNullOrEmpty(value.OnlineBackend)
                ? $"My hit upload wait avg/max: {value.LocalHitUpload.AverageMilliseconds:F0}/{value.LocalHitUpload.MaximumMilliseconds:F0} ms ({value.LocalHitUpload.Count} hits)"
                : $"PvP relay p95/max: {value.RelayHold.P95Milliseconds:F0}/{value.RelayHold.MaximumMilliseconds:F0} ms   hit wait max: {value.HitForward.MaximumMilliseconds:F0} ms");
            DrawLine(ref y, value.ZdoPatchApplied ? "ZDO patch: verified" : "ZDO patch: safe fallback");
            if (value.Conflicts.Length > 0)
            {
                DrawLine(ref y, $"Observer because: {value.Conflicts}");
            }
        }

        private static void DrawLine(ref float y, string text)
        {
            GUI.Label(new Rect(32f, y, 465f, 22f), text);
            y += 21f;
        }
    }
}
