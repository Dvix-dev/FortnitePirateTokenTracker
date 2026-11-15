using System.Globalization;
using System.Reflection;
using FortniteReplayReader.Models.NetFieldExports.Items;
using Unreal.Core;
using Unreal.Core.Contracts;
using Unreal.Core.Exceptions;
using Unreal.Core.Models;
using Unreal.Core.Models.Enums;

namespace FortnitePirateTokenTracker.App.Parsing;

/// <summary>Una apertura de contenedor observada durante el parseo.</summary>
public sealed class ContainerSnapshot
{
    public string ClassName { get; set; } = "";
    public double TimeSeconds { get; set; }
    public uint SearchAnimationCount { get; set; }
    public int LootTier { get; set; }
    public string? ItemId { get; set; }
    public string? SearchingPawn { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public bool HasPosition { get; set; }
}

/// <summary>Apertura del cofre dorado del evento, con el premio concedido.</summary>
public sealed class PrizeEvent
{
    public double TimeSeconds { get; set; }
    public string? PrizeTemplateId { get; set; }
    public bool Success { get; set; }
    public int? Tier { get; set; }
    public uint? OpeningPawn { get; set; }
    public uint? OwnerPersistentId { get; set; }
    public string? TierData { get; set; }
    public string ClassName { get; set; } = "";
}

/// <summary>Un pickup de ítem observado, útil para localizar el premio del cofre.</summary>
public sealed class PickupSnapshot
{
    public string ClassName { get; set; } = "";
    public double TimeSeconds { get; set; }
    public string? ItemId { get; set; }
    public string? Raw { get; set; }
}

/// <summary>
/// Extiende el lector oficial sin modificarlo: aprovecha el gancho OnExportRead
/// para observar cada grupo replicado. Todo el parseo normal sigue funcionando
/// porque se delega en la implementación base.
/// </summary>
public sealed class CapturingReader : FortniteReplayReader.ReplayReader
{
    private readonly Dictionary<uint, bool> _openedContainers = new();

    public CapturingReader(ParseMode mode) : base(null, mode)
    {
    }

    /// <summary>Fuerza la carga del ensamblado de grupos propios antes de crear el parser.</summary>
    private static readonly Type RegistrationAnchor = typeof(FortnitePirateTokenTracker.ReplayReader.ITrackedContainer);

    public List<ContainerSnapshot> Containers { get; } = new();
    public List<PrizeEvent> Prizes { get; } = new();
    public List<PickupSnapshot> Pickups { get; } = new();
    public List<string> UnknownGroups { get; } = new();

    /// <summary>Todos los grupos replicados vistos, con su número de apariciones.</summary>
    public Dictionary<string, int> GroupPaths { get; } = new();

    /// <summary>Nombres de campo declarados por los grupos relevantes para el evento.</summary>
    public Dictionary<string, HashSet<string>> GroupFields { get; } = new();

