using System.Numerics;
using Dalamud.Bindings.ImGui;
using static Haven.HavenTokens;

namespace Haven;

internal sealed partial class FurnishingEditor
{
    // Labelled X / Y / Z, rotation and scale fields. Each drag previews live and saves one undo step on release.
    private void DrawLiveTransforms(Vector3 position, float yaw, Vector3 scale, bool imported, Action<Vector3, float, Vector3> preview, Action? more = null)
    {
        var gap = Px(Space2); var third = (ImGui.GetContentRegionAvail().X - gap * 2) / 3;
        var id = ImGui.GetID("live-position"); var p = positionEdits.GetValueOrDefault(id, position);
        var x = HavenKit.Drag("##live-x", "X", AxisX, ref p.X, 0.01f, "%.2f", third, -10000, 10000); ImGui.SameLine(0, gap);
        var y = HavenKit.Drag("##live-y", "Y", AxisY, ref p.Y, 0.01f, "%.2f", third, -10000, 10000); ImGui.SameLine(0, gap);
        var z = HavenKit.Drag("##live-z", "Z", AxisZ, ref p.Z, 0.01f, "%.2f", third, -10000, 10000);
        if (x.Active || y.Active || z.Active) positionEdits[id] = p; else positionEdits.Remove(id);
        if (x.Changed || y.Changed || z.Changed) { followCursor = false; housing.Run(() => preview(p, yaw, scale)); }
        FinishLiveDrag(x.Committed || y.Committed || z.Committed);

        var half = (ImGui.GetContentRegionAvail().X - gap) / 2;
        id = ImGui.GetID("live-yaw"); var r = floatEdits.GetValueOrDefault(id, yaw);
        var turn = HavenKit.Drag("##live-yaw", "Rotation", TextSecondary, ref r, 0.5f, "%.1f°", imported ? -1 : half, -3600, 3600);
        if (turn.Active) floatEdits[id] = r; else floatEdits.Remove(id);
        if (turn.Changed) { followCursor = false; housing.Run(() => preview(position, r, scale)); }
        FinishLiveDrag(turn.Committed);

        if (imported)
        {
            HavenKit.SectionTitle("Scale");
            id = ImGui.GetID("live-scale3"); var s = positionEdits.GetValueOrDefault(id, scale);
            var sx = HavenKit.Drag("##live-sx", "X", AxisX, ref s.X, 0.01f, "%.2f×", third, 0.05f, 10); ImGui.SameLine(0, gap);
            var sy = HavenKit.Drag("##live-sy", "Y", AxisY, ref s.Y, 0.01f, "%.2f×", third, 0.05f, 10); ImGui.SameLine(0, gap);
            var sz = HavenKit.Drag("##live-sz", "Z", AxisZ, ref s.Z, 0.01f, "%.2f×", third, 0.05f, 10);
            if (sx.Active || sy.Active || sz.Active) positionEdits[id] = s; else positionEdits.Remove(id);
            if (sx.Changed || sy.Changed || sz.Changed) { followCursor = false; housing.Run(() => preview(position, yaw, s)); }
            FinishLiveDrag(sx.Committed || sy.Committed || sz.Committed);
        }
        else
        {
            ImGui.SameLine(0, gap);
            id = ImGui.GetID("live-scale1"); var s = floatEdits.GetValueOrDefault(id, scale.X);
            var size = HavenKit.Drag("##live-scale", "Scale", TextSecondary, ref s, 0.01f, "%.2f×", half, 0.05f, 10);
            if (size.Active) floatEdits[id] = s; else floatEdits.Remove(id);
            if (size.Changed) { followCursor = false; housing.Run(() => preview(position, yaw, new Vector3(s))); }
            FinishLiveDrag(size.Committed);
        }
        more?.Invoke();
        HavenKit.Caption("Drag a value, or Ctrl-click to type one. Release saves one undo step; Esc cancels.");
    }

