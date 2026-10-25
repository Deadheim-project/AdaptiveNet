using AdaptiveNet.Core;

// Latency accumulator: the summary the PvP probes log, write to CSV and send in client reports.
{
    var accumulator = new LatencyAccumulator();
    LatencySummary empty = accumulator.Summarize();
    Test.Equal(0, empty.Count, "an empty window has no samples");
    Test.Near(0d, empty.MaximumMilliseconds, 0.001d, "an empty window reports zero, not garbage");

    for (int index = 0; index < 19; index++) accumulator.Add(8d);
    accumulator.Add(240d);
    LatencySummary summary = accumulator.Summarize();
    Test.Equal(20, summary.Count, "every sample is counted");
    Test.Near(19.6d, summary.AverageMilliseconds, 0.001d, "mean of the window");
    Test.Near(240d, summary.MaximumMilliseconds, 0.001d, "maximum of the window");
    Test.Near(10d, summary.P95Milliseconds, 0.001d, "one spike in twenty stays out of the p95");

    accumulator.Add(double.NaN);
    accumulator.Add(double.PositiveInfinity);
    accumulator.Add(-5d);
    Test.Equal(21, accumulator.Count, "NaN and infinity are dropped; a negative wait counts as zero");

    LatencySummary taken = accumulator.TakeSummary();
    Test.Equal(21, taken.Count, "taking a summary returns the closed window");
    Test.Equal(0, accumulator.Count, "taking a summary starts an empty window");
}

{
    var accumulator = new LatencyAccumulator();
    accumulator.Add(37d);
    Test.Near(37d, accumulator.Summarize().P95Milliseconds, 0.001d,
        "the p95 never reads above the largest sample (bucket bound 40 is clipped to 37)");
}

{
    var older = new LatencySummary(3, 10d, 20d, 30d);
    var newer = new LatencySummary(1, 50d, 50d, 50d);
    LatencySummary merged = LatencySummary.Merge(older, newer);
    Test.Equal(4, merged.Count, "merged count");
    Test.Near(20d, merged.AverageMilliseconds, 0.001d, "merged mean is sample-weighted");
    Test.Near(50d, merged.MaximumMilliseconds, 0.001d, "merged maximum");
    Test.Near(50d, merged.P95Milliseconds, 0.001d, "merged p95 is the conservative larger bound");
    Test.Equal(3, LatencySummary.Merge(older, default).Count, "merging an empty window changes nothing");
    Test.Equal(1, LatencySummary.Merge(default, newer).Count, "merging into an empty window takes the other");
}

{
    var stats = new PvpPeerStats(default, default, -1, double.NaN, -2);
    Test.Equal(0, stats.ServiceCalls, "negative counts are clamped");
    Test.Near(0d, stats.ServiceGapMaximumMilliseconds, 0.001d, "a NaN gap is written as zero");
    Test.Equal(0, stats.QueueRefusals, "negative refusals are clamped");
}

// Relay hold: how long a newer position sat on the server before a nearby player was sent it.
{
    var tracker = new RelayHoldTracker<string, long>();
    RelayHoldTracker<string, long>.Link link = tracker.GetLink(2L, "A");

    tracker.ObserveArrival("A", 10u, 0.00d);
    Test.True(tracker.IsBehind(link, "A"), "a receiver that was never sent the source is behind");
    Test.True(!tracker.ObserveService(link, "A", true, true, 10u, 0.02d, out _),
        "the first copy a receiver gets is a visibility change, not a relay delay");
    Test.True(!tracker.IsBehind(link, "A"), "a receiver sent the latest revision is not behind");

    tracker.ObserveArrival("A", 14u, 0.05d);
    tracker.ObserveArrival("A", 14u, 0.07d);
    tracker.ObserveArrival("A", 19u, 0.10d);
    Test.True(tracker.ObserveService(link, "A", true, true, 19u, 0.12d, out double hold),
        "an update to a receiver already following the source is a sample");
    Test.Near(70d, hold, 0.001d,
        "the hold runs from the oldest revision the receiver had not seen (0.05 s), not the newest");

    Test.True(!tracker.ObserveService(link, "A", true, true, 19u, 0.20d, out _),
        "a service that delivers nothing new is not a sample");
    Test.True(!tracker.ObserveService(link, "A", true, false, 0u, 0.25d, out _),
        "a service whose sent revision is unknown is not a sample");
}

