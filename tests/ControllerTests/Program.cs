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
    // The 2026-10-03 login: a 28 s world-load frame, no player yet, and a report delay that grew
    // from 0.5 s to 4.5 s only because the client's main thread was frozen.
    var calm = new RuntimeHealthSample(1d, 60, 16d, 20d, 40d, 0, 0, 0, 0, 0, 0d, 1000L);
    Test.Equal(string.Empty, DiagnosticAnomalyClassifier.ClassifyPeer(
        false, "steady", true, true, false, false, 4500d, calm, 250d, 500d, 500d),
        "report delay while the client is still loading is not an incident");
    Test.Equal(string.Empty, DiagnosticAnomalyClassifier.ClassifyPeer(
        false, "steady", true, true, true, true, 4500d, calm, 250d, 500d, 500d),
        "report delay across a teleport is not an incident");
    Test.Equal("telemetry-delayed", DiagnosticAnomalyClassifier.ClassifyPeer(
        false, "steady", true, true, true, false, 4500d, calm, 250d, 500d, 500d),
        "report delay of a client in the game still opens an incident");
    // The 2026-10-13 two-client test: the background window ran at 1-3 FPS and its reports
    // arrived 0.5-1.1 s late while the link stayed at 12 ms.
    Test.Equal(string.Empty, DiagnosticAnomalyClassifier.ClassifyPeer(
        false, "steady", true, false, true, false, 1084d, calm, 250d, 500d, 500d),
        "report delay of an unfocused client is not an incident");
    Test.Equal("network-queue-delay", DiagnosticAnomalyClassifier.ClassifyPeer(
        true, "queue-delay", true, false, true, false, 1084d, calm, 250d, 500d, 500d),
        "network congestion still counts while the client is unfocused");
    Test.Equal(string.Empty, DiagnosticAnomalyClassifier.ClassifyPeer(
        false, "steady", true, true, true, false, 120d, calm, 250d, 500d, 500d),
        "report delay under the threshold is not an incident");
    Test.Equal("network-queue-delay", DiagnosticAnomalyClassifier.ClassifyPeer(
        true, "queue-delay", true, true, false, false, 4500d, calm, 250d, 500d, 500d),
        "network congestion still counts while the client is loading");
}

