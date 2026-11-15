using Unreal.Core.Attributes;
using Unreal.Core.Contracts;
using Unreal.Core.Models;
using Unreal.Core.Models.Enums;

namespace FortnitePirateTokenTracker.ReplayReader;

/// <summary>Marca los contenedores que este proyecto sabe interpretar.</summary>
public interface ITrackedContainer : INetFieldExportGroup
{
    // En la librería local con parches estos dos son opcionales: una actualización
    // que no los trae llega como null y no debe leerse como "sin abrir".
    bool? bAlreadySearched { get; }
    uint? SearchAnimationCount { get; }
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
    public bool? bAlreadySearched { get; set; }

    [NetFieldExport("SearchAnimationCount", RepLayoutCmdType.PropertyUInt32)]
    public uint? SearchAnimationCount { get; set; }

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

// El cofre dorado del tesoro (BGA_DelilahTreasureHuntAnimatedChest) ya viene modelado por
// la librería local: su PrizeTemplateId es un bloque opaco que contiene el id del premio.