{
    var tracker = new RelayHoldTracker<string, long>();
    RelayHoldTracker<string, long>.Link link = tracker.GetLink(2L, "A");
    tracker.ObserveArrival("A", 5u, 0d);
    tracker.ObserveService(link, "A", true, true, 5u, 0.1d, out _);

    // The source stands still for twenty seconds, then moves.
    tracker.ObserveArrival("A", 6u, 20.00d);
    Test.True(tracker.ObserveService(link, "A", true, true, 6u, 20.08d, out double hold),
        "the first move after standing still is measured");
    Test.Near(80d, hold, 0.001d, "time spent standing still is not counted as hold");
}

{
    var tracker = new RelayHoldTracker<string, long>();
    RelayHoldTracker<string, long>.Link link = tracker.GetLink(2L, "A");
    tracker.ObserveArrival("A", 1u, 0d);
    tracker.ObserveService(link, "A", true, true, 1u, 0.1d, out _);

    // The source walks out of range and keeps moving there.
    tracker.ObserveArrival("A", 2u, 1d);
    Test.True(!tracker.ObserveService(link, "A", false, true, 2u, 1.1d, out _),
        "updates of a source outside the PvP radius are not sampled");
    tracker.ObserveArrival("A", 3u, 30d);
    Test.True(!tracker.ObserveService(link, "A", true, true, 3u, 40d, out _),
        "the service that sees the source come back into range is not sampled");
    tracker.ObserveArrival("A", 4u, 40.05d);
    Test.True(tracker.ObserveService(link, "A", true, true, 4u, 40.10d, out double hold),
        "once back in range and followed, updates are sampled again");
    Test.Near(50d, hold, 0.001d, "the hold after re-entry starts at the newer arrival");
}

{
    // The newer revision arrives while the source is still out of range, and the source walks
    // into range before the receiver is next served.
    var tracker = new RelayHoldTracker<string, long>();
    RelayHoldTracker<string, long>.Link link = tracker.GetLink(3L, "A");
    tracker.ObserveArrival("A", 1u, 0d);
    tracker.ObserveService(link, "A", false, true, 1u, 0.05d, out _);
    tracker.ObserveArrival("A", 2u, 0.10d);
    tracker.ObserveService(link, "A", true, false, 0u, 0.30d, out _);
    Test.True(tracker.ObserveService(link, "A", true, true, 2u, 0.40d, out double clipped),
        "a revision older than the source's time in range is still sampled");
    Test.Near(100d, clipped, 0.001d, "the hold is clipped to the time the source has been in range");
}

{
    // Inside the PvP radius but outside the receiver's active area: Valheim drops the receiver's
    // copy and stops sending the source until it comes back (seen live as 16-54 s "holds").
    var tracker = new RelayHoldTracker<string, long>();
    RelayHoldTracker<string, long>.Link link = tracker.GetLink(2L, "A");
    tracker.ObserveArrival("A", 1u, 0d);
    tracker.ObserveService(link, "A", true, true, 1u, 0.1d, out _);
    tracker.ObserveArrival("A", 2u, 0.2d);
    tracker.ObserveService(link, "A", true, true, 2u, 0.3d, out _);

    tracker.ObserveArrival("A", 3u, 1d);
    tracker.ObserveMissingCopy(link);
    Test.True(tracker.IsBehind(link, "A"), "a receiver without a copy is behind");
    for (int index = 4; index < 40; index++) tracker.ObserveArrival("A", (uint)index, index * 0.5d);
    tracker.ObserveMissingCopy(link);
    Test.True(!tracker.ObserveService(link, "A", true, true, 39u, 21d, out _),
        "the copy after the receiver had none is a visibility change, not a 20 s hold");
    Test.Near(21d, link.RevisionSince, 0.001d, "the link records when it was last sent a copy");
    tracker.ObserveArrival("A", 40u, 21.05d);
    Test.True(tracker.ObserveService(link, "A", true, true, 40u, 21.30d, out double hold),
        "once followed again, updates are sampled");
    Test.Near(250d, hold, 0.001d, "the hold after reappearing starts at the newer arrival");

    Test.Equal(40, tracker.GetHistory("A", out double oldest), "history keeps every arrival under its size");
    Test.Near(0d, oldest, 0.001d, "the oldest kept arrival");
    Test.Equal(0, tracker.GetHistory("B", out _), "an unknown source has no history");
}