    // A furnishing's tilt (Pitch, its front going down) and lean (Roll, to its side), applied before its turn; Level puts
    // both back to 0. A chair or bed tilted can't be sat on, which the first such tilt says (until told not to).
    private bool tiltWarning, tiltWarningShown, tiltWarningHide;
    private void DrawTilt(Haven.Core.Furnishing item)
    {
        var gap = Px(Space2); var half = (ImGui.GetContentRegionAvail().X - gap) / 2;
        var id = ImGui.GetID("live-pitch"); var pitch = floatEdits.GetValueOrDefault(id, item.Pitch);
        var tilt = HavenKit.Drag("##live-pitch", "Tilt", TextSecondary, ref pitch, 0.5f, "%.1f°", half, -180, 180);
        if (tilt.Active) floatEdits[id] = pitch; else floatEdits.Remove(id);
        ImGui.SameLine(0, gap);
        var rid = ImGui.GetID("live-roll"); var roll = floatEdits.GetValueOrDefault(rid, item.Roll);
        var lean = HavenKit.Drag("##live-roll", "Lean", TextSecondary, ref roll, 0.5f, "%.1f°", half, -180, 180);
        if (lean.Active) floatEdits[rid] = roll; else floatEdits.Remove(rid);
        if (tilt.Changed || lean.Changed)
        {
            followCursor = false;
            var tilted = item with { Pitch = Math.Clamp(pitch, -180, 180), Roll = Math.Clamp(roll, -180, 180) };
            housing.Run(() => housing.PreviewFineFurniture(tilted));
        }
        var committed = tilt.Committed || lean.Committed;
        FinishLiveDrag(committed);
        var seats = Catalogue.Prefab(item).Seats.Length > 0;
        if (committed && (pitch != 0 || roll != 0) && seats && housing.WarnTiltedSeats) tiltWarning = true;
        if (item.Tilted)
        {
            if (HavenKit.Button("Level it##live-level", ButtonKind.Ghost)) { var level = item with { Pitch = 0, Roll = 0 }; housing.Run(() => housing.ChangeFurniture(level)); }
            HavenKit.Tooltip("Tilt and Lean back to 0: upright again, turned as it is.");
            if (seats) HavenKit.Caption("Tilted, so it can't be sat or lain on.");
        }
        DrawTiltWarning();
    }

    private void DrawTiltWarning()
    {
        if (!tiltWarning) { tiltWarningShown = false; return; }
        if (!tiltWarningShown) { ImGui.OpenPopup("##tilt-seat-warning"); tiltWarningShown = true; tiltWarningHide = false; }
        if (!HavenKit.BeginDialog("##tilt-seat-warning", "Tilted seats can't be used", 420)) { tiltWarning = tiltWarningShown = false; return; }
        HavenKit.Caption("A chair, bench or bed that's tilted or leaning can't be sat or lain on: the game only seats people level, so the pose would float or sink. Level it again (Tilt and Lean at 0) to use it.");
        ImGui.Dummy(Px(0, Space1));
        HavenKit.Toggle("Don't show this warning again", ref tiltWarningHide);
        if (tiltWarningHide) HavenKit.Caption("You can turn it back on in Settings → Custom content → Furniture.");
        HavenKit.AlignRight(HavenKit.ButtonWidth("OK"));
        if (HavenKit.Button("OK##tilt-seat-warning", ButtonKind.Primary))
        {
            if (tiltWarningHide) housing.Run(() => housing.WarnTiltedSeats = false);
            tiltWarning = tiltWarningShown = false;
            ImGui.CloseCurrentPopup();
        }
        HavenKit.EndDialog();
    }
    private void FinishLiveDrag(bool committed)
    {
        if (!committed) return;
        // Numeric text edits may own Escape in ImGui; read its physical state so
        // deactivation cannot accidentally commit immediately before cancellation.
        var cancel = WorldEditorKeys.Focused && WorldEditorKeys.Read().Escape;
        housing.Run(() => { if (!housing.IsFineEditing) return; if (cancel) housing.CancelPlacement(); else housing.Place(); });
    }
}
