using AdaptiveNet.Core;

// Covers the seam between the Steam-specific sampler and the vanilla ISocket fallback.
// The Steam path cannot run here — Steamworks needs a live Steam context — so it is
// injected as a delegate and the interesting case is the one where it throws.

// A Steamworks interface that is unavailable in this process raises instead of returning
// an error code, which is how the real failure arrives.
static bool ThrowingSteamSampler(out NetworkSample sample) =>
    throw new InvalidOperationException(
        "Steamworks is not initialized. Please call InitGameServer or Init.");

static bool DecliningSteamSampler(out NetworkSample sample)
{
    sample = default;
    return false;
}

{
    var failures = new List<string>();
    var socket = new FakeSocket(
        localQuality: 0.94f,
        remoteQuality: 0.91f,
        ping: 72,
        outgoingBytesPerSecond: 40_000f,
        incomingBytesPerSecond: 9_000f,
        sendQueueSize: 8_000,
        currentSendRate: 512 * 1024);

    bool ok = ConnectionSampler.TrySample(
        socket,
        ThrowingSteamSampler,
        (stage, exception) => failures.Add(stage),
        out NetworkSample sample);

    Test.True(ok, "a throwing Steam sampler still yields a sample");
    Test.True(sample.Valid, "the fallback sample is marked valid");
    Test.Equal(72, sample.PingMilliseconds, "fallback carries the vanilla ping");
    Test.Near(0.94d, sample.LocalQuality, 0.0001d, "fallback carries local quality");
    Test.Near(0.91d, sample.RemoteQuality, 0.0001d, "fallback carries remote quality");
    Test.Equal(8_000, sample.ManagedQueueBytes, "fallback carries the vanilla queue size");
    Test.Equal(512 * 1024, sample.TransportSendRateBytesPerSecond, "fallback carries the send rate");
    Test.Near(200d, sample.QueueDelayMilliseconds, 0.001d,
        "fallback estimates queue delay from outgoing throughput");
    Test.Equal(1, failures.Count, "the Steam failure is reported exactly once");
    Test.Equal("Steam transport sample", failures[0], "the Steam stage is named in the failure");
    Test.True(socket.QualityCalls == 1 && socket.QueueCalls == 1,
        "the vanilla socket is actually consulted after the Steam throw");
}

{
    // A Steam sampler that simply has nothing to offer must fall through silently.
    var failures = new List<string>();
    var socket = new FakeSocket(0.99f, 0.99f, 30, 0f, 0f, 0, 0);
    bool ok = ConnectionSampler.TrySample(
        socket, DecliningSteamSampler, (stage, exception) => failures.Add(stage), out NetworkSample sample);

    Test.True(ok, "a declining Steam sampler falls through to vanilla");
    Test.Equal(0, failures.Count, "declining is not a failure");
    Test.Near(0d, sample.QueueDelayMilliseconds, 0.001d, "an empty queue has no delay");
}

{
    // When the Steam sampler succeeds its richer sample wins and the socket is left alone.
    var socket = new FakeSocket(0.5f, 0.5f, 999, 1f, 1f, 123, 456);
    var steamSample = new NetworkSample(
        true, 41, 0.98d, 0.97d, 1024, 2048, 512, 4096, 12.5d, 600_000, 120_000d, 90_000d, 120d, 100d);
    bool ok = ConnectionSampler.TrySample(
        socket,
        (out NetworkSample s) => { s = steamSample; return true; },
        (stage, exception) => throw new InvalidOperationException("unexpected failure: " + stage),
        out NetworkSample sample);

    Test.True(ok, "a successful Steam sample is used");
    Test.Equal(41, sample.PingMilliseconds, "the Steam sample wins over the vanilla one");
    Test.Equal(2048, sample.PendingReliableBytes, "the Steam sample keeps its reliable bytes");
    Test.True(socket.QualityCalls == 0 && socket.QueueCalls == 0,
        "the vanilla socket is not consulted when the Steam sample succeeds");
}

{
    // Non-Steam transports pass a null sampler and must still sample.
    var socket = new FakeSocket(0.9f, 0.88f, 55, 0f, 0f, 4_000, 100_000);
    bool ok = ConnectionSampler.TrySample(socket, null, null, out NetworkSample sample);
    Test.True(ok, "a null transport sampler still yields a vanilla sample");
    Test.Near(40d, sample.QueueDelayMilliseconds, 0.001d,
        "queue delay falls back to the current send rate when throughput is idle");
}

