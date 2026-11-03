// Logica pura da correlacao de stalls: sem I/O de arquivo e sem dependencia do Valheim.
// Tecnica: alinhar por segundo, marcar "segundo em stall" por maquina e comparar os
// segundos com >=2 stalls simultaneos com o que a independencia esperaria por acaso.
using System.Globalization;
using System.Text;

namespace StallCorrelation;

public static class Machines
{
    public const string Server = "servidor";
}

/// <summary>Um segundo de uma maquina: reportou e, se reportou, travou ou nao.</summary>
public readonly record struct MachineSecond(long Second, string Machine, string Label, bool Stalled);

public sealed class AnalysisOptions
{
    /// <summary>Frame maximo (ms) a partir do qual o segundo conta como stall.</summary>
    public double ThresholdMs { get; init; } = 250;
}

public sealed class MachineStats
{
    public string Machine { get; init; } = "";
    public string Label { get; init; } = "";
    public int ReportedSeconds { get; init; }
    public int StallSeconds { get; init; }
    /// <summary>Stalls em que havia ao menos outra maquina reportando (base do ALONE).</summary>
    public int ComparableStalls { get; init; }
    /// <summary>Stalls comparaveis em que nenhuma outra maquina travou no mesmo segundo.</summary>
    public int AloneStalls { get; init; }
    /// <summary>Quantos ALONE a independencia esperaria dado o p das outras maquinas.</summary>
    public double ExpectedAloneStalls { get; init; }
    public double Rate => ReportedSeconds == 0 ? 0 : (double)StallSeconds / ReportedSeconds;
    public double AloneFraction => ComparableStalls == 0 ? 0 : (double)AloneStalls / ComparableStalls;
    public double ExpectedAloneFraction => ComparableStalls == 0 ? 0 : ExpectedAloneStalls / ComparableStalls;
    public List<long> AloneSeconds { get; init; } = new();
}

public enum Verdict
{
    InsufficientData,
    Shared,       // observado >> acaso
    Coincidence,  // observado ~ acaso
    Independent,  // observado << acaso
}

public sealed class AnalysisResult
{
    public double ThresholdMs { get; init; }
    public int TotalSeconds { get; init; }
    /// <summary>Segundos com >=2 maquinas reportando (os unicos comparaveis).</summary>
    public int ComparableSeconds { get; init; }
    public int ObservedMultiStallSeconds { get; init; }
    public double ExpectedMultiStallSeconds { get; init; }
    public double Variance { get; init; }
    public double PUpper { get; init; }
    public double PLower { get; init; }
    public Verdict Verdict { get; init; }
    public List<MachineStats> Machines { get; init; } = new();
    /// <summary>Soma dos ALONE de todos os jogadores (servidor fora) e o esperado pelo acaso.</summary>
    public int PlayerComparableStalls { get; init; }
    public int PlayerAloneStalls { get; init; }
    public double PlayerExpectedAlone { get; init; }
    /// <summary>Segundos em que o servidor e ao menos um jogador travaram juntos.</summary>
    public int ServerWithPlayerSeconds { get; init; }
    public double ServerWithPlayerExpected { get; init; }
    public Verdict AloneVerdict { get; init; }
    public double AlonePUpper { get; init; }
    public double AlonePLower { get; init; }
}

public static class Analyzer
{
    /// <summary>Abaixo disto o esperado por acaso e pequeno demais para concluir.</summary>
    public const double MinExpectedForVerdict = 5;
    public const double Alpha = 0.01;

