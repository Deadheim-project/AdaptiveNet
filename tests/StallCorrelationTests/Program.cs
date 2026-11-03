using System.Globalization;
using StallCorrelation;

var opt = new AnalysisOptions();
var rnd = new Random(12345);

static MachineSecond S(long sec, string m, bool st) => new(sec, m, m, st);

// Independencia: 3 maquinas travando a 5% sem relacao => observado perto do acaso.
{
    var data = new List<MachineSecond>();
    for (int s = 0; s < 20000; s++)
        foreach (var m in new[] { "a", "b", Machines.Server })
            data.Add(S(s, m, rnd.NextDouble() < 0.05));
    var r = Analyzer.Analyze(data, opt);
    Test.Equal(Verdict.Coincidence, r.Verdict, "independent data is a coincidence");
    Test.Near(r.ExpectedMultiStallSeconds, r.ObservedMultiStallSeconds, 4 * Math.Sqrt(r.Variance), "observed close to expected");
    Test.Equal(Verdict.Coincidence, r.AloneVerdict, "ALONE rate matches chance when independent");
}

// Causa compartilhada: o servidor trava e arrasta os jogadores.
{
    var data = new List<MachineSecond>();
    for (int s = 0; s < 20000; s++)
    {
        bool shared = rnd.NextDouble() < 0.03;
        data.Add(S(s, Machines.Server, shared));
        foreach (var m in new[] { "a", "b" })
            data.Add(S(s, m, shared || rnd.NextDouble() < 0.01));
    }
    var r = Analyzer.Analyze(data, opt);
    Test.Equal(Verdict.Shared, r.Verdict, "shared cause detected");
    Test.True(r.ObservedMultiStallSeconds > 5 * r.ExpectedMultiStallSeconds, "observed >> expected");
    Test.Equal(Verdict.Shared, r.AloneVerdict, "few ALONE when shared");
    Test.True(r.ServerWithPlayerSeconds > 5 * r.ServerWithPlayerExpected, "server+player together above chance");
}

// Independentes por construcao: nunca duas ao mesmo tempo (alternam) => abaixo do acaso.
{
    var data = new List<MachineSecond>();
    for (int s = 0; s < 20000; s++)
    {
        int who = rnd.NextDouble() < 0.2 ? rnd.Next(3) : -1;
        var names = new[] { "a", "b", "c" };
        for (int i = 0; i < 3; i++) data.Add(S(s, names[i], i == who));
    }
    var r = Analyzer.Analyze(data, opt);
    Test.Equal(0, r.ObservedMultiStallSeconds, "mutually exclusive stalls never overlap");
    Test.Equal(Verdict.Independent, r.Verdict, "fewer than chance => independent");
    Test.Equal(r.PlayerComparableStalls, r.PlayerAloneStalls, "every stall is ALONE");
}

// Conta exata, pequena: so segundos com >=2 maquinas contam e ALONE e listado.
{
    var data = new List<MachineSecond>
    {
        S(1, "a", true), S(1, "b", false),   // a sozinho
        S(2, "a", true), S(2, "b", true),    // juntos
        S(3, "a", false), S(3, "b", true),   // b sozinho
        S(4, "a", true),                      // so a reportou: nao comparavel
        S(2, "a", true),                      // duplicata: nao conta duas vezes
    };
    var r = Analyzer.Analyze(data, opt);
    Test.Equal(4, r.TotalSeconds, "total seconds");
    Test.Equal(3, r.ComparableSeconds, "comparable seconds");
    Test.Equal(1, r.ObservedMultiStallSeconds, "one joint-stall second");
    var a = r.Machines.Single(m => m.Machine == "a");
    Test.Equal(3, a.StallSeconds, "a stall seconds");
    Test.Equal(2, a.ComparableStalls, "a comparable stalls exclude lone-reporter second");
    Test.Equal(1, a.AloneStalls, "a alone once");
    Test.Equal(1L, a.AloneSeconds[0], "alone second recorded");
    Test.Equal(Verdict.InsufficientData, r.Verdict, "tiny sample is insufficient");
}

