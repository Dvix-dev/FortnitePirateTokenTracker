using System.Text.Json;
using FortnitePirateTokenTracker.App.Parsing;
using FortnitePirateTokenTracker.Engine;
using FortnitePirateTokenTracker.Core.Configuration;
using FortnitePirateTokenTracker.Core.Models;
using FortnitePirateTokenTracker.Core.Reporting;

namespace FortnitePirateTokenTracker.App.Reporting;

/// <summary>Ejecuta el análisis completo y guarda los resultados en disco.</summary>
public static class ScanRunner
{
    public static string ReportPath(string dataDir) => Path.Combine(dataDir, "report.json");
    public static string CsvPath(string dataDir) => Path.Combine(dataDir, "resultados_token.csv");

    public static ScanReport Run(AppConfig config, string dataDir, int? limit, bool force, Action<string>? log = null)
    {
        var analyzer = new ReplayAnalyzer(config, dataDir) { Log = log };
        var (matches, fromCache, errors) = analyzer.AnalyzeAll(limit, force);
        var report = ReportBuilder.Build(matches, config, fromCache, errors);

        Directory.CreateDirectory(dataDir);
        File.WriteAllText(ReportPath(dataDir), JsonSerializer.Serialize(report, AppConfig.SerializerOptions));
        CsvWriter.Write(CsvPath(dataDir), report.Chests, config.CsvSeparator);

        config.LastScanUtc = DateTimeOffset.UtcNow;
        config.Save(dataDir);

        return report;
    }

    public static ScanReport? LoadReport(string dataDir)
    {
        var path = ReportPath(dataDir);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ScanReport>(File.ReadAllText(path), AppConfig.SerializerOptions);
        }
        catch
        {
            return null;
        }
    }

    public static string SummaryText(ScanReport report)
    {
        var t = report.Totals;
        return string.Join(Environment.NewLine, new[]
        {
            $"Repeticiones encontradas : {t.ReplaysFound}",
            $"  analizadas             : {t.ReplaysAnalyzed}",
            $"  desde caché            : {t.ReplaysFromCache}",
            $"  con error              : {t.ReplaysWithErrors}",
            $"Cofres detectados        : {t.ChestsDetected} (míos: {t.ChestsByMe})",
            $"Tokens detectados        : {t.TokenChests} (míos: {t.TokenChestsByMe}, otros: {t.TokenChestsByOthers})",
            $"Partidas con Token       : {t.MatchesWithToken} ({t.TokenRate:0.##}%)",
            $"Partidas con 2+ Tokens   : {t.MatchesWithMultipleTokens}",
            $"Media de cofres/partida  : {t.AverageChestsPerMatch:0.##}"
        });
    }
}
