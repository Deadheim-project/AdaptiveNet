using AdaptiveNet.Core;

static NetworkSample Sample(
    int ping = 50,
    double quality = 0.99,
    double? remoteQuality = null,
    int pending = 0,
    int unacked = 0,
    double queueDelay = 0,
    double outgoing = 0,
    bool valid = true)
{
    return new NetworkSample(
        valid,
        ping,
        quality,
        remoteQuality ?? quality,
        pending,
        unacked,
        queueDelay,
        512 * 1024,
        outgoing,
        0);
}

{
    var controller = new AdaptiveConnectionController(StandardOptions());
    AdaptiveDecision remoteLoss = controller.Observe(Sample(
        quality: 0.99, remoteQuality: 0.50, pending: 8 * 1024));
    Test.True(remoteLoss.Congested, "remote quality degradation triggers per-peer backoff");
    Test.Equal("quality", remoteLoss.Reason, "remote quality reason");
}

AdaptiveControllerOptions StandardOptions()
{
    return new AdaptiveControllerOptions
    {
        MinimumSendRateBytesPerSecond = 128 * 1024,
        MaximumSendRateBytesPerSecond = 1024 * 1024,
        InitialSendRateBytesPerSecond = 512 * 1024,
        AdditiveIncreaseBytesPerSecond = 64 * 1024,
        MultiplicativeDecreaseFactor = 0.75,
        HealthySamplesBeforeIncrease = 3,
        TargetQueueDelayMilliseconds = 60,
        CongestedQueueDelayMilliseconds = 180,
        MinimumConnectionQuality = 0.88,
        PingInflationThresholdMilliseconds = 45,
        MinimumZdoQueueBudgetBytes = 10 * 1024,
        MaximumZdoQueueBudgetBytes = 96 * 1024
    };
}

{
    var controller = new AdaptiveConnectionController(StandardOptions());
    AdaptiveDecision idle = controller.Observe(Sample());
    Test.Equal(512 * 1024, idle.SendRateLimitBytesPerSecond, "idle does not inflate rate");
    Test.True(idle.ZdoQueueBudgetBytes >= 10 * 1024 && idle.ZdoQueueBudgetBytes <= 96 * 1024,
        "idle budget remains bounded");
}

{
    var controller = new AdaptiveConnectionController(StandardOptions());
    controller.Observe(Sample(pending: 8 * 1024, queueDelay: 20));
    controller.Observe(Sample(pending: 8 * 1024, queueDelay: 20));
    AdaptiveDecision growth = controller.Observe(Sample(pending: 8 * 1024, queueDelay: 20));
    Test.Equal(576 * 1024, growth.SendRateLimitBytesPerSecond, "healthy demand grows additively");
    Test.Equal("healthy-growth", growth.Reason, "growth reason");
}

{
    var controller = new AdaptiveConnectionController(StandardOptions());
    AdaptiveDecision congestion = controller.Observe(Sample(pending: 16 * 1024, queueDelay: 250));
    Test.Equal(384 * 1024, congestion.SendRateLimitBytesPerSecond, "queue congestion backs off multiplicatively");
    Test.Equal(10 * 1024, congestion.ZdoQueueBudgetBytes, "congestion restores vanilla ZDO budget");
    Test.True(congestion.Congested, "congestion flag");
}

{
    var controller = new AdaptiveConnectionController(StandardOptions());
    controller.Observe(Sample(ping: 50));
    AdaptiveDecision inflated = controller.Observe(Sample(ping: 110, pending: 8 * 1024, queueDelay: 20));
    Test.True(inflated.Congested, "busy ping inflation is congestion");
    Test.Equal("ping-inflation", inflated.Reason, "ping inflation reason");
}

{
    var controller = new AdaptiveConnectionController(StandardOptions());
    AdaptiveDecision current = default;
    for (int index = 0; index < 20; index++)
    {
        current = controller.Observe(Sample(quality: 0.5, pending: 128 * 1024, queueDelay: 300));
    }

    Test.Equal(128 * 1024, current.SendRateLimitBytesPerSecond, "backoff clamps at configured minimum");
}

