namespace FortnitePirateTokenTracker.Core.Models;

/// <summary>Resultado de parsear un único archivo .replay, antes de agregarse.</summary>
public sealed class ParsedMatch
{
    public string MatchId { get; set; } = "";
    public string ReplayFile { get; set; } = "";
    public DateTimeOffset? PlayedAt { get; set; }
    public string Mode { get; set; } = "Desconocido";
    public int LengthSeconds { get; set; }
    public int Players { get; set; }
    public bool LocalPlayerFound { get; set; }
    public List<ParsedChest> Chests { get; set; } = new();
    public List<CosmeticEntry> Cosmetics { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public string? Error { get; set; }
    public int ParseWarnings => Warnings.Count;
}

public sealed class ParsedChest
{
    public int TimeMs { get; set; }
    public string ContainerClass { get; set; } = "";
    public string? ItemId { get; set; }
    public bool IsToken { get; set; }
    public string? Category { get; set; }
    public string Confidence { get; set; } = "C";
    public string? PlayerName { get; set; }
    public bool IsLocalPlayer { get; set; }
    public bool IsSquad { get; set; }
    public bool Destroyed { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public bool PositionApproximate { get; set; } = true;
    public uint SearchAnimationCount { get; set; }
}
