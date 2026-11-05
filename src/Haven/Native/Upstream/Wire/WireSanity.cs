// Haven: identifiers from other players, checked against the installed game data before they reach native
// animation and action calls. An unknown ActionTimeline or Action row can crash the game, and an access violation is
// not something a try/catch recovers from, so a value that names nothing is zeroed (no animation) on receipt.
using System.Collections.Generic;
using Dalamud.Plugin.Services;

namespace HavenNative.Wire;

public static class WireSanity
{
    private static HashSet<uint>? timelines, actions, generalActions;

    public static void Initialise(IDataManager data)
    {
        timelines = [.. RowIds(data.GetExcelSheet<Lumina.Excel.Sheets.ActionTimeline>())];
        actions = [.. RowIds(data.GetExcelSheet<Lumina.Excel.Sheets.Action>())];
        generalActions = [.. RowIds(data.GetExcelSheet<Lumina.Excel.Sheets.GeneralAction>())];
    }
    private static IEnumerable<uint> RowIds<T>(Lumina.Excel.ExcelSheet<T> sheet) where T : struct, Lumina.Excel.IExcelRow<T>
    { foreach (var row in sheet) yield return row.RowId; }

    /// <summary>A timeline a peer asks to play: 0 (none) or a row of the installed ActionTimeline sheet.</summary>
    public static ushort Timeline(ushort id) => id == 0 || timelines == null || timelines.Contains(id) ? id : (ushort)0;

    /// <summary>Zeroes every identifier in a WARM frame that names nothing in the game data.</summary>
    public static void Clean(WarmPayload w)
    {
        w.TimelineId = Timeline(w.TimelineId);
        w.MountAnimTimeline = Timeline(w.MountAnimTimeline);
        w.MountActionTimeline = Timeline(w.MountActionTimeline);
        w.MinionAnim = Timeline(w.MinionAnim);
        w.OrnamentTimeline = Timeline(w.OrnamentTimeline);
        w.OrnamentActionTimeline = Timeline(w.OrnamentActionTimeline);
        w.StandupTimelineId = Timeline(w.StandupTimelineId);
        // EmoteController pose families are 0-6 (0xFF none); a pose index is 0-7.
        if (w.PoseType > 6 && w.PoseType != 0xFF) w.PoseType = 0;
        if (w.CPoseState > 7) w.CPoseState = 0;
        // Cosmetic cast replay: only real actions (1) and general actions (5), and only rows that exist.
        var known = w.ActionType switch
        {
            1 => actions?.Contains(w.ActionId) ?? false,
            5 => generalActions?.Contains(w.ActionId) ?? false,
            _ => false,
        };
        if (!known) { w.ActionId = 0; w.ActionType = 0; }
    }
}
