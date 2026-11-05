using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ManagedFontAtlas;
using Haven.Core;

namespace Haven;

// Outside edit mode, hovering a clickable furnishing outlines its geometry and shows its name and action; a click
// switches it, like vanilla. Anything nearer along the cursor ray (another furnishing) blocks the item behind it.
internal sealed partial class FurnishingEditor
{
    private (Vector3 Origin, Vector3 Direction, object? Furnishings, Guid? Moving, long At, Furnishing? Hovered, float Nearest) pick;
    private void DrawInteractions(House house, bool pointer)
    {
        var npcHit = NpcUnderPointer(pointer, out var npcDistance);
        if (!pointer || ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow) || !TryRay(out var origin, out var direction)) return;
        var matrix = FurnitureCatalogue.HouseMatrix(house);
        Furnishing? hovered = null; var nearest = float.MaxValue;
        // Every model is ray-tested, so while the pointer and camera hold still (most frames) the last answer stands,
        // checked again five times a second for moving parts and switches.
        var now = Environment.TickCount64;
        if (origin == pick.Origin && direction == pick.Direction && ReferenceEquals(house.Furnishings, pick.Furnishings) && housing.MovingId == pick.Moving && now - pick.At < 200)
            (hovered, nearest) = (pick.Hovered, pick.Nearest);
        else
        {
            foreach (var item in house.Furnishings)
            {
                if (item.Id == housing.MovingId) continue;
                var prefab = Catalogue.Prefab(item);
                var on = prefab.Switch == null || housing.SwitchOn(item);
                foreach (var model in prefab.Models)
                {
                    if (!PrefabState.Shown(model.Switch, on) || !prefab.Answers(model)) continue;
                    var hit = ModelHit(model, housing.FurnitureModelTransform(item, model) * FurnitureCatalogue.ItemMatrix(item) * matrix,
                        origin, direction, item.BuildingPart != null, nearest);
                    if (hit is float d && d < nearest) { nearest = d; hovered = item; }
                }
            }
            pick = (origin, direction, house.Furnishings, housing.MovingId, now, hovered, nearest);
        }
        // An NPC in front of every furnishing answers instead: a click makes it the game's target.
        if (npcHit is { } npcNear && npcDistance < nearest)
        {
            if (input.LeftPressed && !OverGameWindow()) Npcs!.Target(npcNear.Slot);
            return;
        }
        // A furnishing with animation sets opens its menu (pose balls), which also offers its switch.
        if (hovered is { Poses: not null } posed && DrawPoseInteraction(house, posed, matrix)) return;
        // A seat whose look is chosen in the inspector is not clicked where one sits.
        if (hovered is not {} target || !housing.CanInteract(target) || Catalogue.Prefab(target).Switch is { InspectorOnly: true }
            || (Catalogue.Prefab(target).PartsOnly ? PartBounds(target) : ItemBounds(target)) is not {} box) return;
        var entry = Catalogue.Get(target);
        var switchOn = housing.SwitchOn(target);
        var center = Vector3.Transform((box.Minimum + box.Maximum) / 2, matrix);
        var inRange = engine.HousingPlayerPosition is {} player && Vector3.Distance(player, center) <= FurnishingInteraction.Range;
        var action = entry.TripleTriad ? inRange ? "Play Triple Triad" : "Move closer"
            : entry.Arcade != null ? inRange ? "Play" : "Move closer"
            : entry.Orchestrion ? FurnishingInteraction.Orchestrion(housing.CanControlMusic, inRange)
            : Catalogue.Prefab(target).Switch == null && Catalogue.Prefab(target).UseTimeline != null ? inRange ? "Use" : "Move closer"
            : FurnishingInteraction.Action(entry.Food, switchOn, Catalogue.Prefab(target).Switch!.DefaultOn, inRange, Catalogue.Prefab(target).Switch!.Verb,
                Catalogue.Prefab(target).Switch!.ClosedOn);
        if (inRange) housing.HighlightedFurnishing = target.Id;
        var top = new Vector3((box.Minimum.X + box.Maximum.X) / 2, box.Maximum.Y, (box.Minimum.Z + box.Maximum.Z) / 2);
        if (gameGui.WorldToScreen(Vector3.Transform(top, matrix), out var anchor))
            DrawNameplate(anchor, housing.NameOf(target), action, inRange);
        if (inRange && input.LeftPressed && !OverGameWindow())
        {
            if (entry.TripleTriad) PlayTriad?.Invoke();
            else if (entry.Arcade != null) PlayArcade?.Invoke(target);
            else if (entry.Orchestrion) ShowPanel(RoomPanel.Music);
            else if (Catalogue.Prefab(target).Switch == null) housing.Run(() => housing.UseFurnishing(target.Id));
            else housing.Run(() => housing.ToggleSwitch(target.Id));
        }
    }

    // NPCs have no game nameplate (characters made on this PC get none), so Haven labels them: the one under the pointer
    // and the targeted one, by name, in the same style as furnishings. The hit box is a body-sized column.
    private (Guid Id, string Name, uint Slot, Vector3 Position, float Height)? NpcUnderPointer(bool pointer, out float distance)
    {
        distance = float.MaxValue;
        if (Npcs == null || !pointer || ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow) || !TryRay(out var origin, out var direction)) return null;
        (Guid, string, uint, Vector3, float)? best = null;
        foreach (var npc in Npcs.Shown)
        {
            if (Npcs.IsPossessed(npc.Id)) continue;
            var hit = PlacementMath.RayBox(origin, direction, new Vector3(-.35f, 0, -.35f), new Vector3(.35f, npc.Height, .35f),
                Matrix4x4.CreateTranslation(npc.Position), exitWhenInside: false);
            if (hit is float d && d < distance) { distance = d; best = npc; }
        }
        return best;
    }

    // Every NPC's name over its head, as the game shows other characters' names, unless the game already draws a
    // nameplate for it (characters made on this PC often get none). Nearby only, like the game's.
    private void DrawNpcNames()
    {
        if (Npcs == null || CinematicMode.Hiding || !housing.IsReady || engine.HousingPlayerPosition is not { } player) return;
        var draw = ImGui.GetBackgroundDrawList();
        foreach (var npc in Npcs.Shown)
        {
            if (Vector3.DistanceSquared(npc.Position, player) > 30 * 30 || Npcs.HasGameNameplate(npc.Slot)) continue;
            if (!gameGui.WorldToScreen(npc.Position + new Vector3(0, npc.Height + .25f, 0), out var anchor)) continue;
            using (NameFont?.Push())
            {
                var size = ImGui.CalcTextSize(npc.Name);
                Edged(draw, new Vector2(anchor.X - size.X / 2, anchor.Y - size.Y), Npcs.Targeted(npc.Slot) ? 0xFFFFE0A0u : 0xFFF0F0F0u, npc.Name);
            }
        }
    }

    /// <summary>Opens a game of Triple Triad (the Triple Triad Board).</summary>
    public Action? PlayTriad { get; set; }
    /// <summary>Starts a game at a Gold Saucer arcade machine.</summary>
    public Action<Furnishing>? PlayArcade { get; set; }

    // The parts that answer (a door's leaves, a machine's play spot), where the label sits and the range is measured from.
    private PlacementBounds? PartBounds(Furnishing item)
    {
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        var prefab = Catalogue.Prefab(item);
        foreach (var model in prefab.Models.Where(prefab.Answers))
        { var box = PlacementBounds.Transform(model.Minimum, model.Maximum, model.Transform * FurnitureCatalogue.ItemMatrix(item)); min = Vector3.Min(min, box.Minimum); max = Vector3.Max(max, box.Maximum); }
        return min.X <= max.X ? new(min, max) : null;
    }

    // Nameplate-style label in the game's own UI font: white name over a softer action line, each with a dark
    // edge instead of a background panel. Fonts are pushed and popped in balanced scopes.
    public IFontHandle? NameFont { get; set; }
    public IFontHandle? ActionFont { get; set; }
    private void DrawNameplate(Vector2 anchor, string name, string action, bool inRange)
    {
        var draw = ImGui.GetBackgroundDrawList();
        Vector2 nameSize, actionSize;
        using (NameFont?.Push()) nameSize = ImGui.CalcTextSize(name);
        using (ActionFont?.Push()) actionSize = ImGui.CalcTextSize(action);
        var bottom = anchor.Y - 12 * HavenUi.S;
        var actionAt = new Vector2(anchor.X - actionSize.X / 2, bottom - actionSize.Y);
        var nameAt = new Vector2(anchor.X - nameSize.X / 2, actionAt.Y - nameSize.Y + 1 * HavenUi.S);
        using (NameFont?.Push()) Edged(draw, nameAt, inRange ? 0xFFFFFFFFu : 0xFFC8C8C8u, name);
        using (ActionFont?.Push()) Edged(draw, actionAt, inRange ? 0xFFFFCD96u : 0xFFA8A8A8u, action);   // light blue, matching the outline
    }
    private static void Edged(ImDrawListPtr draw, Vector2 at, uint color, string text)
    {
        var edge = Math.Max(1f, HavenUi.S);
        for (var x = -1; x <= 1; x++) for (var y = -1; y <= 1; y++)
            if (x != 0 || y != 0) draw.AddText(at + new Vector2(x, y) * edge, 0xD0101010u, text);
        draw.AddText(at, color, text);
    }

    // A click on a game window (chat log, hotbars) in front of a furnishing is not an interaction.
    private static unsafe bool OverGameWindow()
    {
        var module = FFXIVClientStructs.FFXIV.Client.UI.RaptureAtkModule.Instance();
        return module != null && module->AtkCollisionManager.IntersectingAddon != null;
    }
}
