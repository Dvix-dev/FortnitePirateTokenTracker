using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FortnitePirateTokenTracker.Core.Configuration;
using FortnitePirateTokenTracker.Core.Models;
using FortniteReplayReader.Models;
using Unreal.Core.Models.Enums;

namespace FortnitePirateTokenTracker.App.Parsing;

/// <summary>Procesa repeticiones y cachea el resultado para no repetir trabajo.</summary>
public sealed class ReplayAnalyzer
{
    private const string CacheFileName = "parsed-replays.json";

    private readonly AppConfig _config;
    private readonly string _dataDir;
    private readonly string _cachePath;
    private CacheFile _cache;

    public ReplayAnalyzer(AppConfig config, string dataDir)
    {
        _config = config;
        _dataDir = dataDir;
        _cachePath = Path.Combine(dataDir, "cache", CacheFileName);
        _cache = LoadCache();
    }

    public Action<string>? Log { get; set; }

    public List<string> ListReplays()
    {
        if (!Directory.Exists(_config.ReplayFolder))
        {
            return new List<string>();
        }

        return Directory.EnumerateFiles(_config.ReplayFolder, "*.replay", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
    }

    public (List<ParsedMatch> Matches, int FromCache, int Errors) AnalyzeAll(int? limit, bool force)
    {
        var files = ListReplays();
        if (limit is > 0)
        {
            files = files.Take(limit.Value).ToList();
        }

        var matches = new List<ParsedMatch>();
        var fromCache = 0;
        var errors = 0;

        foreach (var file in files)
        {
            var info = new FileInfo(file);
            var key = Path.GetFileName(file);

            if (!force && _cache.Entries.TryGetValue(key, out var cached)
                && cached.Size == info.Length
                && cached.LastWriteTicks == info.LastWriteTimeUtc.Ticks
                && cached.Match is not null)
            {
                fromCache++;
                matches.Add(cached.Match);
                Log?.Invoke($"= {key} (caché)");
                continue;
            }

            Log?.Invoke($"> {key}");
            var parsed = AnalyzeFile(file);
            if (parsed.Error is not null)
            {
                errors++;
            }

            matches.Add(parsed);
            _cache.Entries[key] = new CacheEntry
            {
                Size = info.Length,
                LastWriteTicks = info.LastWriteTimeUtc.Ticks,
                Match = parsed
            };
        }

        SaveCache();
        return (matches, fromCache, errors);
    }

    public ParsedMatch AnalyzeFile(string file)
    {
        var parsed = new ParsedMatch
        {
            ReplayFile = Path.GetFileName(file),
            MatchId = BuildMatchId(file),
            PlayedAt = ParseDateFromName(file)
        };

        var reader = new CapturingReader(ParseMode.Debug);
        FortniteReplay? replay = null;

        try
        {
            using var stream = File.OpenRead(file);
            replay = reader.ReadReplay(stream);
        }
        catch (Exception ex)
        {
            parsed.Error = $"{ex.GetType().Name}: {ex.Message}";
        }

        parsed.Warnings.AddRange(reader.Warnings.Distinct().Take(25));

        if (replay is not null)
        {
            try
            {
                FillFromReplay(parsed, replay);
            }
            catch (Exception ex)
            {
                parsed.Warnings.Add($"Metadatos: {ex.Message}");
            }
        }

        try
        {
            FillChests(parsed, reader, replay);
        }
        catch (Exception ex)
        {
            parsed.Warnings.Add($"Cofres: {ex.Message}");
        }

        return parsed;
    }

    private void FillFromReplay(ParsedMatch parsed, FortniteReplay replay)
    {
        var players = replay.PlayerData?.ToList() ?? new List<PlayerData>();
        parsed.Players = players.Count;
        parsed.Mode = DetectMode(replay);
        parsed.LengthSeconds = DetectLength(replay);

        foreach (var player in players)
        {
            if (player.IsReplayOwner)
            {
                parsed.LocalPlayerFound = true;
            }

            var name = player.PlayerName ?? player.StreamerModeName ?? player.PlayerNameCustomOverride
                       ?? (player.IsBot == true ? "Bot" : $"jugador-{player.PlayerNumber}");

            AddCosmetic(parsed, name, player.IsReplayOwner, "outfit", player.Cosmetics?.Character);
            AddCosmetic(parsed, name, player.IsReplayOwner, "pickaxe", player.Cosmetics?.Pickaxe);
            AddCosmetic(parsed, name, player.IsReplayOwner, "backBling", player.Cosmetics?.Backpack);
            AddCosmetic(parsed, name, player.IsReplayOwner, "glider", player.Cosmetics?.Glider);
            AddCosmetic(parsed, name, player.IsReplayOwner, "loadingScreen", player.Cosmetics?.LoadingScreen);
            AddCosmetic(parsed, name, player.IsReplayOwner, "musicPack", player.Cosmetics?.MusicPack);
            AddCosmetic(parsed, name, player.IsReplayOwner, "pet", player.Cosmetics?.PetSkin);
            AddCosmetic(parsed, name, player.IsReplayOwner, "wrap", player.Cosmetics?.ItemWraps?.FirstOrDefault());
        }
    }

    private static void AddCosmetic(ParsedMatch match, string player, bool isLocal, string slot, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "None" || value.Length < 2)
        {
            return;
        }

        match.Cosmetics.Add(new CosmeticEntry
        {
            PlayerName = player,
            IsLocalPlayer = isLocal,
            Slot = slot,
            ItemPath = ShortenAsset(value)
        });
    }