{
    var options = StandardOptions();
    options.InitialSendRateBytesPerSecond = 16 * 1024 * 1024;
    options.MaximumSendRateBytesPerSecond = 1024 * 1024;
    options.MaximumZdoQueueBudgetBytes = 32 * 1024;
    var controller = new AdaptiveConnectionController(options);
    AdaptiveDecision bounded = controller.Observe(Sample());
    Test.Equal(1024 * 1024, bounded.SendRateLimitBytesPerSecond, "initial rate is clamped");
    Test.Equal(32 * 1024, bounded.ZdoQueueBudgetBytes, "ZDO budget is clamped");
}

{
    var controller = new AdaptiveConnectionController(StandardOptions());
    AdaptiveDecision unavailable = controller.Observe(Sample(valid: false));
    Test.Equal(512 * 1024, unavailable.SendRateLimitBytesPerSecond, "missing sample is a no-op");
    Test.Equal("sample-unavailable", unavailable.Reason, "missing sample reason");
}

{
    // A cfg reload swaps the options without resetting what the connection learned.
    var controller = new AdaptiveConnectionController(StandardOptions());
    controller.Observe(Sample(pending: 8 * 1024, queueDelay: 20));
    controller.Observe(Sample(pending: 8 * 1024, queueDelay: 20));
    controller.Observe(Sample(pending: 8 * 1024, queueDelay: 20));
    Test.Equal(576 * 1024, controller.TargetSendRateBytesPerSecond, "rate learned before the reload");

    var reloaded = StandardOptions();
    reloaded.InitialSendRateBytesPerSecond = 128 * 1024;
    reloaded.AdditiveIncreaseBytesPerSecond = 128 * 1024;
    controller.UpdateOptions(reloaded);
    Test.Equal(576 * 1024, controller.TargetSendRateBytesPerSecond,
        "reload keeps the learned rate instead of restarting from the initial one");
    controller.Observe(Sample(pending: 8 * 1024, queueDelay: 20));
    controller.Observe(Sample(pending: 8 * 1024, queueDelay: 20));
    AdaptiveDecision grown = controller.Observe(Sample(pending: 8 * 1024, queueDelay: 20));
    Test.Equal(704 * 1024, grown.SendRateLimitBytesPerSecond, "growth uses the reloaded step");

    var lowered = StandardOptions();
    lowered.MaximumSendRateBytesPerSecond = 256 * 1024;
    controller.UpdateOptions(lowered);
    Test.Equal(256 * 1024, controller.TargetSendRateBytesPerSecond, "a lower ceiling clamps the learned rate");

    var raised = StandardOptions();
    raised.MinimumSendRateBytesPerSecond = 512 * 1024;
    controller.UpdateOptions(raised);
    Test.Equal(512 * 1024, controller.TargetSendRateBytesPerSecond, "a higher floor lifts the learned rate");
}

{
    var scheduler = new PeerSchedulerOptions();
    Test.Equal(50,
        PeerCadencePolicy.GetIntervalMilliseconds(true, 2, 2, scheduler),
        "duo combat gets 20 Hz service");
    Test.Equal(75,
        PeerCadencePolicy.GetIntervalMilliseconds(true, 8, 30, scheduler),
        "group of eight gets tapered low-latency service");
    Test.Equal(100,
        PeerCadencePolicy.GetIntervalMilliseconds(true, 12, 30, scheduler),
        "dynamic group of twelve gets 10 Hz service");
    Test.Equal(250,
        PeerCadencePolicy.GetIntervalMilliseconds(true, 1, 30, scheduler),
        "dispersed server peer uses economical cadence");
    Test.Equal(50,
        PeerCadencePolicy.GetIntervalMilliseconds(false, 1, 30, scheduler),
        "client upload is never slowed to solo server cadence");
}

