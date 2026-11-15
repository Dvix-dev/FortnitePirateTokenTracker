using System.Text.Json;
using System.Text.Json.Serialization;

namespace FortnitePirateTokenTracker.Core.Configuration;

/// <summary>Ajustes editables desde el panel web (sección Configuración).</summary>
public sealed class AppConfig
{
    public const string DefaultPort = "5178";

    public string ReplayFolder { get; set; } = DefaultReplayFolder();

    /// <summary>Radio de relevancia en metros alrededor del jugador local.</summary>
    public int RadiusMeters { get; set; } = 1000;

    public int Port { get; set; } = int.Parse(DefaultPort);

    public string CsvSeparator { get; set; } = ";";

    public string MinConfidence { get; set; } = "B";

    public bool Anonymize { get; set; }

    public DateTimeOffset? LastScanUtc { get; set; }

    public static string DefaultReplayFolder()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "FortniteGame", "Saved", "Demos");
    }

    public static string ConfigPath(string dataDir) => Path.Combine(dataDir, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static AppConfig Load(string dataDir)
    {
        var path = ConfigPath(dataDir);
        if (!File.Exists(path))
        {
            return new AppConfig();
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
        }
        catch
        {
            // Un config corrupto no debe impedir arrancar: se regenera con valores por defecto.
            return new AppConfig();
        }
    }

    public void Save(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(ConfigPath(dataDir), JsonSerializer.Serialize(this, JsonOptions));
    }

    public static JsonSerializerOptions SerializerOptions => JsonOptions;
}
