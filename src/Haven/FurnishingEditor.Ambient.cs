using System.Numerics;
using Dalamud.Bindings.ImGui;
using Haven.Core;
using static Haven.HavenTokens;

namespace Haven;

// The world's effects and ambient sounds as pieces. They have no surface to click, so while
// decorating each shows a marker where it is (a sparkle for an effect, a note for a sound), which picking also answers
// to (MarkerHit), and the piece being placed shows one at the pointer.
internal sealed partial class FurnishingEditor
{
    private static bool IsAmbient(Furnishing item) => item.BuildingPart is { } path && BuildingParts.IsAmbient(path);

    /// <summary>Where an ambient piece is in the world.</summary>
    private static Vector3 AmbientPosition(Furnishing item, House house) => Vector3.Transform(Vector3.Zero, FurnitureCatalogue.ItemMatrix(item) * FurnitureCatalogue.HouseMatrix(house));

    // A marker's radius on screen, and how far past it a click still counts.
    private float MarkerRadius => Px(13);

    /// <summary>
    /// The ambient piece whose marker is under the pointer (the nearest to it on screen), or null. Markers are drawn
    /// over everything, so they're chosen first, on screen: one set inside a fireplace is still clicked, and the
    /// furniture around a marker keeps its own clicks.
    /// </summary>
    private Guid? MarkerAt(House house)
    {
        if (!editing || CinematicMode.Hiding) return null;
        var mouse = ImGui.GetMousePos(); var reach = MarkerRadius + Px(3);
        Guid? best = null; var nearest = float.MaxValue;
        void Consider(Furnishing item)
        {
            if (!IsAmbient(item) || item.Id == housing.MovingId || housing.MovesWithPlacement(item.Id)) return;
            if (!gameGui.WorldToScreen(AmbientPosition(item, house), out var screen)) return;
            var d = Vector2.Distance(screen, mouse);
            if (d <= reach && d < nearest) { nearest = d; best = item.Id; }
        }
        foreach (var item in house.Furnishings) Consider(item);
        // A guest's proposed effect or sound (the owner chooses it to approve or reject it).
        foreach (var staged in housing.StagedFurniture) Consider(staged.Item);
        return best;
    }

    /// <summary>An effect's or sound's tooltip line: how big an effect is and where the game has each.</summary>
    private string? AmbientNote(string path)
    {
        if (housing.Ambient.Find(path) is not { } entry) return null;
        var where = entry.Zone.Length > 0 ? "From " + entry.Zone : "";
        if (entry.Kind == Haven.Core.World.AmbientKind.Sound)
            return (entry.Repeat == Haven.Core.World.AmbientRepeat.Cycle ? "Varied sounds with pauses between" : "Plays continuously") + (where.Length > 0 ? " · " + where : "");
        var size = entry.Size <= 0 ? "" : entry.Size < 1 ? "Under a metre across" : $"About {entry.Size:0} m across at full size";
        var line = string.Join(" · ", new[] { size, where }.Where(s => s.Length > 0));
        return entry.Guess == true ? line + "\nIts name is a guess: place it to see what it is" : line;
    }

    // The inspector's word on an effect's size: how big it is at its scale here, and at full size when that differs.
    private string EffectSizeLine(Furnishing item)
    {
        if (item.BuildingPart is not { } path || housing.Ambient.Find(path) is not { Size: > 0 } entry) return "";
        var here = entry.Size * item.Scale;
        string Metres(float m) => m < 1 ? $"{m * 100:0} cm" : $"{m:0.#} m";
        return Math.Abs(item.Scale - 1) < .005f ? $" About {Metres(here)} across." : $" About {Metres(here)} across at this scale ({Metres(entry.Size)} at full size).";
    }

    private void DrawAmbientMarkers(House house)
    {
        if (!editing || CinematicMode.Hiding || !housing.IsReady) return;
        var draw = ImGui.GetBackgroundDrawList();
        var moving = housing.MovingId; var hovered = MarkerAt(house);
        foreach (var item in house.Furnishings)
            if (IsAmbient(item) && item.Id != moving && !housing.MovesWithPlacement(item.Id))
                Marker(draw, item, AmbientPosition(item, house), selection.Contains(item.Id) || selected == item.Id, false, item.Id == hovered);
        // A guest's proposed piece, or an existing one's proposed place (beside where it stands now).
        foreach (var staged in housing.StagedFurniture)
            if (IsAmbient(staged.Item) && !staged.Deleted) Marker(draw, staged.Item, AmbientPosition(staged.Item, house), false, true, staged.Item.Id == hovered);
        // A group's, copies' or saved group's effects and sounds, where they'd go.
        foreach (var follower in housing.FollowerPreviews)
            if (IsAmbient(follower)) Marker(draw, follower, AmbientPosition(follower, house), true, true, false);
        if (housing.Placement is { } placing && IsAmbient(placing)) Marker(draw, placing, AmbientPosition(placing, house), true, true, false);
    }

    private void Marker(ImDrawListPtr draw, Furnishing item, Vector3 at, bool chosen, bool placing, bool hovered)
    {
        if (!gameGui.WorldToScreen(at, out var screen)) return;
        var sound = Haven.Core.World.WorldAmbient.IsSoundPath(item.BuildingPart!);
        var radius = MarkerRadius * (hovered ? 1.12f : 1);
        var ring = chosen ? Accent : placing ? Warning : hovered ? new Vector4(.75f, .87f, 1, 1) : new Vector4(1, 1, 1, .85f);
        draw.AddCircleFilled(screen, radius, HavenUi.U(new Vector4(.08f, .09f, .11f, placing ? .55f : .75f)), 24);
        draw.AddCircle(screen, radius, HavenUi.U(ring), 24, Px(chosen ? 2.2f : 1.4f));
        HavenUi.DrawIcon(draw, sound ? HavenIcon.Music : HavenIcon.Sparkle, screen, Px(15), ring);
        // A sound's piece silenced (volume Off) shows dimmed.
        if (sound && item.SoundVolume == 0) draw.AddLine(screen + Px(-8, 8), screen + Px(8, -8), HavenUi.U(ring), Px(1.6f));
    }
}
