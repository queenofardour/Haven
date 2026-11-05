using System.Numerics;

namespace Haven.Core;

/// <summary>
/// The six Gold Saucer arcade furnishings and their GoldSaucerArcadeMachine rows (read from the game data): the window
/// the game opens (by machine type), the time limit, the player's place and idle pose, and per result the player's
/// animation, the machine's timeline and the balloon image. Haven plays them locally; no MGP is spent or won. Parts are
/// the models (file names) that answer the cursor: the play spot, not the whole cabinet several metres tall.
/// </summary>
public sealed record ArcadeMachine(
    ushort Model, uint Row, int Type, string Key, uint NameText, int Timeout,
    float BalloonDelay, float EndDelay, Vector3 PlayerOffset, Vector3 BalloonOffset,
    ushort Idle, ushort[] Actions, byte[] MachineTimelines, uint[] Images, string[] Parts)
{
    /// <summary>The "TIME'S UP" balloon.</summary>
    public const uint TimeUpImage = 121541;
    public bool Gathering => Type is 4 or 5;
    public bool Rounds => Type == 0;
}

public static class ArcadeMachines
{
    private static readonly Vector3 Above = new(0, 2.5f, 0);
    // Model key → machine (HousingFurniture rows 197283, 197368-197372; script 721374 nests these handlers).
    public static readonly IReadOnlyList<ArcadeMachine> All =
    [
        new(705, 2359297, 0, "monstertoss", 9987, 30, 1.5f, 4.2f, new(0, .3f, 3f), new(0, 2.4f, 0), 4076, [4077, 4077, 4077, 4077], [3, 1, 1, 1], [121502, 121501, 121501, 121501], ["w1eb_f1_bskt2", "w1eb_f1_bskt3"]),
        new(704, 2359298, 3, "mooglespaw", 9990, 15, 3f, 6f, new(0, .1f, 3.2f), Above, 4070, [0, 0, 0, 0], [3, 1, 1, 1], [121522, 121521, 121521, 121521], ["fun_b0_m0704b", "fun_b0_m0704h"]),
        new(647, 2359299, 2, "striker", 9989, 15, 2.1f, 6f, new(0, .1f, 1.7f), Above, 4074, [4075, 4075, 4075, 4075], [3, 5, 5, 1], [121514, 121513, 121512, 121511], ["w1eb_f1_hanm2", "w1eb_m1_sabo3"]),
        new(706, 2359300, 1, "cuffacur", 9988, 15, 1.1f, 4f, new(0, .1f, 3f), Above, 4072, [4073, 4073, 4073, 4073], [3, 5, 5, 1], [121534, 121533, 121532, 121531], ["w1eb_m1_glgm1"]),
        new(708, 2359301, 4, "finerminer", 9993, 60, 1.7f, 3f, new(0, .2f, 3.2f), Above, 4670, [4671, 4671, 4671, 4671], [4, 8, 6, 2], [121584, 121583, 121582, 121581], ["fun_b0_m0708d"]),
        new(707, 2359302, 5, "outonalimb", 9994, 60, 1.7f, 3f, new(-.2f, .2f, 1.2f), Above, 4668, [4669, 4669, 4669, 4669], [4, 8, 6, 2], [121594, 121593, 121592, 121591], ["fun_b0_m0707d"]),
    ];
    public static ArcadeMachine? ByModel(ushort model) => All.FirstOrDefault(m => m.Model == model);
}

