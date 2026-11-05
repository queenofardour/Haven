using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Haven;

/// <summary>
/// The target bars' name for an NPC with a level. The bar holds the level ("Lv 100", in level glyphs) and
/// the name in two text nodes, and for a made character the name starts right where the level ends ("Lv 100Maid
/// Bnuuy"). A gap is put back in front of the name, as players have. Where a bar holds both in one node, the gap goes
/// after the level's last digit. The level shows only here; the nameplate overhead leaves it out (NpcNameplates).
/// </summary>
internal sealed unsafe class NpcTargetBars : IDisposable
{
    private static readonly string[] Bars = ["_TargetInfoMainTarget", "_TargetInfo"];
    private const string FocusBar = "_FocusTargetInfo";
    private readonly IAddonLifecycle lifecycle;
    private readonly Func<int, bool> isNpc;
    private readonly IPluginLog log;
    private bool logged;

    public NpcTargetBars(IAddonLifecycle lifecycle, Func<int, bool> isNpc, IPluginLog log)
    {
        this.lifecycle = lifecycle;
        this.isNpc = isNpc;
        this.log = log;
        lifecycle.RegisterListener(AddonEvent.PreDraw, Bars, OnTarget);
        lifecycle.RegisterListener(AddonEvent.PreDraw, FocusBar, OnFocus);
    }

    private void OnTarget(AddonEvent type, AddonArgs args) => Space(args, false);
    private void OnFocus(AddonEvent type, AddonArgs args) => Space(args, true);

    private void Space(AddonArgs args, bool focus)
    {
        var targets = TargetSystem.Instance();
        if (targets == null) return;
        var target = focus ? targets->FocusTarget : targets->GetTargetObject();
        if (target == null || !isNpc(target->ObjectIndex)) return;
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null) return;
        List<string>? seen = logged ? null : [];
        var name = System.Text.Encoding.UTF8.GetBytes(target->NameString);
        var levelShown = false;
        AtkTextNode* nameNode = null;
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null || node->Type != NodeType.Text) continue;
            var text = (AtkTextNode*)node;
            var bytes = text->NodeText.AsSpan();
            if (seen != null && bytes.Length > 0) seen.Add($"{node->NodeId}: {Convert.ToHexString(bytes[..Math.Min(bytes.Length, 48)])}");
            if (EndsWithLevel(bytes)) levelShown = node->IsVisible();
            else if (name.Length > 0 && bytes.SequenceEqual(name)) nameNode = text;
            else if (Spaced(bytes) is { } spaced) text->SetText(spaced);
        }
        if (levelShown && nameNode != null) nameNode->SetText([.. Gap, .. name]);
        // Once, what the bar holds for an NPC, in case the level and name sit in separate nodes after all.
        if (seen is { Count: > 0 }) { logged = true; log.Information($"[NPC] target bar text: {string.Join("; ", seen)}"); }
    }

    private static readonly byte[] Gap = [0x20, 0x20];

    // Only the level, ending in a digit glyph (U+E060..U+E069).
    internal static bool EndsWithLevel(ReadOnlySpan<byte> text) =>
        text.Length >= 3 && text[^3] == 0xEE && text[^2] == 0x81 && text[^1] is >= 0xA0 and <= 0xA9;

    // The text with a gap after the level's last digit glyph (U+E060..U+E069, UTF-8 EE 81 A0..A9) when the name follows
    // it directly; null when there's nothing to change.
    internal static byte[]? Spaced(ReadOnlySpan<byte> text)
    {
        var at = -1;
        for (var i = 0; i + 2 < text.Length; i++)
            if (text[i] == 0xEE && text[i + 1] == 0x81 && text[i + 2] is >= 0xA0 and <= 0xA9) at = i + 3;
        if (at < 0 || at >= text.Length || text[at] is 0x20 or 0x00) return null;
        return [.. text[..at], 0x20, 0x20, .. text[at..]];
    }

    public void Dispose()
    {
        lifecycle.UnregisterListener(AddonEvent.PreDraw, Bars, OnTarget);
        lifecycle.UnregisterListener(AddonEvent.PreDraw, FocusBar, OnFocus);
    }
}