{
    const double Missing = 4d;
    Test.True(!ClientReportPolicy.IsMissing(false, false, 100d, double.NaN, Missing),
        "client still loading the world has no character and is not missing a report");
    Test.True(!ClientReportPolicy.IsMissing(false, false, 100d, 99d, Missing),
        "client is given the full window once its character exists");
    Test.True(ClientReportPolicy.IsMissing(false, false, 104d, 100d, Missing),
        "client in the game that never reported is missing at the threshold");
    Test.True(ClientReportPolicy.IsMissing(false, false, 500d, 100d, Missing),
        "client without the mod stays missing");
    Test.True(!ClientReportPolicy.IsMissing(false, true, 500d, 100d, Missing),
        "client reporting now is not missing");
    Test.True(!ClientReportPolicy.IsMissing(true, false, 500d, 100d, Missing),
        "client that reported and went silent is a delay or stall, not a missing report");
    Test.True(!ClientReportPolicy.IsMissing(true, false, 500d, double.NaN, Missing),
        "reported client without a character is not missing");
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

// The periodic log line folds every one-second sample of its 15-second interval; printing only
// the last one showed a 345 ms world save as localFrameMax=33ms.
{
    RuntimeHealthSample interval = default;
    for (int second = 0; second < 15; second++)
    {
        bool save = second == 6;
        interval = RuntimeHealthSample.Merge(interval, new RuntimeHealthSample(
            1d, save ? 20 : 30, save ? 50d : 33.3d, 34d, save ? 345d : 34d, save ? 1 : 0, 0, 0, 0, 0, 0d, 1000L));
    }
    Test.Near(345d, interval.MaximumFrameMilliseconds, 0.001d,
        "a stall in the middle of the interval reaches the log line");
    Test.Near(15d, interval.IntervalSeconds, 0.001d, "the interval spans every sample");
    Test.Equal(440, interval.FrameCount, "every frame of the interval is counted");
    Test.Near(29.36d, interval.AverageFps, 0.01d, "fps is frame-weighted over the whole interval");
    Test.Equal(1, interval.StallFrames, "the stalled frame is counted once");
}

// ---- Veredito por causa: ajudantes de teste ----
static RuntimeHealthSample Health(double avgMs = 33, double maxMs = 40, int frames = 30)
{
    return new RuntimeHealthSample(1, frames, avgMs, avgMs, maxMs, 0, 0, 0, 0, 0, 0, 0);
}

static VerdictPeerSample PeerSecond(
    double t,
    int ping = 50,
    double local = 0.99,
    double remote = 0.99,
    int pending = 0,
    int unacked = 0,
    double queueDelay = 0,
    int rate = 150 * 1024,
    double outBps = 20 * 1024,
    bool congested = false,
    bool clientAvailable = true,
    bool focused = true,
    bool ready = true,
    bool teleporting = false,
    double clientFrameMax = 30,
    double delay = 0)
{
    return new VerdictPeerSample(t, ping, local, remote, pending, unacked, queueDelay, rate, outBps,
        congested, clientAvailable, focused, ready, teleporting, clientFrameMax, delay);
}

static VerdictPeerInput PeerOf(string name, long uid, bool triggered, int seconds, Func<int, VerdictPeerSample> make)
{
    var list = new List<VerdictPeerSample>();
    for (int t = 0; t < seconds; t++) list.Add(make(t));
    return new VerdictPeerInput(name, uid, triggered, list);
}

static List<VerdictServerSample> ServerOf(int seconds, Func<int, VerdictServerSample> make)
{
    var list = new List<VerdictServerSample>();
    for (int t = 0; t < seconds; t++) list.Add(make(t));
    return list;
}


// ---- Regra 1: server-overloaded (media de frame sustentada, sem depender de pico) ----
{
    var options = new VerdictOptions();
    double threshold = options.ServerOverloadThresholdMs;
    Test.Near(75d, threshold, 0.001, "limiar de servidor sobrecarregado = 1,5x o alvo de 50 ms");

    var slow = new List<double>();
    for (int i = 0; i < 10; i++) slow.Add(90d);
    Test.True(ServerLoad.IsSustained(slow, 10, threshold), "90 ms de media por 10 s e servidor sobrecarregado");

    var capped = new List<double>();
    for (int i = 0; i < 30; i++) capped.Add(33.4);
    Test.True(!ServerLoad.IsSustained(capped, 10, threshold), "~30 FPS do batchmode (33 ms) nao e sobrecarga");

    var worse = new List<double>();
    for (int i = 0; i < 10; i++) worse.Add(45d);
    Test.True(!ServerLoad.IsSustained(worse, 10, threshold), "22 FPS (45 ms) ainda abaixo de 1,5x o alvo");

    var spike = new List<double>();
    for (int i = 0; i < 10; i++) spike.Add(i == 4 ? 400d : 33d);
    Test.True(!ServerLoad.IsSustained(spike, 10, threshold), "um unico pico nao sustenta a janela (mediana)");

    var shortWindow = new List<double> { 90d, 90d, 90d };
    Test.True(!ServerLoad.IsSustained(shortWindow, 10, threshold), "menos amostras que a janela nao conclui");

    var tracker = new ServerOverloadTracker();
    bool entered = false;
    int enteredCount = 0;
    for (int i = 0; i < 25; i++)
    {
        entered = tracker.Observe(Health(avgMs: 90, maxMs: 120), 10, threshold);
        if (entered) enteredCount++;
    }
    Test.Equal(1, enteredCount, "entrar em sobrecarga avisa uma vez por episodio");
    Test.True(tracker.Overloaded, "tracker segue sobrecarregado");
    for (int i = 0; i < 12; i++) tracker.Observe(Health(avgMs: 33), 10, threshold);
    Test.True(!tracker.Overloaded, "tracker se recupera quando o frame volta a 33 ms");
    bool again = false;
    for (int i = 0; i < 10; i++) again |= tracker.Observe(Health(avgMs: 90), 10, threshold);
    Test.True(again, "novo episodio avisa de novo");

    var emptyTracker = new ServerOverloadTracker();
    for (int i = 0; i < 20; i++) emptyTracker.Observe(default, 10, threshold);
    Test.True(!emptyTracker.Overloaded, "intervalo sem frames nao conta como media");

    Test.Equal("server-overloaded", DiagnosticAnomalyClassifier.ClassifyServer(
        Health(avgMs: 90, maxMs: 120), 250, 500, 0, true), "rotulo server-overloaded");
    Test.Equal("server-frame-stall", DiagnosticAnomalyClassifier.ClassifyServer(
        Health(avgMs: 90, maxMs: 700), 250, 500, 0, true), "pico mantem o proprio nome");
    Test.Equal("server-world-save", DiagnosticAnomalyClassifier.ClassifyServer(
        Health(avgMs: 90, maxMs: 120), 250, 500, 400, true), "save mantem o proprio nome");
    Test.Equal(string.Empty, DiagnosticAnomalyClassifier.ClassifyServer(
        Health(avgMs: 33), 250, 500, 0, false), "sem sobrecarga nem pico, nada");
}

// ---- Correlacao entre jogadores no mesmo segundo ----
{
    var options = new VerdictOptions();
    var peers = new List<VerdictPeerInput>();
    for (int p = 0; p < 8; p++)
    {
        int index = p;
        peers.Add(PeerOf("P" + p, 100 + p, p == 0, 20, t => PeerSecond(t, congested: t == 10 && index < 6)));
    }
    PeerCorrelation.Count(peers, 10, options, out int affected, out int total);
    Test.Equal(6, affected, "6 de 8 jogadores afetados no segundo 10");
    Test.Equal(8, total, "8 jogadores com amostra no segundo 10");
    Test.True(PeerCorrelation.IsCorrelated(affected, total, options), "6 de 8 e correlacao");

    PeerCorrelation.Count(peers, 5, options, out int calm, out _);
    Test.Equal(0, calm, "segundo calmo sem afetados");
    Test.True(!PeerCorrelation.IsCorrelated(1, 8, options), "1 de 8 nao e correlacao");
    Test.True(!PeerCorrelation.IsCorrelated(2, 8, options), "2 de 8 abaixo da metade nao e correlacao");
    Test.True(PeerCorrelation.IsCorrelated(2, 3, options), "2 de 3 e correlacao");
    Test.True(!PeerCorrelation.IsCorrelated(1, 1, options), "um jogador so nao correlaciona");

    Test.True(PeerCorrelation.IsAffected(PeerSecond(0, clientFrameMax: 800), options), "travada de 800 ms do cliente em jogo afeta");
    Test.True(!PeerCorrelation.IsAffected(PeerSecond(0, clientFrameMax: 800, focused: false), options), "janela sem foco nao conta");
    Test.True(!PeerCorrelation.IsAffected(PeerSecond(0, clientFrameMax: 800, teleporting: true), options), "teleporte nao conta");
}

// ---- Regra 2: jitter e perda aproximada a partir do History ----
{
    var options = new VerdictOptions();
    var steady = new List<VerdictPeerSample>();
    for (int t = 0; t < 30; t++) steady.Add(PeerSecond(t, ping: 50 + (t % 2)));
    LinkQualityStats calm = LinkQuality.Compute(steady);
    Test.Near(1d, calm.JitterMilliseconds, 0.001, "jitter de ping alternando 1 ms");
    Test.Near(0.01, calm.ApproximateLoss, 0.0001, "perda aprox. de qualidade 0,99");
    Test.True(!LinkQuality.IsBad(calm, options, out _, out _), "link calmo nao e ruim");

    var jittery = new List<VerdictPeerSample>();
    for (int t = 0; t < 30; t++) jittery.Add(PeerSecond(t, ping: t % 2 == 0 ? 40 : 120));
    LinkQualityStats shaky = LinkQuality.Compute(jittery);
    Test.Near(80d, shaky.JitterMilliseconds, 0.001, "jitter medio de 80 ms");
    Test.True(LinkQuality.IsBad(shaky, options, out bool jitterHigh, out bool lossHigh) && jitterHigh && !lossHigh,
        "jitter alto sozinho e conexao ruim");

    var lossy = new List<VerdictPeerSample>();
    for (int t = 0; t < 30; t++) lossy.Add(PeerSecond(t, local: t == 12 ? 0.71 : 0.99, remote: 0.95));
    LinkQualityStats lost = LinkQuality.Compute(lossy);
    Test.Near(0.29, lost.ApproximateLoss, 0.0001, "perda aprox. = 1 - menor qualidade");
    Test.True(LinkQuality.IsBad(lost, options, out jitterHigh, out lossHigh) && lossHigh && !jitterHigh,
        "perda alta sozinha e conexao ruim");

    // 0 = nao medido: nem ping nem qualidade zerados entram na conta.
    var unmeasured = new List<VerdictPeerSample>();
    for (int t = 0; t < 30; t++)
    {
        bool blind = t % 3 == 0;
        unmeasured.Add(PeerSecond(t, ping: blind ? 0 : 50, local: blind ? 0 : 0.99, remote: blind ? 0 : 0.99));
    }
    LinkQualityStats blindStats = LinkQuality.Compute(unmeasured);
    Test.Near(0d, blindStats.JitterMilliseconds, 0.001, "ping 0 nao vira jitter de 50 ms");
    Test.Near(0.01, blindStats.ApproximateLoss, 0.0001, "qualidade 0 nao vira perda de 100%");
    Test.True(!LinkQuality.IsBad(blindStats, options, out _, out _), "links nao medidos nao acusam nada");

    var neverMeasured = new List<VerdictPeerSample>();
    for (int t = 0; t < 30; t++) neverMeasured.Add(PeerSecond(t, ping: 0, local: 0, remote: 0));
    LinkQualityStats none = LinkQuality.Compute(neverMeasured);
    Test.True(!none.QualityMeasured, "qualidade nunca medida");
    Test.Near(0d, none.ApproximateLoss, 0.0001, "sem medida, perda 0");
    Test.True(!LinkQuality.IsBad(none, options, out _, out _), "sem medida nao ha veredito");

    var fewPairs = new List<VerdictPeerSample> { PeerSecond(0, ping: 10), PeerSecond(1, ping: 300), PeerSecond(2, ping: 10) };
    Test.True(!LinkQuality.IsBad(LinkQuality.Compute(fewPairs), options, out _, out _), "poucos pares de ping nao bastam");
}

// ---- Regra 3: link-saturated separa pending de unacked ----
{
    var options = new VerdictOptions();

    var atRate = new List<VerdictPeerSample>();
    for (int t = 0; t < 12; t++) atRate.Add(PeerSecond(t, rate: 150 * 1024, outBps: 148 * 1024, pending: t < 3 ? 0 : 40 * 1024));
    SaturationResult byRate = LinkSaturation.Analyze(atRate, options);
    Test.True(byRate.Saturated && byRate.ByRate, "saida no teto Steam com bytes pendentes por 9 s satura");
    Test.True(byRate.Seconds >= 5, "duracao da saturacao registrada");

    var shortBurst = new List<VerdictPeerSample>();
    for (int t = 0; t < 12; t++) shortBurst.Add(PeerSecond(t, outBps: (t >= 4 && t < 7) ? 148 * 1024 : 10 * 1024, pending: 40 * 1024));
    Test.True(!LinkSaturation.Analyze(shortBurst, options).Saturated, "rajada de 3 s no teto nao e saturacao");

    var fullPipeNoQueue = new List<VerdictPeerSample>();
    for (int t = 0; t < 12; t++) fullPipeNoQueue.Add(PeerSecond(t, outBps: 149 * 1024, pending: 0));
    Test.True(!LinkSaturation.Analyze(fullPipeNoQueue, options).Saturated, "saida no teto sem pendente nao satura (so fluxo cheio)");

    var growing = new List<VerdictPeerSample>();
    for (int t = 0; t < 12; t++) growing.Add(PeerSecond(t, outBps: 60 * 1024, pending: t * 12 * 1024));
    SaturationResult byGrowth = LinkSaturation.Analyze(growing, options);
    Test.True(byGrowth.Saturated && !byGrowth.ByRate, "pendente crescendo sem parar satura");
    Test.True(byGrowth.PeakPendingBytes > byGrowth.PendingFromBytes, "crescimento registrado");

    var sawtooth = new List<VerdictPeerSample>();
    for (int t = 0; t < 12; t++) sawtooth.Add(PeerSecond(t, pending: (t % 2) * 50 * 1024));
    Test.True(!LinkSaturation.Analyze(sawtooth, options).Saturated, "pendente que sobe e desce nao satura");

    // O caso do doc: ping alto (225-250 ms) enche o 'nao confirmado' sem fila real.
    var highPing = new List<VerdictPeerSample>();
    for (int t = 0; t < 20; t++) highPing.Add(PeerSecond(t, ping: 240, unacked: (60 + t * 4) * 1024, outBps: 70 * 1024, pending: 0));
    Test.True(!LinkSaturation.Analyze(highPing, options).Saturated, "ping alto com muito unacked e pendente zero NAO e saturacao");

    var highPingAtRate = new List<VerdictPeerSample>();
    for (int t = 0; t < 20; t++) highPingAtRate.Add(PeerSecond(t, ping: 240, unacked: 200 * 1024, outBps: 149 * 1024, pending: 0));
    Test.True(!LinkSaturation.Analyze(highPingAtRate, options).Saturated, "ping alto com unacked enorme e saida alta, mas pendente 0, NAO satura");

    Test.True(!LinkSaturation.Analyze(new List<VerdictPeerSample>(), options).Saturated, "sem amostras, sem veredito");
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