{
    // PlayFab leaves GetCurrentSendRate unimplemented; that must not cost the whole sample.
    var socket = new FakeSocket(0.9f, 0.9f, 60, 0f, 0f, 2_000, 0)
    {
        SendRateThrows = new NotImplementedException()
    };
    bool ok = ConnectionSampler.TrySample(socket, null, null, out NetworkSample sample);
    Test.True(ok, "an unimplemented send rate still yields a sample");
    Test.Equal(0, sample.TransportSendRateBytesPerSecond, "the unimplemented send rate reads zero");
    Test.Near(1000d, sample.QueueDelayMilliseconds, 0.001d,
        "a backed-up queue with no rate evidence is charged a full second");
}

{
    // A vanilla failure has no fallback beneath it, so the sample is refused.
    var failures = new List<string>();
    var socket = new FakeSocket(0.9f, 0.9f, 60, 0f, 0f, 0, 0)
    {
        QualityThrows = new InvalidOperationException("socket closed")
    };
    bool ok = ConnectionSampler.TrySample(
        socket, ThrowingSteamSampler, (stage, exception) => failures.Add(stage), out NetworkSample sample);

    Test.True(!ok, "a vanilla failure refuses the sample");
    Test.True(!sample.Valid, "a refused sample is not valid");
    Test.Equal(2, failures.Count, "both the Steam and vanilla stages report");
    Test.Equal("Connection sample", failures[1], "the vanilla stage is named in the failure");
}

Test.Summary();

sealed class FakeSocket : IVanillaConnectionMetrics
{
    private readonly float _localQuality;
    private readonly float _remoteQuality;
    private readonly int _ping;
    private readonly float _outgoingBytesPerSecond;
    private readonly float _incomingBytesPerSecond;
    private readonly int _sendQueueSize;
    private readonly int _currentSendRate;

    public FakeSocket(
        float localQuality,
        float remoteQuality,
        int ping,
        float outgoingBytesPerSecond,
        float incomingBytesPerSecond,
        int sendQueueSize,
        int currentSendRate)
    {
        _localQuality = localQuality;
        _remoteQuality = remoteQuality;
        _ping = ping;
        _outgoingBytesPerSecond = outgoingBytesPerSecond;
        _incomingBytesPerSecond = incomingBytesPerSecond;
        _sendQueueSize = sendQueueSize;
        _currentSendRate = currentSendRate;
    }

    public Exception? QualityThrows { get; init; }
    public Exception? SendRateThrows { get; init; }
    public int QualityCalls { get; private set; }
    public int QueueCalls { get; private set; }

    public void GetConnectionQuality(
        out float localQuality,
        out float remoteQuality,
        out int pingMilliseconds,
        out float outgoingBytesPerSecond,
        out float incomingBytesPerSecond)
    {
        QualityCalls++;
        if (QualityThrows != null) throw QualityThrows;
        localQuality = _localQuality;
        remoteQuality = _remoteQuality;
        pingMilliseconds = _ping;
        outgoingBytesPerSecond = _outgoingBytesPerSecond;
        incomingBytesPerSecond = _incomingBytesPerSecond;
    }

    public int GetSendQueueSize()
    {
        QueueCalls++;
        return _sendQueueSize;
    }

    public int GetCurrentSendRate()
    {
        if (SendRateThrows != null) throw SendRateThrows;
        return _currentSendRate;
    }
}

static class Test
{
    private static int _passed;

    public static void Equal<T>(T expected, T actual, string name) where T : IEquatable<T>
    {
        if (!actual.Equals(expected))
        {
            throw new InvalidOperationException($"{name}: expected {expected}, got {actual}");
        }

        _passed++;
    }

    public static void True(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"{name}: condition was false");
        }

        _passed++;
    }

    public static void Near(double expected, double actual, double tolerance, string name)
    {
        if (Math.Abs(expected - actual) > tolerance)
        {
            throw new InvalidOperationException($"{name}: expected {expected}, got {actual}");
        }

        _passed++;
    }

    public static void Summary() => Console.WriteLine($"Sampling tests passed: {_passed}");
}
