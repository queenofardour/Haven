using System.Numerics;
using Dalamud.Bindings.ImGui;
using Haven.Core;
using static Haven.HavenTokens;

namespace Haven;

// Copies in a row, in a circle or mirrored (FurnishingGroups.Row/Circle/Mirror), and keeping pieces in Your groups.
// An arrangement previews in the room while the inspector shows its fields; it sets down like any move.
internal sealed partial class FurnishingEditor
{
    private enum ArrangeKind { Row, Circle, Mirror }
    private sealed class ArrangeState
    {
        public ArrangeKind Kind;
        public Guid Leader;
        public int Count = 3, Axis, Side = 2;
        public Vector3 Step;
        public float Radius = 1.5f;
        public bool Keep = true;
        public PlacementBounds Box;
    }
    private ArrangeState? arranging;
    private string groupName = "";

    // The Copies section under a piece's or a selection's inspector.
    private void DrawCopies(Guid[] ids, Guid leader)
    {
        if (!editing || !housing.CanFurnish || housing.IsPlacing || housing.ActiveHouse is not {} house) return;
        ImGui.Dummy(Px(0, Space1));
        HavenKit.SectionTitle("Copies", ids.Length > 1 ? "of these pieces" : "of this piece");
        var third = (ImGui.GetContentRegionAvail().X - Px(Space2) * 2) / 3;
        if (HavenKit.Button("In a row##copies-row", size: new(third, 0))) BeginArrange(ArrangeKind.Row, ids, leader);
        HavenKit.Tooltip("Copies side by side, a set distance apart: a row of pillars or lanterns");
        ImGui.SameLine(0, Px(Space2));
        if (HavenKit.Button("In a circle##copies-circle", size: new(third, 0))) BeginArrange(ArrangeKind.Circle, ids, leader);
        HavenKit.Tooltip("Copies round a centre in front of it, each turned the same way toward it: chairs round a table");
        ImGui.SameLine(0, Px(Space2));
        if (HavenKit.Button("Mirrored##copies-mirror", size: new(third, 0))) BeginArrange(ArrangeKind.Mirror, ids, leader);
        HavenKit.Tooltip("A mirror image across X or Z: the other wing of a room");
        if (HavenKit.Button("Keep in Your groups…##copies-keep", ButtonKind.Ghost, new(-1, 0)))
        { groupName = ids.Length == 1 ? housing.NameOf(house.Furnishings.First(f => f.Id == leader)) : ""; ImGui.OpenPopup("##keep-group"); }
        HavenKit.Tooltip("Keep them in your own library, to drop into any home from Furnish → Yours");
        if (HavenKit.BeginDialog("##keep-group", "Keep in Your groups"))
        {
            var submit = HavenKit.SubmitField("##keep-group-name", "Name", ref groupName, "Dressed table, bar corner…", maxLength: SavedGroups.MaxName);
            if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere(-1);
            HavenKit.Caption($"{(ids.Length == 1 ? "This piece" : $"These {ids.Length} pieces")}, with their dyes, pictures and pose balls, as they stand. Your groups are on this PC, for all your characters.");
            var valid = !string.IsNullOrWhiteSpace(groupName);
            HavenKit.AlignRight(HavenKit.ButtonWidth("Cancel") + HavenKit.ButtonWidth("Keep") + Px(Space2));
            if (HavenKit.Button("Cancel##keep-group", ButtonKind.Ghost)) ImGui.CloseCurrentPopup();
            ImGui.SameLine(0, Px(Space2)); ImGui.BeginDisabled(!valid);
            if ((HavenKit.Button("Keep##keep-group", ButtonKind.Primary) || submit) && valid)
            { var (pieces, name) = (ids, groupName.Trim()); housing.Run(() => housing.SaveGroup(pieces, name)); ImGui.CloseCurrentPopup(); }
            ImGui.EndDisabled();
            HavenKit.EndDialog();
        }
    }

    private void BeginArrange(ArrangeKind kind, Guid[] ids, Guid leader)
    {
        if (housing.ActiveHouse is not {} house) return;
        var pieces = house.Furnishings.Where(f => ids.Contains(f.Id)).ToArray();
        var box = FurnishingGroups.Around(pieces, Catalogue.Bounds)
            ?? new PlacementBounds(pieces.Aggregate(Vector3.Zero, (s, f) => s + f.Translation) / pieces.Length - Vector3.One / 2, pieces.Aggregate(Vector3.Zero, (s, f) => s + f.Translation) / pieces.Length + Vector3.One / 2);
        var size = box.Maximum - box.Minimum;
        var state = new ArrangeState
        {
            Kind = kind, Leader = leader, Box = box,
            Count = kind == ArrangeKind.Circle ? 4 : 3,
            Step = new(MathF.Round(MathF.Max(size.X, .1f), 2), 0, 0),
            Radius = MathF.Round(MathF.Max(1, MathF.Max(size.X, size.Z)), 1),
        };
        arranging = state;
        followCursor = false; axisDrag = null; movementInput.Reset();
        housing.Run(() => housing.BeginArrangement(ids, leader, Make(state)));
    }

