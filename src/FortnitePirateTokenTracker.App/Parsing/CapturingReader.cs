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
    public bool Destroyed { get; set; }
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

    /// <summary>De dónde sale el premio: el cofre dorado o la habilidad concedida.</summary>
    public string Source { get; set; } = "";
}

/// <summary>Un pickup de ítem observado, útil para localizar el premio del cofre.</summary>
public sealed class PickupSnapshot
{
    public string ClassName { get; set; } = "";
    public double TimeSeconds { get; set; }
    public string? ItemId { get; set; }
    public string? Raw { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public bool HasPosition { get; set; }
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
        "Chest", "Container", "Pickup", "Treasure", "Prize", "Gold", "Token", "Pirate", "Loot", "Reward",
        "Cosmetic", "Vehicle", "Gift", "Delilah", "Inventory", "Locker", "Unlock", "Entitlement"
    };

    private readonly Dictionary<uint, bool> _openedContainers = new();

    /// <summary>Canales de cofre distintos vistos y cuántos llegaron a abrirse.</summary>
    private readonly HashSet<uint> _chestChannels = new();
    private readonly HashSet<uint> _openedChannels = new();
    private readonly HashSet<uint> _destroyedChannels = new();

    public int ChestChannels => _chestChannels.Count;
    public int OpenedChannels => _openedChannels.Count;
    public int DestroyedChannels => _destroyedChannels.Count;

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

    /// <summary>Clase de habilidad del premio ("GA_TreasureHandler_*"): NetGUID → nombre.</summary>
    private readonly Dictionary<uint, string> _treasureHandlerGuids = new();

    private int _abilityPayloads;
    private int _abilityGrants;

    public int AbilityPayloads => _abilityPayloads;
    public int AbilityGrants => _abilityGrants;
    public IEnumerable<string> TreasureHandlerClasses => _treasureHandlerGuids.Values.Distinct().Take(8);

