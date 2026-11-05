using System.Text.Json;
using System.Text.Json.Serialization;

namespace Haven.Core.Odyssey;

/// <summary>The run's screens (design §7); the director is a state machine over them.</summary>
public enum OdysseyScreen { Title, StartingBinder, WorldTitle, Path, Deck, Duel, Reward, Upgrade, MateriaSwap, Event, Shop, Victory, GameOver }

/// <summary>Who an opponent is: a regular duelist, an elite, a world's boss, or a secret duel from an event (Gilgamesh, a guest star).</summary>
public enum OpponentKind { Regular, Elite, Boss, Secret }

/// <summary>What a reward choice gives.</summary>
public enum RewardKind { ClaimCard, Enhancement, Materia, Item, MateriaSlot, MasterMateria }

/// <summary>What a shop ware is.</summary>
public enum WareKind { Enhancement, Materia, Item, Pack }

/// <summary>
/// A card in the binder: its TripleTriadCard row (which keeps its art), what enhancements raised each side (top, right,
/// bottom, left; the effective side is clamped to A), its perk and its type when changed (-1: the card's own). Uid tells
/// two copies of one card apart.
/// </summary>
public sealed class BinderCard
{
    public int Uid { get; set; }
    public uint Id { get; set; }
    public int[] Raised { get; set; } = new int[4];
    public TriadPerk Perk { get; set; }
    public int Type { get; set; } = -1;
    /// <summary>The Enhancements applied to it (a Crystal Shrine's blessing counts), up to OdysseyBalance.MaxEnhancements. Cards of runs saved before the cap read 0.</summary>
    public int Enhancements { get; set; }
    /// <summary>The world and duel the card was gained in (0: the starting binder).</summary>
    public int World { get; set; }
    public int Duel { get; set; }
}

/// <summary>A card as it plays, fully written out (an opponent's enhanced card, a duel's deck).</summary>
public sealed class CardState
{
    public uint Id { get; set; }
    public string Name { get; set; } = "";
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
    public int Left { get; set; }
    public int Stars { get; set; }
    public int Type { get; set; }
    public TriadPerk Perk { get; set; }
    public static CardState From(TriadCard c) => new() { Id = c.Id, Name = c.Name, Top = c.Top, Right = c.Right, Bottom = c.Bottom, Left = c.Left, Stars = c.Stars, Type = c.Type, Perk = c.Perk };
    public TriadCard ToCard() => new(Id, Name, Top, Right, Bottom, Left, Stars, Type) { Perk = Perk };
}

/// <summary>An opponent as the path offers them, deck and all, so the title card, the path and the duel agree.</summary>
public sealed class OpponentPlan
{
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public string Line { get; set; } = "";
    public OpponentKind Kind { get; set; }
    public List<TriadRule> Rules { get; set; } = [];
    public List<string> Materia { get; set; } = [];
    public List<CardState> Deck { get; set; } = [];
    public int AiLevel { get; set; }
    public uint Music { get; set; }
    /// <summary>The card shown on the path panel (an elite's signature, a boss's own, a regular's best).</summary>
    public uint Signature { get; set; }
    public double Power { get; set; }
    /// <summary>For a secret duel, the only cards of the deck that can be claimed (empty: any).</summary>
    public List<uint> Claimable { get; set; } = [];
    /// <summary>The event a secret duel came from, or null.</summary>
    public EventKind? Event { get; set; }
    public bool IsElite => Kind is OpponentKind.Elite or OpponentKind.Secret;
    public bool IsBoss => Kind == OpponentKind.Boss;
}

