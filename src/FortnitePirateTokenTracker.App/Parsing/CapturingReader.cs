using System.Globalization;
using System.Reflection;
using FortniteReplayReader.Models.NetFieldExports;
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
    public string? OwnerKey { get; set; }
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
    public string? OwnerKey { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public bool HasPosition { get; set; }
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
    /// <summary>Fuerza la carga del ensamblado de grupos propios antes de crear el parser.</summary>
    private static readonly Type RegistrationAnchor = typeof(FortnitePirateTokenTracker.ReplayReader.ITrackedContainer);

    private static readonly string[] InterestingKeywords =
    {
        "Chest", "Container", "Pickup", "Treasure", "Prize", "Gold", "Token", "Pirate", "Loot", "Reward"
    };

    private readonly Dictionary<uint, bool> _openedContainers = new();

    /// <summary>Pawn (NetGUID) → PlayerState (NetGUID).</summary>
    private readonly Dictionary<uint, uint?> _pawnToPlayerState = new();

    /// <summary>PlayerState (NetGUID) → clave de jugador (PlayerId / PlayerID / UniqueId).</summary>
    private readonly Dictionary<uint, string> _playerStateKeys = new();

    /// <summary>Última posición replicada conocida de cada pawn, con su instante.</summary>
    private readonly Dictionary<uint, (double T, double X, double Y, double Z)> _pawnPositions = new();

    /// <summary>Cosméticos que replica el pawn (outfit, mochila, pico, música…).</summary>
    private readonly Dictionary<uint, Dictionary<string, string>> _pawnCosmetics = new();

    private static readonly (string Property, string Slot)[] CosmeticSlots =
    {
        ("Character", "outfit"),
        ("Backpack", "backBling"),
        ("Pickaxe", "pickaxe"),
        ("Glider", "glider"),
        ("LoadingScreen", "loadingScreen"),
        ("MusicPack", "musicPack"),
        ("PetSkin", "pet"),
        ("SkyDiveContrail", "contrail")
    };

    private float _nowSeconds;

    public CapturingReader(ParseMode mode) : base(null, mode)
    {
    }

    public List<ContainerSnapshot> Containers { get; } = new();
    public List<PrizeEvent> Prizes { get; } = new();
    public List<PickupSnapshot> Pickups { get; } = new();
    public List<string> UnknownGroups { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<string> Events { get; } = new();

    /// <summary>Cosméticos por pawn, con el instante y la posición en la que se vieron.</summary>
    public IReadOnlyDictionary<uint, Dictionary<string, string>> PawnCosmetics => _pawnCosmetics;

    /// <summary>Última posición conocida de un pawn.</summary>
    public bool TryGetPawnPosition(uint pawn, out (double T, double X, double Y, double Z) position) =>
        _pawnPositions.TryGetValue(pawn, out position);

    /// <summary>Todos los grupos replicados vistos, con su número de apariciones.</summary>
    public Dictionary<string, int> GroupPaths { get; } = new();

    /// <summary>Nombres de campo declarados por los grupos relevantes para el evento.</summary>
    public Dictionary<string, HashSet<string>> GroupFields { get; } = new();

    /// <summary>Nombre y tipo declarado de cada campo, para elegir el lector correcto.</summary>
    public Dictionary<string, List<string>> GroupFieldDetails { get; } = new();

    public int PawnLinks => _pawnToPlayerState.Count;
    public int PlayerStateKeys => _playerStateKeys.Count;
    public int PawnPositions => _pawnPositions.Count;

    /// <summary>Indica si el parser reconoce (y por tanto deserializa) un grupo por su ruta.</summary>
    public bool WillReadGroup(string path) => _netFieldParser.WillReadType(path);

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

        var actorId = ReadActorId(channelIndex);

        try
        {
            TrackIdentity(actorId, exportGroup);

            if (exportGroup is FortnitePirateTokenTracker.ReplayReader.ITrackedContainer tracked)
            {
                TrackTrackedContainer(channelIndex, tracked);
            }
            else if (exportGroup is FortnitePirateTokenTracker.ReplayReader.TreasureHuntChest prizeChest)
            {
                TrackPrizeChest(channelIndex, prizeChest);
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

    private static bool IsInteresting(string path) =>
        InterestingKeywords.Any(k => path.Contains(k, StringComparison.OrdinalIgnoreCase));

    /// <summary>GUID del actor asociado al canal que se está leyendo.</summary>
    private uint? ReadActorId(uint channelIndex)
    {
        try
        {
            if (channelIndex >= Channels.Length)
            {
                return null;
            }

            return Channels[channelIndex]?.ActorId;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Mantiene los enlaces de identidad: el pawn conoce su PlayerState y el
    /// PlayerState la clave del jugador. Sin esto no se puede atribuir nada.
    /// </summary>
    private void TrackIdentity(uint? actorId, INetFieldExportGroup exportGroup)
    {
        if (actorId is not uint guid)
        {
            return;
        }

        switch (exportGroup)
        {
            case PlayerPawn pawn:
                _pawnToPlayerState[guid] = pawn.PlayerState;

                var location = pawn.ReplicatedMovement?.Location ?? pawn.Location;
                if (location is not null)
                {
                    _pawnPositions[guid] = (NowSeconds, ToDouble(location.X), ToDouble(location.Y), ToDouble(location.Z));
                }

                ReadPawnCosmetics(guid, pawn);
                break;

            case FortPlayerState state:
                var key = state.PlayerId?.ToString(CultureInfo.InvariantCulture)
                          ?? state.PlayerID?.ToString(CultureInfo.InvariantCulture)
                          ?? state.UniqueId
                          ?? state.UniqueID;

                if (!string.IsNullOrWhiteSpace(key))
                {
                    _playerStateKeys[guid] = key!;
                }

                break;
        }
    }

    /// <summary>Traduce el pawn que abre un cofre a una clave de jugador y su última posición.</summary>
    private (string? OwnerKey, double X, double Y, double Z, bool HasPosition) ResolvePawn(uint? pawnGuid)
    {
        if (pawnGuid is not uint guid)
        {
            return (null, 0, 0, 0, false);
        }

        string? owner = null;
        if (_pawnToPlayerState.TryGetValue(guid, out var stateGuid)
            && stateGuid is uint state
            && _playerStateKeys.TryGetValue(state, out var key))
        {
            owner = key;
        }

        if (_pawnPositions.TryGetValue(guid, out var position))
        {
            return (owner, position.X, position.Y, position.Z, true);
        }

        return (owner, 0, 0, 0, false);
    }

    /// <summary>
    /// Los cosméticos de la versión actual se replican en el pawn, no en el PlayerState.
    /// Se leen por reflexión para no depender de los nombres exactos del modelo.
    /// </summary>
    private void ReadPawnCosmetics(uint guid, PlayerPawn pawn)
    {
        Dictionary<string, string> slots;
        if (!_pawnCosmetics.TryGetValue(guid, out slots!))
        {
            slots = new Dictionary<string, string>(StringComparer.Ordinal);
            _pawnCosmetics[guid] = slots;
        }

        foreach (var (property, slot) in CosmeticSlots)
        {
            var value = ReadProperty(pawn, property);
            var item = value is null ? null : ReadItemId(value);
            if (!string.IsNullOrWhiteSpace(item) && item != "None")
            {
                slots[slot] = item!;
            }
        }
    }

    /// <summary>
    /// Posición del actor del canal. Los cofres replican su ubicación al spawnear, así que
    /// sirve incluso cuando el grupo no expone el pawn que los abre.
    /// </summary>
    private bool TryGetActorLocation(uint channelIndex, out (double X, double Y, double Z) position)
    {
        position = default;

        try
        {
            if (channelIndex >= Channels.Length)
            {
                return false;
            }

            var location = Channels[channelIndex]?.Actor?.Location;
            if (location is null)
            {
                return false;
            }

            var x = ToDouble(location.X);
            var y = ToDouble(location.Y);
            var z = ToDouble(location.Z);

            if (x == 0 && y == 0 && z == 0)
            {
                return false;
            }

            position = (x, y, z);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void TrackTrackedContainer(uint channelIndex, FortnitePirateTokenTracker.ReplayReader.ITrackedContainer container)
    {
        _openedContainers.TryGetValue(channelIndex, out var wasOpened);

        if (container.bAlreadySearched && !wasOpened)
        {
            _openedContainers[channelIndex] = true;

            var (ownerKey, x, y, z, hasPosition) = ResolvePawn(container.SearchingPawn);
            if (!hasPosition && TryGetActorLocation(channelIndex, out var actorPosition))
            {
                (x, y, z, hasPosition) = (actorPosition.X, actorPosition.Y, actorPosition.Z, true);
            }

            Containers.Add(new ContainerSnapshot
            {
                ClassName = container.GetType().Name,
                TimeSeconds = NowSeconds,
                SearchAnimationCount = container.SearchAnimationCount,
                LootTier = container.ReplicatedLootTier,
                SearchingPawn = container.SearchingPawn?.ToString(CultureInfo.InvariantCulture),
                OwnerKey = ownerKey,
                X = x,
                Y = y,
                Z = z,
                HasPosition = hasPosition
            });
        }
        else if (!container.bAlreadySearched)
        {
            _openedContainers[channelIndex] = false;
        }
    }

    private void TrackContainer(uint channelIndex, BaseContainer container)
    {
        var searched = container.bAlreadySearched;
        _openedContainers.TryGetValue(channelIndex, out var wasOpened);

        // Primera observación ya abierta, o transición cerrado -> abierto.
        if (searched && !wasOpened)
        {
            _openedContainers[channelIndex] = true;

            var pawnGuid = ReadProperty(container, "SearchingPawn") is { } raw
                           && uint.TryParse(raw.ToString(), out var parsed)
                ? parsed
                : (uint?)null;

            var (ownerKey, x, y, z, hasPosition) = ResolvePawn(pawnGuid);
            if (!hasPosition && TryGetActorLocation(channelIndex, out var actorPosition))
            {
                (x, y, z, hasPosition) = (actorPosition.X, actorPosition.Y, actorPosition.Z, true);
            }

            Containers.Add(new ContainerSnapshot
            {
                ClassName = container.GetType().Name,
                TimeSeconds = NowSeconds,
                SearchAnimationCount = container.SearchAnimationCount,
                LootTier = container.ReplicatedLootTier,
                ItemId = ReadItemId(container),
                SearchingPawn = pawnGuid?.ToString(CultureInfo.InvariantCulture),
                OwnerKey = ownerKey,
                X = x,
                Y = y,
                Z = z,
                HasPosition = hasPosition
            });
        }
        else if (!searched)
        {
            _openedContainers[channelIndex] = false;
        }
    }

    private void TrackPrizeChest(uint channelIndex, FortnitePirateTokenTracker.ReplayReader.TreasureHuntChest prizeChest)
    {
        var (ownerKey, x, y, z, hasPosition) = ResolvePawn(prizeChest.OpeningPawn);
        if (!hasPosition && TryGetActorLocation(channelIndex, out var actorPosition))
        {
            (x, y, z, hasPosition) = (actorPosition.X, actorPosition.Y, actorPosition.Z, true);
        }

        Prizes.Add(new PrizeEvent
        {
            TimeSeconds = NowSeconds,
            PrizeTemplateId = ReadTag(prizeChest.PrizeTemplateId),
            Success = prizeChest.bSuccess,
            Tier = prizeChest.Tier,
            OpeningPawn = prizeChest.OpeningPawn,
            OwnerPersistentId = prizeChest.OwnerPersistentId,
            TierData = ReadTag(prizeChest.TierData),
            OwnerKey = ownerKey,
            X = x,
            Y = y,
            Z = z,
            HasPosition = hasPosition,
            ClassName = prizeChest.GetType().Name
        });
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
