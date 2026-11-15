using System.Globalization;
using System.Text;
using FortnitePirateTokenTracker.Core.Models;

namespace FortnitePirateTokenTracker.Core.Reporting;

/// <summary>Exporta los eventos de cofre al CSV detallado.</summary>
public static class CsvWriter
{
    private static readonly string[] Header =
    {
        "match_id", "replay_file", "tiempo", "timestamp_ms", "jugador", "es_jugador_local",
        "es_token", "nivel_confianza", "contenedor", "item_id", "x", "y", "z",
        "posicion_aproximada", "zona", "cofre_en_partida", "token_en_partida",
        "destruido", "de_squad"
    };

    public static void Write(string path, IEnumerable<ChestEvent> chests, string separator = ";")
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(separator, Header));

        foreach (var c in chests)
        {
            var fields = new[]
            {
                c.MatchId,
                c.ReplayFile,
                c.TimeLabel,
                c.TimeMs.ToString(CultureInfo.InvariantCulture),
                c.PlayerName ?? "",
                c.IsLocalPlayer ? "si" : "no",
                c.IsToken ? "si" : "no",
                c.Confidence,
                c.ContainerClass,
                c.ItemId ?? "",
                c.X.ToString("0.#", CultureInfo.InvariantCulture),
                c.Y.ToString("0.#", CultureInfo.InvariantCulture),
                c.Z.ToString("0.#", CultureInfo.InvariantCulture),
                c.PositionApproximate ? "si" : "no",
                c.Zone,
                c.ChestIndexInMatch.ToString(CultureInfo.InvariantCulture),
                c.TokenIndexInMatch.ToString(CultureInfo.InvariantCulture),
                c.Destroyed ? "si" : "no",
                c.IsSquad ? "si" : "no"
            };

            sb.AppendLine(string.Join(separator, fields.Select(f => Escape(f, separator))));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    private static string Escape(string value, string separator)
    {
        if (value.Contains(separator, StringComparison.Ordinal) || value.Contains('"') || value.Contains('\n'))
        {
            return '"' + value.Replace("\"", "\"\"") + '"';
        }

        return value;
    }
}
