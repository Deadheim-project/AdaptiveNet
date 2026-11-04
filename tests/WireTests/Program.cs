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
Assert.Equal(3, ClientTelemetryReport.CurrentProtocol, "the AI/machine report is protocol 3");
Assert.True(!decoded.Ai.Measured && !decoded.Machine.Measured, "a report built without the v3 fields reads as not measured");
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

// Protocol 3 round trip.
var v3Source = new ClientTelemetryReport(44L, 125.5d, true, true, false, health, network, hitUpload,
    new ClientAiSample(14, 9, 5), new ClientMachineSample(612.5d, 43.25d, 3072));
ZPackage v3Package = v3Source.Serialize();
Assert.True(v3Package.Size() <= 512, "the protocol 3 report still fits the 512-byte limit");
v3Package.SetPos(0);
Assert.True(ClientTelemetryReport.TryDeserialize(v3Package, out ClientTelemetryReport v3, out int v3Protocol),
    "protocol 3 report round-trips");
Assert.Equal(3, v3Protocol, "protocol 3 reported");
Assert.True(v3.Ai.Measured && v3.Machine.Measured, "v3 fields are measured");
Assert.Equal(14, v3.Ai.Instances, "client AI instances");
Assert.Equal(9, v3.Ai.Owned, "client AI simulated");
Assert.Equal(5, v3.Ai.Other, "client AI owned by others");
Assert.Near(612.5d, v3.Machine.ProcessCpuMsPerSecond, 0.001d, "client process CPU");
Assert.Near(43.25d, v3.Machine.SystemCpuPercent, 0.001d, "client system CPU");
Assert.Equal(3072, v3.Machine.ProcessMemoryMiB, "client process memory");
Assert.Near(88d, v3.HitUpload.MaximumMilliseconds, 0.001d, "v3 keeps the v2 fields");

// An unmeasured v3 field set stays unmeasured on the wire.
var v3Unmeasured = new ClientTelemetryReport(45L, 126.5d, true, true, false, health, network);
ZPackage v3UnmeasuredPackage = v3Unmeasured.Serialize();
v3UnmeasuredPackage.SetPos(0);
Assert.True(ClientTelemetryReport.TryDeserialize(v3UnmeasuredPackage, out ClientTelemetryReport v3None, out _) &&
            !v3None.Ai.Measured && !v3None.Machine.Measured, "unmeasured v3 fields stay unmeasured");

// A protocol 2 report (same bytes minus the 26-byte v3 tail, version patched) still parses, unmeasured.
byte[] v3Bytes = v3Source.Serialize().GetArray();
byte[] v2Bytes = new byte[v3Bytes.Length - 26];
Array.Copy(v3Bytes, v2Bytes, v2Bytes.Length);
BitConverter.GetBytes(2).CopyTo(v2Bytes, 0);
var v2Package = new ZPackage(v2Bytes);
Assert.True(ClientTelemetryReport.TryDeserialize(v2Package, out ClientTelemetryReport v2, out int v2Protocol),
    "a protocol 2 report is still accepted");
Assert.Equal(2, v2Protocol, "protocol 2 reported");
Assert.True(!v2.Ai.Measured && !v2.Machine.Measured, "protocol 2 reads the new fields as not measured");
Assert.Equal(4, v2.HitUpload.Count, "protocol 2 keeps the hit upload fields");

byte[] v4Bytes = (byte[])v3Bytes.Clone();
BitConverter.GetBytes(4).CopyTo(v4Bytes, 0);
Assert.True(!ClientTelemetryReport.TryDeserialize(new ZPackage(v4Bytes), out _, out int v4Protocol) && v4Protocol == 4,
    "a newer protocol is refused");
var truncatedV3 = new byte[v3Bytes.Length - 10];
Array.Copy(v3Bytes, truncatedV3, truncatedV3.Length);
Assert.True(!ClientTelemetryReport.TryDeserialize(new ZPackage(truncatedV3), out _, out _),
    "a protocol 3 report cut short is refused");

var v3State = new RemoteClientTelemetryState();
Assert.True(v3State.Accept(v3Source, 300d), "v3 report accepted");
RemoteClientTelemetrySnapshot v3Snapshot = v3State.TakeSnapshot(300.2d);
Assert.Equal(9, v3Snapshot.Ai.Owned, "snapshot carries the client AI sample");
Assert.Near(43.25d, v3Snapshot.Machine.SystemCpuPercent, 0.001d, "snapshot carries the client machine sample");

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
Assert.True(normalCadence.TakeSnapshot(228d, 1d, 4d).EverReported,
    "a client that went silent is still remembered as having reported");

var neverReported = new RemoteClientTelemetryState();
RemoteClientTelemetrySnapshot silentFromTheStart = neverReported.TakeSnapshot(500d, 1d, 4d);
Assert.True(!silentFromTheStart.Available && !silentFromTheStart.EverReported,
    "a client that has sent nothing is neither available nor remembered as reporting");

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
Assert.True(IncidentTelemetry.HeaderForTesting.Contains(",client_hit_upload_max_ms,server_world_save_ms,census_creatures_near,"),
    "PvP columns keep their place and the census columns come right after them");
