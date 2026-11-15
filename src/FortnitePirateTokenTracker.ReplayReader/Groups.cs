using Unreal.Core.Attributes;
using Unreal.Core.Contracts;
using Unreal.Core.Models;
using Unreal.Core.Models.Enums;

namespace FortnitePirateTokenTracker.ReplayReader;

/// <summary>Marca los contenedores que este proyecto sabe interpretar.</summary>
public interface ITrackedContainer : INetFieldExportGroup
{
    bool bAlreadySearched { get; }
    uint SearchAnimationCount { get; }
    int ReplicatedLootTier { get; }
    uint? SearchingPawn { get; }
    bool bDestroyed { get; }
}

/// <summary>
/// Campos comunes de los cofres. Incluye SearchingPawn, que el modelo del parser
/// oficial no expone y que identifica el pawn que abre el contenedor.
/// </summary>
public abstract class TrackedContainer : ITrackedContainer
{
    [NetFieldExport("bAlreadySearched", RepLayoutCmdType.PropertyBool)]
    public bool bAlreadySearched { get; set; }

    [NetFieldExport("SearchAnimationCount", RepLayoutCmdType.PropertyUInt32)]
    public uint SearchAnimationCount { get; set; }

    [NetFieldExport("ReplicatedLootTier", RepLayoutCmdType.PropertyInt)]
    public int ReplicatedLootTier { get; set; }

    [NetFieldExport("SearchingPawn", RepLayoutCmdType.PropertyObject)]
    public uint? SearchingPawn { get; set; }

    [NetFieldExport("bDestroyed", RepLayoutCmdType.PropertyBool)]
    public bool bDestroyed { get; set; }
}

[NetFieldExportGroup("/Game/Building/ActorBlueprints/Containers/AlwaysSpawn_NormalChest.AlwaysSpawn_NormalChest_C")]
public class NormalChest : TrackedContainer
{
}

[NetFieldExportGroup("/Game/Building/ActorBlueprints/Containers/AlwaysSpawn_RareChest.AlwaysSpawn_RareChest_C")]
public class RareChest : TrackedContainer
{
}

[NetFieldExportGroup("/Game/Building/ActorBlueprints/Containers/Tiered_Chest_5_Child.Tiered_Chest_5_Child_C")]
public class TieredChestChild : TrackedContainer
{
}

[NetFieldExportGroup("/RelicChest/Gameplay/B_TieredChest_Relics.B_TieredChest_Relics_C")]
public class RelicChest : TrackedContainer
{
}

[NetFieldExportGroup("/SpritePosters/Gameplay/RelicChests/B_Delilah_TieredChest_Relics_Variant1.B_Delilah_TieredChest_Relics_Variant1_C")]
public class RelicChestVariant1 : TrackedContainer
{
}

[NetFieldExportGroup("/SpritePosters/Gameplay/RelicChests/B_Delilah_TieredChest_Relics_Variant2.B_Delilah_TieredChest_Relics_Variant2_C")]
public class RelicChestVariant2 : TrackedContainer
{
}

[NetFieldExportGroup("/SpritePosters/Blueprints/Items/Booty/B_SpritePosters_Booty_Chest.B_SpritePosters_Booty_Chest_C")]
public class BootyChest : TrackedContainer
{
}

/// <summary>
/// Cofre dorado del evento de tesoro. Al abrirse replica el premio concedido
/// (PrizeTemplateId), si se concedió (bSuccess) y quién lo abrió (OpeningPawn).
/// </summary>
[NetFieldExportGroup("/DelilahTreasureHuntGameplay/Gameplay/Actors/BGA_DelilahTreasureHuntAnimatedChest.BGA_DelilahTreasureHuntAnimatedChest_C")]
public class TreasureHuntChest : INetFieldExportGroup
{
    [NetFieldExport("PrizeTemplateId", RepLayoutCmdType.Property)]
    public FGameplayTag PrizeTemplateId { get; set; }

    [NetFieldExport("bSuccess", RepLayoutCmdType.PropertyBool)]
    public bool bSuccess { get; set; }

    [NetFieldExport("Tier", RepLayoutCmdType.PropertyInt)]
    public int? Tier { get; set; }

    [NetFieldExport("TierData", RepLayoutCmdType.Property)]
    public FGameplayTag TierData { get; set; }

    [NetFieldExport("OwnerPersistentID", RepLayoutCmdType.PropertyObject)]
    public uint? OwnerPersistentId { get; set; }

    [NetFieldExport("OpeningPawn", RepLayoutCmdType.PropertyObject)]
    public uint? OpeningPawn { get; set; }

    [NetFieldExport("SourceContainer", RepLayoutCmdType.PropertyObject)]
    public uint? SourceContainer { get; set; }

    [NetFieldExport("bDestroyed", RepLayoutCmdType.PropertyBool)]
    public bool bDestroyed { get; set; }
}
