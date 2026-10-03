using System;
using AdaptiveNet;
using AdaptiveNet.Core;

var health = new RuntimeHealthSample(
    1d,
    60,
    16.5d,
    25d,
    310d,
    1,
    0,
    2,
    1,
    0,
    310d,
    123456789L);
var network = new NetworkSample(
    true,
    73,
    0.97d,
    0.91d,
    1024,
    2048,
    512,
    4096,
    88.5d,
    600000,
    120000d,
    90000d,
    120d,
    100d);
var source = new ClientTelemetryReport(
    42L,
    123.5d,
    true,
    true,
    false,
    health,
    network);

ZPackage package = source.Serialize();
package.SetPos(0);
Assert.True(ClientTelemetryReport.TryDeserialize(package, out ClientTelemetryReport decoded, out int protocol),
    "wire report round-trips");
Assert.Equal(ClientTelemetryReport.CurrentProtocol, protocol, "wire protocol version");
Assert.Equal(42L, decoded.Sequence, "wire sequence");
Assert.Near(310d, decoded.Health.GcCorrelatedStallMilliseconds, 0.001d, "wire GC-correlated frame");
Assert.Equal(2048, decoded.UploadNetwork.PendingReliableBytes, "wire pending reliable bytes");
Assert.Equal(512, decoded.UploadNetwork.PendingUnreliableBytes, "wire pending unreliable bytes");
Assert.Equal(7680, decoded.UploadNetwork.TotalQueuedBytes, "wire total queued bytes");

var remote = new RemoteClientTelemetryState();
Assert.True(remote.Accept(source, 223.5d), "first report accepted");
RemoteClientTelemetrySnapshot baseline = remote.TakeSnapshot(223.5d);
Assert.Near(0d, baseline.DeliveryDelayMilliseconds, 0.001d, "first report establishes delay baseline");
var delayed = new ClientTelemetryReport(43L, 124.5d, true, true, false, health, network);
Assert.True(remote.Accept(delayed, 225.2d), "new sequence accepted");
RemoteClientTelemetrySnapshot delayedSnapshot = remote.TakeSnapshot(225.2d);
Assert.Near(699.15d, delayedSnapshot.DeliveryDelayMilliseconds, 0.01d, "ordered delivery delay survives wire state");
Assert.True(!remote.Accept(delayed, 225.3d), "duplicate report rejected");

var normalCadence = new RemoteClientTelemetryState();
Assert.True(normalCadence.Accept(source, 223.5d), "normal cadence baseline accepted");
Assert.Near(0d, normalCadence.TakeSnapshot(224.7d, 1d).DeliveryDelayMilliseconds, 0.001d,
    "normal time between one-second reports is not treated as queue delay");
Assert.Near(600d, normalCadence.TakeSnapshot(225.6d, 1d).DeliveryDelayMilliseconds, 0.01d,
    "silence beyond the expected report cadence becomes delivery-delay evidence");
Assert.True(!normalCadence.TakeSnapshot(228d, 1d, 4d).Available,
    "client report stream becomes unavailable after the missing threshold");

var slowerCadence = new RemoteClientTelemetryState();
Assert.True(slowerCadence.Accept(source, 223.5d), "slower cadence baseline accepted");
Assert.Near(0d, slowerCadence.TakeSnapshot(229.5d, 5d, 12.5d).DeliveryDelayMilliseconds, 0.001d,
    "configured five-second report cadence is not treated as queue delay");

var burstFlags = new RemoteClientTelemetryState();
var unfocused = new ClientTelemetryReport(1L, 10d, false, true, false, health, network);
var focused = new ClientTelemetryReport(2L, 11d, true, true, false, health, network);
Assert.True(burstFlags.Accept(unfocused, 110d), "unfocused burst report accepted");
Assert.True(burstFlags.Accept(focused, 111d), "focused burst report accepted");
Assert.True(!burstFlags.TakeSnapshot(111d).Focused,
    "one unfocused report suppresses stall classification for the merged burst");

var decision = new AdaptiveDecision(512 * 1024, 32 * 1024, false, false, 42d, "steady");
var ownership = new CharacterOwnershipSnapshot(7, 2);
var point = new PeerTelemetryPoint(
    DateTime.UtcNow.Ticks,
    12.5d,
    "WireTester",
    123L,
    network,
    decision,
    512 * 1024,
    8,
    ownership,
    delayedSnapshot,
    health,
    50,
    3,
    5);
string incidentRow = IncidentTelemetry.FormatForTesting(1, "pre", "wire-test", point);
Assert.Equal(
    IncidentTelemetry.HeaderForTesting.Split(',').Length,
    incidentRow.Split(',').Length,
    "incident CSV header and row stay aligned");

var aggregate = new DiagnosticsSnapshot
{
    EffectiveMode = "active",
    PeerCount = 30,
    P95PingMs = 100d,
    LocalAverageFps = 60d,
    MissingClientReports = 0,
    AverageRateLimitKiB = 2624d,
    AverageTransportRateKiB = 150d,
    MinimumTransportRateKiB = 64d
};
string aggregateRow = CsvTelemetry.FormatForTesting(10d, aggregate);
Assert.Equal(
    CsvTelemetry.HeaderForTesting.Split(',').Length,
    aggregateRow.Split(',').Length,
    "aggregate CSV header and row stay aligned");
Assert.True(CsvTelemetry.HeaderForTesting.EndsWith(",send_rate_avg_kib_s,send_rate_min_kib_s"),
    "real Steam send-rate columns are appended, so older column positions stay put");
Assert.True(aggregateRow.EndsWith(",150.000,64.000"),
    "aggregate CSV carries the real send rate, not the requested ceiling");

Assert.True(PinnedSteamSender.IsRetryLater(Steamworks.EResult.k_EResultLimitExceeded),
    "a full Steam send buffer is retried, not a fault");
Assert.True(PinnedSteamSender.IsRetryLater(Steamworks.EResult.k_EResultNoConnection),
    "a peer closing before its status callback is retried, not a fault");
Assert.True(PinnedSteamSender.IsRetryLater(Steamworks.EResult.k_EResultInvalidState),
    "a connection already past closing is retried, not a fault");
Assert.True(!PinnedSteamSender.IsRetryLater(Steamworks.EResult.k_EResultInvalidParam),
    "an invalid send still disables the pinned path");
Assert.True(!PinnedSteamSender.IsRetryLater(Steamworks.EResult.k_EResultFail),
    "an unexpected result still disables the pinned path");

System.Console.WriteLine($"Wire tests passed: {Assert.Passed}");

static class Assert
{
    public static int Passed { get; private set; }

    public static void True(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        Passed++;
    }

    public static void Equal<T>(T expected, T actual, string name) where T : IEquatable<T>
    {
        if (!actual.Equals(expected))
        {
            throw new InvalidOperationException($"{name}: expected {expected}, got {actual}");
        }
        Passed++;
    }

    public static void Near(double expected, double actual, double tolerance, string name)
    {
        if (Math.Abs(expected - actual) > tolerance)
        {
            throw new InvalidOperationException($"{name}: expected {expected}, got {actual}");
        }
        Passed++;
    }
}
