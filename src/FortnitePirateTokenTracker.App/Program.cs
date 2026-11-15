using System.Diagnostics;
using System.Text.Json;
using FortnitePirateTokenTracker.App.Parsing;
using FortnitePirateTokenTracker.App.Reporting;
using FortnitePirateTokenTracker.App.Web;
using FortnitePirateTokenTracker.Core.Catalog;
using FortnitePirateTokenTracker.Core.Configuration;
using FortnitePirateTokenTracker.Core.Models;
using Unreal.Core.Models.Enums;

var dataDir = Path.Combine(Directory.GetCurrentDirectory(), "data");
var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
var config = AppConfig.Load(dataDir);

var command = args.Length > 0 && !args[0].StartsWith("--") ? args[0].ToLowerInvariant() : "serve";
var flags = args.Skip(1).ToArray();

string? Flag(string name)
{
    for (var i = 0; i < flags.Length; i++)
    {
        if (string.Equals(flags[i], name, StringComparison.OrdinalIgnoreCase))
        {
            return i + 1 < flags.Length && !flags[i + 1].StartsWith("--") ? flags[i + 1] : "";
        }
    }

    return null;
}

bool Has(string name) => flags.Any(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase));

if (command is "help" or "-h" or "--help")
{
    Console.WriteLine("""
    Token Tracker — panel local de cofres, tokens y cosméticos de Fortnite

      TokenTracker serve              Abre el panel en el navegador (por defecto)
      TokenTracker analyze            Analiza las repeticiones y escribe el informe
        --path <carpeta>              Carpeta de .replay a analizar
        --limit <n>                   Procesa solo las n repeticiones más recientes
        --force                       Ignora la caché
      TokenTracker probe <archivo>    Volcado de depuración de una repetición
      TokenTracker config             Muestra la configuración actual
    """);
    return 0;
}

if (command == "config")
{
    Console.WriteLine(JsonSerializer.Serialize(config, AppConfig.SerializerOptions));
    Console.WriteLine($"Archivo: {AppConfig.ConfigPath(dataDir)}");
    return 0;
}

if (command == "probe")
{
    var file = Flag("--file") ?? (flags.FirstOrDefault(f => !f.StartsWith("--")));
    if (string.IsNullOrWhiteSpace(file))
    {
        Console.WriteLine("Uso: TokenTracker probe <archivo.replay>");
        return 1;
    }

    if (!File.Exists(file))
    {
        Console.WriteLine($"No existe el archivo: {file}");
        return 1;
    }

    Console.WriteLine($"Inspeccionando {Path.GetFileName(file)} ...");
    var reader = new CapturingReader(ParseMode.Debug);
    var match = new ReplayAnalyzer(config, dataDir).AnalyzeFile(file);
    var sw = Stopwatch.StartNew();

    try
    {
        using var stream = File.OpenRead(file);
        reader = new CapturingReader(ParseMode.Debug);
        reader.ReadReplay(stream);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Aviso de parseo: {ex.GetType().Name}: {ex.Message}");
    }

    Console.WriteLine($"Tiempo de parseo: {sw.ElapsedMilliseconds} ms");
    Console.WriteLine($"Cofres observados: {reader.Containers.Count}");
    foreach (var c in reader.Containers.Take(15))
    {
        Console.WriteLine($"  [{c.TimeSeconds,7:0.0}s] {c.ClassName} lootTier={c.LootTier} searches={c.SearchAnimationCount} item={c.ItemId ?? "-"} pos={(c.HasPosition ? $"{c.X:0},{c.Y:0},{c.Z:0}" : "sin posición")}");
    }

    Console.WriteLine($"Pickups con item: {reader.Pickups.Count}");
    foreach (var p in reader.Pickups.GroupBy(p => p.ItemId).OrderByDescending(g => g.Count()).Take(15))
    {
        Console.WriteLine($"  {p.Count(),4}x {p.Key}");
    }

    var interesting = reader.Pickups
        .Select(p => p.ItemId!)
        .Where(id => id.Contains("Token", StringComparison.OrdinalIgnoreCase)
                  || id.Contains("Pirate", StringComparison.OrdinalIgnoreCase)
                  || id.Contains("Gold", StringComparison.OrdinalIgnoreCase)
                  || id.Contains("Coin", StringComparison.OrdinalIgnoreCase)
                  || id.Contains("Treasure", StringComparison.OrdinalIgnoreCase)
                  || id.Contains("Sprite", StringComparison.OrdinalIgnoreCase))
        .Distinct()
        .OrderBy(id => id)
        .ToList();

    Console.WriteLine($"Pickups del evento ({interesting.Count}):");
    foreach (var id in interesting)
    {
        Console.WriteLine($"  {id}");
    }

    Console.WriteLine($"Ids únicos de pickup: {reader.Pickups.Select(p => p.ItemId).Distinct().Count()}");
    Console.WriteLine("Grupos de contenedor vistos:");
    foreach (var g in reader.UnknownGroups.Take(25))
    {
        Console.WriteLine($"  {g}");
    }

    if (reader.Warnings.Count > 0)
    {
        Console.WriteLine($"Avisos ({reader.Warnings.Count}):");
        foreach (var w in reader.Warnings.Distinct().Take(10))
        {
            Console.WriteLine($"  {w}");
        }
    }

    Console.WriteLine($"Partidas/jugadores: players={match.Players} local={match.LocalPlayerFound} modo={match.Mode} duración={match.LengthSeconds}s");
    Console.WriteLine($"Cosméticos detectados: {match.Cosmetics.Count}");
    foreach (var cosmetic in match.Cosmetics.Take(8))
    {
        Console.WriteLine($"  {cosmetic.PlayerName} [{cosmetic.Slot}] {cosmetic.ItemPath}");
    }

    Console.WriteLine($"Errores de parseo del analizador: {match.Error ?? "ninguno"}");
    return 0;
}