    private void FillChests(ParsedMatch parsed, CapturingReader reader, FortniteReplay? replay)
    {
        foreach (var container in reader.Containers.OrderBy(c => c.TimeSeconds))
        {
            var itemId = container.ItemId;
            parsed.Chests.Add(new ParsedChest
            {
                TimeMs = (int)Math.Round(container.TimeSeconds * 1000),
                ContainerClass = container.ClassName,
                ItemId = itemId,
                IsToken = LooksLikeToken(itemId),
                Confidence = LooksLikeToken(itemId) ? "B" : "C",
                X = container.X,
                Y = container.Y,
                Z = container.Z,
                PositionApproximate = !container.HasPosition,
                SearchAnimationCount = container.SearchAnimationCount
            });
        }

        // Los pickups con identificador de Token también cuentan como hallazgo del evento.
        if (replay is null)
        {
            return;
        }

        foreach (var pickup in reader.Pickups.Where(p => LooksLikeToken(p.ItemId)))
        {
            parsed.Chests.Add(new ParsedChest
            {
                TimeMs = (int)Math.Round(pickup.TimeSeconds * 1000),
                ContainerClass = pickup.ClassName,
                ItemId = pickup.ItemId,
                IsToken = true,
                Confidence = "B",
                PositionApproximate = true
            });
        }
    }

    private static bool LooksLikeToken(string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return false;
        }

        return itemId.Contains("Token", StringComparison.OrdinalIgnoreCase)
               || itemId.Contains("PirateCoin", StringComparison.OrdinalIgnoreCase)
               || itemId.Contains("GoldCoin", StringComparison.OrdinalIgnoreCase);
    }

    private static string DetectMode(FortniteReplay replay)
    {
        var text = TryReadString(replay.GameData, "PlaylistName")
                   ?? TryReadString(replay.GameData, "Playlist")
                   ?? TryReadString(replay.GameData, "GameMode");
        if (string.IsNullOrWhiteSpace(text))
        {
            return "Desconocido";
        }

        return text.Contains("Creative", StringComparison.OrdinalIgnoreCase) ? "Creativo" : text;
    }

    private static int DetectLength(FortniteReplay replay)
    {
        var value = TryReadNumber(replay.Info, "LengthInMs");
        return value is null ? 0 : (int)Math.Round(value.Value / 1000);
    }

    private static string? TryReadString(object? source, string name)
    {
        var value = source?.GetType().GetProperty(name)?.GetValue(source);
        return value?.ToString();
    }

    private static double? TryReadNumber(object? source, string name)
    {
        if (source is null)
        {
            return null;
        }

        var value = source.GetType().GetProperty(name)?.GetValue(source);
        return value is null ? null : Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    private static string ShortenAsset(string value)
    {
        var name = value.Contains('.') ? value[..value.IndexOf('.')] : value;
        var slash = name.LastIndexOf('/');
        return slash >= 0 ? name[(slash + 1)..] : name;
    }

    private static string BuildMatchId(string file) => Path.GetFileNameWithoutExtension(file);

    private static DateTimeOffset? ParseDateFromName(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var parts = name.Split('-');
        if (parts.Length < 3)
        {
            return null;
        }

        var datePart = parts[^2];
        var timePart = parts[^1];
        var text = $"{datePart} {timePart.Replace('.', ':')}";
        return DateTimeOffset.TryParseExact(text, "yyyy.MM.dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out var value)
            ? value
            : null;
    }

    private CacheFile LoadCache()
    {
        try
        {
            if (File.Exists(_cachePath))
            {
                var json = File.ReadAllText(_cachePath);
                return JsonSerializer.Deserialize<CacheFile>(json, AppConfig.SerializerOptions) ?? new CacheFile();
            }
        }
        catch
        {
            // Caché ilegible: se reconstruye en el siguiente análisis.
        }

        return new CacheFile();
    }

    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(_cache, AppConfig.SerializerOptions));
        }
        catch
        {
            // La caché es una optimización: un fallo al escribirla no invalida el informe.
        }
    }

    private sealed class CacheFile
    {
        public int SchemaVersion { get; set; } = 1;
        public string ParserVersion { get; set; } = "FortniteReplayReader-3.1.0";
        public Dictionary<string, CacheEntry> Entries { get; set; } = new();
    }

    private sealed class CacheEntry
    {
        public long Size { get; set; }
        public long LastWriteTicks { get; set; }
        public ParsedMatch? Match { get; set; }
    }
}