{
    var tracker = new RelayHoldTracker<string, long>();
    RelayHoldTracker<string, long>.Link link = tracker.GetLink(2L, "A");
    tracker.ObserveArrival("A", 100u, 0d);
    tracker.ObserveService(link, "A", true, true, 100u, 0.01d, out _);
    tracker.ObserveArrival("A", 3u, 1d);
    Test.True(!tracker.ObserveService(link, "A", true, true, 3u, 1.05d, out _),
        "a revision that goes backwards (ZDO recreated) is not sampled");
    tracker.ObserveArrival("A", 4u, 1.10d);
    Test.True(tracker.ObserveService(link, "A", true, true, 4u, 1.15d, out double hold),
        "sampling resumes on the recreated ZDO's own revisions");
    Test.Near(50d, hold, 0.001d, "the recreated history starts clean");
}

{
    var tracker = new RelayHoldTracker<string, long>();
    RelayHoldTracker<string, long>.Link link = tracker.GetLink(2L, "A");
    tracker.ObserveArrival("A", 1u, 0d);
    tracker.ObserveService(link, "A", true, true, 1u, 0.01d, out _);
    int total = RelayHoldTracker<string, long>.RevisionHistory + 10;
    for (int index = 0; index < total; index++)
    {
        tracker.ObserveArrival("A", (uint)(index + 2), 1d + index * 0.05d);
    }
    double now = 1d + total * 0.05d;
    Test.True(tracker.ObserveService(link, "A", true, true, (uint)(total + 1), now, out double hold),
        "a receiver further behind than the history still yields a sample");
    double oldestKept = 1d + 10 * 0.05d;
    Test.Near((now - oldestKept) * 1000d, hold, 0.001d,
        "beyond the history, the oldest kept arrival gives a lower bound");
}

{
    var tracker = new RelayHoldTracker<string, long>();
    tracker.ObserveArrival("A", 1u, 0d);
    tracker.ObserveArrival("B", 1u, 0d);
    tracker.GetLink(1L, "A");
    tracker.GetLink(1L, "B");
    tracker.GetLink(2L, "A");
    tracker.GetLink(2L, "C");
    tracker.Retain(new HashSet<string> { "A" }, new HashSet<long> { 1L });
    Test.Equal(1, tracker.SourceCount, "a source that left is forgotten");
    Test.Equal(1, tracker.ReceiverCount, "a receiver that left is forgotten");
    RelayHoldTracker<string, long>.Link kept = tracker.GetLink(1L, "A");
    Test.True(tracker.IsBehind(kept, "A"), "a surviving link keeps working");
    Test.True(!tracker.IsBehind(tracker.GetLink(1L, "B"), "B"), "nothing is known about a forgotten source");
}

// World saves get their own label: they freeze every player at once, on purpose.
{
    var calm = new RuntimeHealthSample(1d, 50, 20d, 25d, 40d, 0, 0, 0, 0, 0, 0d, 1000L);
    var frozen = new RuntimeHealthSample(1d, 30, 40d, 50d, 900d, 1, 1, 0, 0, 0, 0d, 1000L);
    var gcStall = new RuntimeHealthSample(1d, 50, 25d, 30d, 300d, 1, 0, 1, 0, 0, 300d, 1000L);
    Test.Equal("server-world-save", DiagnosticAnomalyClassifier.ClassifyServer(frozen, 250d, 500d, 870d),
        "a long world save names itself instead of a generic stall");
    Test.Equal("server-world-save", DiagnosticAnomalyClassifier.ClassifyServer(calm, 250d, 500d, 300d),
        "a save over the stall threshold opens a capture even when the frame sample missed it");
    Test.Equal("server-gc-correlated-stall", DiagnosticAnomalyClassifier.ClassifyServer(gcStall, 250d, 500d, 120d),
        "a short save does not hide a GC stall in the same window");
    Test.Equal("server-frame-stall", DiagnosticAnomalyClassifier.ClassifyServer(frozen, 250d, 500d),
        "without a save, a severe stall keeps its old label");
    Test.Equal(string.Empty, DiagnosticAnomalyClassifier.ClassifyServer(calm, 250d, 500d),
        "a calm server raises nothing");
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

    public static void Summary() => Console.WriteLine($"PvP tests passed: {_passed}");
}
