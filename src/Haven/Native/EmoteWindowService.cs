// Haven: inside a home, the game's emote window lists the emotes you don't own and shows them as usable.
//
// Adapted from BypassEmote by Aspher0 (https://github.com/Aspher0/BypassEmote, AGPL-3.0-or-later; see
// THIRD_PARTY_NOTICES.md): EmoteAddonValues, EmoteListDimmer and ContextMenuValues. Haven plays every emote inside a
// home (EmoteUnlockService), so only the window needs teaching. It is left alone outside a home.
//
// The window's AtkValues hold its rows from index 21, six values each: emote, icon, condition flags (bits 10 and 11
// mark a locked row), kind (the category, or 6 | category << 8 in search results), name and search text. Index 20
// counts them; AgentEmote +0x34/+0x36 hold the list's row counts and +0x31 the open tab. Rows the game drew dimmed
// are painted back to full brightness each frame, and the right-click menu's Execute is enabled.
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace Haven.Native;

internal sealed unsafe class EmoteWindowService : IDisposable
{
    private const string EmoteAddon = "Emote", ContextMenuAddon = "ContextMenu";
    private const int CountIndex = 20, FirstRow = 21, ValuesPerRow = 6;

    private readonly IAddonLifecycle lifecycle;
    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly IDataManager data;
    private readonly IPluginLog log;
    private readonly Func<bool> inside;
    private readonly Func<ushort, bool> owned;

    // The emotes this character does not own, in the game's order, with their names and commands.
    private readonly HashSet<uint> locked = [];
    private readonly HashSet<string> lockedNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<uint> lockedByOrder = [];
    private readonly Dictionary<uint, string[]> searchTerms = [];
    // Search text for rows Haven writes: native strings the window keeps pointing at, freed on dispose.
    private readonly Dictionary<uint, nint> commandText = [];
    private bool wasInside;

    public EmoteWindowService(IAddonLifecycle lifecycle, IFramework framework, IGameGui gameGui, IDataManager data, IPluginLog log,
        Func<bool> inside, Func<ushort, bool> owned)
    {
        this.lifecycle = lifecycle; this.framework = framework; this.gameGui = gameGui; this.data = data; this.log = log;
        this.inside = inside; this.owned = owned;
        lifecycle.RegisterListener(AddonEvent.PreSetup, EmoteAddon, OnEmoteValues);
        lifecycle.RegisterListener(AddonEvent.PreRefresh, EmoteAddon, OnEmoteValues);
        lifecycle.RegisterListener(AddonEvent.PreSetup, ContextMenuAddon, OnContextValues);
        lifecycle.RegisterListener(AddonEvent.PreRefresh, ContextMenuAddon, OnContextValues);
        framework.Update += OnFrame;
    }

    public void Dispose()
    {
        framework.Update -= OnFrame;
        lifecycle.UnregisterListener(AddonEvent.PreSetup, EmoteAddon, OnEmoteValues);
        lifecycle.UnregisterListener(AddonEvent.PreRefresh, EmoteAddon, OnEmoteValues);
        lifecycle.UnregisterListener(AddonEvent.PreSetup, ContextMenuAddon, OnContextValues);
        lifecycle.UnregisterListener(AddonEvent.PreRefresh, ContextMenuAddon, OnContextValues);
        // A window still open points at these strings; leaking them then is safer than freeing them under it.
        if (Addon(EmoteAddon, ready: false) == null)
            foreach (var text in commandText.Values) Marshal.FreeHGlobal(text);
        commandText.Clear();
    }

    // The agent's row counts (+0x31/+0x34/+0x36) aren't in ClientStructs: off after a game patch until Haven is updated.
    private bool Active => inside() && locked.Count > 0 && Haven.Native.PatchCheck.PatchGuard.Allows(Haven.Core.PatchFeature.EmoteWindow);

    private void OnFrame(IFramework _)
    {
        try
        {
            var now = inside();
            if (now && !wasInside) Refresh();
            wasInside = now;
            if (!Active) return;
            if (Addon(EmoteAddon) is var window && window != null) PaintRows(window);
            if (Addon(ContextMenuAddon) is var menu && menu != null) EnableExecute(menu);
        }
        catch (Exception ex) { log.Error(ex, "[Haven] Emote window update failed."); }
    }