Test.Near(0.0, Analyzer.ProbAtLeastTwo(new[] { 0.5 }), 1e-12, "one machine cannot have two stalls");
Test.Near(0.25, Analyzer.ProbAtLeastTwo(new[] { 0.5, 0.5 }), 1e-12, "two fair coins");
Test.Near(0.5, Analyzer.ProbAtLeastTwo(new[] { 0.5, 0.5, 0.5 }), 1e-12, "three fair coins");
{
    var (up, low) = Analyzer.Tails(0, 3.0, 3.0);
    Test.Near(1.0, up, 1e-12, "P(X>=0)=1");
    Test.Near(Math.Exp(-3), low, 1e-12, "P(X<=0)=e^-3");
    var (up2, _) = Analyzer.Tails(5, 3.0, 3.0);
    Test.Near(0.18474, up2, 1e-4, "Poisson(3) P(X>=5)");
}

// CSV: aspas, filtros de foco/pronto/teleporte e relatorio velho.
{
    string header = "utc,player,uid,client_present,client_fresh,client_focused,client_player_ready,client_teleporting,client_frame_max_ms,server_frame_avg_ms,server_frame_max_ms";
    string csv = string.Join("\n", header,
        "2026-10-31T07:11:02.8Z,\"a,b\",1,1,1,1,1,0,300,33,40",   // jogador em stall (nome com virgula)
        "2026-10-31T07:11:03.1Z,\"x\",2,1,1,0,1,0,900,33,40",     // sem foco: ignorado
        "2026-10-31T07:11:03.2Z,\"x\",3,1,1,1,1,1,900,33,40",     // teleportando: ignorado
        "2026-10-31T07:11:03.3Z,\"x\",4,1,0,1,1,0,900,33,40",     // relatorio velho: ignorado
        "2026-10-31T07:11:04.0Z,\"x\",5,1,1,1,0,0,900,33,300");   // nao pronto + servidor em stall
    var summary = new IncidentCsv.ReadSummary();
    var list = IncidentCsv.Read(new StringReader(csv), opt, summary).ToList();
    Test.Equal(1, summary.PlayerRowsUsed, "one player row used");
    Test.Equal(4, summary.PlayerRowsIgnored, "four player rows ignored");
    Test.True(list.Any(x => x.Label == "a,b" && x.Stalled), "quoted name with comma parsed, stall flagged");
    Test.Equal(5, list.Count(x => x.Machine == Machines.Server), "server reported on every row");
    Test.Equal(1, list.Count(x => x.Machine == Machines.Server && x.Stalled), "server stall at 300 ms");
    Test.Equal(new[] { "a", "b,c", "d\"e" }, IncidentCsv.SplitLine("a,\"b,c\",\"d\"\"e\"").ToArray(), "split quotes");
}

Test.Summary();

static class Test
{
    private static int _passed;

    public static void Equal<T>(T expected, T actual, string name) where T : notnull
    {
        if (!actual.Equals(expected)) throw new InvalidOperationException($"{name}: expected {expected}, got {actual}");
        _passed++;
    }

    public static void Equal<T>(T[] expected, T[] actual, string name)
    {
        if (!expected.SequenceEqual(actual)) throw new InvalidOperationException($"{name}: sequences differ");
        _passed++;
    }

    public static void True(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"{name}: condition was false");
        _passed++;
    }

    public static void Near(double expected, double actual, double tolerance, string name)
    {
        if (Math.Abs(expected - actual) > tolerance)
            throw new InvalidOperationException($"{name}: expected {expected.ToString(CultureInfo.InvariantCulture)}, got {actual.ToString(CultureInfo.InvariantCulture)}");
        _passed++;
    }

    public static void Summary() => Console.WriteLine($"StallCorrelation tests passed: {_passed}");
}