    /// <summary>Un premio viaja como texto dentro del bloque opaco del cofre o de la habilidad.</summary>
    private static readonly System.Text.RegularExpressions.Regex PrizeIdPattern = new(
        "[A-Za-z][A-Za-z0-9_]*:[A-Za-z0-9_.\\-]+|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    public CapturingReader(ParseMode mode) : base(null, mode)
    {
        // Las habilidades del jugador no las decodifica la librería (sus campos no llevan
        // nombre exportado). RawProbe entrega sus bits en crudo y aquí se busca qué
        // habilidad de premio se concedió y cuándo.
        // Desactivado por defecto: interceptar los bits de la lista de habilidades multiplica
        // por diez el tiempo de análisis. Se activa con TT_ABILITY_PROBE=1 para depurar.
        if (Environment.GetEnvironmentVariable("TT_ABILITY_PROBE") == "1")
        {
        RawProbe.Filter = path => path.Contains("FortAbilitySystemComponent", StringComparison.Ordinal);
        RawProbe.Sink = (channel, group, field, data, bits) =>
        {
            if (group.Contains("AbilitySystemComponent", StringComparison.Ordinal)
                && group.EndsWith("_ClassNetCache", StringComparison.Ordinal)
                && field is "?" or "ActivatableAbilities"
                && _treasureHandlerGuids.Count > 0)
            {
                _abilityPayloads++;
                ReadAbilityGrants(channel, data, bits);
            }
        };
        }

        PromoteGroup(
            "/Game/Building/ActorBlueprints/Containers/Tiered_Chest_Athena.Tiered_Chest_Athena_C",
            "/Tokenizer/TieredChestAthena");
        PromoteGroup(
            "/Game/Building/ActorBlueprints/Containers/Tiered_Ammo_Athena.Tiered_Ammo_Athena_C",
            "/Tokenizer/TieredAmmoAthena");
    }

    /// <summary>
    /// El cofre escalonado es el más común y su clase oficial no declara SearchingPawn, así
    /// que no se sabe quién lo abre ni dónde está. El parser instancia una sola vez cada
    /// grupo en su constructor, por lo que hay que sustituir el tipo, los campos y la
    /// instancia compartida del grupo registrado por los de nuestra clase.
    /// </summary>
    private void PromoteGroup(string realPath, string aliasPath)
    {
        try
        {
            var parserType = _netFieldParser.GetType();
            var groups = parserType
                .GetField("NetFieldGroups", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(_netFieldParser)!;
            var groupsType = groups.GetType();
            var infoType = groupsType.GetGenericArguments().Last();
            var tryGet = groupsType.GetMethod("TryGetValue", new[] { typeof(string), infoType.MakeByRefType() })!;

            object? Get(string path)
            {
                var args = new object?[] { path, null };
                return (bool)tryGet.Invoke(groups, args)! ? args[1] : null;
            }

            var real = Get(realPath);
            var alias = Get(aliasPath);
            if (real is null || alias is null)
            {
                Warnings.Add($"No se encontró el grupo {realPath} para promoverlo.");
                return;
            }

            var promoteType = (Type)infoType.GetProperty("Type")!.GetValue(alias)!;
            var promoteProperties = infoType.GetProperty("Properties")!.GetValue(alias);
            var typeId = (int)infoType.GetProperty("TypeId")!.GetValue(real)!;

            infoType.GetField("<Type>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(real, promoteType);
            infoType.GetProperty("Properties")!.SetValue(real, promoteProperties);

            // Instancia compartida del grupo: se recrea con la clase nueva.
            var objects = (Array)parserType
                .GetField("_objects", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(_netFieldParser)!;
            var entry = objects.GetValue(typeId)!;
            entry.GetType().GetProperty("Instance", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(entry, Activator.CreateInstance(promoteType));

            var changed = (System.Collections.IList)entry.GetType()
                .GetProperty("ChangedProperties", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(entry)!;
            changed.Clear();
        }
        catch (Exception ex)
        {
            Warnings.Add($"Promoción de {realPath}: {ex.GetType().Name}: {ex.Message}");
        }
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

    /// <summary>Nombres de tag de la caché que huelen a premio, con su índice.</summary>
    public IEnumerable<string> InterestingTags()
    {
        var cache = _netFieldParser.GetType()
            .GetField("GuidCache", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(_netFieldParser)!;

        string[] keys = { "Delilah", "Treasure", "Prize", "Reward", "Gizmo", "Car", "Vehicle", "LilUzi" };

        foreach (var field in cache.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (field.GetValue(cache) is not System.Collections.IDictionary dictionary)
            {
                continue;
            }

            foreach (System.Collections.DictionaryEntry entry in dictionary)
            {
                var value = entry.Value?.ToString() ?? "";
                if (keys.Any(k => value.Contains(k, StringComparison.OrdinalIgnoreCase)))
                {
                    yield return $"{entry.Key} -> {value}";
                }
            }
        }
    }

    /// <summary>Clave de jugador (PlayerId) a partir del actor de su PlayerState.</summary>
    public string? PlayerStateKey(uint actorGuid) =>
        _playerStateKeys.TryGetValue(actorGuid, out var key) ? key : null;

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
            else if (exportGroup is TreasureHuntChest prizeChest)
            {
                TrackPrizeChest(channelIndex, prizeChest);
            }
            else if (exportGroup is BaseContainer container)
            {
                TrackContainer(channelIndex, container);
            }
            else if (exportGroup.GetType().Name.Contains("Pickup", StringComparison.OrdinalIgnoreCase))
            {
                TrackPickup(channelIndex, exportGroup);
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
        _chestChannels.Add(channelIndex);
        if (container.bDestroyed)
        {
            _destroyedChannels.Add(channelIndex);
        }

        _openedContainers.TryGetValue(channelIndex, out var wasOpened);

        if (container.bAlreadySearched == true && !wasOpened)
        {
            _openedContainers[channelIndex] = true;
            _openedChannels.Add(channelIndex);

            var (ownerKey, x, y, z, hasPosition) = ResolvePawn(container.SearchingPawn);
            if (!hasPosition && TryGetActorLocation(channelIndex, out var actorPosition))
            {
                (x, y, z, hasPosition) = (actorPosition.X, actorPosition.Y, actorPosition.Z, true);
            }

            Containers.Add(new ContainerSnapshot
            {
                ClassName = container.GetType().Name,
                TimeSeconds = NowSeconds,
                SearchAnimationCount = container.SearchAnimationCount ?? 0,
                LootTier = container.ReplicatedLootTier,
                SearchingPawn = container.SearchingPawn?.ToString(CultureInfo.InvariantCulture),
                OwnerKey = ownerKey,
                X = x,
                Y = y,
                Z = z,
                HasPosition = hasPosition
            });
        }
        else if (container.bDestroyed && !wasOpened)
        {
            // Cofre destruido sin abrirse: también cuenta como apertura.
            _openedContainers[channelIndex] = true;
            _openedChannels.Add(channelIndex);

            var (ownerKey, x, y, z, hasPosition) = ResolvePawn(container.SearchingPawn);
            if (!hasPosition && TryGetActorLocation(channelIndex, out var actorPosition))
            {
                (x, y, z, hasPosition) = (actorPosition.X, actorPosition.Y, actorPosition.Z, true);
            }

            Containers.Add(new ContainerSnapshot
            {
                ClassName = container.GetType().Name + " (destruido)",
                TimeSeconds = NowSeconds,
                SearchAnimationCount = container.SearchAnimationCount ?? 0,
                LootTier = container.ReplicatedLootTier,
                SearchingPawn = container.SearchingPawn?.ToString(CultureInfo.InvariantCulture),
                OwnerKey = ownerKey,
                X = x,
                Y = y,
                Z = z,
                HasPosition = hasPosition,
                Destroyed = true
            });
        }
        else if (container.bAlreadySearched == false)
        {
            _openedContainers[channelIndex] = false;
        }
    }

    private void TrackContainer(uint channelIndex, BaseContainer container)
    {
        _chestChannels.Add(channelIndex);
        if (container.bDestroyed)
        {
            _destroyedChannels.Add(channelIndex);
        }

        var searched = container.bAlreadySearched;
        _openedContainers.TryGetValue(channelIndex, out var wasOpened);

        // Primera observación ya abierta, o transición cerrado -> abierto.
        if (searched == true && !wasOpened)
        {
            _openedContainers[channelIndex] = true;
            _openedChannels.Add(channelIndex);

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
                SearchAnimationCount = container.SearchAnimationCount ?? 0,
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
        else if (container.bDestroyed && !wasOpened)
        {
            _openedContainers[channelIndex] = true;
            _openedChannels.Add(channelIndex);

            var hasPosition = TryGetActorLocation(channelIndex, out var actorPosition);

            Containers.Add(new ContainerSnapshot
            {
                ClassName = container.GetType().Name + " (destruido)",
                TimeSeconds = NowSeconds,
                SearchAnimationCount = container.SearchAnimationCount ?? 0,
                LootTier = container.ReplicatedLootTier,
                X = hasPosition ? actorPosition.X : 0,
                Y = hasPosition ? actorPosition.Y : 0,
                Z = hasPosition ? actorPosition.Z : 0,
                HasPosition = hasPosition,
                Destroyed = true
            });
        }
        else if (searched == false)
        {
            _openedContainers[channelIndex] = false;
        }
    }

    private void TrackPrizeChest(uint channelIndex, TreasureHuntChest prizeChest)
    {
        var opener = prizeChest.OpeningPawn ?? prizeChest.Instigator;
        var (ownerKey, x, y, z, hasPosition) = ResolvePawn(opener);
        if (!hasPosition && TryGetActorLocation(channelIndex, out var actorPosition))
        {
            (x, y, z, hasPosition) = (actorPosition.X, actorPosition.Y, actorPosition.Z, true);
        }

        Prizes.Add(new PrizeEvent
        {
            TimeSeconds = NowSeconds,
            // El premio no es un tag: es un bloque opaco con el id en texto
            // (por ejemplo "DelilahPrize:dpid_item4").
            PrizeTemplateId = ReadOpaqueText(prizeChest.PrizeTemplateId),
            TierData = ReadOpaqueText(prizeChest.Tier),
            Success = prizeChest.bSuccess != false,
            OpeningPawn = opener,
            OwnerPersistentId = prizeChest.OwnerPersistentID is { } owner ? (uint?)owner.IntPacked : null,
            OwnerKey = ownerKey,
            X = x,
            Y = y,
            Z = z,
            HasPosition = hasPosition,
            ClassName = prizeChest.GetType().Name,
            Source = "cofre"
        });
    }

    /// <summary>
    /// Saca el identificador legible de un bloque de bits opaco. El premio llega como texto
    /// suelto dentro de la estructura, así que se busca un patrón "algo:id" o un GUID.
    /// </summary>
    private static string? ReadOpaqueText(DebuggingObject? raw)
    {
        if (raw is null || raw.TotalBits == 0)
        {
            return null;
        }

        var bytes = raw.Bytes;
        var bits = raw.TotalBits;
        string? guid = null;

        // El texto puede empezar en cualquier bit: se prueban los ocho desplazamientos.
        for (var shift = 0; shift < 8; shift++)
        {
            var builder = new System.Text.StringBuilder();
            var text = new System.Text.StringBuilder();
            for (var bit = shift; bit + 8 <= bits; bit += 8)
            {
                var value = 0;
                for (var k = 0; k < 8; k++)
                {
                    var position = bit + k;
                    value |= (bytes[position >> 3] >> (position & 7) & 1) << k;
                }

                var c = (char)value;
                if (c is >= (char)32 and < (char)127)
                {
                    text.Append(c);
                }
                else
                {
                    if (text.Length >= 5)
                    {
                        builder.Append(text).Append(' ');
                    }
                    text.Clear();
                }
            }

            if (text.Length >= 5)
            {
                builder.Append(text);
            }

            var ascii = builder.ToString();
            if (ascii.Length == 0)
            {
                continue;
            }

            foreach (System.Text.RegularExpressions.Match match in PrizeIdPattern.Matches(ascii))
            {
                var value = match.Value;
                if (value.Contains("dpid_", StringComparison.OrdinalIgnoreCase)
                    || value.Contains("DelilahPrize", StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }

                guid ??= value;
            }
        }

        return guid;
    }

    /// <summary>
    /// La clase de la habilidad de premio se exporta la primera vez que el jugador recibe un
    /// tesoro; su NetGUID es lo que luego aparece en la lista de habilidades concedidas.
    /// </summary>
    protected override void OnNetGuidExported(uint netGuid, string pathName)
    {
        base.OnNetGuidExported(netGuid, pathName);

        if (string.IsNullOrEmpty(pathName) || !pathName.Contains("TreasureHandler", StringComparison.Ordinal))
        {
            return;
        }

        var name = pathName[(pathName.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        if (dot >= 0)
        {
            name = name[(dot + 1)..];
        }

        _treasureHandlerGuids[netGuid] = name.Replace("Default__", "", StringComparison.Ordinal);
    }

    /// <summary>
    /// Lee la lista de habilidades concedidas (FastArray "ActivatableAbilities") sin nombres de
    /// campo: cabecera, y por cada elemento su id y sus propiedades. Cuando una referencia es una
    /// habilidad de premio, ese jugador acaba de recibir un tesoro.
    /// </summary>
    private void ReadAbilityGrants(uint channel, byte[] data, int bits)
    {
        var pos = 1; // bSupportsFastArrayDeltaStructSerialization
        int Bit() => pos < bits ? (data[pos >> 3] >> (pos & 7)) & 1 : throw new IndexOutOfRangeException();
        uint Read(int n)
        {
            uint value = 0;
            for (var k = 0; k < n; k++)
            {
                value |= (uint)Bit() << k;
                pos++;
            }
            return value;
        }
        uint ReadPacked()
        {
            uint value = 0;
            for (var shift = 0; shift < 35; shift += 7)
            {
                var b = Read(8);
                value |= (b >> 1) << shift;
                if ((b & 1) == 0)
                {
                    return value;
                }
            }
            throw new InvalidDataException();
        }

        try
        {
            _ = Read(32);
            _ = Read(32);
            var deletes = Read(32);
            var changed = Read(32);
            if (deletes > 512 || changed > 512 || bits < 129)
            {
                return;
            }

            pos += (int)deletes * 32;
            for (var i = 0; i < changed; i++)
            {
                var elementId = (int)Read(32);
                while (true)
                {
                    var handle = ReadPacked();
                    if (handle == 0)
                    {
                        break;
                    }

                    var numBits = (int)ReadPacked();
                    if (numBits < 0 || pos + numBits > bits)
                    {
                        return;
                    }

                    if (numBits is 8 or 16 or 24 or 32 or 40)
                    {
                        var start = pos;
                        uint guid = 0;
                        for (var shift = 0; pos < start + numBits; shift += 7)
                        {
                            var b = Read(8);
                            guid |= (b >> 1) << shift;
                            if ((b & 1) == 0)
                            {
                                break;
                            }
                        }

                        pos = start + numBits;
                        if (_treasureHandlerGuids.TryGetValue(guid, out var handler))
                        {
                            var owner = channel < Channels.Length ? Channels[channel]?.ActorId ?? 0 : 0;
                            _playerStateKeys.TryGetValue(owner, out var ownerKey);
                            if (!Prizes.Any(p => p.Source == "habilidad" && p.OwnerPersistentId == owner && p.Tier == elementId))
                            {
                                _abilityGrants++;
                                Prizes.Add(new PrizeEvent
                                {
                                    TimeSeconds = NowSeconds,
                                    PrizeTemplateId = null,
                                    TierData = handler,
                                    Success = true,
                                    Tier = elementId,
                                    OpeningPawn = owner,
                                    OwnerPersistentId = owner,
                                    OwnerKey = ownerKey,
                                    ClassName = "GA_TreasureHandler",
                                    Source = "habilidad"
                                });
                            }
                        }
                    }
                    else
                    {
                        pos += numBits;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or InvalidDataException)
        {
            // No era una lista de habilidades: se ignora.
        }
    }

    private void TrackPickup(uint channelIndex, INetFieldExportGroup exportGroup)
    {
        var itemId = ReadItemId(exportGroup);
        if (string.IsNullOrEmpty(itemId))
        {
            return;
        }

        var hasPosition = TryGetActorLocation(channelIndex, out var position);

        Pickups.Add(new PickupSnapshot
        {
            ClassName = exportGroup.GetType().Name,
            TimeSeconds = NowSeconds,
            ItemId = itemId,
            Raw = Describe(exportGroup),
            X = hasPosition ? position.X : 0,
            Y = hasPosition ? position.Y : 0,
            Z = hasPosition ? position.Z : 0,
            HasPosition = hasPosition
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
