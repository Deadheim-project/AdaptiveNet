using AdaptiveNet.Core;

static GroupCaptureSample Sample(long utc, double frameMax = 20d, int ping = 40) =>
    new GroupCaptureSample(
        utc, true, true, false, true, 60, 16.5d, 25d, frameMax, 0, 1, 0, 0, 0d,
        ping, 0.97d, 0.9d, 120000d, 90000d, 5d, 2048, 600000, 12, 300d, 40d, 2500);

static GroupCaptureSample[] Range(long first, int count, int ping = 40) =>
    Enumerable.Range(0, count).Select(index => Sample(first + index, 16d + index % 7, ping + index % 5)).ToArray();

static GroupCaptureParticipant[] Peers(params (long uid, string name, bool mod)[] peers) =>
    peers.Select(p => new GroupCaptureParticipant(p.uid, p.name, p.mod)).ToArray();

static int WindowFor(int requested)
{
    var c = new GroupCaptureCoordinator(8d, 120d);
    c.TryBegin(0d, "A", requested, 1L, Peers((1L, "P", true)), out GroupCaptureResult? r, out _);
    return r!.Seconds;
}

// Codec: a 120 s series round-trips and stays far under the per-client ceiling.
{
    GroupCaptureSample[] series = Range(1_700_000_000L, 120);
    byte[] block = GroupCaptureCodec.Encode(series, GroupCaptureLimits.MaximumCompressedBytes, out int included);
    Test.Equal(120, included, "a full series fits");
    Test.True(block.Length < 6000, $"120 samples deflate well under the ceiling ({block.Length} bytes)");
    Test.True(GroupCaptureCodec.TryDecode(block, out GroupCaptureSample[] decoded), "the block decodes");
    Test.Equal(120, decoded.Length, "decoded count");
    Test.Equal(1_700_000_000L, decoded[0].UtcSecond, "first UTC second");
    Test.Equal(1_700_000_119L, decoded[119].UtcSecond, "last UTC second");
    Test.Near(16.5d, decoded[3].FrameAvgMs, 0.001d, "frame average survives");
    Test.Equal(12, decoded[3].AiInstances, "AI instances survive");
    Test.Near(0.97d, decoded[3].QualityLocal, 0.005d, "quality survives quantization");
    Test.Equal(2500, decoded[3].ProcessMemoryMiB, "memory survives");
    Test.True(!GroupCaptureCodec.TryDecode(new byte[] { 1, 2, 3, 4 }, out _), "garbage is refused");
    Test.True(!GroupCaptureCodec.TryDecode(new byte[GroupCaptureLimits.MaximumCompressedBytes + 1], out _), "an oversized block is refused");
}

// Size ceiling: a tight ceiling drops the oldest samples first and never exceeds it.
{
    var noisy = new Random(7);
    GroupCaptureSample[] series = Enumerable.Range(0, 120)
        .Select(index => new GroupCaptureSample(
            1000 + index, true, true, false, true, noisy.Next(1, 300), noisy.NextDouble() * 500, noisy.NextDouble() * 500,
            noisy.NextDouble() * 900, noisy.Next(0, 9), noisy.Next(0, 3), 0, 0, noisy.NextDouble() * 400,
            noisy.Next(10, 400), noisy.NextDouble(), noisy.NextDouble(), noisy.NextDouble() * 1e6, noisy.NextDouble() * 1e6,
            noisy.NextDouble() * 300, noisy.Next(0, 1 << 20), noisy.Next(0, 1 << 20), noisy.Next(0, 200),
            noisy.NextDouble() * 1000, noisy.NextDouble() * 100, noisy.Next(1000, 9000)))
        .ToArray();
    byte[] block = GroupCaptureCodec.Encode(series, 2048, out int included);
    Test.True(block.Length <= 2048, $"the block respects the ceiling ({block.Length})");
    Test.True(included > 0 && included < 120, $"the oldest samples were dropped ({included} kept)");
    Test.True(GroupCaptureCodec.TryDecode(block, out GroupCaptureSample[] decoded), "a trimmed block decodes");
    Test.Equal(1000L + 120 - included, decoded[0].UtcSecond, "the newest samples are the ones kept");
    Test.Equal(1119L, decoded[^1].UtcSecond, "the last second is always kept");
    Test.Equal(0, GroupCaptureCodec.Encode(series, 4, out int none).Length, "a ceiling nothing fits in sends nothing");
    Test.Equal(0, none, "and reports zero samples");
}

