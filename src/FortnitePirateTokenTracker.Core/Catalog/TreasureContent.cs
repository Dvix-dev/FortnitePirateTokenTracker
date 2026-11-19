using System.Text.Json;

namespace FortnitePirateTokenTracker.Core.Catalog;

/// <summary>
/// Nombres de los premios del tesoro. El juego solo replica un identificador
/// ("DelilahPrize:dpid_item5"); este catálogo lo traduce al nombre real del premio.
/// Los datos se copian de fortnite.bot (ver Catalog/treasure-content.json).
/// </summary>
public static class TreasureContent
{
    public sealed record PrizeInfo(string PrizeId, string Kind, string Name, int Week);

    private static readonly Lazy<Dictionary<string, PrizeInfo>> _byPrizeId = new(Load);
    private static readonly Lazy<Dictionary<string, string>> _kinds = new(LoadKinds);

    /// <summary>Nombre del premio a partir del id replicado, si está en el catálogo.</summary>
    public static string? Name(string? prizeId) =>
        prizeId is not null && _byPrizeId.Value.TryGetValue(prizeId, out var info) ? info.Name : null;

    /// <summary>Clase de premio ("car", "gizmo", "outfit"…) a partir del id replicado.</summary>
    public static string? Kind(string? prizeId) =>
        prizeId is not null && _byPrizeId.Value.TryGetValue(prizeId, out var info) ? info.Kind : null;

    /// <summary>Categoría del panel ("vehicle", "jamTrack"…) para una clase del catálogo.</summary>
    public static string? PanelKind(string? prizeId)
    {
        var kind = Kind(prizeId);
        if (kind is null)
        {
            return null;
        }

        // La clave tiene que coincidir con la del catálogo del panel, que es la que se cuenta
        // en «encontrados». El fichero de contenido habla de "car"/"jam"/"loading".
        return kind switch
        {
            "car" => "vehicle",
            "jam" => "jamTrack",
            "loading" => "loadingScreen",
            "pirate-shop" => "token",
            _ => _kinds.Value.TryGetValue(kind, out var mapped) ? mapped : kind
        };
    }

    private static Dictionary<string, PrizeInfo> Load()
    {
        var result = new Dictionary<string, PrizeInfo>(StringComparer.OrdinalIgnoreCase);
        using var stream = typeof(TreasureContent).Assembly
            .GetManifestResourceStream("FortnitePirateTokenTracker.Core.Catalog.treasure-content.json");
        if (stream is null)
        {
            return result;
        }

        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("weeks", out var weeks) || weeks.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var week in weeks.EnumerateArray())
        {
            var number = week.TryGetProperty("number", out var n) && n.TryGetInt32(out var parsed) ? parsed : 0;
            if (!week.TryGetProperty("prizes", out var prizes) || prizes.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var prize in prizes.EnumerateArray())
            {
                var kind = prize.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
                var name = prize.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
                if (!prize.TryGetProperty("prizeIds", out var ids) || ids.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var id in ids.EnumerateArray())
                {
                    var value = id.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        result[value] = new PrizeInfo(value, kind, name, number);
                    }
                }
            }
        }

        return result;
    }

    private static Dictionary<string, string> LoadKinds()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var stream = typeof(TreasureContent).Assembly
            .GetManifestResourceStream("FortnitePirateTokenTracker.Core.Catalog.treasure-content.json");
        if (stream is null)
        {
            return result;
        }

        using var document = JsonDocument.Parse(stream);
        if (document.RootElement.TryGetProperty("odds", out var odds) && odds.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in odds.EnumerateArray())
            {
                var kind = entry.TryGetProperty("kind", out var k) ? k.GetString() : null;
                if (!string.IsNullOrWhiteSpace(kind))
                {
                    result[kind] = kind;
                }
            }
        }

        return result;
    }
}