    public static AnalysisResult Analyze(IEnumerable<MachineSecond> samples, AnalysisOptions? options = null)
    {
        options ??= new AnalysisOptions();

        // segundo -> maquina -> travou. O mesmo (segundo, maquina) repetido (incidentes
        // sobrepostos despejam o mesmo historico) conta uma vez; um stall em qualquer copia vale.
        var bySecond = new SortedDictionary<long, Dictionary<string, bool>>();
        var labels = new Dictionary<string, string>();
        foreach (var s in samples)
        {
            if (!bySecond.TryGetValue(s.Second, out var row)) bySecond[s.Second] = row = new Dictionary<string, bool>();
            row[s.Machine] = row.TryGetValue(s.Machine, out bool old) ? old || s.Stalled : s.Stalled;
            labels[s.Machine] = s.Label;
        }

        // Taxa de stall de cada maquina nos segundos em que ela reportou.
        var reported = new Dictionary<string, int>();
        var stalls = new Dictionary<string, int>();
        foreach (var row in bySecond.Values)
            foreach (var (m, st) in row)
            {
                reported[m] = reported.GetValueOrDefault(m) + 1;
                if (st) stalls[m] = stalls.GetValueOrDefault(m) + 1;
            }
        var p = reported.ToDictionary(kv => kv.Key, kv => (double)stalls.GetValueOrDefault(kv.Key) / kv.Value);

        int comparable = 0, observed = 0;
        double expected = 0, variance = 0;
        int serverWith = 0; double serverWithExp = 0;
        var comparableStalls = new Dictionary<string, int>();
        var aloneStalls = new Dictionary<string, int>();
        var expectedAlone = new Dictionary<string, double>();
        var aloneSeconds = new Dictionary<string, List<long>>();
        double playerExpectedAlone = 0, playerAloneVariance = 0;

        foreach (var (second, row) in bySecond)
        {
            if (row.Count < 2) continue;
            comparable++;
            int c = row.Values.Count(x => x);
            if (c >= 2) observed++;

            double q = ProbAtLeastTwo(row.Keys.Select(m => p[m]));
            expected += q;
            variance += q * (1 - q);

            // Servidor + algum jogador no mesmo segundo.
            if (row.TryGetValue(Machines.Server, out bool serverStall) && row.Count >= 2)
            {
                var players = row.Keys.Where(m => m != Machines.Server).ToList();
                if (players.Count > 0)
                {
                    double pAnyPlayer = 1 - players.Aggregate(1.0, (a, m) => a * (1 - p[m]));
                    serverWithExp += p[Machines.Server] * pAnyPlayer;
                    if (serverStall && players.Any(m => row[m])) serverWith++;
                }
            }

            foreach (var (m, st) in row)
            {
                // Para o ALONE cada maquina e comparada com todas as outras que reportaram.
                double pNoOther = row.Where(kv => kv.Key != m).Aggregate(1.0, (a, kv) => a * (1 - p[kv.Key]));
                if (!st) continue;
                // Dado que ESTA maquina travou, a chance de nenhuma outra travar por acaso.
                expectedAlone[m] = expectedAlone.GetValueOrDefault(m) + pNoOther;
                if (m != Machines.Server) { playerExpectedAlone += pNoOther; playerAloneVariance += pNoOther * (1 - pNoOther); }
                comparableStalls[m] = comparableStalls.GetValueOrDefault(m) + 1;
                if (row.Where(kv => kv.Key != m).All(kv => !kv.Value))
                {
                    aloneStalls[m] = aloneStalls.GetValueOrDefault(m) + 1;
                    if (!aloneSeconds.TryGetValue(m, out var l)) aloneSeconds[m] = l = new List<long>();
                    l.Add(second);
                }
            }
        }

        var machines = reported.Keys
            .OrderBy(m => m == Machines.Server ? 0 : 1).ThenBy(m => labels[m], StringComparer.OrdinalIgnoreCase)
            .Select(m => new MachineStats
            {
                Machine = m,
                Label = labels[m],
                ReportedSeconds = reported[m],
                StallSeconds = stalls.GetValueOrDefault(m),
                ComparableStalls = comparableStalls.GetValueOrDefault(m),
                AloneStalls = aloneStalls.GetValueOrDefault(m),
                ExpectedAloneStalls = expectedAlone.GetValueOrDefault(m),
                AloneSeconds = aloneSeconds.GetValueOrDefault(m) ?? new List<long>(),
            }).ToList();

        var (pUp, pLow) = Tails(observed, expected, variance);
        var players2 = machines.Where(m => m.Machine != Machines.Server).ToList();
        int pc = players2.Sum(m => m.ComparableStalls), pa = players2.Sum(m => m.AloneStalls);
        double pe = playerExpectedAlone;
        var (apUp, apLow) = Tails(pa, pe, playerAloneVariance);
        return new AnalysisResult
        {
            ThresholdMs = options.ThresholdMs,
            TotalSeconds = bySecond.Count,
            ComparableSeconds = comparable,
            ObservedMultiStallSeconds = observed,
            ExpectedMultiStallSeconds = expected,
            Variance = variance,
            PUpper = pUp,
            PLower = pLow,
            Verdict = Decide(expected, observed, pUp, pLow),
            Machines = machines,
            PlayerComparableStalls = pc,
            PlayerAloneStalls = pa,
            PlayerExpectedAlone = pe,
            ServerWithPlayerSeconds = serverWith,
            ServerWithPlayerExpected = serverWithExp,
            AloneVerdict = pc == 0 ? Verdict.InsufficientData : DecideAlone(pc, pa, pe, playerAloneVariance),
            AlonePUpper = apUp,
            AlonePLower = apLow,
        };
    }