/// <summary>The player's choices on the Deck screen: the five binder cards (uids) and the Items used for this duel.</summary>
public sealed class DuelPrep
{
    public List<int> Deck { get; set; } = [];
    public List<int> Potions { get; set; } = [];
    public bool HiPotion { get; set; }
    public bool Elixir { get; set; }
    public bool Megalixir { get; set; }
    public bool Ether { get; set; }
    public bool Gysahl { get; set; }
    public bool Remedy { get; set; }
    public TriadRule? Echo { get; set; }
    /// <summary>Tonics in effect (item keys); their edge changes are written into the dealt duel.</summary>
    public List<string> Tonics { get; set; } = [];
    /// <summary>A Treasure Map's least rarity for the won duel's reward (null: no map).</summary>
    public Rarity? Map { get; set; }
    /// <summary>Prism Powder: the player's hand is concealed.</summary>
    public bool Hidden { get; set; }
    /// <summary>Queen of Cards' Favour: a rule added to the duel.</summary>
    public TriadRule? Added { get; set; }
    /// <summary>A Ribbon: their rules down to the Open rule and Sudden Death, their Materia cancelled.</summary>
    public bool Ribbon { get; set; }
    /// <summary>Pandora's Box: the Legendary Materia it gave for this duel.</summary>
    public string? Pandora { get; set; }
    public List<string> Used { get; set; } = [];
}

/// <summary>A dealt duel: everything the match (or the simulator) needs, written out so a load replays the same deal.</summary>
public sealed class DuelState
{
    public List<CardState> Blue { get; set; } = [];
    public List<CardState> Red { get; set; } = [];
    public TriadPlayer First { get; set; }
    public List<TriadRule> Rules { get; set; } = [];
    public List<string> BlueMateria { get; set; } = [];
    public List<string> RedMateria { get; set; } = [];
    /// <summary>An Ether's reveal and a Megalixir's reflect, on top of the Materia.</summary>
    public bool ExtraReveal { get; set; }
    public int ExtraReflect { get; set; }
    /// <summary>The tonics on each side's edge (item keys: their Mine on blue's, their Theirs on red's), and a Prism Powder's concealed hand.</summary>
    public List<string> BlueTonics { get; set; } = [];
    public List<string> RedTonics { get; set; } = [];
    public bool Hidden { get; set; }
    public int AiLevel { get; set; }
    public uint Music { get; set; }
    public int Seed { get; set; }
    /// <summary>The player's whole binder as it plays (deck cards buffed), for the opponent's guesses at hidden cards.</summary>
    public List<CardState> Unseen { get; set; } = [];
    public double PlayerPower { get; set; }
    public double OpponentPower { get; set; }
    /// <summary>How many times this duel was dealt (draws and revives deal it again).</summary>
    public int Attempt { get; set; } = 1;
}

public sealed class RewardChoice
{
    public RewardKind Kind { get; set; }
    public string Key { get; set; } = "";
    public Rarity Rarity { get; set; }
}

/// <summary>The reward screen: the choices offered, rerolls so far, and the card claim's sub-choice.</summary>
public sealed class RewardState
{
    public string Heading { get; set; } = "";
    public List<RewardChoice> Choices { get; set; } = [];
    public List<CardState> Claimable { get; set; } = [];
    public int Rerolls { get; set; }
    public bool Claiming { get; set; }
    /// <summary>A boss gives both: stage 0 claims a card, stage 1 picks a Materia.</summary>
    public int Stage { get; set; }
    public OpponentKind From { get; set; }
    public bool Secret { get; set; }
    /// <summary>The free Enhancement before world I (no duel behind it, no rerolls).</summary>
    public bool Starting { get; set; }
}

/// <summary>An Enhancement waiting for its card (and side, corner or type), and where to go after.</summary>
public sealed class UpgradeState
{
    public string Enhancement { get; set; } = "";
    public OdysseyScreen ReturnTo { get; set; }
    /// <summary>The gil paid for it, given back if the player goes back before applying it (a shop ware, the workshop).</summary>
    public int Paid { get; set; }
    /// <summary>The shop ware it came from (marked unsold again on going back), or -1.</summary>
    public int Ware { get; set; } = -1;
}

/// <summary>A new Materia with the slots full: keep it in place of one, or discard it.</summary>
public sealed class SwapState
{
    public string Materia { get; set; } = "";
    public OdysseyScreen ReturnTo { get; set; }
    /// <summary>As UpgradeState: the gil paid and the shop ware, for going back before deciding.</summary>
    public int Paid { get; set; }
    public int Ware { get; set; } = -1;
}