Assert.True(IncidentTelemetry.HeaderForTesting.EndsWith(",client_process_memory_mib,host_process_cpu_ms_s,host_system_cpu_pct,host_steal_pct,owner_echo_median_ms,owner_echo_last_ms,owner_echo_samples,owner_echo_timeouts,owner_echo_mobs_simulated,verdict"),
    "census, protocol 3, host CPU and owner echo columns come before the verdict, which is appended last");
string verdictRow = IncidentTelemetry.FormatForTesting(1, "pre", "wire-test", point, "server-stall [alta]: servidor travou 640 ms, 6 de 8");
Assert.True(verdictRow.EndsWith(",\"server-stall [alta]: servidor travou 640 ms, 6 de 8\""),
    "the verdict is quoted as the last cell, commas inside included");
{
    string[] notMeasured = incidentRow.Split(',');
    Assert.Equal("-1", notMeasured[Array.IndexOf(incidentColumns, "census_creatures_near")], "no census reads -1");
    Assert.Equal("-1", notMeasured[Array.IndexOf(incidentColumns, "client_ai_owned")], "no client AI reads -1");
    Assert.Equal("-1", notMeasured[Array.IndexOf(incidentColumns, "client_system_cpu_pct")], "no client CPU reads -1");
}
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

var censusPoint = new PeerTelemetryPoint(
    DateTime.UtcNow.Ticks, 14.5d, "CensusTester", 125L, network, decision, 150 * 1024, 4, ownership, v3Snapshot, health, 50, 3, 5,
    default, 0d, new PeerCensusSample(12, 7, 2, 1, 4200, 1.75d, 3.5d, false));
string[] censusRow = IncidentTelemetry.FormatForTesting(3, "pre", "wire-test", censusPoint).Split(',');
string[] censusColumns = IncidentTelemetry.HeaderForTesting.Split(',');
Assert.Equal(censusColumns.Length, censusRow.Length, "a census row stays aligned with the header");
Assert.Equal("12", censusRow[Array.IndexOf(censusColumns, "census_creatures_near")], "census creatures column");
Assert.Equal("7", censusRow[Array.IndexOf(censusColumns, "census_owned_by_peer")], "census owned by peer column");
Assert.Equal("2", censusRow[Array.IndexOf(censusColumns, "census_owned_by_others")], "census other players column");
Assert.Equal("1.750", censusRow[Array.IndexOf(censusColumns, "census_scan_ms")], "census scan cost column");
Assert.Equal("9", censusRow[Array.IndexOf(censusColumns, "client_ai_owned")], "client AI owned column");
Assert.Equal("43.250", censusRow[Array.IndexOf(censusColumns, "client_system_cpu_pct")], "client system CPU column");
Assert.Equal("3072", censusRow[Array.IndexOf(censusColumns, "client_process_memory_mib")], "client memory column");


var echoPoint = new PeerTelemetryPoint(
    DateTime.UtcNow.Ticks, 15.5d, "EchoTester", 126L, network, decision, 150 * 1024, 4, ownership, v3Snapshot, health, 50, 3, 5,
    default, 0d, default, default, new OwnerEchoSample(true, true, 123.5d, 98d, 8, 2, 14, "Ana"));
string[] echoRow = IncidentTelemetry.FormatForTesting(4, "pre", "wire-test", echoPoint).Split(',');
Assert.Equal(censusColumns.Length, echoRow.Length, "an owner echo row stays aligned with the header");
Assert.Equal("123.500", echoRow[Array.IndexOf(censusColumns, "owner_echo_median_ms")], "owner echo median column");
Assert.Equal("98.000", echoRow[Array.IndexOf(censusColumns, "owner_echo_last_ms")], "owner echo last column");
Assert.Equal("8", echoRow[Array.IndexOf(censusColumns, "owner_echo_samples")], "owner echo samples column");
Assert.Equal("2", echoRow[Array.IndexOf(censusColumns, "owner_echo_timeouts")], "owner echo timeouts column");
Assert.Equal("14", echoRow[Array.IndexOf(censusColumns, "owner_echo_mobs_simulated")], "owner echo mobs column");
Assert.Equal("-1", incidentRow.Split(',')[Array.IndexOf(censusColumns, "owner_echo_median_ms")], "no echo reads -1 ms");
Assert.Equal("-1", incidentRow.Split(',')[Array.IndexOf(censusColumns, "owner_echo_timeouts")], "a peer never probed reads -1 timeouts");
var silentPoint = new PeerTelemetryPoint(
    DateTime.UtcNow.Ticks, 16.5d, "NoMod", 127L, network, decision, 150 * 1024, 4, ownership, v3Snapshot, health, 50, 3, 5,
    default, 0d, default, default, new OwnerEchoSample(true, false, 0d, 0d, 0, 3, 9, null));
string[] silentRow = IncidentTelemetry.FormatForTesting(5, "pre", "wire-test", silentPoint).Split(',');
Assert.Equal("-1", silentRow[Array.IndexOf(censusColumns, "owner_echo_median_ms")], "a peer that never answered is not measured");
Assert.Equal("3", silentRow[Array.IndexOf(censusColumns, "owner_echo_timeouts")], "its timeouts are still counted");