    /// <summary>Nombre y tipo declarado de cada campo, para elegir el lector correcto.</summary>
    public Dictionary<string, List<string>> GroupFieldDetails { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<string> Events { get; } = new();

    /// <summary>Indica si el parser reconoce (y por tanto deserializa) un grupo por su ruta.</summary>
    public bool WillReadGroup(string path) => _netFieldParser.WillReadType(path);

    protected override void OnExportRead(uint channelIndex, INetFieldExportGroup? exportGroup)
    {
        try
        {
            base.OnExportRead(channelIndex, exportGroup);
        }
        catch (Exception ex)
        {
            Warnings.Add($"OnExportRead base: {ex.GetType().Name}: {ex.Message}");
        }

        if (exportGroup is null)
        {
            return;
        }

        try
        {
            if (exportGroup is FortnitePirateTokenTracker.ReplayReader.ITrackedContainer tracked)
            {
                TrackTrackedContainer(channelIndex, tracked);
            }
            else if (exportGroup is FortnitePirateTokenTracker.ReplayReader.TreasureHuntChest prizeChest)
            {
                Prizes.Add(new PrizeEvent
                {
                    TimeSeconds = NowSeconds,
                    PrizeTemplateId = ReadTag(prizeChest.PrizeTemplateId),
                    Success = prizeChest.bSuccess,
                    Tier = prizeChest.Tier,
                    OpeningPawn = prizeChest.OpeningPawn,
                    OwnerPersistentId = prizeChest.OwnerPersistentId,
                    TierData = ReadTag(prizeChest.TierData),
                    ClassName = prizeChest.GetType().Name
                });
            }
            else if (exportGroup is BaseContainer container)
            {
                TrackContainer(channelIndex, container);
            }
            else if (exportGroup.GetType().Name.Contains("Pickup", StringComparison.OrdinalIgnoreCase))
            {
                TrackPickup(exportGroup);
            }
        }
        catch (Exception ex)
        {
            Warnings.Add($"Captura {exportGroup.GetType().Name}: {ex.Message}");
        }
    }

    protected override void OnNetDeltaRead(uint channelIndex, NetDeltaUpdate update)
    {
        try
        {
            base.OnNetDeltaRead(channelIndex, update);
        }
        catch (Exception ex)
        {
            Warnings.Add($"OnNetDeltaRead: {ex.Message}");
        }
    }

    /// <summary>
    /// Los grupos de cofre están marcados con minimalParseMode Debug; se usa ese modo,
    /// pero se toleran los eventos desconocidos para no abortar el archivo completo.
    /// </summary>
    public override void ReadEvent(FArchive archive)
    {
        try
        {
            base.ReadEvent(archive);
        }
        catch (UnknownEventException)
        {
            // Los eventos no reconocidos no aportan datos de cofres: se ignoran.
        }
        catch (Exception ex)
        {
            Warnings.Add($"Evento: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Registra los grupos que la librería no sabe leer (descubrimiento de clases nuevas).</summary>
    public override bool ReceiveProperties(FBitArchive archive, NetFieldExportGroup group, uint channelIndex,
        out INetFieldExportGroup? exportGroup, bool enablePropertyChecksum = true, bool netDeltaUpdate = false)
    {
        var path = group?.PathName ?? "";
        GroupPaths[path] = GroupPaths.GetValueOrDefault(path) + 1;

        if (IsInteresting(path))
        {
            if (!UnknownGroups.Contains(path))
            {
                UnknownGroups.Add(path);
            }

            if (!GroupFields.TryGetValue(path, out var fields))
            {
                fields = new HashSet<string>(StringComparer.Ordinal);
                GroupFields[path] = fields;
            }

            if (!GroupFieldDetails.TryGetValue(path, out var details))
            {
                details = new List<string>();
                GroupFieldDetails[path] = details;
            }

            foreach (var export in group.NetFieldExports)
            {
                if (export?.Name is { Length: > 0 } name)
                {
                    fields.Add(name);

                    var detail = $"{name} [{export.Type}] handle={export.Handle}";
                    if (!details.Contains(detail))
                    {
                        details.Add(detail);
                    }
                }
            }
        }

        return base.ReceiveProperties(archive, group, channelIndex, out exportGroup, enablePropertyChecksum, netDeltaUpdate);
    }

    private static readonly string[] InterestingKeywords =
    {
        "Chest", "Container", "Pickup", "Treasure", "Prize", "Gold", "Token", "Pirate", "Loot", "Reward"
    };

    private static bool IsInteresting(string path) =>
        InterestingKeywords.Any(k => path.Contains(k, StringComparison.OrdinalIgnoreCase));

    private float _nowSeconds;

    /// <summary>Instante de la trama que se está leyendo, en segundos desde el inicio de la partida.</summary>
    private float NowSeconds => _nowSeconds;

    /// <summary>
    /// La versión 3.1.0 no expone el tiempo del frame, así que se lee la cabecera del
    /// demo frame y se restaura la posición del archivo antes de delegar en el lector oficial.
    /// </summary>
    public override void ReadDemoFrameIntoPlaybackPackets(FArchive archive)
    {
        var start = archive.Position;
        try
        {
            if (archive.NetworkVersion >= NetworkVersionHistory.HISTORY_MULTIPLE_LEVELS)
            {
                archive.ReadInt32();
            }

            var time = archive.ReadSingle();
            if (!float.IsNaN(time) && time is >= 0 and < 86400)
            {
                _nowSeconds = time;
            }
        }
        catch
        {
            // Si el frame no se puede leer, se conserva el último tiempo conocido.
        }
        finally
        {
            archive.Seek(start, SeekOrigin.Begin);
        }

        base.ReadDemoFrameIntoPlaybackPackets(archive);
    }

    private void TrackContainer(uint channelIndex, BaseContainer container)
    {
        var searched = container.bAlreadySearched;
        _openedContainers.TryGetValue(channelIndex, out var wasOpened);

        // Primera observación ya abierta, o transición cerrado -> abierto.
        if (searched && !wasOpened)
        {
            _openedContainers[channelIndex] = true;

            var snapshot = new ContainerSnapshot
            {
                ClassName = container.GetType().Name,
                TimeSeconds = NowSeconds,
                SearchAnimationCount = container.SearchAnimationCount,
                LootTier = container.ReplicatedLootTier,
                ItemId = ReadItemId(container),
                SearchingPawn = ReadProperty(container, "SearchingPawn")?.ToString()
            };

            TryReadPosition(channelIndex, snapshot);
            Containers.Add(snapshot);
        }
        else if (!searched)
        {
            _openedContainers[channelIndex] = false;
        }
    }

    private void TrackTrackedContainer(uint channelIndex, FortnitePirateTokenTracker.ReplayReader.ITrackedContainer container)
    {
        _openedContainers.TryGetValue(channelIndex, out var wasOpened);

        if (container.bAlreadySearched && !wasOpened)
        {
            _openedContainers[channelIndex] = true;

            var snapshot = new ContainerSnapshot
            {
                ClassName = container.GetType().Name,
                TimeSeconds = NowSeconds,
                SearchAnimationCount = container.SearchAnimationCount,
                LootTier = container.ReplicatedLootTier,
                SearchingPawn = container.SearchingPawn?.ToString()
            };

            TryReadPosition(channelIndex, snapshot);
            Containers.Add(snapshot);
        }
        else if (!container.bAlreadySearched)
        {
            _openedContainers[channelIndex] = false;
        }
    }

    private void TrackPickup(INetFieldExportGroup exportGroup)
    {
        var itemId = ReadItemId(exportGroup);
        if (string.IsNullOrEmpty(itemId))
        {
            return;
        }

        Pickups.Add(new PickupSnapshot
        {
            ClassName = exportGroup.GetType().Name,
            TimeSeconds = NowSeconds,
            ItemId = itemId,
            Raw = Describe(exportGroup)
        });
    }

    /// <summary>Convierte una etiqueta de gameplay en texto legible sin acoplarse a su tipo.</summary>
    private static string? ReadTag(object tag)
    {
        if (tag is null)
        {
            return null;
        }

        var tagName = ReadProperty(tag, "TagName") ?? ReadProperty(tag, "Tag");
        if (tagName is not null)
        {
            var name = ReadProperty(tagName, "Name")?.ToString();
            if (!string.IsNullOrWhiteSpace(name) && name != "None")
            {
                return name;
            }

            var text = tagName.ToString();
            if (!string.IsNullOrWhiteSpace(text) && text != "None")
            {
                return text;
            }
        }

        var fallback = tag.ToString();
        return string.IsNullOrWhiteSpace(fallback) || fallback.StartsWith("Unreal.Core", StringComparison.Ordinal)
            ? null
            : fallback;
    }

    private static string? ReadItemId(object source)
    {
        foreach (var name in new[] { "ItemDefinition", "WeaponData", "ItemEntry", "PrimaryAssetName" })
        {
            var value = ReadProperty(source, name);
            if (value is null)
            {
                continue;
            }

            var nested = ReadProperty(value, "Name") ?? ReadProperty(value, "PathName")
                         ?? ReadProperty(value, "ObjectName") ?? ReadProperty(value, "AssetPathName");
            var text = nested?.ToString();
            if (!string.IsNullOrWhiteSpace(text) && text != "None")
            {
                return text;
            }

            var raw = value.ToString();
            if (!string.IsNullOrWhiteSpace(raw) && raw != "None" && raw != value.GetType().FullName)
            {
                return raw;
            }
        }

        return null;
    }

    private void TryReadPosition(uint channelIndex, ContainerSnapshot snapshot)
    {
        try
        {
            var channels = ReadProperty(this, "Channels") as Array;
            var channel = channels?.GetValue((int)channelIndex);
            var actor = ReadProperty(channel, "Actor");
            var movement = ReadProperty(actor, "ReplicatedMovement");
            var location = ReadProperty(movement, "Location");
            if (location is null)
            {
                return;
            }

            snapshot.X = ToDouble(ReadProperty(location, "X"));
            snapshot.Y = ToDouble(ReadProperty(location, "Y"));
            snapshot.Z = ToDouble(ReadProperty(location, "Z"));
            snapshot.HasPosition = snapshot.X != 0 || snapshot.Y != 0;
        }
        catch
        {
            // La posición del cofre es opcional: si no está disponible se marca como aproximada.
        }
    }

    private static object? ReadProperty(object? source, string name)
    {
        if (source is null)
        {
            return null;
        }

        var type = source.GetType();
        var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (prop is not null)
        {
            return prop.GetValue(source);
        }

        var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
        return field?.GetValue(source);
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

    private static string Describe(object source)
    {
        var parts = new List<string>();
        foreach (var prop in source.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length > 0)
            {
                continue;
            }

            try
            {
                var value = prop.GetValue(source);
                if (value is null || value is System.Collections.IEnumerable and not string)
                {
                    continue;
                }

                var text = value.ToString();
                if (!string.IsNullOrWhiteSpace(text) && text != "0" && text != "False")
                {
                    parts.Add($"{prop.Name}={text}");
                }
            }
            catch
            {
                // Una propiedad que falla al leerse no debe romper el volcado.
            }
        }

        return string.Join(", ", parts.Take(12));
    }
}