    /// <summary>P(pelo menos 2 sucessos) de Bernoullis independentes.</summary>
    public static double ProbAtLeastTwo(IEnumerable<double> ps)
    {
        double p0 = 1, p1 = 0;
        foreach (double p in ps)
        {
            p1 = p1 * (1 - p) + p0 * p;
            p0 *= 1 - p;
        }
        return Math.Max(0, 1 - p0 - p1);
    }

    private static Verdict Decide(double expected, int observed, double pUp, double pLow)
    {
        if (expected < MinExpectedForVerdict) return Verdict.InsufficientData;
        if (pUp < Alpha) return Verdict.Shared;
        if (pLow < Alpha) return Verdict.Independent;
        return Verdict.Coincidence;
    }

    private static Verdict DecideAlone(int comparable, int alone, double expectedAlone, double variance)
    {
        // Para ALONE o raciocinio inverte: ALONE >> acaso = independentes;
        // ALONE << acaso = causa compartilhada.
        if (comparable < 20) return Verdict.InsufficientData;
        var (up, low) = Tails(alone, expectedAlone, variance);
        if (up < Alpha) return Verdict.Independent;
        if (low < Alpha) return Verdict.Shared;
        return Verdict.Coincidence;
    }

    /// <summary>P(X>=obs) e P(X&lt;=obs): Poisson exato se pequeno, normal com correcao de continuidade se grande.</summary>
    public static (double up, double low) Tails(int observed, double mean, double variance)
    {
        if (mean <= 0) return (observed > 0 ? 0 : 1, 1);
        if (mean < 50)
        {
            double term = Math.Exp(-mean), cdf = term;           // P(X<=0)
            double cdfBelow = 0;                                  // P(X<=obs-1)
            for (int k = 1; k <= observed; k++)
            {
                cdfBelow = cdf;
                term *= mean / k;
                cdf += term;
            }
            if (observed == 0) cdfBelow = 0;
            return (Math.Clamp(1 - cdfBelow, 0, 1), Math.Clamp(cdf, 0, 1));
        }
        double sd = Math.Sqrt(Math.Max(variance, 1e-9));
        double zUp = (observed - 0.5 - mean) / sd, zLow = (observed + 0.5 - mean) / sd;
        return (1 - NormalCdf(zUp), NormalCdf(zLow));
    }

    private static double NormalCdf(double z) => 0.5 * Erfc(-z / Math.Sqrt(2));

