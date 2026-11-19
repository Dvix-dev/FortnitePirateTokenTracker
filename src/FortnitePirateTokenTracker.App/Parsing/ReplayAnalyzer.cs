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
    private const string CurrentParserVersion = "23";

    private readonly AppConfig _config;
    private readonly string _dataDir;
    private readonly string _cachePath;
    private CacheFile _cache;

    public ReplayAnalyzer(AppConfig config, string dataDir)
    {
        _config = config;
        _dataDir = dataDir;
        // Un proceso hijo escribe su propia caché parcial (TT_CACHE) para no pisar la buena.
        var cacheOverride = Environment.GetEnvironmentVariable("TT_CACHE");
        _cachePath = string.IsNullOrWhiteSpace(cacheOverride)
            ? Path.Combine(dataDir, "cache", CacheFileName)
            : cacheOverride;
        _cache = LoadCache();
    }

    public Action<string>? Log { get; set; }

    private readonly object _cacheLock = new();
    private readonly object _logLock = new();

    private void LogLine(string text)
    {
        lock (_logLock)
        {
            Log?.Invoke(text);
        }
    }

    /// <summary>
    /// Cuántas repeticiones se analizan a la vez. Cada lectura usa unos 250 MB, así que se
    /// limita aunque la máquina tenga muchos núcleos. TT_JOBS lo fija a mano.
    /// </summary>
    /// <summary>
    /// Reparte las repeticiones entre varios procesos. Dos lectores en el mismo proceso
    /// comparten estado de la librería y el recuento de cofres deja de cuadrar, así que cada
    /// proceso analiza su parte por separado y aquí se juntan los resultados.
    /// </summary>
    private void RunInChildProcesses(string exe, List<string> files, bool force)
    {
        var jobs = Math.Min(JobCount(files.Count), files.Count);
        var cacheDir = Path.GetDirectoryName(_cachePath)!;
        var shards = new List<string>();
        var children = new List<System.Diagnostics.Process>();

        try
        {
            for (var i = 0; i < jobs; i++)
            {
                var chunk = new List<string>();
                for (var index = i; index < files.Count; index += jobs)
                {
                    chunk.Add(Path.GetFileName(files[index]));
                }

                if (chunk.Count == 0)
                {
                    continue;
                }

                var shard = Path.Combine(cacheDir, $"shard-{Guid.NewGuid():N}.json");
                shards.Add(shard);

                var start = new System.Diagnostics.ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                start.ArgumentList.Add("analyze");
                start.ArgumentList.Add("--path");
                start.ArgumentList.Add(_config.ReplayFolder);
                if (force)
                {
                    start.ArgumentList.Add("--force");
                }

                start.Environment["TT_JOBS"] = "1";
                start.Environment["TT_CHILD"] = "1";
                start.Environment["TT_ONLY_FILES"] = string.Join(",", chunk);
                start.Environment["TT_CACHE"] = shard;

                var child = System.Diagnostics.Process.Start(start)!;
                child.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        LogLine(e.Data);
                    }
                };
                child.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        LogLine(e.Data);
                    }
                };
                child.BeginOutputReadLine();
                child.BeginErrorReadLine();
                children.Add(child);
            }

            foreach (var child in children)
            {
                child.WaitForExit();
            }
        }
        finally
        {
            foreach (var child in children)
            {
                child.Dispose();
            }

            MergeShards(shards);
        }
    }

    /// <summary>Junta lo analizado por cada proceso hijo en la caché buena.</summary>
    private void MergeShards(List<string> shards)
    {
        foreach (var shard in shards)
        {
            try
            {
                if (!File.Exists(shard))
                {
                    continue;
                }

                var child = JsonSerializer.Deserialize<CacheFile>(
                    File.ReadAllText(shard), AppConfig.SerializerOptions);
                if (child is null)
                {
                    continue;
                }

                lock (_cacheLock)
                {
                    foreach (var (key, entry) in child.Entries)
                    {
                        if (entry.Match is not null)
                        {
                            _cache.Entries[key] = entry;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogLine($"  ! no se pudo juntar un proceso hijo: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                try
                {
                    File.Delete(shard);
                }
                catch
                {
                    // Un fichero temporal que no se puede borrar no rompe nada.
                }
            }
        }
    }

    private static int JobCount(int files)
    {
        if (files <= 1)
        {
            return 1;
        }

        // Desactivado por defecto: el análisis en procesos separados da un recuento de cofres
        // distinto al de la pasada en serie (cada repetición depende de lo analizado antes en
        // el proceso), y las cifras tienen que cuadrar. Se activa a mano para medir.
        if (int.TryParse(Environment.GetEnvironmentVariable("TT_JOBS"), out var jobs))
        {
            return Math.Clamp(jobs, 1, 16);
        }

        return 1;
    }

    /// <summary>
    /// Modo de parseo. Debug lee todos los grupos (también los que no usamos) y multiplica el
    /// tiempo; TT_PARSE_MODE=Normal lo limita a los grupos normales para analizar rápido.
    /// </summary>
    public static ParseMode ParseModeFromEnvironment() =>
        Enum.TryParse<ParseMode>(Environment.GetEnvironmentVariable("TT_PARSE_MODE"), true, out var mode)
            ? mode
            : ParseMode.Debug;

    public List<string> ListReplays()
    {
        if (!Directory.Exists(_config.ReplayFolder))
        {
            return new List<string>();
        }

        var files = Directory.EnumerateFiles(_config.ReplayFolder, "*.replay", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();

        // Un proceso hijo recibe solo su parte de repeticiones.
        var only = Environment.GetEnvironmentVariable("TT_ONLY_FILES");
        if (!string.IsNullOrWhiteSpace(only))
        {
            var names = new HashSet<string>(
                only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);
            files = files.Where(f => names.Contains(Path.GetFileName(f))).ToList();
        }

        return files;
    }

    /// <summary>
    /// Analiza las repeticiones nuevas y devuelve TODO lo guardado: lo analizado ahora, lo que
    /// ya estaba en memoria y lo de repeticiones que Fortnite ya borró. Nada se elimina.
    /// </summary>
    public (List<ParsedMatch> Matches, int FromCache, int Errors) AnalyzeAll(int? limit, bool force)
    {
        var files = ListReplays();
        if (limit is > 0)
        {
            files = files.Take(limit.Value).ToList();
        }

        var onDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fromCache = 0;
        var errors = 0;
        var jobs = JobCount(files.Count);

        void Process(string file)
        {
            var info = new FileInfo(file);
            var key = Path.GetFileName(file);
            lock (_cacheLock)
            {
                onDisk.Add(key);
            }

            // Una repetición que acaba de cambiar se está grabando: se deja para el próximo
            // análisis en vez de guardar un resultado vacío.
            if (!force && (DateTime.UtcNow - info.LastWriteTimeUtc).TotalSeconds < 90)
            {
                LogLine($"... {key} (grabándose)");
                return;
            }

            CacheEntry? previous;
            lock (_cacheLock)
            {
                _cache.Entries.TryGetValue(key, out previous);
            }

            if (!force && previous is not null
                && !string.IsNullOrEmpty(previous.ParserVersion)
                && previous.ParserVersion == CurrentParserVersion
                && previous.Size == info.Length
                && previous.LastWriteTicks == info.LastWriteTimeUtc.Ticks
                && previous.Match is not null)
            {
                Interlocked.Increment(ref fromCache);
                LogLine($"= {key} (memoria)");
                return;
            }

            LogLine($"> {key}");
            var parsed = AnalyzeFile(file);

            if (parsed.Error is not null)
            {
                Interlocked.Increment(ref errors);

                // Un análisis fallido no debe borrar uno bueno: se conserva el anterior.
                if (previous?.Match is not null && previous.Match.Error is null)
                {
                    previous.LastError = parsed.Error;
                    previous.LastAttemptUtc = DateTimeOffset.UtcNow;
                    LogLine($"  ! {key}: se conserva lo anterior ({parsed.Error})");
                    return;
                }
            }

            var entry = new CacheEntry
            {
                Size = info.Length,
                LastWriteTicks = info.LastWriteTimeUtc.Ticks,
                ParserVersion = CurrentParserVersion,
                LastAttemptUtc = DateTimeOffset.UtcNow,
                LastError = parsed.Error,
                Match = parsed
            };

            lock (_cacheLock)
            {
                _cache.Entries[key] = entry;
            }
        }

        var exe = Environment.ProcessPath;

        if (jobs <= 1 || files.Count <= 1 || string.IsNullOrEmpty(exe))
        {
            foreach (var file in files)
            {
                Process(file);
            }
        }
        else
        {
            foreach (var file in files)
            {
                lock (_cacheLock)
                {
                    onDisk.Add(Path.GetFileName(file));
                }
            }

            RunInChildProcesses(exe, files, force);

            lock (_cacheLock)
            {
                errors = _cache.Entries.Count(e => onDisk.Contains(e.Key) && e.Value.LastError is not null);
            }
        }

        SaveCache();

        var archived = _cache.Entries.Count(e => e.Value.Match is not null && !onDisk.Contains(e.Key));
        if (archived > 0)
        {
            Log?.Invoke($"+ {archived} repeticiones guardadas de partidas ya borradas del disco");
        }

        var matches = _cache.Entries
            .Where(e => e.Value.Match is not null)
            .Select(e => e.Value.Match!)
            .GroupBy(m => m.ReplayFile, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(m => m.PlayedAt ?? DateTimeOffset.MinValue)
            .ToList();

        return (matches, fromCache, errors);
    }

    /// <summary>Repite la lectura con la vía cruda de habilidades (muy lenta).</summary>
    public bool AbilityProbe { get; set; }

    public ParsedMatch AnalyzeFile(string file)
    {
        var abilityProbe = AbilityProbe;
        var parsed = new ParsedMatch
        {
            ReplayFile = Path.GetFileName(file),
            MatchId = BuildMatchId(file),
            PlayedAt = ParseDateFromName(file)
        };

        var reader = new CapturingReader(ParseModeFromEnvironment());
        FortniteReplay? replay = null;
        var playersByKey = new Dictionary<string, (string Name, bool IsLocal)>(StringComparer.Ordinal);
        var tracks = new List<PlayerTrack>();
        var teamsByName = new Dictionary<string, int?>(StringComparer.Ordinal);
        int? localTeam = null;

        var cronometro = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // Fortnite mantiene la repetición abierta mientras graba: se abre en modo
            // compartido para poder leerla igualmente (y reintentar si aún está a medias).
            using var stream = new FileStream(
                file, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            replay = reader.ReadReplay(stream);
            cronometro.Stop();

            if (Environment.GetEnvironmentVariable("TT_TIME") == "1")
            {
                Console.WriteLine($"[tiempo] lectura={cronometro.ElapsedMilliseconds}ms");
            }
        }
        catch (Exception ex)
        {
            parsed.Error = $"{ex.GetType().Name}: {ex.Message}";
        }

        // Si en la partida se concedió un tesoro, se repite la lectura habilitando la vía
        // cruda de las habilidades: así se sabe quién lo recibió (y a veces qué premio es).
        if (reader.TreasureHandlerClasses.Any()
            && (abilityProbe || Environment.GetEnvironmentVariable("TT_DEEP_TREASURE") == "1"))
        {
            try
            {
                // La segunda lectura solo necesita las habilidades concedidas y quién las tiene,
                // y en modo Normal cuesta la mitad que en Debug con el mismo resultado.
                // TT_DEEP_MODE=Debug la devuelve al modo completo para depurar.
                var deepMode = Enum.TryParse<ParseMode>(Environment.GetEnvironmentVariable("TT_DEEP_MODE"), true, out var mode)
                    ? mode
                    : ParseMode.Normal;

                var profundo = new CapturingReader(
                    deepMode,
                    abilityProbe: true,
                    prizeScan: Environment.GetEnvironmentVariable("TT_PRIZE_SCAN") == "1");
                using var stream2 = new FileStream(
                    file, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                profundo.ReadReplay(stream2);

                // Si en el flujo apareció el identificador del premio en ese mismo instante,
                // se le asigna al tesoro que no lo traía. El id se replica aparte de la
                // habilidad, pocos segundos alrededor de la concesión.
                foreach (var prize in profundo.Prizes.Where(p => p.Source == "habilidad"
                                                                && string.IsNullOrEmpty(p.PrizeTemplateId)
                                                                && profundo.PrizeTexts.Count > 0))
                {
                    var near = profundo.PrizeTexts
                        .Where(t => Math.Abs(t.TimeSeconds - prize.TimeSeconds) <= 4)
                        .OrderBy(t => Math.Abs(t.TimeSeconds - prize.TimeSeconds))
                        .FirstOrDefault();

                    if (!string.IsNullOrEmpty(near.Token))
                    {
                        prize.PrizeTemplateId = near.Token;
                    }
                }

                foreach (var prize in profundo.Prizes.Where(p => p.Source == "habilidad" && p.OwnerPersistentId is not null))
                {
                    reader.Prizes.Add(prize);
                }

                if (Environment.GetEnvironmentVariable("TT_TIME") == "1")
                {
                    foreach (var prize in profundo.Prizes.Where(p => p.Source == "habilidad"))
                    {
                        Console.WriteLine(
                            $"[tesoro] {prize.TimeSeconds:F1}s {prize.TierData ?? "?"} premio={prize.PrizeTemplateId ?? "(sin id)"} dueno={prize.OwnerKey ?? "-"} t={prize.Tier}");
                    }

                    foreach (var text in profundo.PrizeTexts.Take(40))
                    {
                        Console.WriteLine($"[premio-texto] {text.TimeSeconds:F1}s canal={text.Channel} {text.Token}");
                    }

                    foreach (var tag in profundo.InterestingTags().Distinct().Take(20))
                    {
                        Console.WriteLine($"[tag] {tag}");
                    }

                    foreach (var group in profundo.GroupPaths
                                 .Where(g => g.Key.Contains("Delilah", StringComparison.OrdinalIgnoreCase)
                                             || g.Key.Contains("Prize", StringComparison.OrdinalIgnoreCase)
                                             || g.Key.Contains("Gizmo", StringComparison.OrdinalIgnoreCase)
                                             || g.Key.Contains("Treasure", StringComparison.OrdinalIgnoreCase))
                                 .OrderByDescending(g => g.Value)
                                 .Take(20))
                    {
                        Console.WriteLine($"[grupo] {group.Value,6}  {group.Key}");
                    }
                }
            }
            catch (Exception ex)
            {
                parsed.Warnings.Add($"Tesoro: {ex.GetType().Name}: {ex.Message}");
            }
        }

        parsed.Warnings.AddRange(reader.Warnings.Distinct().Take(25));

        if (replay is not null)
        {
            try
            {
                FillFromReplay(parsed, replay, reader, playersByKey, tracks, teamsByName, ref localTeam);
            }
            catch (Exception ex)
            {
                parsed.Warnings.Add($"Metadatos: {ex.Message}");
            }
        }

        try
        {
            var reloj = System.Diagnostics.Stopwatch.StartNew();
            var players = new PlayerIndex(tracks);
            FillChests(parsed, reader, replay, playersByKey, teamsByName, localTeam, players);
            FillPawnCosmetics(parsed, reader, players);

            if (Environment.GetEnvironmentVariable("TT_TIME") == "1")
            {
                Console.WriteLine($"[tiempo] atribucion={reloj.ElapsedMilliseconds}ms");
            }
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

    private void FillFromReplay(ParsedMatch parsed, FortniteReplay replay, CapturingReader reader,
        Dictionary<string, (string Name, bool IsLocal)> playersByKey, List<PlayerTrack> tracks,
        Dictionary<string, int?> teamsByName, ref int? localTeam)
    {
        var players = replay.PlayerData?.ToList() ?? new List<PlayerData>();

        // El dueño de la repetición solo se marca si llega RecorderPlayerState. Cuando no,
        // se resuelve el id del grabador con el mapa de PlayerStates capturado.
        var recorderKey = replay.GameData?.RecorderId is uint recorderId
            ? reader.PlayerStateKey(recorderId)
            : null;
        parsed.Players = players.Count;
        parsed.Mode = DetectMode(replay);
        parsed.LengthSeconds = DetectLength(replay);

        foreach (var player in players)
        {
            var isLocal = player.IsReplayOwner
                          || (recorderKey is not null && player.Id?.ToString(CultureInfo.InvariantCulture) == recorderKey);

            if (isLocal)
            {
                parsed.LocalPlayerFound = true;
                localTeam ??= player.TeamIndex;
            }

            var name = player.PlayerName ?? player.StreamerModeName ?? player.PlayerNameCustomOverride
                       ?? (player.IsBot == true ? "Bot" : $"jugador-{player.PlayerNumber}");

            // El cofre replica el NetGUID del pawn; el pawn conoce su PlayerState, que replica
            // el PlayerId. Con ese id se llega al nombre del jugador.
            void IndexPlayer(string? key)
            {
                if (!string.IsNullOrWhiteSpace(key))
                {
                    playersByKey[key] = (name, isLocal);
                }
            }

            if (player.TeamIndex is not null)
            {
                teamsByName[name] = player.TeamIndex;
            }

            IndexPlayer(player.Id?.ToString(CultureInfo.InvariantCulture));
            IndexPlayer(player.PlayerNumber?.ToString(CultureInfo.InvariantCulture));
            IndexPlayer(player.EpicId);

            AddCosmetic(parsed, name, isLocal, "outfit", player.Cosmetics?.Character);
            AddCosmetic(parsed, name, isLocal, "pickaxe", player.Cosmetics?.Pickaxe);
            AddCosmetic(parsed, name, isLocal, "backBling", player.Cosmetics?.Backpack);
            AddCosmetic(parsed, name, isLocal, "glider", player.Cosmetics?.Glider);
            AddCosmetic(parsed, name, isLocal, "loadingScreen", player.Cosmetics?.LoadingScreen);
            AddCosmetic(parsed, name, isLocal, "musicPack", player.Cosmetics?.MusicPack);
            AddCosmetic(parsed, name, isLocal, "pet", player.Cosmetics?.PetSkin);
            AddCosmetic(parsed, name, isLocal, "wrap", player.Cosmetics?.ItemWraps?.FirstOrDefault());

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
                tracks.Add(new PlayerTrack(name, isLocal, samples));
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
        Dictionary<string, (string Name, bool IsLocal)> playersByKey, Dictionary<string, int?> teamsByName,
        int? localTeam, PlayerIndex players)
    {
        // El reloj del servidor (trayectorias) y el del frame del replay no comparten origen:
        // se estima el desfase con los cofres cuya posición ya se conoce.
        var offset = players.EstimateOffset(
            reader.Containers.Where(c => c.HasPosition).Select(c => (c.TimeSeconds, c.X, c.Y, c.Z)));

        // Compañero de escuadra: mismo equipo que el jugador local y no es él.
        bool IsSquadMember(string? name, bool isLocal) =>
            !isLocal && name is not null && localTeam is not null
            && teamsByName.TryGetValue(name, out var team) && team == localTeam;

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
            var timed = players.MatchTimed(time, x, y, z, offset);
            return timed.Name is not null ? timed : players.Match(x, y, z);
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
                IsSquad = IsSquadMember(ownerName, isLocal),
                Destroyed = container.Destroyed,
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

            // El id replicado se traduce al nombre real del premio con el catálogo del evento.
            var prizeName = TreasureContent.Name(prizeId);
            var handler = prize.TierData?.StartsWith("GA_TreasureHandler", StringComparison.Ordinal) == true
                ? prize.TierData
                : null;
            var shown = prizeName ?? prizeId
                        ?? (handler is not null ? "Tesoro del evento (premio sin identificar)" : null);

            var timeMs = (int)Math.Round(prize.TimeSeconds * 1000);
            var containerClass = prize.Source == "habilidad"
                ? "Tesoro concedido (habilidad)"
                : "Cofre dorado (tesoro)";

            // La habilidad del tesoro se exporta con dos nombres (clase y plantilla):
            // el mismo premio llegaría dos veces en el mismo instante.
            if (parsed.Chests.Any(c => c.TimeMs == timeMs && c.ContainerClass == containerClass))
            {
                continue;
            }

            parsed.Chests.Add(new ParsedChest
            {
                TimeMs = timeMs,
                ContainerClass = containerClass,
                ItemId = shown,
                IsToken = LooksLikeToken(prizeId),
                Category = TreasureContent.PanelKind(prizeId) ?? TreasureCatalog.ClassifyPrize(prizeId),
                Confidence = string.IsNullOrWhiteSpace(prizeId) ? "B" : "A",
                PlayerName = ownerName,
                IsLocalPlayer = isLocal,
                IsSquad = IsSquadMember(ownerName, isLocal),
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

        var usable = Guid.TryParse(tag, out _)
                     || tag.Contains("dpid_", StringComparison.OrdinalIgnoreCase)
                     || tag.Contains("Cosmetic", StringComparison.OrdinalIgnoreCase)
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
                // La memoria es permanente: aunque cambien las reglas, lo guardado se conserva.
                // Cada entrada se reanaliza por separado cuando su versión es antigua.
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
            // La cabecera se queda al día con la versión con la que se ha escrito la caché.
            _cache.ParserVersion = CurrentParserVersion;
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(_cache, AppConfig.SerializerOptions));
        }
        catch
        {
            // La caché es una optimización: un fallo al escribirla no invalida el informe.
        }
    }

    private sealed class CacheFile
    {
        public int SchemaVersion { get; set; } = 2;
        public string ParserVersion { get; set; } = CurrentParserVersion;
        public Dictionary<string, CacheEntry> Entries { get; set; } = new();
    }

    private sealed class CacheEntry
    {
        public long Size { get; set; }
        public long LastWriteTicks { get; set; }

        /// <summary>
        /// Versión de reglas con la que se analizó esta repetición. Vacía = entrada antigua sin
        /// versión: se reanaliza cuando el archivo siga en disco.
        /// </summary>
        public string ParserVersion { get; set; } = "";

        public DateTimeOffset? LastAttemptUtc { get; set; }
        public string? LastError { get; set; }
        public ParsedMatch? Match { get; set; }
    }
}
