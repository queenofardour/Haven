using Haven.Core;
namespace Haven;

internal sealed record FurnitureEntry(uint ItemId, uint FurnitureId, ushort ModelKey, ushort Icon, string Name, string Category, string Description, byte Usage, byte DyeSlots,
    string? BuildingPart = null, uint? FinishPart = null, uint CustomTalk = 0)
{
    // The game's shared switch script (lamps, instruments, cupboards...) and table food are clickable in vanilla.
    public const uint SwitchScript = 721037;
    public const byte TableFood = 7;
    // Orchestrion furnishings open the music player instead of a native orchestrion service.
    public const byte OrchestrionUsage = 10;
    public bool Interactive => CustomTalk == SwitchScript || Usage == TableFood;
    public bool Orchestrion => Usage == OrchestrionUsage;
    public bool Food => Usage == TableFood;
    // The Triple Triad Board (usage 9): a click opens Triple Triad (TripleTriadMatch, after choosing an opponent).
    public bool TripleTriad => Usage == 9;
    /// <summary>A Gold Saucer arcade machine's game (the six arcade furnishings), or null.</summary>
    public ArcadeMachine? Arcade => ArcadeMachines.ByModel(ModelKey);
    public string WidgetId { get; } = BuildingPart ?? ItemId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    // A picture frame, easel or wall frame (HousingFurniture usage 12, parameter 2): it shows a chosen painting.
    // Partitions (parameter 3) take a wallpaper instead, through FinishPart 0 like a building piece's wall.
    public bool TakesPicture { get; init; }
    // Wall picture frames can stand upright (Furnishing.Portrait); the Easel cannot.
    public bool TurnsUpright { get; init; }
    // A vase (display kind 1) or an Angler's Canvas (4 small to 7 extra large): the Vase row whose VaseFlower rows it
    // shows (Furnishing.Display). 0 for anything else.
    public uint DisplayKind { get; init; }
    public bool Vase => DisplayKind == 1;
    // An indoor flowerpot (usage 11: the Riviera, Glade and Oasis Flowerpots). Real gardening is the server's; Haven shows
    // a chosen flower fully grown, from the same list as a vase (DisplayKind 1), as a cosmetic display.
    public bool Flowerpot { get; init; }
    public bool Canvas => DisplayKind is >= 4 and <= 7;
    // An aquarium's tier (1 to 4: its slots, and the total fish size it holds); 0 for anything else.
    public int AquariumTier { get; init; }
}
// Something a vase or canvas shows: its VaseFlower row and display kind, the item (flowers, or the fish whose print it
// is) with its name and icon, and its model or texture number.
internal sealed record DisplayEntry(uint Row, uint Kind, uint ItemId, string Name, ushort Icon, ushort Index, byte Colour = 0);
// A fish an aquarium can hold: its AquariumFish row, item, name and icon, model number, water (1 fresh, 2 salt) and size (1 small to 4 extra large).
internal sealed record AquariumFishEntry(uint Row, uint ItemId, string Name, ushort Icon, ushort Model, uint Water, int Size)
{
    public string SizeCode => Size switch { 1 => "sm", 2 => "mi", 3 => "la", _ => "ll" };
}
// A painting a frame can show: its Picture row, the painting item's name, its image icon and the material that draws it.
internal sealed record PictureEntry(uint Id, string Name, uint Image, string Material);
internal sealed record FixtureEntry(uint ItemId, uint FixtureId, ushort Icon, string Name, uint Part);
internal sealed record DyeEntry(uint Id, string Name, uint Color);
