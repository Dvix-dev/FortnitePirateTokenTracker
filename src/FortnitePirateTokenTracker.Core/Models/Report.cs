namespace FortnitePirateTokenTracker.Core.Models;

/// <summary>Informe agregado de un análisis. Es lo que consume el panel web.</summary>
public sealed class ScanReport
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
    public string ToolVersion { get; set; } = "0.2.0";
    public string ParserVersion { get; set; } = "FortniteReplayReader 3.1.0";
    public ReportConfig Config { get; set; } = new();
    public ScanTotals Totals { get; set; } = new();
    public List<MatchSummary> Matches { get; set; } = new();
    public List<ChestEvent> Chests { get; set; } = new();
    public List<CategoryStat> Categories { get; set; } = new();
    public List<ZoneStat> Zones { get; set; } = new();
    public List<CosmeticEntry> Cosmetics { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

public sealed class ReportConfig
{
    public string ReplayFolder { get; set; } = "";
    public int RadiusMeters { get; set; }
    public string MinConfidence { get; set; } = "B";
    public bool Anonymize { get; set; }
}

public sealed class ScanTotals
{
    public int ReplaysFound { get; set; }
    public int ReplaysAnalyzed { get; set; }
    public int ReplaysFromCache { get; set; }
    public int ReplaysWithErrors { get; set; }
    public int ChestsDetected { get; set; }
    public int ChestsByMe { get; set; }
    public int TokenChests { get; set; }
    public int TokenChestsByMe { get; set; }
    public int TokenChestsByOthers { get; set; }
    public int MatchesWithToken { get; set; }
    public int MatchesWithMultipleTokens { get; set; }
    public double TokenRate { get; set; }
    public double AverageChestsPerMatch { get; set; }
    public int TotalSecondsAnalyzed { get; set; }
}

public sealed class MatchSummary
{
    public string MatchId { get; set; } = "";
    public string ReplayFile { get; set; } = "";
    public DateTimeOffset? PlayedAt { get; set; }
    public string Mode { get; set; } = "";
    public int LengthSeconds { get; set; }
    public int Players { get; set; }
    public int Chests { get; set; }
    public int MyChests { get; set; }
    public int Tokens { get; set; }
    public int MyTokens { get; set; }
    public int ParseWarnings { get; set; }
    public string? Error { get; set; }
    public bool LocalPlayerFound { get; set; }
}

public sealed class ChestEvent
{
    public string MatchId { get; set; } = "";
    public string ReplayFile { get; set; } = "";
    public int TimeMs { get; set; }
    public string TimeLabel { get; set; } = "00:00";
    public string ContainerClass { get; set; } = "";
    public string? PlayerName { get; set; }
    public bool IsLocalPlayer { get; set; }
    public bool IsToken { get; set; }
    public string Confidence { get; set; } = "C";
    public string? ItemId { get; set; }
    public string? Category { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public bool PositionApproximate { get; set; }
    public string Zone { get; set; } = "desconocida";
    public int ChestIndexInMatch { get; set; }
    public int TokenIndexInMatch { get; set; }
}

public sealed class CategoryStat
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string? Odds { get; set; }
    public string Accent { get; set; } = "#ffd400";
    public int Found { get; set; }
}

public sealed class ZoneStat
{
    public string Name { get; set; } = "desconocida";
    public double X { get; set; }
    public double Y { get; set; }
    public int Chests { get; set; }
    public int Tokens { get; set; }
}

public sealed class CosmeticEntry
{
    public string PlayerName { get; set; } = "";
    public bool IsLocalPlayer { get; set; }
    public string Slot { get; set; } = "";
    public string? ItemPath { get; set; }
}