public sealed class EventState
{
    public EventKind Kind { get; set; }
    public int Stage { get; set; }
    public string? Result { get; set; }
    /// <summary>The merchant's wares (item keys), the Moogle Post's parcel, the guest star's index.</summary>
    public List<string> Wares { get; set; } = [];
    public string? Parcel { get; set; }
    public int Guest { get; set; }
    /// <summary>The stable: the card offered for the one chosen.</summary>
    public int TradeUid { get; set; } = -1;
    public CardState? Offered { get; set; }
}

public sealed class Ware
{
    public WareKind Kind { get; set; }
    public string Key { get; set; } = "";
    public Rarity Rarity { get; set; }
    public int Price { get; set; }
    public bool Sold { get; set; }
}

public sealed class ShopState
{
    public List<Ware> Wares { get; set; } = [];
    public List<CardState> Pack { get; set; } = [];
    public bool PackSold { get; set; }
    public int Removals { get; set; }
    public int Rerolls { get; set; }
    /// <summary>A binder card was sold on this visit (one a visit).</summary>
    public bool CardSold { get; set; }
}

/// <summary>One duel of the run summary.</summary>
public sealed class DuelLog
{
    public int World { get; set; }
    public int Duel { get; set; }
    public string Opponent { get; set; } = "";
    public OpponentKind Kind { get; set; }
    public string Outcome { get; set; } = "";
    public int Margin { get; set; }
    public double PlayerPower { get; set; }
    public double OpponentPower { get; set; }
    public List<TriadRule> Rules { get; set; } = [];
    public bool Luck { get; set; }
    public string? Revived { get; set; }
    public int Mgp { get; set; }
}

public sealed class RunStats
{
    public int DuelsPlayed { get; set; }
    public int DuelsWon { get; set; }
    public Dictionary<uint, int> Captures { get; set; } = [];
    public int BestPlacement { get; set; }
    public uint BestPlacementCard { get; set; }
    public string BestPlacementWhere { get; set; } = "";
    public List<uint> Claimed { get; set; } = [];
    public List<string> MateriaHistory { get; set; } = [];
    public List<DuelLog> Duels { get; set; } = [];
    public int MgpEarned { get; set; }
    public int MgpSpent { get; set; }
    public int Revives { get; set; }
}

/// <summary>
/// The whole run, saved at every screen (design §7). Every random decision draws from Seed and Step, so a loaded run
/// continues exactly as the unsaved one would have.
/// </summary>
public sealed class OdysseyRun
{
    public const int CurrentVersion = 1;
    public int Version { get; set; } = CurrentVersion;
    public int Seed { get; set; }
    public int Step { get; set; }
    public int Rank { get; set; }
    public int World { get; set; }
    public int Duel { get; set; }
    public OdysseyScreen Screen { get; set; }
    public bool Over { get; set; }
    public bool Won { get; set; }
    public DateTime Started { get; set; }

    public List<BinderCard> Binder { get; set; } = [];
    public int NextUid { get; set; } = 1;
    public List<string> Materia { get; set; } = [];
    public int Slots { get; set; } = OdysseyBalance.StartingSlots;
    /// <summary>The Master Materia was taken: one more Legendary Materia may be slotted (once a run).</summary>
    public bool MasterMateria { get; set; }
    public List<string> Items { get; set; } = [];
    public int Mgp { get; set; }
    public int ReDealsLeft { get; set; }
    /// <summary>Pity in weight points: each reward screen without a Rare raises the next one's Rare chance.</summary>
    public double Pity { get; set; }

    // Per-world flags.
    public bool CardSharpUsed { get; set; }
    public bool PinionUsed { get; set; }
    public bool EventLastDuel { get; set; }
    public List<string> UsedNames { get; set; } = [];
    // Per-run flags.
    public bool GilgameshDone { get; set; }
    public bool GuestStarDone { get; set; }
    public bool SecondFormDone { get; set; }

