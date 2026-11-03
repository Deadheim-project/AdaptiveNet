using AdaptiveNet;
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

// --- Censo de donos por ZDO (servidor) ---
{
    const long server = 1L;
    var tally = new OwnershipTally();
    tally.BeginCycle();
    // Jogador A (uid 10) simula 3 criaturas, B (uid 20) simula 1, 1 sem dono, 1 do servidor.
    // O ZDO (7,1) esta na area de A e de B: conta uma vez so.
    Test.True(tally.Add(new ZdoKey(7, 1), 10), "primeira vez conta");
    Test.True(!tally.Add(new ZdoKey(7, 1), 10), "o mesmo ZDO em duas areas nao conta duas vezes");
    tally.Add(new ZdoKey(7, 2), 10);
    tally.Add(new ZdoKey(7, 3), 10);
    tally.Add(new ZdoKey(7, 4), 20);
    tally.Add(new ZdoKey(7, 5), 0);
    tally.Add(new ZdoKey(7, 6), server);
    tally.EndCycle(server);
    Test.Equal(6, tally.Active, "criaturas vistas no ciclo");
    Test.Equal(1, tally.Unowned, "sem dono");
    Test.Equal(1, tally.ServerOwned, "do servidor");
    Test.Equal(3, tally.Get(10).OwnedNonPlayerCharacters, "agregacao por uid: A");
    Test.Equal(1, tally.Get(20).OwnedNonPlayerCharacters, "agregacao por uid: B");
    Test.Equal(0, tally.Get(99).OwnedNonPlayerCharacters, "uid sem criaturas");
    Test.Equal(1, tally.Get(10).ScanId.CompareTo(0L), "ciclo numerado");
    Test.Equal(0, tally.Get(10).TransfersSincePreviousScan, "primeiro ciclo nao tem transferencias");

    // Segundo ciclo: (7,3) passa de A para B, (7,2) some, aparece (7,9) de B.
    tally.BeginCycle();
    tally.Add(new ZdoKey(7, 1), 10);
    tally.Add(new ZdoKey(7, 3), 20);
    tally.Add(new ZdoKey(7, 4), 20);
    tally.Add(new ZdoKey(7, 9), 20);
    tally.EndCycle(server);
    Test.Equal(1, tally.Get(10).OwnedNonPlayerCharacters, "A depois da troca");
    Test.Equal(3, tally.Get(20).OwnedNonPlayerCharacters, "B depois da troca");
    Test.Equal(2, tally.Get(10).ChangeSincePreviousScan, "mudanca de contagem de A (3 -> 1)");
    Test.Equal(2, tally.Get(20).ChangeSincePreviousScan, "mudanca de contagem de B (1 -> 3)");
    Test.Equal(1, tally.Get(10).TransfersSincePreviousScan, "transferencia conta para quem perdeu");
    Test.Equal(1, tally.Get(20).TransfersSincePreviousScan, "transferencia conta para quem ganhou");
    Test.Equal(2L, tally.CyclesCompleted, "dois ciclos");
    var top = tally.TopOwners(1);
    Test.Equal(20L, top[0].Key, "maior dono");

    tally.Reset();
    Test.Equal(0, tally.Active, "reset zera");
}

// --- Planejador do censo: um jogador por varredura, dentro do orcamento ---
{
    var planner = new CensusPlanner();
    Test.True(planner.ShouldStartCycle(0d, 5d), "primeiro ciclo comeca logo");
    planner.StartCycle(0d, new long[] { 10, 20, 30 });
    Test.True(!planner.ShouldStartCycle(1d, 5d), "ciclo aberto nao reinicia");
    Test.True(planner.ScanDue(0d), "primeira varredura imediata");
    planner.TryNext(out long first);
    Test.Equal(10L, first, "ordem dos jogadores");
    planner.ScanFinished(0d, 0.2d, 1d, 1d);
    Test.True(!planner.ScanDue(0.9d), "respeita o intervalo");
    Test.True(planner.ScanDue(1d), "intervalo cumprido");
    planner.TryNext(out long second);
    planner.ScanFinished(1d, 4d, 1d, 1d);
    Test.True(!planner.ScanDue(4.9d), "varredura de 4 ms com orcamento 1 ms/s espera 4 s");
    Test.True(planner.ScanDue(5d), "orcamento cumprido");
    planner.TryNext(out long third);
    Test.Equal(30L, third, "terceiro jogador");
    Test.True(!planner.CycleOpen, "ciclo fechado");
    Test.True(!planner.ShouldStartCycle(3d, 5d), "novo ciclo respeita o espacamento minimo");
    Test.True(planner.ShouldStartCycle(5d, 5d), "novo ciclo depois do espacamento");
}

