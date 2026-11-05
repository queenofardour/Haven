using Haven.Core;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace Haven.Games;

/// <summary>Every Triple Triad card the game has, from its sheets: numbers, stars and type.</summary>
public static class TriadCards
{
    public static TriadCard[] Load(ExcelSheet<TripleTriadCardResident> residents, ExcelSheet<TripleTriadCard> cards) =>
        [.. residents.Where(r => r.Top > 0 && r.TripleTriadCardRarity.IsValid && cards.HasRow(r.RowId))
            .Select(r => new TriadCard(r.RowId, cards.GetRow(r.RowId).Name.ToString(), r.Top, r.Right, r.Bottom, r.Left,
                r.TripleTriadCardRarity.Value.Stars, (int)r.TripleTriadCardType.RowId))
            .Where(c => c.Name.Length > 0)];
}