/// <summary>
/// Out on a Limb and The Finer Miner as Haven plays the server's part. A game starts with "Gauge Your Skill!": the zone
/// hit (Cactuar 0, Morbol 1, Titan 2) sets the difficulty. Then rounds of "Find the Sweet Spot!": a hidden spot on the
/// 0-100 gauge; each swing's distance from it gives a level (Poor, Good, Great, Excellent, by the machine's own distance
/// bands) that cuts the round's gauge (by 0, 1, 4 or 10 of 10). Empty the gauge before the swings run out to win the
/// round, then claim or Double Down, up to six rounds. Payouts are shown, never paid.
/// </summary>
public sealed class GatheringArcade
{
    public const int GaugeMax = 10, MaxRounds = 6;
    // The machine sheet's per-level bytes (never read by the client): distance bands A..B and gauge cut C.
    private static readonly int[] MinerNear = [15, 8, 2, 0], MinerFar = [100, 14, 7, 1], LimbNear = [21, 11, 3, 0], LimbFar = [100, 20, 10, 2];
    private static readonly int[] Cut = [0, 1, 4, 10];
    // Payouts by consecutive wins (community tables), per difficulty Cactuar, Morbol, Titan.
    private static readonly int[][] LimbPay = [[50, 100, 150, 400, 500, 600], [60, 125, 185, 500, 625, 750], [85, 175, 260, 700, 875, 1050]];
    private static readonly int[][] MinerPay = [[35, 85, 125, 175, 210, 250], [50, 150, 360, 735, 1425, 2625], [60, 200, 600, 1600, 4000, 9600]];

    private readonly Random random;
    public bool Miner { get; }
    public int Difficulty { get; private set; } = -1;
    public int Round { get; private set; }
    public int Spot { get; private set; }
    public int Gauge { get; private set; } = GaugeMax;
    public int Swings { get; private set; }

    public GatheringArcade(bool miner, Random random) { Miner = miner; this.random = random; }

    /// <summary>Swings per round: The Finer Miner by difficulty (6, 5, 4), Out on a Limb always 10.</summary>
    public int Attempts => Miner ? Difficulty switch { 2 => 4, 1 => 5, _ => 6 } : 10;
    /// <summary>The cursor's cycle (the agent's speed value, ÷500 = seconds): Out on a Limb quicker with difficulty.</summary>
    public ushort Speed => Miner ? (ushort)1000 : Difficulty switch { 2 => (ushort)700, 1 => (ushort)1000, _ => (ushort)1500 };
    public int SwingsLeft => Attempts - Swings;
    public bool RoundWon => Gauge <= 0;
    public bool CanDoubleDown => RoundWon && Round + 1 < MaxRounds;
    /// <summary>The payout now (after the rounds won) and the next round's.</summary>
    public int Payout => Round < 0 || Difficulty < 0 ? 0 : (Miner ? MinerPay : LimbPay)[Difficulty][Math.Min(Round, MaxRounds - 1)];
    public int NextPayout => Difficulty < 0 ? 0 : (Miner ? MinerPay : LimbPay)[Difficulty][Math.Min(Round + 1, MaxRounds - 1)];

    /// <summary>The first swing's zone sets the difficulty; the first round begins.</summary>
    public void Choose(int zone) { Difficulty = Math.Clamp(zone, 0, 2); Round = 0; NewRound(); }

    /// <summary>Double Down: the next round, with a new hidden spot.</summary>
    public void Next() { Round++; NewRound(); }

    private void NewRound() { Spot = random.Next(5, 96); Gauge = GaugeMax; Swings = 0; }

    /// <summary>A swing at a gauge position 0-100: its level (0 Poor … 3 Excellent), which cuts the gauge.</summary>
    public int Swing(int position)
    {
        var level = Level(Math.Abs(Math.Clamp(position, 0, 100) - Spot));
        Swings++;
        Gauge = Math.Max(0, Gauge - Cut[level]);
        return level;
    }

    /// <summary>The level for a distance from the spot, by the machine's bands.</summary>
    public int Level(int distance)
    {
        var near = Miner ? MinerNear : LimbNear; var far = Miner ? MinerFar : LimbFar;
        for (var level = 3; level >= 0; level--) if (distance >= near[level] && distance <= far[level]) return level;
        return 0;
    }

    /// <summary>The log line for a level (LogMessage: The Finer Miner 4785-4788, Out on a Limb 4790-4793).</summary>
    public uint LevelMessage(int level) => (Miner ? 4785u : 4790u) + (uint)Math.Clamp(level, 0, 3);
}