if (command == "analyze")
{
    var path = Flag("--path");
    if (!string.IsNullOrWhiteSpace(path))
    {
        config.ReplayFolder = path;
    }

    var limitText = Flag("--limit");
    int? limit = int.TryParse(limitText, out var parsedLimit) ? parsedLimit : null;

    Console.WriteLine($"Carpeta: {config.ReplayFolder}");
    if (!Directory.Exists(config.ReplayFolder))
    {
        Console.WriteLine("La carpeta no existe. Indica otra con --path.");
        return 2;
    }

    Console.WriteLine("Analizando repeticiones...");
    var report = ScanRunner.Run(config, dataDir, limit, Has("--force"), Console.WriteLine);
    Console.WriteLine();
    Console.WriteLine(ScanRunner.SummaryText(report));
    Console.WriteLine();
    Console.WriteLine($"Informe : {ScanRunner.ReportPath(dataDir)}");
    Console.WriteLine($"CSV     : {ScanRunner.CsvPath(dataDir)}");
    return 0;
}

// Comando por defecto: servir el panel.
var portText = Flag("--port");
if (int.TryParse(portText, out var portOverride))
{
    config.Port = portOverride;
}

if (!Directory.Exists(webRoot))
{
    Console.WriteLine($"No se encontró la interfaz web en {webRoot}");
    return 1;
}

if (!Directory.Exists(config.ReplayFolder))
{
    Console.WriteLine($"Aviso: la carpeta de repeticiones no existe todavía ({config.ReplayFolder}).");
    Console.WriteLine("Configúrala desde el panel o con: TokenTracker analyze --path <carpeta>");
    Console.WriteLine();
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var scanner = new SemaphoreSlim(1, 1);

(int, string, byte[]) HandleApi(string method, string path, string? body)
{
    var report = ScanRunner.LoadReport(dataDir);

    if (path.Equals("/api/state", StringComparison.OrdinalIgnoreCase) && method is "GET" or "HEAD")
    {
        var files = Directory.Exists(config.ReplayFolder)
            ? Directory.EnumerateFiles(config.ReplayFolder, "*.replay").Count()
            : 0;

        return PanelServer.Json(new
        {
            config,
            report,
            catalog = TreasureCatalog.Prizes.Select(p => new { p.Key, p.Label, p.Odds, p.Accent, p.Description, p.Image }),
            folderExists = Directory.Exists(config.ReplayFolder),
            replayCount = files,
            version = "0.2.0",
            paths = new
            {
                replays = config.ReplayFolder,
                report = ScanRunner.ReportPath(dataDir),
                csv = ScanRunner.CsvPath(dataDir)
            }
        });
    }

    if (path.Equals("/api/config", StringComparison.OrdinalIgnoreCase) && method == "POST")
    {
        try
        {
            var incoming = JsonSerializer.Deserialize<AppConfig>(body ?? "{}", AppConfig.SerializerOptions);
            if (incoming is null)
            {
                return PanelServer.Json(new { error = "Cuerpo inválido" }, 400);
            }

            incoming.RadiusMeters = Math.Clamp(incoming.RadiusMeters, 50, 5000);
            incoming.Port = Math.Clamp(incoming.Port, 1024, 65535);
            incoming.CsvSeparator = string.IsNullOrEmpty(incoming.CsvSeparator) ? ";" : incoming.CsvSeparator[..1];
            incoming.MinConfidence = incoming.MinConfidence is "A" or "B" or "C" ? incoming.MinConfidence : "B";
            incoming.LastScanUtc = config.LastScanUtc;
            incoming.Save(dataDir);
            config = incoming;

            return PanelServer.Json(new { ok = true, config });
        }
        catch (Exception ex)
        {
            return PanelServer.Json(new { error = ex.Message }, 500);
        }
    }

    if (path.Equals("/api/scan", StringComparison.OrdinalIgnoreCase) && method == "POST")
    {
        if (!scanner.Wait(0))
        {
            return PanelServer.Json(new { error = "Ya hay un análisis en curso" }, 409);
        }

        try
        {
            int? limit = null;
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("limit", out var limitElement)
                    && limitElement.TryGetInt32(out var parsed))
                {
                    limit = parsed;
                }
            }

            var logs = new List<string>();
            var result = ScanRunner.Run(config, dataDir, limit, false, logs.Add);
            return PanelServer.Json(new { ok = true, report = result, log = logs });
        }
        catch (Exception ex)
        {
            return PanelServer.Json(new { error = $"{ex.GetType().Name}: {ex.Message}" }, 500);
        }
        finally
        {
            scanner.Release();
        }
    }

    return PanelServer.Json(new { error = "Ruta no encontrada" }, 404);
}

var server = new PanelServer(config.Port, webRoot, HandleApi);
var url = $"http://localhost:{config.Port}/";

Console.WriteLine();
Console.WriteLine("  TOKEN TRACKER");
Console.WriteLine("  ------------");
Console.WriteLine($"  Panel: {url}");
Console.WriteLine($"  Replays: {config.ReplayFolder}");
Console.WriteLine($"  Informe: {(File.Exists(ScanRunner.ReportPath(dataDir)) ? "cargado" : "sin analizar")}");
Console.WriteLine();
Console.WriteLine("  Ctrl+C para detener.");
Console.WriteLine();

try
{
    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
catch
{
    // Si no se puede abrir el navegador, la URL ya está impresa.
}

await server.RunAsync(cts.Token);
return 0;