// Client series: one sample per UTC second, a window, and the 120 s cap.
{
    var series = new GroupCaptureSeries();
    for (int second = 0; second < 150; second++) series.Add(Sample(5000 + second));
    series.Add(Sample(5149, frameMax: 99d));
    Test.Equal(120, series.Count, "the series keeps at most 120 s");
    GroupCaptureSample[] last60 = series.Take(5149, 60);
    Test.Equal(60, last60.Length, "a 60 s request returns 60 samples");
    Test.Equal(5090L, last60[0].UtcSecond, "the window starts 59 s before now");
    Test.Near(99d, last60[^1].FrameMaxMs, 0.001d, "the same second is replaced, not duplicated");
    Test.Equal(120, series.Take(5149, 9999).Length, "a request over the maximum is clamped to 120 s");
    Test.Equal(GroupCaptureLimits.DefaultSeconds, GroupCaptureLimits.ClampSeconds(0), "zero means the default");
    Test.Equal(GroupCaptureLimits.MaximumSeconds, GroupCaptureLimits.ClampSeconds(500), "500 s is clamped to 120");
}

// Join by UTC second: a skewed clock, a player without the mod, one that times out.
{
    var coordinator = new GroupCaptureCoordinator(8d, 120d);
    var peers = Peers((11L, "Ana", true), (22L, "Bruno", true), (33L, "Caio", false), (44L, "Dede", true));
    Test.True(coordinator.TryBegin(100d, "Admin", 60, 2000L, peers, out GroupCaptureResult? capture, out _), "the capture starts");
    Test.True(coordinator.Active, "and is active");
    Test.Equal(GroupCapturePeerState.NoMod, capture!.Peers[2].State, "a player without the mod is not asked");

    Test.True(coordinator.AcceptReply(11L, capture.CaptureId, Range(1941, 60), 2_000_300L, 2_000_450L, 3000), "Ana replies");
    Test.True(coordinator.Poll(103d) == null, "still waiting for the others");
    // Bruno's clock runs 10 s behind the server.
    Test.True(coordinator.AcceptReply(22L, capture.CaptureId, Range(1931, 60), 1_990_000L, 2_000_100L, 2800), "Bruno replies");
    Test.True(!coordinator.AcceptReply(22L, capture.CaptureId, Range(1931, 60), 1_990_000L, 2_000_100L, 2800), "a second reply is ignored");
    Test.True(!coordinator.AcceptReply(33L, capture.CaptureId, Range(1941, 5), 2_000_000L, 2_000_000L, 100), "a player who was never asked is ignored");
    Test.True(!coordinator.AcceptReply(44L, capture.CaptureId + 1, Range(1941, 5), 2_000_000L, 2_000_000L, 100), "a reply to another capture is ignored");

    GroupCaptureResult? done = coordinator.Poll(108.5d);
    Test.True(done != null, "the capture closes at the timeout");
    Test.True(!coordinator.Active, "and is no longer active");
    Test.Equal(GroupCapturePeerState.TimedOut, done!.Peers[3].State, "Dede is listed as timed out");
    Test.Equal(10, done.Peers[1].SkewSeconds, "Bruno's 10 s skew is measured");
    Test.Equal(0, done.Peers[0].SkewSeconds, "Ana's sub-2 s difference is not corrected");

    string csv = GroupCaptureCsv.Format(done);
    string[] lines = csv.TrimEnd('\n').Split('\n');
    Test.True(lines[0].StartsWith("# group-capture id=1 requested_by=Admin"), "summary line");
    Test.True(lines[1].StartsWith("# responded=Ana_11 samples=60") || lines[1].StartsWith("# responded=Ana(11 samples=60"), "responders listed");
    Test.True(lines[2] == "# no_mod=Caio(33)", "no-mod listed");
    Test.True(lines[3] == "# timed_out=Dede(44)", "timeouts listed");
    Test.Equal(GroupCaptureCsv.Header, lines[4], "header");
    Test.Equal(120, lines.Length - 5, "60 rows per responder");
    Test.Equal(120, GroupCaptureCsv.RowCount(done), "row count");
    // Bruno's 1931..1990 shifted by +10 land on 1941..2000, the same seconds as Ana.
    string[] first = lines[5].Split(',');
    string[] second = lines[6].Split(',');
    Test.Equal("1941", first[1], "rows are ordered by UTC second");
    Test.Equal(first[1], second[1], "both players share the same UTC second");
    Test.Equal("Ana", first[2], "same second, ordered by uid");
    Test.Equal("Bruno", second[2], "Bruno is aligned onto Ana's second");
    Test.Equal("10", second[4], "the correction is shown");
    Test.Equal(GroupCaptureCsv.Header.Split(',').Length, first.Length, "a row has as many columns as the header");
}

