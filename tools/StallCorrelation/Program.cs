using System.Globalization;
using StallCorrelation;

// Uso: StallCorrelation <csv|pasta>... [--limiar ms] [--listar-alone] [--sem-servidor]
// Ferramenta offline: le CSVs de incidentes (caixa-preta) e responde se os stalls coincidem.
double threshold = 250;
bool listAlone = false, noServer = false;
var inputs = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--limiar" when i + 1 < args.Length:
            threshold = double.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--listar-alone":
            listAlone = true;
            break;
        case "--sem-servidor":
            noServer = true;
            break;
        case "-h" or "--ajuda":
            Console.WriteLine("Uso: StallCorrelation <csv|pasta>... [--limiar ms (padrao 250)] [--listar-alone] [--sem-servidor]");
            return 0;
        default:
            inputs.Add(args[i]);
            break;
    }
}

var files = new List<string>();
foreach (var input in inputs)
{
    if (Directory.Exists(input)) files.AddRange(Directory.GetFiles(input, "incidents-*.csv", SearchOption.AllDirectories));
    else if (File.Exists(input)) files.Add(input);
    else { Console.Error.WriteLine($"nao encontrado: {input}"); return 2; }
}
if (files.Count == 0) { Console.Error.WriteLine("nenhum CSV de incidentes informado (use --ajuda)"); return 2; }

var options = new AnalysisOptions { ThresholdMs = threshold };
var summary = new IncidentCsv.ReadSummary();
var samples = new List<MachineSecond>();
foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
{
    using var reader = new StreamReader(file);
    samples.AddRange(IncidentCsv.Read(reader, options, summary));
}

Console.WriteLine($"Arquivos: {files.Count}; linhas: {summary.Rows}; linhas de jogador usadas: {summary.PlayerRowsUsed}, " +
                  $"ignoradas (sem foco/pronto/relatorio fresco ou teleportando): {summary.PlayerRowsIgnored}");
Console.Write(ReportWriter.Format(Analyzer.Analyze(noServer ? samples.Where(x => x.Machine != Machines.Server) : samples, options), listAlone));
return 0;
