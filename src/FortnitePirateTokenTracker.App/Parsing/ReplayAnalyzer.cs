using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FortnitePirateTokenTracker.Core.Catalog;
using FortnitePirateTokenTracker.Core.Configuration;
using FortnitePirateTokenTracker.Core.Models;
using FortniteReplayReader.Models;
using Unreal.Core.Models.Enums;

namespace FortnitePirateTokenTracker.App.Parsing;

/// <summary>Procesa repeticiones y cachea el resultado para no repetir trabajo.</summary>
public sealed class ReplayAnalyzer
{
    private const string CacheFileName = "parsed-replays.json";

    /// <summary>Subir esto invalida la caché cuando cambian las reglas de detección.</summary>
    private const string CurrentParserVersion = "10";

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
        var playersByKey = new Dictionary<string, (string Name, bool IsLocal)>(StringComparer.Ordinal);
        var tracks = new List<PlayerTrack>();

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
                FillFromReplay(parsed, replay, playersByKey, tracks);
            }
            catch (Exception ex)
            {
                parsed.Warnings.Add($"Metadatos: {ex.Message}");
            }
        }

        try
        {
            var players = new PlayerIndex(tracks);
            FillChests(parsed, reader, replay, playersByKey, players);
            FillPawnCosmetics(parsed, reader, players);
        }
        catch (Exception ex)
        {
            parsed.Warnings.Add($"Cofres: {ex.Message}");
        }

        return parsed;
    }

    /// <summary>Trayectoria de un jugador, ordenada por tiempo, para atribuir por posición.</summary>
    private sealed record PlayerTrack(string Name, bool IsLocal, List<(double T, double X, double Y, double Z)> Samples);

    /// <summary>
    /// Índice espacial de las trayectorias: agrupa las muestras en celdas de 1.000 uu
    /// para atribuir por proximidad sin recorrer cientos de miles de puntos por cofre.
    /// </summary>
    private sealed class PlayerIndex
    {
        private const double CellSize = 1000;
        private const double MaxDistanceSquared = 500 * 500;
        private const double UniqueMarginSquared = 1500 * 1500;

        private readonly List<PlayerTrack> _tracks;
        private readonly Dictionary<long, List<(int Track, double T, double X, double Y, double Z)>> _cells = new();

        public PlayerIndex(List<PlayerTrack> tracks)
        {
            _tracks = tracks;

            for (var t = 0; t < tracks.Count; t++)
            {
                foreach (var sample in tracks[t].Samples)
                {
                    var key = Key(Cell(sample.X), Cell(sample.Y));
                    if (!_cells.TryGetValue(key, out var bucket))
                    {
                        bucket = new List<(int Track, double T, double X, double Y, double Z)>();
                        _cells[key] = bucket;
                    }

                    bucket.Add((t, sample.T, sample.X, sample.Y, sample.Z));
                }
            }
        }

        public int Count => _tracks.Count;

        public (string? Name, bool IsLocal) Match(double x, double y, double z)
        {
            if (_tracks.Count == 0 || (x == 0 && y == 0 && z == 0))
            {
                return (null, false);
            }

            var perTrack = new double[_tracks.Count];
            Array.Fill(perTrack, double.MaxValue);

            var cx = Cell(x);
            var cy = Cell(y);

            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    if (!_cells.TryGetValue(Key(cx + dx, cy + dy), out var bucket))
                    {
                        continue;
                    }

                    foreach (var (track, st, sx, sy, sz) in bucket)
                    {
                        var ex = sx - x;
                        var ey = sy - y;
                        var ez = sz - z;
                        var distance = ex * ex + ey * ey + ez * ez;

                        if (distance < perTrack[track])
                        {
                            perTrack[track] = distance;
                        }
                    }
                }
            }

            string? bestName = null;
            var bestLocal = false;
            var bestDistance = double.MaxValue;
            var secondDistance = double.MaxValue;

            for (var t = 0; t < perTrack.Length; t++)
            {
                var distance = perTrack[t];
                if (distance < bestDistance)
                {
                    secondDistance = bestDistance;
                    bestDistance = distance;
                    bestName = _tracks[t].Name;
                    bestLocal = _tracks[t].IsLocal;
                }
                else if (distance < secondDistance)
                {
                    secondDistance = distance;
                }
            }

            if (bestDistance > MaxDistanceSquared || secondDistance - bestDistance < UniqueMarginSquared)
            {
                return (null, false);
            }

            return (bestName, bestLocal);
        }

        /// <summary>
        /// Diferencia entre el reloj del servidor y el tiempo del frame del replay. Se estima
        /// casando por geometría cofres con posición conocida con la trayectoria más cercana.
        /// </summary>
        public double EstimateOffset(IEnumerable<(double Time, double X, double Y, double Z)> events)
        {
            var offsets = new List<double>();

            foreach (var (time, x, y, z) in events)
            {
                var nearest = NearestSample(x, y, z);
                if (nearest is { } sample)
                {
                    offsets.Add(sample.T - time);
                }
            }

            if (offsets.Count < 3)
            {
                return 0;
            }

            offsets.Sort();
            return offsets[offsets.Count / 2];
        }

        /// <summary>Atribuye en un instante concreto, sosteniendo la última posición conocida.</summary>
        public (string? Name, bool IsLocal) MatchTimed(double time, double x, double y, double z, double offset)
        {
            if (_tracks.Count == 0 || (x == 0 && y == 0 && z == 0))
            {
                return (null, false);
            }

            var target = time + offset;
            var perTrack = new double[_tracks.Count];
            Array.Fill(perTrack, double.MaxValue);

            for (var t = 0; t < _tracks.Count; t++)
            {
                var samples = _tracks[t].Samples;
                var index = NearestIndex(samples, target);

                for (var i = Math.Max(0, index - 2); i <= Math.Min(samples.Count - 1, index + 2); i++)
                {
                    var sample = samples[i];
                    var dx = sample.X - x;
                    var dy = sample.Y - y;
                    var dz = sample.Z - z;
                    var distance = dx * dx + dy * dy + dz * dz;

                    if (distance < perTrack[t])
                    {
                        perTrack[t] = distance;
                    }
                }
            }

            return SelectBest(perTrack);
        }

        private (string? Name, bool IsLocal) SelectBest(double[] perTrack)
        {
            string? bestName = null;
            var bestLocal = false;
            var bestDistance = double.MaxValue;
            var secondDistance = double.MaxValue;

            for (var t = 0; t < perTrack.Length; t++)
            {
                var distance = perTrack[t];
                if (distance < bestDistance)
                {
                    secondDistance = bestDistance;
                    bestDistance = distance;
                    bestName = _tracks[t].Name;
                    bestLocal = _tracks[t].IsLocal;
                }
                else if (distance < secondDistance)
                {
                    secondDistance = distance;
                }
            }

            if (bestDistance > MaxDistanceSquared || secondDistance - bestDistance < UniqueMarginSquared)
            {
                return (null, false);
            }

            return (bestName, bestLocal);
        }

        private (double T, double X, double Y, double Z)? NearestSample(double x, double y, double z)
        {
            var cx = Cell(x);
            var cy = Cell(y);
            var best = double.MaxValue;
            (double T, double X, double Y, double Z)? found = null;

            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    if (!_cells.TryGetValue(Key(cx + dx, cy + dy), out var bucket))
                    {
                        continue;
                    }

                    foreach (var (_, t, sx, sy, sz) in bucket)
                    {
                        var ex = sx - x;
                        var ey = sy - y;
                        var ez = sz - z;
                        var distance = ex * ex + ey * ey + ez * ez;

                        if (distance < best)
                        {
                            best = distance;
                            found = (t, sx, sy, sz);
                        }
                    }
                }
            }

            return best <= MaxDistanceSquared ? found : null;
        }

        private static int NearestIndex(List<(double T, double X, double Y, double Z)> samples, double time)
        {
            var low = 0;
            var high = samples.Count - 1;

            while (low < high)
            {
                var mid = (low + high) / 2;
                if (samples[mid].T < time)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid;
                }
            }

            return low;
        }

        private static long Cell(double value) => (long)Math.Floor(value / CellSize);

        private static long Key(long cx, long cy) => cx * 1_000_003L + cy;
    }

    private void FillFromReplay(ParsedMatch parsed, FortniteReplay replay,
        Dictionary<string, (string Name, bool IsLocal)> playersByKey, List<PlayerTrack> tracks)
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

            // El cofre replica el NetGUID del pawn; el pawn conoce su PlayerState, que replica
            // el PlayerId. Con ese id se llega al nombre del jugador.
            void IndexPlayer(string? key)
            {
                if (!string.IsNullOrWhiteSpace(key) && !playersByKey.ContainsKey(key))
                {
                    playersByKey[key] = (name, player.IsReplayOwner);
                }
            }

            IndexPlayer(player.Id?.ToString(CultureInfo.InvariantCulture));
            IndexPlayer(player.PlayerNumber?.ToString(CultureInfo.InvariantCulture));
            IndexPlayer(player.EpicId);

            AddCosmetic(parsed, name, player.IsReplayOwner, "outfit", player.Cosmetics?.Character);
            AddCosmetic(parsed, name, player.IsReplayOwner, "pickaxe", player.Cosmetics?.Pickaxe);
            AddCosmetic(parsed, name, player.IsReplayOwner, "backBling", player.Cosmetics?.Backpack);
            AddCosmetic(parsed, name, player.IsReplayOwner, "glider", player.Cosmetics?.Glider);
            AddCosmetic(parsed, name, player.IsReplayOwner, "loadingScreen", player.Cosmetics?.LoadingScreen);
            AddCosmetic(parsed, name, player.IsReplayOwner, "musicPack", player.Cosmetics?.MusicPack);
            AddCosmetic(parsed, name, player.IsReplayOwner, "pet", player.Cosmetics?.PetSkin);
            AddCosmetic(parsed, name, player.IsReplayOwner, "wrap", player.Cosmetics?.ItemWraps?.FirstOrDefault());

            var samples = new List<(double T, double X, double Y, double Z)>();
            foreach (var movement in player.Locations ?? new List<PlayerMovement>())
            {
                var time = movement.ReplicatedWorldTimeSecondsDouble ?? movement.ReplicatedWorldTimeSeconds;
                var position = movement.ReplicatedMovement?.Location;
                if (time is null || position is null)
                {
                    continue;
                }

                var x = ToDouble(position.X);
                var y = ToDouble(position.Y);
                if (x == 0 && y == 0)
                {
                    continue;
                }

                samples.Add((time.Value, x, y, ToDouble(position.Z)));
            }

            if (samples.Count > 0)
            {
                samples.Sort((a, b) => a.T.CompareTo(b.T));
                tracks.Add(new PlayerTrack(name, player.IsReplayOwner, samples));
            }
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

    private void FillChests(ParsedMatch parsed, CapturingReader reader, FortniteReplay? replay,
        Dictionary<string, (string Name, bool IsLocal)> playersByKey, PlayerIndex players)
    {
        // El reloj del servidor (trayectorias) y el del frame del replay no comparten origen:
        // se estima el desfase con los cofres cuya posición ya se conoce.
        var offset = players.EstimateOffset(
            reader.Containers.Where(c => c.HasPosition).Select(c => (c.TimeSeconds, c.X, c.Y, c.Z)));

        (string? Name, bool IsLocal) Resolve(string? ownerKey, double time, double x, double y, double z, bool hasPosition)
        {
            if (ownerKey is not null && playersByKey.TryGetValue(ownerKey, out var owner))
            {
                return (owner.Name, owner.IsLocal);
            }

            if (!hasPosition)
            {
                return (null, false);
            }

            // Sin enlace directo pawn→PlayerState se atribuye cruzando la posición del
            // pawn que abre con la trayectoria replicada de cada jugador en ese instante.
            return players.MatchTimed(time, x, y, z, offset);
        }

        foreach (var container in reader.Containers.OrderBy(c => c.TimeSeconds))
        {
            var itemId = container.ItemId;
            var (ownerName, isLocal) = Resolve(container.OwnerKey, container.TimeSeconds, container.X, container.Y, container.Z, container.HasPosition);

            parsed.Chests.Add(new ParsedChest
            {
                TimeMs = (int)Math.Round(container.TimeSeconds * 1000),
                ContainerClass = container.ClassName,
                ItemId = itemId,
                IsToken = LooksLikeToken(itemId),
                Confidence = LooksLikeToken(itemId) ? "B" : "C",
                PlayerName = ownerName,
                IsLocalPlayer = isLocal,
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

        var pickups = reader.Pickups.Where(p => p.HasPosition).ToList();

        // El premio del cofre dorado también aparece como objeto recogible junto a él:
        // si la etiqueta replicada no es utilizable, se toma el objeto que salió al lado.
        string? RewardNear(double time, double x, double y, double z)
        {
            PickupSnapshot? best = null;
            var bestDistance = 15.0 * 15.0;

            foreach (var pickup in pickups)
            {
                if (Math.Abs(pickup.TimeSeconds - time) > 12 || !IsRewardItem(pickup.ItemId))
                {
                    continue;
                }

                var dx = pickup.X - x;
                var dy = pickup.Y - y;
                var dz = pickup.Z - z;
                var distance = dx * dx + dy * dy + dz * dz;

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = pickup;
                }
            }

            return best?.ItemId;
        }

        foreach (var prize in reader.Prizes.OrderBy(p => p.TimeSeconds))
        {
            var (ownerName, isLocal) = Resolve(prize.OwnerKey, prize.TimeSeconds, prize.X, prize.Y, prize.Z, prize.HasPosition);

            var prizeId = UsablePrizeTag(prize.PrizeTemplateId)
                          ?? RewardNear(prize.TimeSeconds, prize.X, prize.Y, prize.Z);

            parsed.Chests.Add(new ParsedChest
            {
                TimeMs = (int)Math.Round(prize.TimeSeconds * 1000),
                ContainerClass = "Cofre dorado (tesoro)",
                ItemId = prizeId,
                IsToken = LooksLikeToken(prizeId),
                Category = TreasureCatalog.ClassifyPrize(prizeId),
                Confidence = string.IsNullOrWhiteSpace(prizeId) ? "B" : "A",
                PlayerName = ownerName,
                IsLocalPlayer = isLocal,
                X = prize.X,
                Y = prize.Y,
                Z = prize.Z,
                PositionApproximate = !prize.HasPosition
            });
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

    /// <summary>Cosméticos replicados por cada pawn, atribuidos a su jugador por posición.</summary>
    private void FillPawnCosmetics(ParsedMatch parsed, CapturingReader reader, PlayerIndex players)
    {
        foreach (var (pawn, slots) in reader.PawnCosmetics)
        {
            if (slots.Count == 0 || !reader.TryGetPawnPosition(pawn, out var position))
            {
                continue;
            }

            var (name, isLocal) = players.Match(position.X, position.Y, position.Z);
            var label = name ?? $"pawn {pawn}";

            foreach (var (slot, item) in slots)
            {
                if (parsed.Cosmetics.Count > 600)
                {
                    return;
                }

                if (parsed.Cosmetics.Any(c => c.PlayerName == label && c.Slot == slot && c.ItemPath == item))
                {
                    continue;
                }

                parsed.Cosmetics.Add(new CosmeticEntry
                {
                    PlayerName = label,
                    IsLocalPlayer = isLocal,
                    Slot = slot,
                    ItemPath = item
                });
            }
        }
    }

    private static double ToDouble(object? value) => value switch
    {
        null => 0,
        float f => f,
        double d => d,
        int i => i,
        uint u => u,
        long l => l,
        _ => double.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0
    };

    /// <summary>La etiqueta de premio replicada a veces trae basura de otro campo.</summary>
    private static string? UsablePrizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var usable = tag.Contains("Cosmetic", StringComparison.OrdinalIgnoreCase)
                     || tag.Contains("Delilah", StringComparison.OrdinalIgnoreCase)
                     || tag.Contains("Treasure", StringComparison.OrdinalIgnoreCase)
                     || tag.Contains("Token", StringComparison.OrdinalIgnoreCase)
                     || tag.Contains("AGID", StringComparison.OrdinalIgnoreCase)
                     || tag.StartsWith("Athena", StringComparison.OrdinalIgnoreCase);

        return usable ? tag : null;
    }

    /// <summary>Descarta munición y armas: el premio del tesoro es cosmético u objeto del evento.</summary>
    private static bool IsRewardItem(string? itemId) =>
        !string.IsNullOrWhiteSpace(itemId)
        && !itemId!.StartsWith("WID_", StringComparison.OrdinalIgnoreCase)
        && !itemId.Contains("Ammo", StringComparison.OrdinalIgnoreCase)
        && !itemId.Contains("Playset", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeToken(string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return false;
        }

        return itemId.Contains("Token", StringComparison.OrdinalIgnoreCase)
               || itemId.Contains("PirateCoin", StringComparison.OrdinalIgnoreCase)
               || itemId.Contains("PirateShop", StringComparison.OrdinalIgnoreCase)
               || itemId.Contains("ShopAccess", StringComparison.OrdinalIgnoreCase)
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
                var cache = JsonSerializer.Deserialize<CacheFile>(json, AppConfig.SerializerOptions) ?? new CacheFile();
                if (cache.ParserVersion != CurrentParserVersion)
                {
                    // Cambiaron las reglas: se descarta lo cacheado para no servir datos viejos.
                    return new CacheFile { ParserVersion = CurrentParserVersion };
                }

                return cache;
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
        public string ParserVersion { get; set; } = CurrentParserVersion;
        public Dictionary<string, CacheEntry> Entries { get; set; } = new();
    }

    private sealed class CacheEntry
    {
        public long Size { get; set; }
        public long LastWriteTicks { get; set; }
        public ParsedMatch? Match { get; set; }
    }
}