// Everyone replies: closes at once. No mod anywhere: closes empty.
{
    var coordinator = new GroupCaptureCoordinator(8d, 120d);
    coordinator.TryBegin(0d, "Admin", 30, 2000L, Peers((1L, "A", true)), out GroupCaptureResult? capture, out _);
    coordinator.AcceptReply(1L, capture!.CaptureId, Range(1970, 30), 2_000_000L, 2_000_000L, 900);
    Test.True(coordinator.Poll(0.5d) != null, "all replied: closes immediately");

    var empty = new GroupCaptureCoordinator(8d, 120d);
    empty.TryBegin(0d, "Admin", 30, 2000L, Peers((9L, "Vanilla", false)), out _, out _);
    GroupCaptureResult? none = empty.Poll(0d);
    Test.True(none != null && GroupCaptureCsv.RowCount(none) == 0, "nobody with the mod: closes with no rows");
}

// Rate limit: one capture at a time and one every two minutes.
{
    var coordinator = new GroupCaptureCoordinator(8d, 120d);
    Test.True(coordinator.TryBegin(1000d, "A", 60, 1L, Peers((1L, "P", true)), out _, out _), "first capture starts");
    Test.True(!coordinator.TryBegin(1001d, "A", 60, 1L, Peers((1L, "P", true)), out _, out string busy) && busy.Contains("already"), "a second one while open is refused");
    coordinator.Poll(1009d);
    Test.True(!coordinator.TryBegin(1060d, "A", 60, 1L, Peers((1L, "P", true)), out _, out string limited) && limited.Contains("rate limit"), "one minute later is refused");
    Test.True(coordinator.TryBegin(1120d, "A", 60, 1L, Peers((1L, "P", true)), out GroupCaptureResult? again, out _), "two minutes later it starts");
    Test.Equal(2, again!.CaptureId, "capture ids increase");
    Test.Equal(GroupCaptureLimits.MaximumSeconds, WindowFor(500), "the window is clamped to 120 s");
}

// Names cannot break the CSV or become formulas.
{
    Test.Equal("_Ev_il_", GroupCaptureCsv.Clean("=Ev,il\n"), "separators and a leading = are neutralised");
    Test.Equal("unknown", GroupCaptureCsv.Clean(""), "an empty name reads unknown");
}

Test.Summary();

static class Test
{
    private static int _passed;

    public static void Equal<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new InvalidOperationException($"{name}: expected {expected}, got {actual}");
        _passed++;
    }

    public static void True(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"{name}: condition was false");
        _passed++;
    }

    public static void Near(double expected, double actual, double tolerance, string name)
    {
        if (Math.Abs(expected - actual) > tolerance) throw new InvalidOperationException($"{name}: expected {expected}, got {actual}");
        _passed++;
    }

    public static void Summary() => Console.WriteLine($"Group capture tests passed: {_passed}");
}
