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

// El lector oficial registra la ruta del cofre escalonado con una clase que no declara
// SearchingPawn. Se declara aquí bajo un alias y el lector promueve el grupo real a esta
// clase al construirse (tipo, campos e instancia compartida del parser).
[NetFieldExportGroup("/Tokenizer/TieredChestAthena")]
public class TieredChestAthena : FortniteReplayReader.Models.NetFieldExports.Items.Chest, ITrackedContainer
{
    [NetFieldExport("SearchingPawn", RepLayoutCmdType.PropertyObject)]
    public uint? SearchingPawn { get; set; }
}

[NetFieldExportGroup("/Tokenizer/TieredAmmoAthena")]
public class TieredAmmoAthena : TrackedContainer
{
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

// Cofres y contenedores del evento que la librería no tipa: sin estas clases sus
// aperturas no se cuentan en absoluto.
[NetFieldExportGroup("/DelilahDynamicLevels/Gameplay/BGA_LootCannon_StandardChest_Pirate.BGA_LootCannon_StandardChest_Pirate_C")]
public class PirateLootCannonChest : TrackedContainer
{
}

[NetFieldExportGroup("/ExtractionPointGameplay/Gameplay/BGA_ExtractionPointGameplay_Chest.BGA_ExtractionPointGameplay_Chest_C")]
public class ExtractionChest : TrackedContainer
{
}

[NetFieldExportGroup("/ExtractionRelicFinder/Gameplay/Chests/Prop_Athena_BuriedChest_RelicFinder.Prop_Athena_BuriedChest_RelicFinder_C")]
public class BuriedChestProp : TrackedContainer
{
}

[NetFieldExportGroup("/ExtractionRelicFinder/Gameplay/Chests/B_Athena_BuriedChest_RelicFinder.B_Athena_BuriedChest_RelicFinder_C")]
public class BuriedChestRelicFinder : TrackedContainer
{
}

[NetFieldExportGroup("/Game/Athena/Items/ForagedItems/BuriedChest/B_Athena_BuriedChest_Chest.B_Athena_BuriedChest_Chest_C")]
public class BuriedChest : TrackedContainer
{
}

[NetFieldExportGroup("/Game/Building/ActorBlueprints/Containers/FoodBox_Produce_Athena.FoodBox_Produce_Athena_C")]
public class ProduceBox : TrackedContainer
{
}

[NetFieldExportGroup("/Hera/Props/Container/Container_WoodenCrates_A/Blueprints/BP_Container_WoodenCrates_B.BP_Container_WoodenCrates_B_C")]
public class WoodenCrateB : TrackedContainer
{
}

[NetFieldExportGroup("/Hera/Props/Container/Container_WoodenCrates_A/Blueprints/BP_Container_WoodenCrates_D.BP_Container_WoodenCrates_D_C")]
public class WoodenCrateD : TrackedContainer
{
}

/// <summary>
/// Cofre dorado del evento de tesoro. Al abrirse replica el premio concedido
/// (PrizeTemplateId), si se concedió (bSuccess) y quién lo abrió (OpeningPawn).
/// </summary>
/// <summary>
/// Cofre dorado del evento. El grupo declara PrizeTemplateId en DOS handles (48 y 49) con el
/// mismo nombre y el parser lo indexa por nombre, así que se lee el valor del campo
/// equivocado. Aquí se declaran por handle para poder leer ambos y quedarse con el bueno.
/// </summary>
[NetFieldExportGroup("/DelilahTreasureHuntGameplay/Gameplay/Actors/BGA_DelilahTreasureHuntAnimatedChest.BGA_DelilahTreasureHuntAnimatedChest_C")]
public class TreasureHuntChest : INetFieldExportGroup
{
    [NetFieldExportHandle(4, RepLayoutCmdType.Ignore)]
    public object RemoteRole { get; set; }

    [NetFieldExportHandle(13, RepLayoutCmdType.Ignore)]
    public object Role { get; set; }

    [NetFieldExportHandle(14, RepLayoutCmdType.PropertyObject)]
    public uint? Instigator { get; set; }

    [NetFieldExportHandle(15, RepLayoutCmdType.PropertyObject)]
    public uint? OwnerPersistentId { get; set; }

    [NetFieldExportHandle(28, RepLayoutCmdType.PropertyBool)]
    public bool bDestroyed { get; set; }

    [NetFieldExportHandle(31, RepLayoutCmdType.PropertyBool)]
    public bool bInstantDeath { get; set; }

    [NetFieldExportHandle(44, RepLayoutCmdType.PropertyObject)]
    public uint? SourceContainer { get; set; }

    [NetFieldExportHandle(45, RepLayoutCmdType.PropertyObject)]
    public uint? OpeningPawn { get; set; }

    [NetFieldExportHandle(46, RepLayoutCmdType.PropertyBool)]
    public bool bSuccess { get; set; }

    [NetFieldExportHandle(48, RepLayoutCmdType.Property)]
    public FGameplayTag Prize48 { get; set; }

    [NetFieldExportHandle(49, RepLayoutCmdType.Property)]
    public FGameplayTag Prize49 { get; set; }

    [NetFieldExportHandle(54, RepLayoutCmdType.PropertyInt)]
    public int? Tier { get; set; }

    [NetFieldExportHandle(55, RepLayoutCmdType.Property)]
    public FGameplayTag TierData { get; set; }
}
