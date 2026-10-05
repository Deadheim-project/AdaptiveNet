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
var hitUpload = new LatencySummary(4, 31.5d, 50d, 88d);
var source = new ClientTelemetryReport(
    42L,
    123.5d,
    true,
    true,
    false,
    health,
    network,
    hitUpload);

ZPackage package = source.Serialize();
package.SetPos(0);
Assert.True(ClientTelemetryReport.TryDeserialize(package, out ClientTelemetryReport decoded, out int protocol),
    "wire report round-trips");
Assert.Equal(ClientTelemetryReport.CurrentProtocol, protocol, "wire protocol version");
Assert.Equal(2, ClientTelemetryReport.CurrentProtocol, "the hit-upload report is protocol 2");
Assert.Equal(42L, decoded.Sequence, "wire sequence");
Assert.Near(310d, decoded.Health.GcCorrelatedStallMilliseconds, 0.001d, "wire GC-correlated frame");
Assert.Equal(2048, decoded.UploadNetwork.PendingReliableBytes, "wire pending reliable bytes");
Assert.Equal(512, decoded.UploadNetwork.PendingUnreliableBytes, "wire pending unreliable bytes");
Assert.Equal(7680, decoded.UploadNetwork.TotalQueuedBytes, "wire total queued bytes");
Assert.Equal(4, decoded.HitUpload.Count, "wire hit upload count");
Assert.Near(31.5d, decoded.HitUpload.AverageMilliseconds, 0.001d, "wire hit upload mean");
Assert.Near(88d, decoded.HitUpload.MaximumMilliseconds, 0.001d, "wire hit upload maximum");
Assert.True(package.Size() <= 512, "the report still fits the server's 512-byte limit");

var noHits = new ClientTelemetryReport(43L, 124.5d, true, true, false, health, network);
ZPackage noHitsPackage = noHits.Serialize();
noHitsPackage.SetPos(0);
Assert.True(ClientTelemetryReport.TryDeserialize(noHitsPackage, out ClientTelemetryReport decodedNoHits, out _),
    "a report without hits round-trips");
Assert.Equal(0, decodedNoHits.HitUpload.Count, "no hits decode as an empty window");

ZPackage oldProtocol = new ZPackage();
oldProtocol.Write(1);
oldProtocol.Write(1L);
oldProtocol.SetPos(0);
Assert.True(!ClientTelemetryReport.TryDeserialize(oldProtocol, out _, out int oldVersion) && oldVersion == 1,
    "a protocol 1 report is refused and its version reported");

var hitState = new RemoteClientTelemetryState();
Assert.True(hitState.Accept(source, 223.5d), "hit report accepted");
Assert.True(hitState.Accept(new ClientTelemetryReport(43L, 124.5d, true, true, false, health, network,
    new LatencySummary(2, 10d, 12d, 140d)), 224.5d), "second hit report accepted");
RemoteClientTelemetrySnapshot hitSnapshot = hitState.TakeSnapshot(224.5d);
Assert.Equal(6, hitSnapshot.HitUpload.Count, "hits from every report merged into one snapshot");
Assert.Near(140d, hitSnapshot.HitUpload.MaximumMilliseconds, 0.001d, "merged hit upload maximum");
Assert.Near(24.333d, hitSnapshot.HitUpload.AverageMilliseconds, 0.001d, "merged hit upload mean is hit-weighted");
Assert.Equal(0, hitState.TakeSnapshot(224.6d).HitUpload.Count, "a snapshot starts the next hit window empty");

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

string[] incidentColumns = IncidentTelemetry.HeaderForTesting.Split(',');
Assert.Equal(75, Array.IndexOf(incidentColumns, "server_owned_nonplayer_characters"),
    "the incident columns from 0.4 keep their positions");
Assert.True(IncidentTelemetry.HeaderForTesting.EndsWith(",client_hit_upload_max_ms,server_world_save_ms"),
    "PvP columns are appended at the end of the incident CSV");
var pvpPoint = new PeerTelemetryPoint(
    DateTime.UtcNow.Ticks,
    13.5d,
    "PvpTester",
    124L,
    network,
    decision,
    150 * 1024,
    12,
    ownership,
    hitSnapshot,
    health,
    50,
    3,
    5,
    new PvpPeerStats(new LatencySummary(9, 41d, 75d, 96d), new LatencySummary(3, 22d, 30d, 35d), 18, 112d, 2),
    870d);