    public List<OpponentPlan> Path { get; set; } = [];
    public OpponentPlan? Opponent { get; set; }
    public DuelPrep Prep { get; set; } = new();
    public DuelState? Dealt { get; set; }
    public RewardState? Reward { get; set; }
    public UpgradeState? Upgrade { get; set; }
    public SwapState? Swap { get; set; }
    public EventState? Event { get; set; }
    public ShopState? Shop { get; set; }
    public RunStats Stats { get; set; } = new();
    /// <summary>The Moogle's Verdict, once the run is over.</summary>
    public string Verdict { get; set; } = "";
    /// <summary>The power gap, margin and luck of the lost duel, for the verdict and the records.</summary>
    public double LastGap { get; set; }
    public int LastMargin { get; set; }
    public bool LastLuck { get; set; }

    /// <summary>A generator for one decision; the step moves on so the next decision draws afresh and a load replays this one.</summary>
    public Random Next() => new(Mix(Seed, Step++));
    /// <summary>A generator for a decision that must not depend on when it is made (a world's boss, which the fortune teller can show early).</summary>
    public Random Fixed(int key) => new(Mix(Seed, -1000 - key));
    /// <summary>A stable hash (HashCode.Combine is randomised per process).</summary>
    public static int Mix(int seed, int step)
    {
        unchecked
        {
            var h = (uint)seed * 0x9E3779B1u ^ ((uint)step + 0x7F4A7C15u) * 0x85EBCA6Bu;
            h ^= h >> 15; h *= 0xC2B2AE35u; h ^= h >> 13;
            return (int)h;
        }
    }
}

/// <summary>One finished run, in the records.</summary>
public sealed class RunRecord
{
    public DateTime When { get; set; }
    public bool Won { get; set; }
    public int World { get; set; }
    public int Duel { get; set; }
    public int Rank { get; set; }
    public int DuelsWon { get; set; }
    public List<string> Materia { get; set; } = [];
    public string Verdict { get; set; } = "";
}

/// <summary>The records per PC (odyssey-records.json, design §7).</summary>
public sealed class OdysseyRecords
{
    public const int CurrentVersion = 1;
    public int Version { get; set; } = CurrentVersion;
    public int Runs { get; set; }
    public int Clears { get; set; }
    public int BestWorld { get; set; }
    public int BestDuel { get; set; }
    /// <summary>The highest Crystal Rank cleared (-1: no clear yet).</summary>
    public int BestRankCleared { get; set; } = -1;
    public int DuelsWon { get; set; }
    public uint MvpCard { get; set; }
    public int MvpCaptures { get; set; }
    public List<RunRecord> Recent { get; set; } = [];
    /// <summary>The Crystal Rank a new run may be started at: one above the best cleared.</summary>
    public int UnlockedRank => Math.Min(OdysseyBalance.MaxRank, BestRankCleared + 1);
}

/// <summary>Saving and loading the run and the records as JSON strings (the atomic write is the plugin's). Round-trips are exact.</summary>
public static class OdysseyJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        IncludeFields = false,
        WriteIndented = false,
    };

    public static string Save(OdysseyRun run) => JsonSerializer.Serialize(run, Options);
    public static OdysseyRun Load(string json)
    {
        var run = JsonSerializer.Deserialize<OdysseyRun>(json, Options) ?? throw new InvalidDataException("The run file is empty.");
        if (run.Version != OdysseyRun.CurrentVersion) throw new InvalidDataException($"The run file is version {run.Version}; this Haven reads version {OdysseyRun.CurrentVersion}.");
        return run;
    }
    public static string Save(OdysseyRecords records) => JsonSerializer.Serialize(records, Options);
    public static OdysseyRecords LoadRecords(string json)
    {
        var records = JsonSerializer.Deserialize<OdysseyRecords>(json, Options) ?? throw new InvalidDataException("The records file is empty.");
        if (records.Version != OdysseyRecords.CurrentVersion) throw new InvalidDataException($"The records file is version {records.Version}.");
        return records;
    }
}