// --- Cruzamento cliente x servidor ---
{
    Test.True(!OwnershipDisagreementTracker.Disagree(10, 9, 4, 0.5d), "10 x 9 concordam");
    Test.True(!OwnershipDisagreementTracker.Disagree(3, 0, 4, 0.5d), "diferenca pequena nao conta");
    Test.True(OwnershipDisagreementTracker.Disagree(12, 2, 4, 0.5d), "12 x 2 discordam");
    var tracker = new OwnershipDisagreementTracker();
    Test.True(!tracker.Observe(5, true, 12, 2, 4, 0d), "1a discordancia: sem aviso");
    Test.True(!tracker.Observe(5, true, 12, 2, 4, 1d), "2a discordancia: sem aviso");
    Test.True(tracker.Observe(5, true, 12, 2, 4, 2d), "3a seguida avisa");
    Test.True(!tracker.Observe(5, true, 12, 2, 4, 3d), "aviso limitado por frequencia");
    Test.True(tracker.Observe(5, true, 12, 2, 4, 130d), "avisa de novo depois do intervalo");
    Test.True(!tracker.Observe(6, true, 12, 2, 4, 0d) && !tracker.Observe(6, false, 12, 2, 4, 1d) &&
              !tracker.Observe(6, true, 12, 2, 4, 2d) && !tracker.Observe(6, true, 12, 2, 4, 3d),
        "dado nao comparavel zera a sequencia");
}

