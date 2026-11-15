namespace FortnitePirateTokenTracker.Core.Catalog;

/// <summary>
/// Catálogo de premios del evento y sus probabilidades por cofre.
/// Las tasas provienen de la tabla pública de fortnite.bot/treasure; se muestran
/// como referencia y no como dato calculado a partir de las repeticiones.
/// </summary>
public static class TreasureCatalog
{
    public sealed record Prize(string Key, string Label, string Odds, string Accent, string Description, string Image);

    public static readonly IReadOnlyList<Prize> Prizes = new List<Prize>
    {
        new("outfit", "Skin semanal (Gold Outfit)", "1 en 1.562.500", "#ffd400",
            "Traje dorado de la semana del evento.", "outfit.webp"),

        new("vehicle", "Vehículo y decal", "1 en 7.813", "#7b5cff",
            "Vehículo o decal dorado de la semana.", "vehicle.webp"),
        new("pickaxe", "Pico o mochila", "1 en 568", "#c86bff",
            "Pico o back bling semanal.", "pickaxe.webp"),
        new("jamTrack", "Jam Track", "1 en 391", "#38c8ff",
            "Pista musical desbloqueable.", "jam.webp"),
        new("loadingScreen", "Pantalla de carga", "1 en 272", "#3ddc97",
            "Pantalla de carga del evento.", "loading.webp"),
        new("gizmo", "Gizmo (cualquiera)", "1 en 127", "#ff5d5d",
            "Sprite/gizmo de extracción o localización.", "gizmo.webp"),
        new("token", "Token pirata · acceso a la tienda", "1 en 137.500", "#ffd400",
            "El token del evento: desbloquea el catálogo de la tienda pirata.", "token.webp")
    };

    /// <summary>
    /// Clasifica el identificador de premio que replica el cofre dorado.
    /// Los nombres provienen del propio juego, así que se comparan por fragmentos.
    /// </summary>
    public static string? ClassifyPrize(string prizeId)
    {
        if (string.IsNullOrWhiteSpace(prizeId))
        {
            return null;
        }

        var id = prizeId.ToLowerInvariant();

        if (id.Contains("token") || id.Contains("pirateshop") || id.Contains("shopaccess"))
        {
            return "token";
        }

        if (id.Contains("vehicle") || id.Contains("decal") || id.Contains("car"))
        {
            return "vehicle";
        }

        if (id.Contains("loadingscreen") || id.Contains("loadings"))
        {
            return "loadingScreen";
        }

        if (id.Contains("musicpack") || id.Contains("jamtrack") || id.Contains("music"))
        {
            return "jamTrack";
        }

        if (id.Contains("pickaxe") || id.Contains("harvestingtool") || id.Contains("backpack") || id.Contains("backbling"))
        {
            return "pickaxe";
        }

        if (id.Contains("sprite") || id.Contains("gizmo") || id.Contains("extractor") || id.Contains("locator"))
        {
            return "gizmo";
        }

        if (id.Contains("character") || id.Contains("outfit") || id.Contains("skin"))
        {
            return "outfit";
        }

        return null;
    }

    public static Prize? ByKey(string key) =>
        Prizes.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Etiqueta legible para una clase de contenedor del replay.</summary>
    public static string DescribeContainer(string className)
    {
        if (string.IsNullOrWhiteSpace(className))
        {
            return "Contenedor";
        }

        var name = className.Contains('.') ? className[(className.LastIndexOf('.') + 1)..] : className;
        name = name.Replace("_C", "", StringComparison.Ordinal);

        return name switch
        {
            "Tiered_Chest_Athena" => "Cofre (Tiered Chest)",
            "Creative_Tiered_Chest" => "Cofre creativo",
            "Tiered_Chest_Athena_FactionChest_NoLocks" => "Cofre de facción",
            "AmmoBox" => "Caja de munición",
            "FishingBarrel" => "Barril de pesca",
            "GiftBox" => "Caja de regalo",
            _ => name.Replace('_', ' ')
        };
    }
}
