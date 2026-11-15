using FortnitePirateTokenTracker.Core.Catalog;
using FortnitePirateTokenTracker.Core.Configuration;
using FortnitePirateTokenTracker.Core.Models;

namespace FortnitePirateTokenTracker.Core.Reporting;

/// <summary>Convierte los resultados por partida en el informe agregado que muestra el panel.</summary>
public static class ReportBuilder
{
    /// <summary>Lado de celda de agrupación geográfica, en unidades Unreal (50 m).</summary>
    private const double ZoneCellUnits = 5000;

    public static ScanReport Build(IReadOnlyList<ParsedMatch> matches, AppConfig config, int fromCache, int withErrors)
    {
        var report = new ScanReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Config = new ReportConfig
            {
                ReplayFolder = config.ReplayFolder,
                RadiusMeters = config.RadiusMeters,
                MinConfidence = config.MinConfidence,
                Anonymize = config.Anonymize
            }
        };

        foreach (var match in matches.OrderBy(m => m.PlayedAt ?? DateTimeOffset.MinValue))
        {
            var summary = new MatchSummary
            {
                MatchId = match.MatchId,
                ReplayFile = match.ReplayFile,
                PlayedAt = match.PlayedAt,
                Mode = match.Mode,
                LengthSeconds = match.LengthSeconds,
                Players = match.Players,
                Chests = match.Chests.Count,
                MyChests = match.Chests.Count(c => c.IsLocalPlayer),
                Tokens = match.Chests.Count(c => c.IsToken),
                MyTokens = match.Chests.Count(c => c.IsToken && c.IsLocalPlayer),
                ParseWarnings = match.Warnings.Count,
                Error = match.Error,
                LocalPlayerFound = match.LocalPlayerFound
            };

            report.Matches.Add(summary);
            report.Cosmetics.AddRange(match.Cosmetics.Select(c => new CosmeticEntry
            {
                PlayerName = SanitizeName(c.PlayerName, config.Anonymize),
                IsLocalPlayer = c.IsLocalPlayer,
                Slot = c.Slot,
                ItemPath = c.ItemPath
            }));

            var chestIndex = 0;
            var tokenIndex = 0;
            foreach (var chest in match.Chests.OrderBy(c => c.TimeMs))
            {
                chestIndex++;
                if (chest.IsToken)
                {
                    tokenIndex++;
                }

                report.Chests.Add(new ChestEvent
                {
                    MatchId = match.MatchId,
                    ReplayFile = match.ReplayFile,
                    TimeMs = chest.TimeMs,
                    TimeLabel = FormatTime(chest.TimeMs),
                    ContainerClass = TreasureCatalog.DescribeContainer(chest.ContainerClass),
                    PlayerName = SanitizeName(chest.PlayerName, config.Anonymize),
                    IsLocalPlayer = chest.IsLocalPlayer,
                    IsToken = chest.IsToken,
                    Confidence = chest.Confidence,
                    ItemId = chest.ItemId,
                    Category = chest.IsToken ? "token" : null,
                    X = Math.Round(chest.X, 1),
                    Y = Math.Round(chest.Y, 1),
                    Z = Math.Round(chest.Z, 1),
                    PositionApproximate = chest.PositionApproximate,
                    Zone = ZoneName(chest),
                    ChestIndexInMatch = chestIndex,
                    TokenIndexInMatch = tokenIndex
                });
            }

            report.Warnings.AddRange(match.Warnings.Take(20).Select(w => $"{match.ReplayFile}: {w}"));
            if (!string.IsNullOrEmpty(match.Error))
            {
                report.Warnings.Add($"{match.ReplayFile}: {match.Error}");
            }
        }

        var tokens = report.Chests.Where(c => c.IsToken).ToList();
        var matchesWithToken = report.Matches.Count(m => m.Tokens > 0);
        var analyzedMatches = report.Matches.Count(m => m.Error is null);

        report.Totals = new ScanTotals
        {
            ReplaysFound = matches.Count,
            ReplaysAnalyzed = analyzedMatches,
            ReplaysFromCache = fromCache,
            ReplaysWithErrors = withErrors,
            ChestsDetected = report.Chests.Count,
            ChestsByMe = report.Chests.Count(c => c.IsLocalPlayer),
            TokenChests = tokens.Count,
            TokenChestsByMe = tokens.Count(c => c.IsLocalPlayer),
            TokenChestsByOthers = tokens.Count(c => !c.IsLocalPlayer),
            MatchesWithToken = matchesWithToken,
            MatchesWithMultipleTokens = report.Matches.Count(m => m.Tokens > 1),
            TokenRate = analyzedMatches == 0 ? 0 : Math.Round((double)matchesWithToken / analyzedMatches * 100, 2),
            AverageChestsPerMatch = analyzedMatches == 0 ? 0 : Math.Round((double)report.Chests.Count / analyzedMatches, 2),
            TotalSecondsAnalyzed = report.Matches.Sum(m => m.LengthSeconds)
        };

        report.Categories = TreasureCatalog.Prizes
            .Select(p => new CategoryStat
            {
                Key = p.Key,
                Label = p.Label,
                Odds = p.Odds,
                Accent = p.Accent,
                Found = p.Key == "token"
                    ? tokens.Count
                    : report.Chests.Count(c => c.Category == p.Key)
            })
            .ToList();

        report.Zones = report.Chests
            .GroupBy(c => c.Zone)
            .Select(g => new ZoneStat
            {
                Name = g.Key,
                X = Math.Round(g.Average(c => c.X), 0),
                Y = Math.Round(g.Average(c => c.Y), 0),
                Chests = g.Count(),
                Tokens = g.Count(c => c.IsToken)
            })
            .OrderByDescending(z => z.Chests)
            .ToList();

        return report;
    }

    private static string ZoneName(ParsedChest chest)
    {
        if (chest.PositionApproximate && chest.X == 0 && chest.Y == 0)
        {
            return "desconocida";
        }

        var cx = (long)Math.Floor(chest.X / ZoneCellUnits);
        var cy = (long)Math.Floor(chest.Y / ZoneCellUnits);
        return $"Celda {cx}:{cy}";
    }

    private static string? SanitizeName(string? name, bool anonymize)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        if (!anonymize)
        {
            return name;
        }

        // Identificador estable y no reversible para poder compartir resultados.
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name));
        return "jugador-" + Convert.ToHexString(hash)[..6].ToLowerInvariant();
    }

    public static string FormatTime(int timeMs)
    {
        var span = TimeSpan.FromMilliseconds(Math.Max(0, timeMs));
        return $"{(int)span.TotalMinutes:00}:{span.Seconds:00}";
    }
}