{
    var history = new CircularHistory<int>(3);
    history.Add(1);
    history.Add(2);
    history.Add(3);
    history.Add(4);
    var ordered = new List<int>();
    history.CopyOrderedTo(ordered);
    Test.Equal(3, history.Count, "history stays at fixed capacity");
    Test.Equal(2, ordered[0], "history drops oldest value after wrap");
    Test.Equal(4, ordered[2], "history preserves chronological order after wrap");

    var shrunk = history.WithCapacity(2);
    var shrunkOrdered = new List<int>();
    shrunk.CopyOrderedTo(shrunkOrdered);
    Test.Equal(2, shrunk.Capacity, "resized history takes the new capacity");
    Test.Equal(3, shrunkOrdered[0], "shrinking keeps the newest values");
    Test.Equal(4, shrunkOrdered[1], "shrinking keeps chronological order");
    Test.Equal(3, history.Count, "the original history is left untouched");

    var grown = history.WithCapacity(5);
    var grownOrdered = new List<int>();
    grown.CopyOrderedTo(grownOrdered);
    Test.Equal(3, grown.Count, "growing keeps every value");
    grown.Add(5);
    grown.Add(6);
    grown.Add(7);
    grownOrdered.Clear();
    grown.CopyOrderedTo(grownOrdered);
    Test.Equal(5, grownOrdered.Count, "grown history fills to its new capacity");
    Test.Equal(3, grownOrdered[0], "grown history wraps at its new capacity");
}

{
    var estimator = new DeliveryDelayEstimator();
    Test.Near(0d, estimator.Observe(1, 10d, 110d), 0.001d,
        "first client report establishes clock offset");
    Test.Near(399.3d, estimator.Observe(2, 11d, 111.4d), 0.01d,
        "delivery estimator detects extra ordered-queue delay");
    Test.Near(0d, estimator.Observe(1, 1d, 201d), 0.001d,
        "new client sequence resets clock-offset baseline");
}

{
    var estimator = new DeliveryDelayEstimator();
    double client = 10d;
    double server = 110d;
    estimator.Observe(1, client, server);
    long sequence = 1;
    for (int minute = 0; minute < 12 * 60; minute++)
    {
        sequence++;
        client += 60d;
        server += 60d * 1.00005d;
        double driftDelay = estimator.Observe(sequence, client, server);
        Test.True(driftDelay < 1d, "twelve-hour 50ppm clock drift does not look like delivery lag");
    }
    Test.True(estimator.Observe(sequence + 1, client + 1d, server + 1.7d) > 650d,
        "sudden delay remains visible after long-running clock drift");
}

{
    var health = new RuntimeHealthSample(
        1d, 30, 33d, 50d, 700d, 1, 1, 0, 0, 0, 0d, 1000L);
    string active = DiagnosticAnomalyClassifier.ClassifyPeer(
        false, "steady", true, true, true, false, 0d, health, 250d, 500d, 500d);
    Test.Equal("client-frame-stall", active, "severe focused client frame triggers diagnostic incident");
    string altTabbed = DiagnosticAnomalyClassifier.ClassifyPeer(
        false, "steady", true, false, true, false, 0d, health, 250d, 500d, 500d);
    Test.Equal(string.Empty, altTabbed, "alt-tab frame does not trigger client stall incident");
    string teleporting = DiagnosticAnomalyClassifier.ClassifyPeer(
        false, "steady", true, true, true, true, 0d, health, 250d, 500d, 500d);
    Test.Equal(string.Empty, teleporting, "real teleport does not trigger client stall incident");
}

{
    var gcHealth = new RuntimeHealthSample(
        1d, 60, 16d, 20d, 310d, 1, 0, 1, 0, 0, 310d, 1000L);
    string gc = DiagnosticAnomalyClassifier.ClassifyPeer(
        false, "steady", true, true, true, false, 0d, gcHealth, 250d, 500d, 500d);
    Test.Equal("client-gc-correlated-stall", gc,
        "collection in stalled frame is labeled correlated rather than causal");
}

{
    var older = new RuntimeHealthSample(
        1d, 50, 20d, 33d, 100d, 1, 0, 1, 0, 0, 0d, 100L);
    var newer = new RuntimeHealthSample(
        1d, 100, 10d, 16d, 250d, 1, 1, 0, 1, 0, 250d, 200L);
    RuntimeHealthSample merged = RuntimeHealthSample.Merge(older, newer);
    Test.Equal(150, merged.FrameCount, "health merge preserves frame count");
    Test.Near(13.333d, merged.AverageFrameMilliseconds, 0.001d,
        "health merge uses frame-weighted average");
    Test.Equal(200L, merged.ManagedMemoryBytes, "health merge keeps newest memory sample");
}

Test.Summary();

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

    public static void Summary() => Console.WriteLine($"Controller tests passed: {_passed}");
}
