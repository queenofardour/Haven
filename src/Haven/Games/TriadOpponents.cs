using Dalamud.Plugin.Services;
using Haven.Core;
using Lumina.Excel;

namespace Haven.Games;

/// <summary>A Triple Triad NPC from the game: who and where, their two rules, and their fixed and variable cards.</summary>
public sealed record TriadNpc(uint Row, string Name, string Place, IReadOnlyList<TriadRule> Rules, IReadOnlyList<TriadCard> Fixed, IReadOnlyList<TriadCard> Variable)
{
    public TriadOpponent Opponent => new(Name, Rules, random => TriadGame.NpcDeck(Fixed, Variable, random), Place);
}

/// <summary>
/// Every Triple Triad NPC, read from the game's sheets (TripleTriad rows, the ENpcBase that names each, and the Level
/// that places it): the opponents Haven's Triple Triad Board offers.
/// </summary>
public static class TriadOpponents
{
    private const uint FirstRow = 0x230000, EndRow = 0x240000;

    public static List<TriadNpc> Load(IDataManager data, IReadOnlyList<TriadCard> cards)
    {
        var byId = cards.ToDictionary(c => c.Id);
        var npcs = new Dictionary<uint, uint>();
        foreach (var npc in data.Excel.GetSheet<RawRow>(name: "ENpcBase"))
            for (var column = 2; column <= 33; column++)
            {
                var value = npc.ReadUInt32Column(column);
                if (value >= FirstRow && value < EndRow) npcs.TryAdd(value, npc.RowId);
            }
        // Where each NPC stands (Level rows of type 8 place an ENpc in a territory).
        var places = new Dictionary<uint, string>();
        var territories = data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>();
        foreach (var level in data.Excel.GetSheet<RawRow>(name: "Level"))
        {
            if (level.ReadUInt8Column(5) != 8) continue;
            var territory = territories.GetRowOrDefault(level.ReadUInt16Column(9));
            if (territory?.PlaceName.ValueNullable?.Name.ExtractText() is { Length: > 0 } place) places.TryAdd(level.ReadUInt32Column(6), place);
        }
        var residents = data.Excel.GetSheet<RawRow>(name: "ENpcResident");
        var result = new List<TriadNpc>();
        foreach (var row in data.Excel.GetSheet<RawRow>(name: "TripleTriad"))
        {
            if (!npcs.TryGetValue(row.RowId, out var npc) || !residents.HasRow(npc)) continue;
            var name = residents.GetRow(npc).ReadStringColumn(0).ExtractText();
            if (name.Length == 0) continue;
            name = char.ToUpperInvariant(name[0]) + name[1..];
            TriadCard? Card(int column) => byId.GetValueOrDefault(row.ReadUInt16Column(column));
            var fixedCards = Enumerable.Range(0, 5).Select(Card).OfType<TriadCard>().ToArray();
            var variable = Enumerable.Range(5, 5).Select(Card).OfType<TriadCard>().ToArray();
            if (fixedCards.Length + variable.Length < 5) continue;
            var rules = new[] { row.ReadUInt8Column(10), row.ReadUInt8Column(11) }.Where(r => r is >= 1 and <= 15).Select(r => (TriadRule)r).ToArray();
            result.Add(new(row.RowId, name, places.GetValueOrDefault(npc, ""), rules, fixedCards, variable));
        }
        return [.. result.OrderBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase)];
    }
}