    // Erfc de Numerical Recipes (erro relativo < 1.2e-7), suficiente para p-valores de decisao.
    private static double Erfc(double x)
    {
        double z = Math.Abs(x), t = 1 / (1 + 0.5 * z);
        double r = t * Math.Exp(-z * z - 1.26551223 + t * (1.00002368 + t * (0.37409196 + t * (0.09678418 +
            t * (-0.18628806 + t * (0.27886807 + t * (-1.13520398 + t * (1.48851587 +
            t * (-0.82215223 + t * 0.17087277)))))))));
        return x >= 0 ? r : 2 - r;
    }
}

/// <summary>Le o CSV de incidentes (caixa-preta) e produz um MachineSecond por maquina/segundo valido.</summary>
public static class IncidentCsv
{
    public sealed class ReadSummary
    {
        public int Rows { get; set; }
        public int PlayerRowsUsed { get; set; }
        public int PlayerRowsIgnored { get; set; }
        public int ServerSecondsUsed { get; set; }
    }

    public static IEnumerable<MachineSecond> Read(TextReader reader, AnalysisOptions options, ReadSummary? summary = null)
    {
        summary ??= new ReadSummary();
        string? headerLine = reader.ReadLine();
        if (headerLine == null) yield break;
        var header = SplitLine(headerLine);
        var index = new Dictionary<string, int>();
        for (int i = 0; i < header.Count; i++) index[header[i]] = i;
        foreach (var required in new[] { "utc", "player", "uid", "client_frame_max_ms", "server_frame_max_ms" })
            if (!index.ContainsKey(required)) throw new FormatException($"coluna ausente no CSV: {required}");

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            var f = SplitLine(line);
            if (f.Count < header.Count) continue; // linha truncada (CSV ainda em escrita)
            summary.Rows++;

            if (!DateTime.TryParse(Get(f, index, "utc"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc)) continue;
            long second = new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeSeconds();

            // O servidor reporta em toda linha do segundo.
            if (TryNum(f, index, "server_frame_max_ms", out double srv) && TryNum(f, index, "server_frame_avg_ms", out double srvAvg) && srvAvg > 0)
            {
                summary.ServerSecondsUsed++;
                yield return new MachineSecond(second, Machines.Server, "servidor", srv >= options.ThresholdMs);
            }

            // Jogador conta so com relatorio fresco, janela com foco, pronto e sem teleporte:
            // fora disso o frame alto e do cliente em segundo plano / carregando, nao do jogo.
            bool ok = Flag(f, index, "client_present") && Flag(f, index, "client_fresh")
                      && Flag(f, index, "client_focused") && Flag(f, index, "client_player_ready")
                      && !Flag(f, index, "client_teleporting")
                      && TryNum(f, index, "client_frame_max_ms", out _);
            if (!ok) { summary.PlayerRowsIgnored++; continue; }
            TryNum(f, index, "client_frame_max_ms", out double cl);
            summary.PlayerRowsUsed++;
            string uid = Get(f, index, "uid");
            yield return new MachineSecond(second, "uid:" + uid, Get(f, index, "player"), cl >= options.ThresholdMs);
        }
    }

    private static string Get(List<string> f, Dictionary<string, int> idx, string col)
        => idx.TryGetValue(col, out int i) && i < f.Count ? f[i] : "";

    private static bool Flag(List<string> f, Dictionary<string, int> idx, string col)
    {
        string v = Get(f, idx, col);
        return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNum(List<string> f, Dictionary<string, int> idx, string col, out double v)
        => double.TryParse(Get(f, idx, col), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    /// <summary>Divide uma linha CSV respeitando aspas e "" escapado.</summary>
    public static List<string> SplitLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields;
    }
}

public static class ReportWriter
{
    public static string VerdictText(Verdict v) => v switch
    {
        Verdict.Shared => "CAUSA COMPARTILHADA (observado >> acaso)",
        Verdict.Coincidence => "COINCIDENCIA (observado ~ acaso)",
        Verdict.Independent => "INDEPENDENTES (observado << acaso)",
        _ => "DADOS INSUFICIENTES",
    };

