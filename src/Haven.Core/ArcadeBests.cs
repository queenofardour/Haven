using System.Text.Json;

namespace Haven.Core;

/// <summary>
/// Personal bests at the arcade machines, kept on this PC per character (arcade-bests.json): Monster Toss counts
/// baskets, Cuff-a-Cur and the Crystal Tower Striker their best hit, The Moogle's Paw its best catch, and Out on a
/// Limb and The Finer Miner the points banked (the MGP the Gold Saucer would have paid). Nothing leaves the PC.
/// </summary>
public sealed class ArcadeBests
{
    private static readonly string[] StrikerHits = ["WEAK...", "GLANCING!", "CRUSHING!!", "PULVERIZING!!!"];
    private static readonly string[] CuffHits = ["WEAK...", "BRUISING!", "PUNISHING!!", "BRUTAL!!!"];
    private readonly string path;
    private Dictionary<string, Dictionary<string, int>> bests = [];

    public ArcadeBests(string path)
    {
        this.path = path;
        try { if (File.Exists(path)) bests = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(File.ReadAllText(path)) ?? []; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { bests = []; }
    }

    public int? Best(string character, string machine) =>
        bests.TryGetValue(character, out var mine) && mine.TryGetValue(machine, out var score) ? score : null;

    /// <summary>Records a score; true when it beats the character's best (a first score of 0 is not a best).</summary>
    public bool Record(string character, string machine, int score)
    {
        if (score <= 0 || Best(character, machine) is { } best && best >= score) return false;
        if (!bests.TryGetValue(character, out var mine)) bests[character] = mine = [];
        mine[machine] = score;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(bests));
            File.Move(temporary, path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return true;
    }

    /// <summary>
    /// A machine's score from one game: baskets made, the hit's level (0-3, as 1-4 so a weak hit still counts), the
    /// claw's catch (1 small, 2 large), or the points banked.
    /// </summary>
    public static int Score(string machine, int level, int baskets, int points) => machine switch
    {
        "monstertoss" => baskets,
        "striker" or "cuffacur" => Math.Clamp(level, 0, 3) + 1,
        "mooglespaw" => level switch { 1 => 2, 3 => 1, _ => 0 },
        _ => points,
    };

    /// <summary>A score in words, as the machine would put it.</summary>
    public static string Describe(string machine, int score) => machine switch
    {
        "monstertoss" => score == 1 ? "1 basket" : $"{score} baskets",
        "striker" => StrikerHits[Math.Clamp(score - 1, 0, 3)],
        "cuffacur" => CuffHits[Math.Clamp(score - 1, 0, 3)],
        "mooglespaw" => score >= 2 ? "SEIZED! (large prize)" : "SEIZED! (small prize)",
        _ => $"{score:N0} points",
    };
}
