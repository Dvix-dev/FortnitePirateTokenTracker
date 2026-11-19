using FortnitePirateTokenTracker.Engine;
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
    private const string CurrentParserVersion = "24";

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

    public ParsedMatch AnalyzeFile(string file) => new ReplayParser { AbilityProbe = AbilityProbe }.ParseFile(file);

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