// Owner echo RPC: names carry the mod prefix and a version; the packet is 12 bytes, version + sequence.
Assert.True(OwnerEchoWire.RequestRpcName.StartsWith("AdaptiveNet.") && OwnerEchoWire.RequestRpcName.EndsWith(".1"), "echo request RPC name is prefixed and versioned");
Assert.True(OwnerEchoWire.ReplyRpcName.StartsWith("AdaptiveNet.") && OwnerEchoWire.ReplyRpcName.EndsWith(".1"), "echo reply RPC name is prefixed and versioned");
Assert.True(OwnerEchoWire.RequestRpcName != OwnerEchoWire.ReplyRpcName, "request and reply use different RPCs");
ZPackage echoPackage = OwnerEchoWire.Write(123456789012L);
Assert.Equal(12, echoPackage.Size(), "an echo packet is 12 bytes");
Assert.True(OwnerEchoWire.TryRead(new ZPackage(echoPackage.GetArray()), out long echoSequence) && echoSequence == 123456789012L, "echo packet round-trips its sequence");
var wrongVersion = new ZPackage();
wrongVersion.Write(2);
wrongVersion.Write(5L);
Assert.True(!OwnerEchoWire.TryRead(new ZPackage(wrongVersion.GetArray()), out _), "an unknown echo version is ignored");
var truncatedEcho = new ZPackage();
truncatedEcho.Write(1);
Assert.True(!OwnerEchoWire.TryRead(new ZPackage(truncatedEcho.GetArray()), out _), "a truncated echo packet is ignored");
var oversizedEcho = new ZPackage();
oversizedEcho.Write(1);
oversizedEcho.Write(5L);
oversizedEcho.Write(new string('x', 64));
Assert.True(!OwnerEchoWire.TryRead(new ZPackage(oversizedEcho.GetArray()), out _), "an oversized echo packet is ignored");
Assert.True(!OwnerEchoWire.TryRead(null, out _), "a null echo packet is ignored");

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

// Group capture RPCs: request, ask and reply packages round-trip and hostile ones are refused.
{
    ZPackage request = GroupCaptureWire.WriteRequest(500);
    request.SetPos(0);
    Assert.True(GroupCaptureWire.TryReadRequest(request, out int requestedSeconds), "group capture request round-trips");
    Assert.Equal(120, requestedSeconds, "a request over the maximum is clamped to 120 s");
    ZPackage defaultRequest = GroupCaptureWire.WriteRequest(0);
    defaultRequest.SetPos(0);
    Assert.True(GroupCaptureWire.TryReadRequest(defaultRequest, out int defaultSeconds), "a request for the configured length round-trips");
    Assert.Equal(0, defaultSeconds, "0 stays 0 so the server applies GroupCapture.Seconds from the cfg");
    var wrongProtocol = new ZPackage();
    wrongProtocol.Write(99);
    wrongProtocol.Write(60);
    wrongProtocol.SetPos(0);
    Assert.True(!GroupCaptureWire.TryReadRequest(wrongProtocol, out _), "an unknown request protocol is refused");

    ZPackage ask = GroupCaptureWire.WriteAsk(7, 60);
    ask.SetPos(0);
    Assert.True(GroupCaptureWire.TryReadAsk(ask, out int askId, out int askSeconds), "group capture ask round-trips");
    Assert.Equal(7, askId, "ask capture id");
    Assert.Equal(60, askSeconds, "ask seconds");

    var series = new GroupCaptureSeries();
    for (int second = 0; second < 60; second++)
    {
        series.Add(GroupCaptureSample.FromReport(1000 + second, health, network, true, true, false));
    }
    byte[] block = GroupCaptureCodec.Encode(series.Take(1059, 60), GroupCaptureLimits.MaximumCompressedBytes, out int included);
    ZPackage reply = GroupCaptureWire.WriteReply(7, 1_059_000L, block);
    Assert.True(reply.Size() <= GroupCaptureLimits.MaximumReplyPackageBytes, "a reply fits the per-client ceiling");
    reply.SetPos(0);
    Assert.True(GroupCaptureWire.TryReadReply(reply, out int replyId, out long clientMs, out byte[] replyBlock), "group capture reply round-trips");
    Assert.Equal(7, replyId, "reply capture id");
    Assert.Equal(1_059_000L, clientMs, "reply client clock");
    Assert.True(GroupCaptureCodec.TryDecode(replyBlock, out GroupCaptureSample[] decodedSeries) && decodedSeries.Length == included,
        "the reply block decodes to the samples sent");
    Assert.Equal(73, decodedSeries[0].PingMs, "client ping seen by the client survives the wire");

    var oversized = new ZPackage();
    oversized.Write(GroupCaptureLimits.Protocol);
    oversized.Write(7);
    oversized.Write(1L);
    oversized.Write(new byte[GroupCaptureLimits.MaximumReplyPackageBytes]);
    oversized.SetPos(0);
    Assert.True(!GroupCaptureWire.TryReadReply(oversized, out _, out _, out _), "an oversized reply is refused");
}

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
