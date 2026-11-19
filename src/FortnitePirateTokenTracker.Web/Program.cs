using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using System.Text.Json;
using FortnitePirateTokenTracker.Core.Configuration;
using FortnitePirateTokenTracker.Core.Models;
using FortnitePirateTokenTracker.Core.Reporting;
using FortnitePirateTokenTracker.Engine;

namespace FortnitePirateTokenTracker.Web;

/// <summary>
/// Lo que la web puede pedirle al motor .NET compilado a WebAssembly. Todo ocurre en el
/// navegador: aquí no se abre ningún socket ni se guarda nada.
/// </summary>
public static partial class PanelExports
{
    /// <summary>Analiza una repetición (en base64) y devuelve el resumen en JSON.</summary>
    [JSExport]
    public static string Analyze(string payload, string fileName, bool deepTreasure)
    {
        var parser = new ReplayParser { AbilityProbe = deepTreasure };
        var match = parser.ParseBytes(Convert.FromBase64String(payload), fileName);
        return JsonSerializer.Serialize(match, AppConfig.SerializerOptions);
    }

    /// <summary>Recalcula el informe completo a partir de las partidas guardadas en el navegador.</summary>
    [JSExport]
    public static string BuildReport(string matchesJson, string configJson)
    {
        var matches = JsonSerializer.Deserialize<List<ParsedMatch>>(matchesJson, AppConfig.SerializerOptions)
                      ?? new List<ParsedMatch>();
        var config = DeserializeConfig(configJson);
        var report = ReportBuilder.Build(matches, config, 0, matches.Count(m => m.Error is not null));
        return JsonSerializer.Serialize(report, AppConfig.SerializerOptions);
    }

    private static AppConfig DeserializeConfig(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new AppConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<AppConfig>(json, AppConfig.SerializerOptions) ?? new AppConfig();
        }
        catch (JsonException)
        {
            return new AppConfig();
        }
    }

    /// <summary>Catálogo de premios con sus probabilidades (para la pestaña Premios).</summary>
    [JSExport]
    public static string Catalog() =>
        // Los records del catálogo se serializan tal cual: un tipo anónimo lo quitaría el recorte.
        JsonSerializer.Serialize(
            FortnitePirateTokenTracker.Core.Catalog.TreasureCatalog.Prizes,
            AppConfig.SerializerOptions);

    [JSExport]
    public static string Version()
    {
        var builder = new StringBuilder();
        builder.Append(typeof(PanelExports).Assembly.GetName().Version?.ToString() ?? "0.0.0");
        return builder.ToString();
    }
}

public class Entry
{
    public static void Main()
    {
    }
}
