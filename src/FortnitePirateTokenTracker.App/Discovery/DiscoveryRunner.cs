using System.Text.Json;
using FortnitePirateTokenTracker.App.Parsing;
using FortnitePirateTokenTracker.Engine;
using FortnitePirateTokenTracker.Core.Configuration;
using Unreal.Core.Models.Enums;

namespace FortnitePirateTokenTracker.App.Discovery;

/// <summary>
/// Inventario de lo que replican las repeticiones: grupos, campos declarados y
/// pickups. Sirve para localizar qué grupo transporta los premios del evento
/// antes de escribir reglas de detección.
/// </summary>
public static class DiscoveryRunner
{
    private static readonly string[] Keywords =
    {
        "Chest", "Container", "Treasure", "Prize", "Gold", "Token", "Pirate", "Loot", "Reward"
    };

    private sealed class Inventory
    {
        public int Files { get; set; }
        public int Errors { get; set; }
        public Dictionary<string, int> Groups { get; set; } = new();
        public Dictionary<string, List<string>> Fields { get; set; } = new();
        public Dictionary<string, int> Pickups { get; set; } = new();
    }

    public static int Run(AppConfig config, string dataDir, int limit)
    {
        if (!Directory.Exists(config.ReplayFolder))
        {
            Console.WriteLine($"La carpeta no existe: {config.ReplayFolder}");
            return 2;
        }

        var files = Directory.EnumerateFiles(config.ReplayFolder, "*.replay")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(limit)
            .ToList();

        var inventory = new Inventory();
        Console.WriteLine($"Inventariando {files.Count} repeticiones...");

        foreach (var file in files)
        {
            var reader = new CapturingReader(ParseMode.Debug);
            try
            {
                using var stream = File.OpenRead(file);
                reader.ReadReplay(stream);
            }
            catch (Exception ex)
            {
                inventory.Errors++;
                Console.WriteLine($"! {Path.GetFileName(file)}: {ex.GetType().Name}: {ex.Message}");
            }

            inventory.Files++;

            foreach (var (path, count) in reader.GroupPaths)
            {
                inventory.Groups[path] = inventory.Groups.GetValueOrDefault(path) + count;
            }

            foreach (var (path, names) in reader.GroupFields)
            {
                if (!inventory.Fields.TryGetValue(path, out var merged))
                {
                    merged = new List<string>();
                    inventory.Fields[path] = merged;
                }

                foreach (var name in names.Where(n => !merged.Contains(n)))
                {
                    merged.Add(name);
                }
            }

            foreach (var pickup in reader.Pickups)
            {
                if (!string.IsNullOrEmpty(pickup.ItemId))
                {
                    inventory.Pickups[pickup.ItemId] = inventory.Pickups.GetValueOrDefault(pickup.ItemId) + 1;
                }
            }
        }

        Directory.CreateDirectory(dataDir);
        File.WriteAllText(Path.Combine(dataDir, "discovery.json"),
            JsonSerializer.Serialize(inventory, AppConfig.SerializerOptions));

        Report(inventory);
        return 0;
    }

    private static void Report(Inventory inventory)
    {
        Console.WriteLine();
        Console.WriteLine($"Repeticiones: {inventory.Files} (errores: {inventory.Errors})");
        Console.WriteLine($"Grupos distintos: {inventory.Groups.Count}");
        Console.WriteLine($"Items de pickup distintos: {inventory.Pickups.Count}");

        Console.WriteLine();
        Console.WriteLine("GRUPOS RELACIONADOS CON COFRES / PREMIOS");
        foreach (var (path, count) in inventory.Groups
                     .Where(g => Keywords.Any(k => g.Key.Contains(k, StringComparison.OrdinalIgnoreCase)))
                     .OrderByDescending(g => g.Value)
                     .Take(60))
        {
            Console.WriteLine($"  {count,7}  {path}");
        }

        Console.WriteLine();
        Console.WriteLine("CAMPOS DECLARADOS POR ESOS GRUPOS");
        foreach (var (path, fields) in inventory.Fields.OrderBy(f => f.Key))
        {
            Console.WriteLine($"  {path}");
            Console.WriteLine($"     {string.Join(", ", fields.Take(30))}");
        }

        Console.WriteLine();
        Console.WriteLine("PICKUPS NO ARMAMENTÍSTICOS");
        foreach (var (id, count) in inventory.Pickups
                     .Where(p => !p.Key.StartsWith("WID_", StringComparison.Ordinal)
                                 && !p.Key.StartsWith("AthenaAmmo", StringComparison.Ordinal))
                     .OrderByDescending(p => p.Value)
                     .Take(60))
        {
            Console.WriteLine($"  {count,6}  {id}");
        }
    }
}