// --- Contas de CPU do cliente ---
{
    Test.Near(250d, CpuUsageMath.ProcessMsPerSecond(500d, 2d), 0.001d, "CPU do processo em ms/s");
    Test.Near(-1d, CpuUsageMath.ProcessMsPerSecond(500d, 0d), 0.001d, "sem intervalo = nao medido");
    Test.Near(25d, CpuUsageMath.SystemPercent(750UL, 600UL, 400UL), 0.001d, "uso do sistema: (K+U-idle)/(K+U)");
    Test.Near(-1d, CpuUsageMath.SystemPercent(0UL, 0UL, 0UL), 0.001d, "sem delta = nao medido");
    var machine = new ClientMachineSample(-5d, 140d, 2048);
    Test.Near(-1d, machine.ProcessCpuMsPerSecond, 0.001d, "CPU negativa vira nao medido");
    Test.Near(100d, machine.SystemCpuPercent, 0.001d, "CPU do sistema limitada a 100");
    var ai = new ClientAiSample(10, 14, 0);
    Test.Equal(10, ai.Owned, "IAs simuladas nunca passam das instanciadas");
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

// ---- Regra 4: rotulos informativos de carregamento e teleporte ----
{
    Test.Equal("client-loading", DiagnosticAnomalyClassifier.ClassifyPeerContext(true, false, false), "cliente nao pronto = carregando");
    Test.Equal("client-teleporting", DiagnosticAnomalyClassifier.ClassifyPeerContext(true, true, true), "cliente pronto e teleportando");
    Test.Equal(string.Empty, DiagnosticAnomalyClassifier.ClassifyPeerContext(true, true, false), "cliente em jogo: sem rotulo");
    Test.Equal(string.Empty, DiagnosticAnomalyClassifier.ClassifyPeerContext(false, false, false), "sem relatorio nao se afirma carregamento");
    // O gatilho continua mudo: o rotulo informativo nao muda o que abre captura.
    Test.Equal(string.Empty, DiagnosticAnomalyClassifier.ClassifyPeer(
        false, string.Empty, true, true, false, false, 4000, Health(maxMs: 3000), 250, 500, 500), "carregando continua sem gatilho");
}

// ---- Regra 5: churn de ZDOs contra a mediana de 60 s ----
{
    var options = new VerdictOptions();
    var series = new List<double>();
    for (int t = 0; t < 60; t++) series.Add(1200d + (t % 5) * 20);
    series.Add(5000d);
    ChurnResult spike = ZdoChurn.EvaluateAt(series, series.Count - 1, options);
    Test.True(spike.Triggered, "5000 ZDOs/s contra mediana ~1240 aciona (>= 3x e acima do piso)");
    Test.True(spike.Ratio > 3.5 && spike.Ratio < 4.5, "razao ~4x");
    Test.Near(1240d, spike.Median, 30d, "mediana da janela");

    var belowFactor = new List<double>();
    for (int t = 0; t < 60; t++) belowFactor.Add(1200d);
    belowFactor.Add(3000d);
    Test.True(!ZdoChurn.EvaluateAt(belowFactor, belowFactor.Count - 1, options).Triggered, "2,5x nao aciona");

    var belowFloor = new List<double>();
    for (int t = 0; t < 60; t++) belowFloor.Add(100d);
    belowFloor.Add(900d);
    Test.True(!ZdoChurn.EvaluateAt(belowFloor, belowFloor.Count - 1, options).Triggered, "9x mas abaixo do piso de 2000 nao aciona");

    var young = new List<double> { 100d, 100d, 100d, 9000d };
    Test.True(!ZdoChurn.EvaluateAt(young, 3, options).Triggered, "sem baseline suficiente nao conclui");

    var unreadable = new List<double>();
    for (int t = 0; t < 60; t++) unreadable.Add(1200d);
    unreadable.Add(-1d);
    Test.True(!ZdoChurn.EvaluateAt(unreadable, unreadable.Count - 1, options).Triggered, "contador ilegivel (-1) e ignorado");

    var inMiddle = new List<double>();
    for (int t = 0; t < 40; t++) inMiddle.Add(t == 25 ? 8000d : 1000d);
    ChurnResult peak = ZdoChurn.Peak(inMiddle, options);
    Test.True(peak.Triggered && peak.Current == 8000d, "Peak acha o segundo do salto no meio da serie");
    Test.True(!ZdoChurn.Peak(belowFactor.GetRange(0, 60), options).Triggered, "serie estavel nao tem pico");
}

// ---- Veredito por causa (funcao pura) ----
static VerdictCause? Find(IReadOnlyList<VerdictCause> causes, string code, string player = "")
{
    foreach (VerdictCause cause in causes)
    {
        if (cause.Code == code && (player.Length == 0 || cause.Player == player)) return cause;
    }
    return null;
}

{
    var options = new VerdictOptions();

    // Servidor travou 640 ms e 6 dos 8 jogadores congestionaram no mesmo segundo.
    var peers = new List<VerdictPeerInput>();
    for (int p = 0; p < 8; p++)
    {
        int index = p;
        peers.Add(PeerOf("P" + p, 100 + p, p == 0, 40, t => PeerSecond(t, congested: (t == 30 || t == 31) && index < 6)));
    }
    List<VerdictServerSample> server = ServerOf(40, t => new VerdictServerSample(
        t, t == 30 ? Health(avgMs: 60, maxMs: 640) : Health(), 0));
    List<VerdictCause> stalled = IncidentVerdict.Evaluate(peers, server, options);
    VerdictCause? stall = Find(stalled, "server-stall");
    Test.True(stall != null, "travada correlacionada gera server-stall");
    Test.True(stall!.Confidence == VerdictConfidence.High, "6 de 8 no mesmo segundo = confianca alta");
    Test.True(stall.Sentence.StartsWith("indício de"), "correlacao comeca por 'indício de'");
    Test.True(stall.Sentence.Contains("640") && stall.Sentence.Contains("6 de 8"), "frase traz a travada e a contagem");
    Test.Equal("server-stall", stalled[0].Code, "servidor correlacionado lidera a lista");
    Test.True(Find(stalled, "server-overloaded") == null, "uma travada nao e sobrecarga sustentada");

    // Mesma travada com um jogador so: mede, mas nao correlaciona.
    var lone = new List<VerdictPeerInput> { PeerOf("Solo", 1, true, 40, t => PeerSecond(t, congested: t == 30)) };
    VerdictCause? loneStall = Find(IncidentVerdict.Evaluate(lone, server, options), "server-stall");
    Test.True(loneStall != null && loneStall.Confidence == VerdictConfidence.Medium, "um jogador so: confianca media");
    Test.True(!loneStall!.Sentence.StartsWith("indício de"), "sem correlacao a frase e uma medida, nao um indicio");

    // Servidor lento sem pico algum: frame medio de 90 ms por 20 s.
    List<VerdictServerSample> slow = ServerOf(40, t => new VerdictServerSample(t, t >= 15 && t < 35 ? Health(avgMs: 90, maxMs: 130) : Health(), 0));
    List<VerdictCause> overloaded = IncidentVerdict.Evaluate(peers, slow, options);
    VerdictCause? load = Find(overloaded, "server-overloaded");
    Test.True(load != null, "frame medio de 90 ms sustentado gera server-overloaded");
    Test.True(Find(overloaded, "server-stall") == null, "sem pico nao ha server-stall");
    Test.True(load!.Sentence.Contains("90"), "frase traz o frame medio");

    // Servidor saudavel: nada do servidor.
    List<VerdictCause> healthy = IncidentVerdict.Evaluate(peers, ServerOf(40, t => new VerdictServerSample(t, Health(), 0)), options);
    Test.True(Find(healthy, "server-stall") == null && Find(healthy, "server-overloaded") == null, "servidor saudavel nao e acusado");

    // Salvamento do mundo e nomeado.
    VerdictCause? save = Find(IncidentVerdict.Evaluate(peers, ServerOf(40, t => new VerdictServerSample(t, Health(), t == 30 ? 700 : 0)), options), "server-stall");
    Test.True(save != null && save.Sentence.Contains("salvamento do mundo"), "save longo vira 'salvamento do mundo'");
}

{
    var options = new VerdictOptions();
    List<VerdictServerSample> calmServer = ServerOf(40, t => new VerdictServerSample(t, Health(), 0));

    // Conexao ruim so de quem disparou; o vizinho calmo nao e acusado.
    var peers = new List<VerdictPeerInput>
    {
        PeerOf("Ruim", 1, true, 40, t => PeerSecond(t, ping: t % 2 == 0 ? 40 : 130, local: t == 20 ? 0.7 : 0.99)),
        PeerOf("Calmo", 2, false, 40, t => PeerSecond(t, ping: t % 2 == 0 ? 40 : 130, local: t == 20 ? 0.7 : 0.99)),
    };
    List<VerdictCause> causes = IncidentVerdict.Evaluate(peers, calmServer, options);
    VerdictCause? bad = Find(causes, "bad-connection", "Ruim");
    Test.True(bad != null && bad.Confidence == VerdictConfidence.High, "jitter e perda juntos = confianca alta");
    Test.True(bad!.Sentence.StartsWith("indício de conexão ruim de Ruim"), "frase de conexao ruim");
    Test.True(bad.Sentence.Contains("0,70") && bad.Sentence.Contains("30%"), "qualidade com virgula e perda em %");
    Test.True(Find(causes, "bad-connection", "Calmo") == null, "jogador que nao disparou nao e avaliado");

    // Sem ninguem disparando (marcador manual), todos entram.
    var all = new List<VerdictPeerInput>
    {
        PeerOf("A", 1, false, 40, t => PeerSecond(t, ping: t % 2 == 0 ? 40 : 130)),
        PeerOf("B", 2, false, 40, t => PeerSecond(t)),
    };
    List<VerdictCause> manual = IncidentVerdict.Evaluate(all, calmServer, options);
    Test.True(Find(manual, "bad-connection", "A") != null && Find(manual, "bad-connection", "B") == null, "sem disparador, todos sao avaliados");

    // Ping alto com muito unacked: NAO e saturacao nem conexao ruim.
    var highPing = new List<VerdictPeerInput>
    {
        PeerOf("Longe", 3, true, 40, t => PeerSecond(t, ping: 240 + (t % 3), unacked: 250 * 1024, outBps: 80 * 1024, pending: 0)),
    };
    List<VerdictCause> farAway = IncidentVerdict.Evaluate(highPing, calmServer, options);
    Test.True(Find(farAway, "link-saturated") == null, "ping alto com unacked alto NAO vira link-saturated");
    Test.True(Find(farAway, "bad-connection") == null, "ping alto e estavel NAO vira conexao ruim");
    Test.Equal(IncidentVerdict.NoCause, IncidentVerdict.Format(farAway), "sem regra casada, o texto diz isso");

    // Saturacao de verdade.
    var saturated = new List<VerdictPeerInput>
    {
        PeerOf("Cheio", 4, true, 40, t => PeerSecond(t, rate: 150 * 1024, outBps: 148 * 1024, pending: t >= 20 ? 60 * 1024 : 0)),
    };
    VerdictCause? sat = Find(IncidentVerdict.Evaluate(saturated, calmServer, options), "link-saturated", "Cheio");
    Test.True(sat != null && sat.Sentence.StartsWith("indício de link saturado") && sat.Sentence.Contains("148"), "saturacao real e dita em KiB/s");

    // Carregando e teleportando entram no veredito sem gatilho proprio.
    var loading = new List<VerdictPeerInput>
    {
        PeerOf("Chegando", 5, true, 40, t => PeerSecond(t, ready: t >= 35, congested: t == 10)),
        PeerOf("Pulando", 6, true, 40, t => PeerSecond(t, teleporting: t >= 38)),
    };
    List<VerdictCause> context = IncidentVerdict.Evaluate(loading, calmServer, options);
    VerdictCause? loadCause = Find(context, "client-loading", "Chegando");
    Test.True(loadCause != null && loadCause.Sentence.Contains("carregando o mundo"), "cliente nao pronto vira client-loading");
    Test.True(loadCause!.Confidence == VerdictConfidence.Medium, "carregou e ja terminou: confianca media");
    VerdictCause? tp = Find(context, "client-teleporting", "Pulando");
    Test.True(tp != null && tp.Confidence == VerdictConfidence.High, "ainda teleportando no fim: confianca alta");
}

{
    var options = new VerdictOptions();
    var peers = new List<VerdictPeerInput> { PeerOf("A", 1, true, 70, t => PeerSecond(t)) };

    // Churn: 1200 ZDOs/s por 60 s e depois 5000/s.
    List<VerdictServerSample> churn = ServerOf(70, t => new VerdictServerSample(t, Health(), 0, 800_000, t < 62 ? 600 : 2500, t < 62 ? 600 : 2500, 10));
    VerdictCause? c = Find(IncidentVerdict.Evaluate(peers, churn, options), "zdo-churn");
    Test.True(c != null && c.Sentence.StartsWith("indício de carga de objetos"), "salto de ZDOs vira zdo-churn");

    // Contador ilegivel (-1) nao acusa nada.
    List<VerdictServerSample> blind = ServerOf(70, t => new VerdictServerSample(t, Health(), 0));
    Test.True(Find(IncidentVerdict.Evaluate(peers, blind, options), "zdo-churn") == null, "sem contadores nao ha churn");

    // Ordem: confianca e depois pontuacao; formato final.
    var mixed = new List<VerdictPeerInput>();
    for (int p = 0; p < 4; p++) mixed.Add(PeerOf("P" + p, p, p == 0, 40, t => PeerSecond(t, congested: t == 30)));
    List<VerdictCause> ordered = IncidentVerdict.Evaluate(
        mixed, ServerOf(40, t => new VerdictServerSample(t, t == 30 ? Health(maxMs: 900) : Health(), 0)), options);
    for (int i = 1; i < ordered.Count; i++) Test.True((int)ordered[i - 1].Confidence >= (int)ordered[i].Confidence, "causas ordenadas por confianca");
    string text = IncidentVerdict.Format(ordered);
    Test.True(text.StartsWith("server-stall [alta]: indício de servidor travado"), "Format abre pela causa mais forte");
    Test.True(!text.Contains('\n') && !text.Contains('"'), "texto cabe numa celula de CSV");
    Test.Equal(IncidentVerdict.NoCause, IncidentVerdict.Format(new List<VerdictCause>()), "lista vazia");
    Test.Equal(0, IncidentVerdict.Evaluate(null, null, null).Count, "entradas nulas nao quebram");
}

// ---- Eco ao dono: mediana, timeout, "nao medido", escolha e regra mob-owner-lag ----
{
    var tracker = new OwnerEchoTracker(window: 4, timeoutSeconds: 2d, staleSeconds: 20d);
    Test.True(!tracker.Get(7L, 0d).Tracked, "peer nunca sondado nao tem amostra");
    Test.Equal(-1, tracker.Get(7L, 0d).Samples, "peer nunca sondado reporta -1 amostras");

    // Voltas de 40, 60, 500, 80 ms: a mediana ignora o pico isolado.
    double[] rounds = { 0.040, 0.060, 0.500, 0.080 };
    double clock = 0d;
    foreach (double round in rounds)
    {
        Test.True(tracker.IsDue(7L, clock, 4d), "peer na vez de receber eco");
        long seq = tracker.BeginSend(7L, clock);
        Test.True(!tracker.IsDue(7L, clock + 10d, 4d), "com eco pendente nao envia outro");
        Test.True(tracker.Reply(7L, seq, clock + round, out double ms), "resposta da pendente e aceita");
        Test.Near(round * 1000d, ms, 0.001, "volta em ms");
        clock += 4d;
    }
    OwnerEchoSample sample = tracker.Get(7L, clock);
    Test.True(sample.Measured, "peer que respondeu esta medido");
    Test.Near(70d, sample.MedianMilliseconds, 0.001, "mediana de 40,60,80,500 = 70 ms");
    Test.Near(80d, sample.LastMilliseconds, 0.001, "ultima volta");
    Test.Equal(4, sample.Samples, "amostras na janela");

    // A janela descarta a mais antiga.
    long s5 = tracker.BeginSend(7L, clock);
    tracker.Reply(7L, s5, clock + 0.100, out _);
    Test.Equal(4, tracker.Get(7L, clock + 1d).Samples, "janela de 4 mantem 4 amostras");
    Test.Near(90d, tracker.Get(7L, clock + 1d).MedianMilliseconds, 0.001, "mediana de 60,80,100,500 = 90 ms");

    // Resposta duplicada ou de peer nao sondado nao conta.
    Test.True(!tracker.Reply(7L, s5, clock + 0.2, out _), "resposta duplicada e ignorada");
    Test.True(!tracker.Reply(99L, s5, clock + 0.2, out _), "resposta de quem nao foi sondado e ignorada");

    // Resposta velha demais deixa de contar como medida.
    Test.True(!tracker.Get(7L, clock + 60d).Measured, "ultima resposta velha = nao medido");
}
{
    // Timeout: peer que ja respondeu passa a valer o timeout; peer que nunca respondeu continua nao medido.
    var tracker = new OwnerEchoTracker(window: 4, timeoutSeconds: 2d);
    long a = tracker.BeginSend(1L, 0d);
    tracker.Reply(1L, a, 0.050, out _);
    long late = tracker.BeginSend(1L, 4d);
    Test.Equal(0, tracker.Expire(5.9d), "antes do timeout nada expira");
    Test.Equal(1, tracker.Expire(6.1d), "depois de 2 s o pendente expira");
    OwnerEchoSample after = tracker.Get(1L, 6.1d);
    Test.Equal(1, after.Timeouts, "timeout contado");
    Test.True(after.Measured, "quem ja respondeu continua medido");
    Test.Near(1025d, after.MedianMilliseconds, 0.001, "mediana de 50 ms e do timeout de 2000 ms");
    Test.True(!tracker.Reply(1L, late, 6.5d, out _), "resposta depois do timeout e ignorada");

    long ghost = 0;
    for (int attempt = 0; attempt < OwnerEchoTracker.SilentTimeoutsBeforeBackoff; attempt++)
    {
        double at = attempt * 4d;
        Test.True(tracker.IsDue(2L, at, 4d), "peer silencioso e sondado no inicio");
        ghost = tracker.BeginSend(2L, at);
        tracker.Expire(at + 2.5d);
    }
    OwnerEchoSample silent = tracker.Get(2L, 13d);
    Test.True(silent.Tracked && !silent.Measured, "peer sem o mod fica sondado mas nao medido");
    Test.Near(-1d, silent.MedianMilliseconds, 0.0001, "nao medido = -1 ms");
    Test.Equal(3, silent.Timeouts, "timeouts do peer silencioso contados");
    Test.True(ghost > 0 && !tracker.IsDue(2L, 20d, 4d), "peer silencioso recua para uma tentativa por minuto");
    Test.True(tracker.IsDue(2L, 80d, 4d), "e tenta de novo depois do recuo");
    tracker.Forget(2L);
    Test.True(!tracker.Get(2L, 80d).Tracked, "Forget remove o peer");
}
{
    var tracker = new OwnerEchoTracker();
    var owners = new List<KeyValuePair<long, int>>
    {
        new KeyValuePair<long, int>(0L, 50),   // sem dono
        new KeyValuePair<long, int>(10L, 3),
        new KeyValuePair<long, int>(11L, 40),
        new KeyValuePair<long, int>(12L, 20),
        new KeyValuePair<long, int>(13L, 15),
        new KeyValuePair<long, int>(14L, 9),
        new KeyValuePair<long, int>(99L, 80),  // servidor / desconectado
    };
    List<long> picked = OwnerEchoPlanner.Select(owners, uid => uid != 99L, tracker, 0d, 4d, 4);
    Test.Equal(4, picked.Count, "escolhe no maximo 4 donos");
    Test.Equal(11L, picked[0], "o que mais simula vem primeiro");
    Test.True(!picked.Contains(0L) && !picked.Contains(99L), "sem dono e nao conectado ficam de fora");
    Test.True(!picked.Contains(10L), "o 5o colocado nao entra");
    tracker.BeginSend(11L, 0d);
    List<long> next = OwnerEchoPlanner.Select(owners, uid => uid != 99L, tracker, 1d, 4d, 4);
    Test.True(!next.Contains(11L), "peer com eco pendente nao recebe outro");
    Test.Equal(0, OwnerEchoPlanner.Select(owners, uid => true, tracker, 1d, 4d, 0).Count, "maximo 0 nao escolhe ninguem");
}
{
    var near = new OwnerNearMap();
    near.Record(5L, 100L, 7, 10d);
    near.Record(5L, 101L, 2, 10d);
    near.Record(5L, 100L, 9, 12d);
    List<KeyValuePair<long, int>> list = near.Get(5L, 15d, 30d, out int total);
    Test.Equal(11, total, "total de mobs perto de jogadores (ultima varredura de cada um)");
    Test.Equal(100L, list[0].Key, "jogador com mais mobs do dono vem primeiro");
    near.Get(5L, 100d, 30d, out int stale);
    Test.Equal(0, stale, "entradas velhas somem");
    near.Forget(100L);
    near.Get(5L, 15d, 30d, out int afterForget);
    Test.Equal(2, afterForget, "Forget do jogador tira os mobs perto dele");
}
{
    var options = new VerdictOptions();
    Test.Near(200d, options.OwnerEchoLagThresholdMs, 0.001, "limiar padrao do eco = 200 ms");
    VerdictPeerSample Second(double t, double echo, int mobs, bool ready = true) =>
        new VerdictPeerSample(t, 50, 0.99, 0.99, 0, 0, 0, 150 * 1024, 20 * 1024, false, true, true, ready, false, 30, 0,
            echo, mobs, "Ana, Bia");
    bool HasLag(VerdictPeerInput peer) =>
        IncidentVerdict.Evaluate(new[] { peer }, new List<VerdictServerSample>(), options).Exists(c => c.Code == "mob-owner-lag");

    var slow = PeerOf("Dede", 1L, false, 6, t => Second(t, 320, 14));
    var victim = PeerOf("Ana", 2L, true, 6, t => PeerSecond(t));
    var found = IncidentVerdict.Evaluate(new[] { victim, slow }, new List<VerdictServerSample>(), options);
    VerdictCause? cause = found.Find(c => c.Code == "mob-owner-lag");
    Test.True(cause != null, "dono com eco lento e mobs perto de jogadores vira causa");
    Test.Equal("Dede", cause!.Player, "a causa e sobre o dono, nao sobre a vitima");
    Test.True(cause.Sentence.StartsWith("indício de dono com atraso: Dede responde em 320 ms e simula 14 mobs perto de Ana, Bia"), "frase da causa");
    Test.True(cause.Confidence == VerdictConfidence.Medium, "1,6x o limiar = confianca media");

    Test.True(IncidentVerdict.Evaluate(new[] { PeerOf("Dede", 1L, false, 6, t => Second(t, 900, 14)) }, null, options)
        .Find(c => c.Code == "mob-owner-lag")!.Confidence == VerdictConfidence.High, "4,5x o limiar = confianca alta");
    Test.True(!HasLag(PeerOf("Dede", 1L, false, 6, t => Second(t, 150, 14))), "abaixo do limiar nao vira causa");
    Test.True(!HasLag(PeerOf("Dede", 1L, false, 6, t => Second(t, 900, 1))), "dono com poucos mobs nao vira causa");
    Test.True(!HasLag(PeerOf("Dede", 1L, false, 6, t => Second(t, -1, 14))), "eco nao medido (-1, peer sem o mod) nunca vira culpado");
    Test.True(!HasLag(PeerOf("Dede", 1L, false, 6, t => Second(t, 900, 14, ready: false))), "cliente carregando o mundo nao e dono com atraso");
    Test.True(!HasLag(PeerOf("Antigo", 3L, false, 6, t => PeerSecond(t))), "amostra sem campos do eco (padrao) nao dispara a regra");
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