string[] pvpRow = IncidentTelemetry.FormatForTesting(2, "pre", "wire-test", pvpPoint).Split(',');
Assert.Equal(incidentColumns.Length, pvpRow.Length, "a row with PvP data stays aligned with the header");
Assert.Equal("96.000", pvpRow[Array.IndexOf(incidentColumns, "relay_hold_max_ms")], "relay hold maximum column");
Assert.Equal("3", pvpRow[Array.IndexOf(incidentColumns, "hit_forward_count")], "hit forward count column");
Assert.Equal("2", pvpRow[Array.IndexOf(incidentColumns, "zdo_queue_refusals")], "queue refusal column");
Assert.Equal("6", pvpRow[Array.IndexOf(incidentColumns, "client_hit_upload_count")], "client hit upload count column");
Assert.Equal("870.000", pvpRow[Array.IndexOf(incidentColumns, "server_world_save_ms")], "world save column");

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
string[] aggregateColumns = CsvTelemetry.HeaderForTesting.Split(',');
string[] aggregateValues = aggregateRow.Split(',');
Assert.Equal(28, Array.IndexOf(aggregateColumns, "send_rate_avg_kib_s"),
    "real Steam send-rate columns keep the position they had in 0.4.1");
Assert.Equal("150.000", aggregateValues[28], "aggregate CSV carries the real send rate, not the requested ceiling");
Assert.Equal("64.000", aggregateValues[29], "aggregate CSV carries the lowest real send rate");
Assert.True(CsvTelemetry.HeaderForTesting.EndsWith(",zdo_queue_refusals,world_save_ms"),
    "PvP columns are appended at the end of the aggregate CSV");

var pvpAggregate = new DiagnosticsSnapshot
{
    OnlineBackend = "Steamworks",
    RelayHold = new LatencySummary(40, 35d, 75d, 160d),
    HitForward = new LatencySummary(5, 20d, 40d, 48d),
    ServiceGapMaximumMs = 260d,
    ZdoQueueRefusals = 3,
    WorldSaveMs = 1200d
};
string[] pvpAggregateValues = CsvTelemetry.FormatForTesting(11d, pvpAggregate).Split(',');
Assert.Equal("\"Steamworks\"", pvpAggregateValues[Array.IndexOf(aggregateColumns, "online_backend")], "backend column");
Assert.Equal("75.000", pvpAggregateValues[Array.IndexOf(aggregateColumns, "relay_hold_p95_ms")], "relay hold p95 column");
Assert.Equal("48.000", pvpAggregateValues[Array.IndexOf(aggregateColumns, "hit_forward_max_ms")], "hit forward maximum column");
Assert.Equal("1200.000", pvpAggregateValues[Array.IndexOf(aggregateColumns, "world_save_ms")], "world save column");

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

// ServerSync wraps peer.m_socket once per mod copy and can leave the wrappers in place.
var realSocket = new StubSocket();
Assert.True(ReferenceEquals(realSocket, SocketUnwrapper.Unwrap(realSocket)),
    "a plain socket is its own transport");
ISocket nested = new WrappingSocket(new WrappingSocket(new OtherWrappingSocket(realSocket)));
Assert.True(ReferenceEquals(realSocket, SocketUnwrapper.Unwrap(nested)),
    "nested ServerSync-style wrappers of different types unwrap to the real socket");
var emptyWrapper = new WrappingSocket(null);
Assert.True(ReferenceEquals(emptyWrapper, SocketUnwrapper.Unwrap(emptyWrapper)),
    "a wrapper without an inner socket is left as it is");
var unrelated = new SocketWithUnrelatedOriginal();
Assert.True(ReferenceEquals(unrelated, SocketUnwrapper.Unwrap(unrelated)),
    "a field named Original that is not a socket is not followed");
Assert.True(SocketUnwrapper.Unwrap(null) == null, "no socket stays no socket");

System.Console.WriteLine($"Wire tests passed: {Assert.Passed}");

class StubSocket : ISocket
{
    public bool IsConnected() => true;
    public void Send(ZPackage pkg) { }
    public ZPackage Recv() => null;
    public int GetSendQueueSize() => 0;
    public int GetCurrentSendRate() => 0;
    public bool IsHost() => false;
    public void Dispose() { }
    public bool GotNewData() => false;
    public void Close() { }
    public string GetEndPointString() => "stub";
    public void GetAndResetStats(out int totalSent, out int totalRecv) { totalSent = 0; totalRecv = 0; }
    public void GetConnectionQuality(out float localQuality, out float remoteQuality, out int ping,
        out float outByteSec, out float inByteSec)
    {
        localQuality = 0f; remoteQuality = 0f; ping = 0; outByteSec = 0f; inByteSec = 0f;
    }
    public ISocket Accept() => null;
    public int GetHostPort() => 0;
    public bool Flush() => true;
    public string GetHostName() => "stub";
    public void VersionMatch() { }
}

// Shaped like ServerSync's BufferingSocket: a public readonly ISocket named Original.
class WrappingSocket : StubSocket
{
    public readonly ISocket Original;
    public WrappingSocket(ISocket original) => Original = original;
}

// A second copy's wrapper is a different type, as each mod compiles its own ServerSync.
class OtherWrappingSocket : StubSocket
{
    private readonly ISocket Original;
    public OtherWrappingSocket(ISocket original) => Original = original;
}

class SocketWithUnrelatedOriginal : StubSocket
{
    public readonly string Original = "not a socket";
}

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