    // Who owns what is read once per entry into a home: the character may differ from the last visit.
    private void Refresh()
    {
        locked.Clear(); lockedNames.Clear(); lockedByOrder.Clear(); searchTerms.Clear();
        var ordered = new List<(uint Id, ushort Order)>();
        var ownedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var emote in data.GetExcelSheet<Emote>())
        {
            if (emote.RowId is 0 or > ushort.MaxValue) continue;
            var name = emote.Name.ExtractText().Trim();
            if (owned((ushort)emote.RowId)) { if (name.Length > 0) ownedNames.Add(name); continue; }
            if (emote.Order == 0 || name.Length == 0 || emote.TextCommand.RowId == 0) continue;
            locked.Add(emote.RowId); lockedNames.Add(name);
            ordered.Add((emote.RowId, emote.Order));
        }
        lockedNames.ExceptWith(ownedNames);
        ordered.Sort((a, b) => a.Order.CompareTo(b.Order));
        foreach (var (id, _) in ordered)
        {
            var commands = Commands(id);
            lockedByOrder.Add(id);
            if (!commandText.ContainsKey(id)) commandText[id] = Native(string.Join(" ", commands));
            if (data.GetExcelSheet<Emote>().GetRowOrDefault(id) is { } row) commands.Insert(0, row.Name.ExtractText());
            searchTerms[id] = [.. commands];
        }
        log.Information($"[Haven] Emote window: {lockedByOrder.Count} emotes you don't own are listed inside the home.");
    }

    private List<string> Commands(uint id)
    {
        var commands = new List<string>();
        if (data.GetExcelSheet<Emote>().GetRowOrDefault(id)?.TextCommand.ValueNullable is not { } command) return commands;
        foreach (var text in new[] { command.Command.ExtractText(), command.ShortCommand.ExtractText(), command.Alias.ExtractText(), command.ShortAlias.ExtractText() })
            if (!string.IsNullOrWhiteSpace(text) && !commands.Contains(text)) commands.Add(text);
        return commands;
    }

    private static nint Native(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var buffer = Marshal.AllocHGlobal(bytes.Length + 1);
        Marshal.Copy(bytes, 0, buffer, bytes.Length);
        Marshal.WriteByte(buffer, bytes.Length, 0);
        return buffer;
    }

    // ── The window's values ──────────────────────────────────────────────────────────────────────────────────

    private void OnEmoteValues(AddonEvent type, AddonArgs args)
    {
        try
        {
            if (!Active) return;
            AtkValue* values; uint count;
            switch (args)
            {
                case AddonSetupArgs setup: values = (AtkValue*)setup.AtkValues; count = setup.AtkValueCount; break;
                case AddonRefreshArgs refresh: values = (AtkValue*)refresh.AtkValues; count = refresh.AtkValueCount; break;
                default: return;
            }
            if (values == null) return;
            AddMissing(values, count);
            if (type == AddonEvent.PreRefresh && SearchText((AtkUnitBase*)args.Addon.Address) is { Length: > 0 } search)
                AddSearchMatches(values, count, Matching(search));
            ClearLockedFlags(values, count);
        }
        catch (Exception ex) { log.Error(ex, "[Haven] Emote window values failed."); }
    }

    private List<uint> Matching(string search)
    {
        var matches = new List<uint>();
        foreach (var id in lockedByOrder)
            if (searchTerms.TryGetValue(id, out var terms) && terms.Any(t => t.Contains(search, StringComparison.OrdinalIgnoreCase))) matches.Add(id);
        return matches;
    }

    private void ClearLockedFlags(AtkValue* values, uint count)
    {
        for (var at = FirstRow; at + ValuesPerRow <= count; at += ValuesPerRow)
            if (IsRow(values, at) && locked.Contains(values[at].UInt)) values[at + 2].UInt &= ~(1u << 10 | 1u << 11);
    }

    private void AddMissing(AtkValue* values, uint count)
    {
        if (count <= CountIndex || values[CountIndex].Type != AtkValueType.UInt) return;
        var agent = AgentEmote.Instance();
        if (agent == null) return;
        var total = (int)values[CountIndex].UInt;
        if (total <= 0) return;
        var listRows = ListRows(values, count, total);
        if (listRows <= 0) return;
        var present = new HashSet<uint>();
        var rows = new Row[listRows];
        for (var row = 0; row < listRows; row++)
        {
            var at = FirstRow + row * ValuesPerRow;
            present.Add(values[at].UInt);
            rows[row] = new(values[at + 3].UInt, OrderOf(values[at].UInt), StringAt(values + at + 4));
        }
        var missing = lockedByOrder.Where(id => !present.Contains(id)).ToList();
        if (missing.Count == 0) return;
        var newList = listRows + missing.Count;
        var newTotal = total + missing.Count;
        if (FirstRow + newTotal * ValuesPerRow > count) return;
        var plan = Plan(rows, missing);
        if (plan.Count != newList) return;
        var saved = new AtkValue[listRows * ValuesPerRow];
        for (var i = 0; i < saved.Length; i++) saved[i] = values[FirstRow + i];
        // Rows after the list (the window's other sections) move down to make room.
        for (var row = total - listRows - 1; row >= 0; row--)
            for (var v = 0; v < ValuesPerRow; v++)
                values[FirstRow + (newList + row) * ValuesPerRow + v] = values[FirstRow + (listRows + row) * ValuesPerRow + v];
        for (var row = 0; row < plan.Count; row++)
        {
            var target = FirstRow + row * ValuesPerRow;
            if (plan[row].Source < 0) { WriteRow(values, target, plan[row].Emote, inSearch: false); continue; }
            for (var v = 0; v < ValuesPerRow; v++) values[target + v] = saved[plan[row].Source * ValuesPerRow + v];
        }
        values[CountIndex].UInt = (uint)newTotal;
        *(ushort*)((byte*)agent + 0x34) = (ushort)newList;
        *(ushort*)((byte*)agent + 0x36) = (ushort)(newList + 26);
    }

    private void AddSearchMatches(AtkValue* values, uint count, List<uint> matches)
    {
        if (matches.Count == 0 || count <= CountIndex || values[0].Type != AtkValueType.Int || values[0].Int != 2) return;
        var agent = AgentEmote.Instance();
        if (agent == null) return;
        var first = FirstRow + *(ushort*)((byte*)agent + 0x36) * ValuesPerRow;
        if (first + 10 * ValuesPerRow > count) return;
        var kind = values[first + 3];
        if (kind.Type != AtkValueType.UInt || (kind.UInt & 0xFF) != 6) return;
        var shown = new List<(ushort Order, int Source, uint Emote)>();
        var present = new HashSet<uint>();
        for (var row = 0; row < 10; row++)
        {
            var at = first + row * ValuesPerRow;
            if (!IsRow(values, at)) break;
            present.Add(values[at].UInt);
            shown.Add((OrderOf(values[at].UInt), row, values[at].UInt));
        }
        foreach (var id in matches) if (present.Add(id)) shown.Add((OrderOf(id), -1, id));
        shown.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Emote.CompareTo(b.Emote));
        var saved = new AtkValue[10 * ValuesPerRow];
        for (var i = 0; i < saved.Length; i++) saved[i] = values[first + i];
        var rows = Math.Min(shown.Count, 10);
        var moved = new HashSet<int>();
        for (var row = 0; row < rows; row++) if (shown[row].Source >= 0) moved.Add(shown[row].Source);
        // Values no longer shown are released; those that move keep their contents.
        for (var row = 0; row < rows; row++)
            if (!moved.Contains(row))
                for (var v = 0; v < ValuesPerRow; v++) (values + first + row * ValuesPerRow + v)->Dtor();
        for (var row = 0; row < rows; row++)
        {
            var target = first + row * ValuesPerRow;
            if (shown[row].Source < 0) { WriteRow(values, target, shown[row].Emote, inSearch: true); continue; }
            for (var v = 0; v < ValuesPerRow; v++) values[target + v] = saved[shown[row].Source * ValuesPerRow + v];
        }
    }

    private static bool IsRow(AtkValue* values, int at)
        => values[at].Type == AtkValueType.UInt && values[at + 2].Type == AtkValueType.UInt && values[at].UInt != 0;

    private static int ListRows(AtkValue* values, uint count, int total)
    {
        for (var row = 0; row < total; row++)
        {
            var at = FirstRow + row * ValuesPerRow;
            if (at + ValuesPerRow > count || values[at + 3].Type != AtkValueType.UInt || values[at + 3].UInt > 3) return row;
        }
        return total;
    }

    // Each missing emote joins its category's run, in that run's order (by sort order, or by name when the window
    // sorts by name); categories the window has no run for go at the end.
    private List<(int Source, uint Emote)> Plan(Row[] rows, List<uint> missing)
    {
        var runs = new List<(uint Kind, int Start, int Count)>();
        for (var row = 0; row < rows.Length; row++)
        {
            if (runs.Count > 0 && runs[^1].Kind == rows[row].Kind) { var last = runs[^1]; runs[^1] = (last.Kind, last.Start, last.Count + 1); continue; }
            runs.Add((rows[row].Kind, row, 1));
        }
        var buckets = new Dictionary<uint, List<(uint Emote, string Name, ushort Order)>>();
        var homeless = new List<uint>();
        foreach (var id in missing)
        {
            var kind = KindOf(id);
            if (!runs.Exists(r => r.Kind == kind)) { homeless.Add(id); continue; }
            if (!buckets.TryGetValue(kind, out var bucket)) buckets[kind] = bucket = [];
            bucket.Add((id, NameOf(id), OrderOf(id)));
        }
        var plan = new List<(int Source, uint Emote)>(rows.Length + missing.Count);
        foreach (var run in runs)
        {
            buckets.Remove(run.Kind, out var bucket);
            if (bucket == null || bucket.Count == 0) { for (var r = 0; r < run.Count; r++) plan.Add((run.Start + r, 0u)); continue; }
            var byName = false;
            for (var r = run.Start + 1; r < run.Start + run.Count && !byName; r++) byName = rows[r].Order < rows[r - 1].Order;
            bucket.Sort((a, b) => byName ? string.CompareOrdinal(a.Name, b.Name) : a.Order.CompareTo(b.Order));
            int kept = run.Start, end = run.Start + run.Count, next = 0;
            while (kept < end || next < bucket.Count)
            {
                var takeAdded = kept >= end || next < bucket.Count && (byName
                    ? string.CompareOrdinal(bucket[next].Name, rows[kept].Name) <= 0
                    : bucket[next].Order <= rows[kept].Order);
                if (takeAdded) plan.Add((-1, bucket[next++].Emote)); else plan.Add((kept++, 0u));
            }
        }
        foreach (var id in homeless) plan.Add((-1, id));
        return plan;
    }

    private void WriteRow(AtkValue* values, int at, uint id, bool inSearch)
    {
        EmoteController.EmoteDetails details;
        if (!EmoteController.TryGetEmoteDetails(id, &details) || details.Name.Value == null)
        {
            values[at].Type = AtkValueType.UInt; values[at].UInt = 0;
            return;
        }
        var name = details.Name.Value;
        var trailer = commandText.TryGetValue(id, out var commands) && commands != 0 ? (byte*)commands : name;
        var flags = details.EmoteCategory == 3 ? 0x3FFu : ConditionFlags(&details);
        SetUInt(values, at, id);
        SetUInt(values, at + 1, details.Icon);
        SetUInt(values, at + 2, flags);
        SetUInt(values, at + 3, inSearch ? 6 | (uint)details.EmoteCategory << 8 : details.EmoteCategory);
        SetString(values, at + 4, name);
        SetString(values, at + 5, trailer);
    }

    private static uint ConditionFlags(EmoteController.EmoteDetails* d)
    {
        var flags = 0u;
        if (d->SittingOnGround) flags |= 1u << 0;
        if (d->SittingInChair) flags |= 1u << 1;
        if (d->Mounted) flags |= 1u << 2;
        if (d->Fishing) flags |= 1u << 3;
        if (d->Standing) flags |= 1u << 4;
        if (d->Swimming) flags |= 1u << 5;
        if (d->Diving) flags |= 1u << 6;
        if (d->HoldingTorch) flags |= 1u << 7;
        if (d->WearingFashionAccessory) flags |= 1u << 8;
        if (d->HoldingUmbrella) flags |= 1u << 9;
        return flags;
    }

    private static void SetUInt(AtkValue* values, int at, uint value) { values[at].Type = AtkValueType.UInt; values[at].UInt = value; }
    private static void SetString(AtkValue* values, int at, byte* value) { values[at].Type = AtkValueType.ConstString; values[at].String = value; }

    private static string StringAt(AtkValue* value)
    {
        if (value->Type is not (AtkValueType.String or AtkValueType.ConstString or AtkValueType.ManagedString)) return "";
        var text = value->String.Value;
        return text == null ? "" : Marshal.PtrToStringUTF8((nint)text) ?? "";
    }

    private ushort OrderOf(uint id) => data.GetExcelSheet<Emote>().GetRowOrDefault(id)?.Order ?? ushort.MaxValue;
    private string NameOf(uint id) => data.GetExcelSheet<Emote>().GetRowOrDefault(id)?.Name.ExtractText() ?? "";
    private static uint KindOf(uint id)
    {
        EmoteController.EmoteDetails details;
        return EmoteController.TryGetEmoteDetails(id, &details) ? details.EmoteCategory : 0u;
    }

    private readonly record struct Row(uint Kind, ushort Order, string Name);

    // ── Painting the rows the game dimmed ───────────────────────────────────────────────────────────────────

    private void PaintRows(AtkUnitBase* addon)
    {
        var agent = AgentEmote.Instance();
        if (agent == null) return;
        switch (*((byte*)agent + 0x31))
        {
            case 0:
                if (EmoteHistoryModule.Instance() is var history && history != null)
                {
                    PaintSlots(addon, 20u, history->History);
                    PaintSlots(addon, 25u, history->Favorites);
                }
                break;
            case 4:
                if (Component(addon, 47u, ComponentType.TreeList) is var search && search != null) PaintNamed((AtkComponentList*)search);
                break;
            default:
                if (Component(addon, 4u, ComponentType.List) is var list && list != null) PaintNamed((AtkComponentList*)list);
                break;
        }
    }

    private void PaintNamed(AtkComponentList* list)
    {
        for (var row = 0; row < list->ListLength; row++)
        {
            var renderer = list->GetItemRenderer(row);
            if (renderer == null || renderer->AtkComponentButton.AtkComponentBase.OwnerNode == null) continue;
            if (ShowsLocked(&renderer->AtkComponentButton.AtkComponentBase)) Light(&renderer->AtkComponentButton.AtkComponentBase.OwnerNode->AtkResNode);
        }
    }

    private void PaintSlots(AtkUnitBase* addon, uint nodeId, Span<ushort> slots)
    {
        var component = Component(addon, nodeId, ComponentType.List);
        if (component == null) return;
        var list = (AtkComponentList*)component;
        for (var row = 0; row < Math.Min(list->ListLength, slots.Length); row++)
        {
            var renderer = list->GetItemRenderer(row);
            if (renderer == null || renderer->AtkComponentButton.AtkComponentBase.OwnerNode == null) continue;
            var id = (uint)(slots[row] & 0xFFF);
            if (id != 0 && locked.Contains(id)) Light(&renderer->AtkComponentButton.AtkComponentBase.OwnerNode->AtkResNode);
        }
    }

    private bool ShowsLocked(AtkComponentBase* component)
    {
        var manager = &component->UldManager;
        for (var i = 0; i < manager->NodeListCount; i++)
        {
            var node = manager->NodeList[i];
            if (node != null && node->Type == NodeType.Text && lockedNames.Contains(((AtkTextNode*)node)->NodeText.ToString().Trim())) return true;
        }
        return false;
    }

    private static void Light(AtkResNode* node)
    {
        if (node->MultiplyRed == 100 && node->MultiplyGreen == 100 && node->MultiplyBlue == 100) return;
        node->MultiplyRed = node->MultiplyGreen = node->MultiplyBlue = 100;
    }

    // ── The right-click menu's Execute ──────────────────────────────────────────────────────────────────────

    private void OnContextValues(AddonEvent type, AddonArgs args)
    {
        try
        {
            if (!Active) return;
            switch (args)
            {
                case AddonSetupArgs setup: EnableExecute((AtkValue*)setup.AtkValues, setup.AtkValueCount); break;
                case AddonRefreshArgs refresh: EnableExecute((AtkValue*)refresh.AtkValues, refresh.AtkValueCount); break;
            }
        }
        catch (Exception ex) { log.Error(ex, "[Haven] Emote menu values failed."); }
    }

    private static void EnableExecute(AtkValue* values, uint count)
    {
        if (!FindExecute(out var item)) return;
        ClearExecuteFlag(values, count, item);
        AgentContext.Instance()->CurrentContextMenu->ContextItemDisabledMask &= ~(1u << item);
    }

    private static void EnableExecute(AtkUnitBase* menu)
    {
        if (!FindExecute(out var item)) return;
        ClearExecuteFlag(menu->AtkValues, menu->AtkValuesCount, item);
        if (FirstList(menu) is var list && list != null && item < list->ListLength && list->GetItemDisabledState(item)) list->SetItemDisabledState(item, false);
    }

    // The emote window's Execute entry: the item whose handler is AgentEmote with parameter 0x10001.
    private static bool FindExecute(out int item)
    {
        item = -1;
        var agent = AgentEmote.Instance();
        var context = AgentContext.Instance();
        if (agent == null || context == null || context->CurrentContextMenu == null) return false;
        var menu = context->CurrentContextMenu;
        for (var entry = 0; entry < 26; entry++)
            if (menu->EventHandlers[8 + entry].Value == (AtkModuleInterface.AtkEventInterface*)agent && menu->EventHandlerParams[8 + entry] == 0x10001)
            { item = entry; return true; }
        return false;
    }

    private static void ClearExecuteFlag(AtkValue* values, uint count, int item)
    {
        if (values == null || count <= 8 || values[0].Type != AtkValueType.UInt) return;
        var items = (int)values[0].UInt;
        var flag = 8 + items + item;
        if (item < items && flag < count) values[flag].SetInt(0);
    }

    // ── Addon helpers ───────────────────────────────────────────────────────────────────────────────────────

    private AtkUnitBase* Addon(string name, bool ready = true)
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName(name).Address;
        if (addon == null) return null;
        return !ready || addon->IsVisible && addon->UldManager.LoadedState == AtkLoadState.Loaded ? addon : null;
    }

    private static AtkComponentBase* Component(AtkUnitBase* addon, uint nodeId, ComponentType type)
    {
        var node = addon->GetNodeById(nodeId);
        if (node == null || (ushort)node->Type < 1000) return null;
        var component = ((AtkComponentNode*)node)->Component;
        return component != null && component->GetComponentType() == type ? component : null;
    }

    private static AtkComponentList* FirstList(AtkUnitBase* addon)
    {
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null || (ushort)node->Type < 1000) continue;
            var component = ((AtkComponentNode*)node)->Component;
            if (component != null && component->GetComponentType() == ComponentType.List) return (AtkComponentList*)component;
        }
        return null;
    }

    private static string? SearchText(AtkUnitBase* addon)
    {
        if (addon == null) return null;
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null || (ushort)node->Type < 1000) continue;
            var component = ((AtkComponentNode*)node)->Component;
            if (component != null && component->GetComponentType() == ComponentType.TextInput)
                return ((AtkComponentTextInput*)component)->AtkComponentInputBase.EvaluatedString.ToString().Trim();
        }
        return null;
    }
}