    public static string Format(AnalysisResult r, bool listAlone)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine($"Limiar de stall: frame max >= {r.ThresholdMs.ToString("0.#", ci)} ms");
        sb.AppendLine($"Segundos distintos: {r.TotalSeconds}; comparaveis (>=2 maquinas reportando): {r.ComparableSeconds}");
        sb.AppendLine();
        sb.AppendLine("== Segundos com >=2 stalls simultaneos ==");
        sb.AppendLine($"observado {r.ObservedMultiStallSeconds}, esperado por acaso {r.ExpectedMultiStallSeconds.ToString("0.00", ci)} " +
                      $"(razao {Ratio(r.ObservedMultiStallSeconds, r.ExpectedMultiStallSeconds)}), " +
                      $"P(>=obs)={Fmt(r.PUpper)}, P(<=obs)={Fmt(r.PLower)}");
        sb.AppendLine($"veredito: {VerdictText(r.Verdict)}");
        sb.AppendLine($"servidor + algum jogador juntos: observado {r.ServerWithPlayerSeconds}, esperado {r.ServerWithPlayerExpected.ToString("0.00", ci)}");
        sb.AppendLine();
        sb.AppendLine("== Stalls de jogador sozinhos (ALONE) vs acaso ==");
        sb.AppendLine($"stalls comparaveis {r.PlayerComparableStalls}, ALONE {r.PlayerAloneStalls} " +
                      $"({Pct(r.PlayerAloneStalls, r.PlayerComparableStalls)}), esperado por acaso {r.PlayerExpectedAlone.ToString("0.0", ci)} " +
                      $"({Pct(r.PlayerExpectedAlone, r.PlayerComparableStalls)}), P(>=obs)={Fmt(r.AlonePUpper)}, P(<=obs)={Fmt(r.AlonePLower)}");
        sb.AppendLine($"veredito (ALONE): {AloneText(r.AloneVerdict)}");
        sb.AppendLine();
        sb.AppendLine("== Por maquina ==");
        sb.AppendLine($"{"maquina",-24} {"seg",7} {"stalls",7} {"taxa%",6} {"compar.",8} {"ALONE",6} {"ALONE%",7} {"esp.%",6}");
        foreach (var m in r.Machines)
            sb.AppendLine($"{Trunc(m.Label, 24),-24} {m.ReportedSeconds,7} {m.StallSeconds,7} {(m.Rate * 100).ToString("0.00", ci),6} " +
                          $"{m.ComparableStalls,8} {m.AloneStalls,6} {(m.AloneFraction * 100).ToString("0", ci),7} {(m.ExpectedAloneFraction * 100).ToString("0", ci),6}");
        if (listAlone)
        {
            sb.AppendLine();
            sb.AppendLine("== ALONE (UTC) ==");
            foreach (var m in r.Machines.Where(m => m.AloneSeconds.Count > 0))
                sb.AppendLine($"{m.Label}: " + string.Join(", ", m.AloneSeconds.Select(s =>
                    DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime.ToString("MM-dd HH:mm:ss", ci))));
        }
        return sb.ToString();
    }

    private static string AloneText(Verdict v) => v switch
    {
        Verdict.Independent => "ALONE acima do acaso: stalls em boa parte independentes/locais",
        Verdict.Shared => "ALONE abaixo do acaso: stalls agrupam (causa compartilhada)",
        Verdict.Coincidence => "ALONE ~ acaso: o 'sozinho' e o que a independencia ja prevê",
        _ => "DADOS INSUFICIENTES",
    };

    private static string Ratio(double obs, double exp) => exp <= 0 ? "n/a" : (obs / exp).ToString("0.0", CultureInfo.InvariantCulture) + "x";
    private static string Fmt(double p) => p < 1e-4 ? "<1e-4" : p.ToString("0.0000", CultureInfo.InvariantCulture);
    private static string Pct(double a, double b) => b == 0 ? "n/a" : (100 * a / b).ToString("0.0", CultureInfo.InvariantCulture) + "%";
    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n];
}