    private static Func<IReadOnlyList<Furnishing>, IReadOnlyList<Furnishing>> Make(ArrangeState s)
    {
        var (count, step, radius, axis, keep, leader) = (s.Count, s.Step, s.Radius, s.Axis, s.Keep, s.Leader);
        var plane = s.Side switch { 0 => s.Box.Minimum[axis], 1 => (s.Box.Minimum[axis] + s.Box.Maximum[axis]) / 2, _ => s.Box.Maximum[axis] };
        return s.Kind switch
        {
            ArrangeKind.Row => pieces => FurnishingGroups.Row(pieces, count, step),
            ArrangeKind.Circle => pieces => FurnishingGroups.Circle(pieces, pieces.First(f => f.Id == leader), count, radius),
            _ => pieces => FurnishingGroups.Mirror(pieces, axis, plane, keep),
        };
    }

    // The inspector while an arrangement shows: its fields, then Set down / Cancel.
    private void DrawArrangement(ArrangeState state)
    {
        var title = state.Kind switch { ArrangeKind.Row => "Copies in a row", ArrangeKind.Circle => "Copies in a circle", _ => "Mirrored copy" };
        HavenKit.SectionTitle(title, $"{housing.ArrangedCount} pieces in all");
        var changed = false;
        var width = ImGui.GetContentRegionAvail().X; var gap = Px(Space2); var third = (width - gap * 2) / 3;
        if (state.Kind is ArrangeKind.Row or ArrangeKind.Circle)
        {
            float count = state.Count;
            // No more sets than the home has room for (MaxObjects): the preview would show them, and setting down fail.
            var most = Math.Min(FurnishingGroups.MaxCopies, housing.ArrangeRoom);
            if (state.Count > most) { state.Count = most; changed = true; }
            var drag = HavenKit.Drag("##arrange-count", "Sets", TextSecondary, ref count, .05f, "%.0f", -1, 2, most);
            if (drag.Changed && (int)MathF.Round(count) != state.Count) { state.Count = Math.Clamp((int)MathF.Round(count), 2, most); changed = true; }
            HavenKit.Caption(state.Count == 2 ? "The original and one copy." : $"The original and {state.Count - 1} copies.");
        }
        if (state.Kind == ArrangeKind.Row)
        {
            HavenKit.Caption("Each copy this far from the last (metres)");
            var s = state.Step;
            var x = HavenKit.Drag("##arrange-x", "X", AxisX, ref s.X, .01f, "%.2f", third, -100, 100); ImGui.SameLine(0, gap);
            var y = HavenKit.Drag("##arrange-y", "Y", AxisY, ref s.Y, .01f, "%.2f", third, -100, 100); ImGui.SameLine(0, gap);
            var z = HavenKit.Drag("##arrange-z", "Z", AxisZ, ref s.Z, .01f, "%.2f", third, -100, 100);
            if (x.Changed || y.Changed || z.Changed) { state.Step = s; changed = true; }
            var size = state.Box.Maximum - state.Box.Minimum;
            if (HavenKit.Button("Side by side X##arrange-sx", size: new(third, 0))) { state.Step = new(MathF.Round(size.X, 2), 0, 0); changed = true; }
            ImGui.SameLine(0, gap);
            if (HavenKit.Button("Side by side Z##arrange-sz", size: new(third, 0))) { state.Step = new(0, 0, MathF.Round(size.Z, 2)); changed = true; }
            ImGui.SameLine(0, gap);
            if (HavenKit.Button("Stacked##arrange-sy", size: new(third, 0))) { state.Step = new(0, MathF.Round(size.Y, 2), 0); changed = true; }
        }
        else if (state.Kind == ArrangeKind.Circle)
        {
            var r = state.Radius;
            if (HavenKit.Drag("##arrange-radius", "Radius", TextSecondary, ref r, .01f, "%.2f m", -1, .1f, 50).Changed) { state.Radius = r; changed = true; }
            HavenKit.Caption("The centre is this far in front of the piece (the way it faces), so a chair facing its table circles the table.");
        }
        else
        {
            var axis = state.Axis == 0 ? 0 : 1;
            if (HavenKit.Segmented("arrange-axis", ["Across X", "Across Z"], ref axis)) { state.Axis = axis == 0 ? 0 : 2; changed = true; }
            var side = state.Side;
            var letter = state.Axis == 0 ? "X" : "Z";
            if (HavenKit.Segmented("arrange-side", [$"−{letter} side", "In place", $"+{letter} side"], ref side)) { state.Side = side; changed = true; }
            var keep = state.Keep;
            if (HavenKit.Toggle("Keep the originals##arrange-keep", ref keep)) { state.Keep = keep; changed = true; }
            HavenKit.Caption((state.Keep ? "A mirrored copy is added." : "The pieces themselves are flipped.")
                + " Pieces keep their own shape: one that isn't the same on both sides isn't flipped itself.");
        }
        if (changed) housing.Run(() => housing.UpdateArrangement(Make(state)));
        ImGui.Dummy(Px(0, Space1));
        var half = (width - gap) / 2;
        if (HavenKit.Button("Set down##arrange-done", ButtonKind.Primary, new(half, 0))) { axisDrag = null; housing.Run(housing.KeepPlacement); }
        ImGui.SameLine(0, gap);
        if (HavenKit.Button("Cancel##arrange-cancel", ButtonKind.Ghost, new(half, 0))) { axisDrag = null; housing.Run(housing.CancelPlacement); }
        HavenKit.Caption("Drag a handle or use the arrow keys to move them all. Click, right-click or Done sets them down; Escape cancels.");
    }
}
