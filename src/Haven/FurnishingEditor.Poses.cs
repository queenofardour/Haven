using System.Numerics;
using Dalamud.Bindings.ImGui;
using Haven.Core;
using Haven.Core.Animation;
using static Haven.HavenTokens;

namespace Haven;

// Pose balls in the room: the balls of every open set, drawn as glowing orbs where a player will
// stand, a click on one to take it, the menu a furnishing with animation sets opens, and the HUD for the player's own
// ball (move, turn, scale, keep the fit, stand up). Balls someone holds are hidden.
internal sealed partial class FurnishingEditor
{
    private (Guid Furnishing, Vector2 At)? poseMenu;
    private UiBounds poseMenuBounds, poseHudBounds;
    private bool poseHudCollapsed;
    // The HUD's handles on the player's own character: X right, Y up, Z forward in the ball's own space.
    private AxisDrag? poseAxisDrag;
    private readonly AxisHandle[] poseHandles = new AxisHandle[3];
    private static readonly (Vector3 Axis, string Name)[] PoseAxes = [(Vector3.UnitX, "Right"), (Vector3.UnitY, "Up"), (Vector3.UnitZ, "Forward")];
    // Whom the HUD adjusts: the player (0), a partner on the set, or everyone together (Everyone).
    private ulong poseTarget;
    private const ulong Everyone = ulong.MaxValue;
    // A handle drag moves a third of the pointer's travel along the axis, at most 3 cm a frame (Shift: a tenth of that).
    private const float HandleRate = .35f, HandleStep = .03f;

    // Every frame in a ready home: the balls, a click on a free one, the furnishing menu, the HUD and its handles.
    // Returns whether the pointer is busy with a ball or a handle (the furnishing behind it isn't clicked).
    private bool DrawPoses(House house, bool pointer, bool focused)
    {
        var overHandle = DrawPoseHandles(pointer, focused);
        var balls = housing.PoseBalls();
        var hovered = DrawPoseBalls(balls, pointer && !editing && poseMenu == null && !overHandle);
        if (hovered is { } ball && input.LeftPressed && !OverGameWindow())
            housing.Run(() => housing.TakePoseBall(ball.Furnishing, ball.Set.Id, ball.Role));
        DrawPoseMenu(house);
        DrawPoseHud();
        return hovered != null || overHandle;
    }

    /// <summary>
    /// While the player is on a ball, the movement keys (and jump, turn and strafe) are held back from the game, so the
    /// emote isn't ended by walking: they leave only with Stand up. Typing in the chat box is never touched.
    /// </summary>
    private static readonly Dalamud.Game.ClientState.Keys.VirtualKey[] HeldOnBall =
    [
        Dalamud.Game.ClientState.Keys.VirtualKey.W, Dalamud.Game.ClientState.Keys.VirtualKey.A, Dalamud.Game.ClientState.Keys.VirtualKey.S, Dalamud.Game.ClientState.Keys.VirtualKey.D,
        Dalamud.Game.ClientState.Keys.VirtualKey.Q, Dalamud.Game.ClientState.Keys.VirtualKey.E, Dalamud.Game.ClientState.Keys.VirtualKey.SPACE,
        Dalamud.Game.ClientState.Keys.VirtualKey.LEFT, Dalamud.Game.ClientState.Keys.VirtualKey.RIGHT, Dalamud.Game.ClientState.Keys.VirtualKey.UP, Dalamud.Game.ClientState.Keys.VirtualKey.DOWN,
    ];
    private void HoldPoseKeys()
    {
        if (housing.Bound is not { Playing: true } && !housing.ArcadeHolding && !housing.NpcHolding && !housing.CameraHolding || !WorldEditorKeys.Focused || GameTextInput()) return;
        foreach (var key in HeldOnBall) if (keyState[key]) keyState[key] = false;
    }

    // The X/Y/Z handles at the player's feet, in the ball's own directions; dragging one moves the player along it.
    private bool DrawPoseHandles(bool pointer, bool focused)
    {
        Array.Clear(poseHandles);
        var who = PoseTargetCharacter();
        if (housing.SeatOf(housing.PoseSubject) is not { Playing: true } || poseHudCollapsed || housing.BallFrameOf(who) is not { } frame || housing.FitOf(who) is not { } fit)
        { poseAxisDrag = null; return false; }
        var placed = fit.Move;
        var start3 = Vector3.Transform(placed, frame);
        if (gameGui.WorldToScreen(start3, out var start))
            for (var i = 0; i < PoseAxes.Length; i++)
            {
                if (!gameGui.WorldToScreen(Vector3.Transform(placed + PoseAxes[i].Axis, frame), out var unit)) continue;
                var pixels = Vector2.Distance(start, unit);
                if (!float.IsFinite(pixels) || pixels < 2) continue;
                var length = Math.Clamp(80 * HavenUi.S / pixels, .1f, 10);
                if (!gameGui.WorldToScreen(Vector3.Transform(placed + PoseAxes[i].Axis * length, frame), out var end)) continue;
                var screenAxis = end - start;
                if (screenAxis.LengthSquared() < 28 * 28 * HavenUi.S * HavenUi.S) continue;
                poseHandles[i] = new(i, start + Vector2.Normalize(screenAxis) * 16 * HavenUi.S, end, true);
            }
        var hovered = pointer && focused ? PlacementGizmoUi.HitTest(poseHandles, ImGui.GetMousePos(), HavenUi.S) : -1;
        var io = ImGui.GetIO();
        var buttons = focused ? WorldEditorKeys.Read() : default;
        if (poseAxisDrag == null && hovered >= 0 && input.LeftPressed && TryRay(out var origin, out var direction))
            poseAxisDrag = AxisDrag.Begin(hovered, placed, frame, origin, direction);
        if (poseAxisDrag is { } drag)
        {
            if (!buttons.Left) poseAxisDrag = null;
            else if (TryRay(out var o, out var d) && drag.Update(o, d, 0, buttons.Shift, HandleRate, HandleStep) is { } move)
                ApplyPoseFit(fit with { Move = move }, ImGui.GetIO().KeyCtrl);
        }
        // Over a handle (or dragging one) the mouse is Haven's, so the game neither turns the camera nor clicks through.
        if (hovered >= 0 || poseAxisDrag != null)
        {
            io.WantCaptureMouse = true; ImGui.SetNextFrameWantCaptureMouse(true);
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
        }
        PlacementGizmoUi.Draw(poseHandles, hovered, poseAxisDrag?.Axis ?? -1);
        return hovered >= 0 || poseAxisDrag != null;
    }

    private PoseBallView? DrawPoseBalls(List<PoseBallView> balls, bool pointer)
    {
        if (balls.Count == 0) return null;
        var draw = ImGui.GetBackgroundDrawList();
        var mouse = ImGui.GetMousePos();
        var player = PoseActorPosition();
        var pulse = 1 + .06f * MathF.Sin((float)ImGui.GetTime() * 3);
        PoseBallView? hovered = null; var nearest = float.MaxValue;
        // The one taking balls (the player, or the NPC they possess) already has one.
        var mine = housing.SeatOf(housing.PossessedNpc?.Invoke() is { } npc ? PoseNpcs.Key(npc) : engine.HousingCharacterId) != null;
        // Where each shown ball is on screen. A taken ball is hidden; the player on a ball sees the rest of their
        // set's free balls, fainter.
        List<(PoseBallView Ball, Vector2 Screen, float Radius)> shown = [];
        foreach (var ball in balls)
        {
            if (ball.Holder != 0 && !ball.Preview) continue;
            var center = ball.Position + new Vector3(0, .45f, 0);
            if (!gameGui.WorldToScreen(center, out var screen) || !gameGui.WorldToScreen(center + new Vector3(0, .11f, 0), out var top)) continue;
            shown.Add((ball, screen, Math.Clamp(Vector2.Distance(screen, top), 3, 60) * pulse));
        }
        // Balls placed close together (a pair fitted into one pose) would cover each other: each group that overlaps
        // on screen is fanned out side by side around its middle, each labelled, with a line back to where it stands.
        var drawn = shown.Select(s => s.Screen).ToArray();
        var spread = new bool[shown.Count];
        var grouped = new bool[shown.Count];
        for (var i = 0; i < shown.Count; i++)
        {
            if (grouped[i]) continue;
            List<int> group = [i];
            for (var k = 0; k < group.Count; k++)
                for (var j = 0; j < shown.Count; j++)
                    if (!grouped[j] && !group.Contains(j) && Vector2.Distance(shown[group[k]].Screen, shown[j].Screen) < (shown[group[k]].Radius + shown[j].Radius) * 1.15f)
                        group.Add(j);
            foreach (var j in group) grouped[j] = true;
            if (group.Count < 2) continue;
            var middle = group.Aggregate(Vector2.Zero, (sum, j) => sum + shown[j].Screen) / group.Count;
            var step = Math.Max(group.Max(j => shown[j].Radius) * 2.6f, 26 * HavenUi.S);
            // Left to right in the order the balls appear on screen, so the fan keeps their arrangement.
            var order = group.OrderBy(j => shown[j].Screen.X).ToArray();
            for (var n = 0; n < order.Length; n++)
            {
                drawn[order[n]] = middle + new Vector2((n - (order.Length - 1) / 2f) * step, 0);
                spread[order[n]] = true;
            }
        }
        for (var i = 0; i < shown.Count; i++)
        {
            var (ball, screen, radius) = shown[i];
            var at = drawn[i];
            var faint = mine && !ball.Preview;
            var reach = player is { } p && Vector3.Distance(p, ball.Position) <= 8;
            var over = pointer && !faint && reach && Vector2.Distance(mouse, at) <= Math.Max(radius * 1.4f, 10);
            if (over && Vector2.Distance(mouse, at) < nearest) { nearest = Vector2.Distance(mouse, at); hovered = ball; }
            if (spread[i])
            {
                var line = Rgb(ball.Color, faint ? .3f : .6f);
                draw.AddLine(at, screen, line, 1.5f);
                draw.AddCircleFilled(screen, 3, line);
            }
            Orb(draw, at, radius, ball.Color, faint ? .4f : 1, over);
            if (ball.Preview)
            {
                // The way a player on it faces, along the floor.
                var forward = new Vector3(MathF.Sin(ball.Yaw), 0, MathF.Cos(ball.Yaw));
                if (gameGui.WorldToScreen(ball.Position, out var foot) && gameGui.WorldToScreen(ball.Position + forward * .35f, out var tip))
                {
                    var color = Rgb(ball.Color, .9f);
                    draw.AddCircle(foot, Math.Max(3, radius * .6f), color, 0, 1.5f);
                    draw.AddLine(foot, tip, color, 2);
                    draw.AddCircleFilled(tip, 3, color);
                }
                Label(draw, at + new Vector2(0, radius + 4), (ball.Holder != 0 ? housing.PoseName(ball.Holder) + " · " : "") + RoleLabel(ball.Set, ball.Role), "");
            }
            else if (spread[i] && !over) Label(draw, at + new Vector2(0, radius + 4), RoleLabel(ball.Set, ball.Role), "");
        }
        if (hovered is { } chosen && shown.FindIndex(s => s.Ball.Equals(chosen)) is var index and >= 0)
            Label(draw, drawn[index] + new Vector2(0, shown[index].Radius + 4), RoleLabel(chosen.Set, chosen.Role), chosen.Set.Name + " · click to take this place");
        return hovered;
    }

    private static string RoleLabel(PoseSet set, int role) => set.Roles[role].Name.Length > 0 ? set.Roles[role].Name : $"Place {role + 1}";
    private static uint Rgb(uint rgb, float alpha) => HavenUi.U(new Vector4(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, alpha));
    private static uint Shade(uint rgb, float factor, float alpha) =>
        HavenUi.U(new Vector4(Math.Min(1, ((rgb >> 16) & 255) / 255f * factor), Math.Min(1, ((rgb >> 8) & 255) / 255f * factor), Math.Min(1, (rgb & 255) / 255f * factor), alpha));

    // A glowing ball: a soft halo, a darker body, a lit face and a highlight, so it reads as a sphere.
    private static void Orb(ImDrawListPtr draw, Vector2 c, float r, uint rgb, float alpha, bool hovered)
    {
        for (var i = 3; i >= 1; i--) draw.AddCircleFilled(c, r * (1 + .32f * i), Rgb(rgb, (hovered ? .1f : .06f) * alpha), 32);
        draw.AddCircleFilled(c, r, Shade(rgb, .62f, .95f * alpha), 32);
        draw.AddCircleFilled(c - new Vector2(r * .12f, r * .14f), r * .8f, Shade(rgb, 1f, .95f * alpha), 32);
        draw.AddCircleFilled(c - new Vector2(r * .24f, r * .28f), r * .48f, Shade(rgb, 1.25f, .9f * alpha), 32);
        draw.AddCircleFilled(c - new Vector2(r * .36f, r * .4f), r * .2f, HavenUi.U(new Vector4(1, 1, 1, .75f * alpha)), 16);
        draw.AddCircle(c, r, HavenUi.U(new Vector4(1, 1, 1, (hovered ? .8f : .3f) * alpha)), 32, hovered ? 2.5f : 1.2f);
    }

    private void Label(ImDrawListPtr draw, Vector2 topCenter, string name, string action)
    {
        Vector2 nameSize, actionSize = default;
        using (NameFont?.Push()) nameSize = ImGui.CalcTextSize(name);
        if (action.Length > 0) using (ActionFont?.Push()) actionSize = ImGui.CalcTextSize(action);
        using (NameFont?.Push()) Edged(draw, topCenter - new Vector2(nameSize.X / 2, 0), 0xFFFFFFFFu, name);
        if (action.Length > 0) using (ActionFont?.Push()) Edged(draw, topCenter + new Vector2(-actionSize.X / 2, nameSize.Y), 0xFFFFCD96u, action);
    }

    // A furnishing with animation sets: hovering names it, a click opens its menu (its switch too, when it has one).
    private bool DrawPoseInteraction(House house, Furnishing target, Matrix4x4 matrix)
    {
        if (target.Poses == null || ItemBounds(target) is not { } box) return false;
        var center = Vector3.Transform((box.Minimum + box.Maximum) / 2, matrix);
        var inRange = PoseActorPosition() is { } player && Vector3.Distance(player, center) <= FurnishingInteraction.Range + 2;
        if (inRange) housing.HighlightedFurnishing = target.Id;
        var top = new Vector3((box.Minimum.X + box.Maximum.X) / 2, box.Maximum.Y, (box.Minimum.Z + box.Maximum.Z) / 2);
        if (gameGui.WorldToScreen(Vector3.Transform(top, matrix), out var anchor))
            DrawNameplate(anchor, housing.NameOf(target), inRange ? target.Poses.Length == 1 ? target.Poses[0].Name : "Animations" : "Move closer", inRange);
        if (inRange && input.LeftPressed && !OverGameWindow()) { poseMenu = (target.Id, ImGui.GetMousePos()); poseMenuBounds = default; }
        return true;
    }

    private void DrawPoseMenu(House house)
    {
        if (poseMenu is not { } menu) { poseMenuBounds = default; return; }
        var item = house.Furnishings.FirstOrDefault(f => f.Id == menu.Furnishing);
        if (item?.Poses == null || input.LeftPressed && !poseMenuBounds.Contains(ImGui.GetMousePos()) && poseMenuBounds.Size.X > 0 || ImGui.IsKeyPressed(ImGuiKey.Escape))
        { poseMenu = null; poseMenuBounds = default; return; }
        HavenKit.PushTheme();
        ImGui.SetNextWindowPos(menu.At + Px(12, 0), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Px(Space3, Space3)); ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, Px(RadiusWindow));
        if (ImGui.Begin("###Haven.PoseMenu", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar))
        {
            HavenKit.KeepOnTop();
            poseMenuBounds = new(ImGui.GetWindowPos(), ImGui.GetWindowSize());
            HavenKit.SectionTitle(housing.NameOf(item), "choose an animation");
            var entries = housing.PoseEntries.ToArray();
            foreach (var set in item.Poses)
            {
                var holders = PoseSessions.Holders(entries, item.Id, set);
                var detail = $"{set.Roles.Length} {(set.Roles.Length == 1 ? "place" : "places")}" + (holders.Any(h => h != 0) ? $" · {holders.Count(h => h != 0)} taken" : "");
                if (HavenKit.ListRow("##pose-set-" + set.Id, set.Name, detail, PoseSessions.Open(entries, item.Id, set.Id), HavenIcon.Custom, Px(260)))
                { var id = set.Id; housing.Run(() => housing.OpenPoseSet(item.Id, id)); poseMenu = null; }
            }
            // The owner, decorators and whoever is on or viewing a set hide its balls for everyone; whoever is on one stands up.
            if (entries.Any(e => !e.Empty && e.Furnishing == item.Id && housing.MayClosePoseSet(item.Id, e.Set)))
            {
                HavenKit.MenuRule();
                if (HavenKit.Button("Hide the balls##pose-close", ButtonKind.Ghost, new(Px(260), 0))) { var id = item.Id; housing.Run(() => housing.ClosePoseSets(id)); poseMenu = null; }
                if (ImGui.IsItemHovered()) HavenKit.Tooltip("Hides these balls for everyone. Anyone on one stands up.");
            }
            if (Catalogue.Prefab(item).Switch is { InspectorOnly: false } && housing.CanInteract(item))
            {
                HavenKit.MenuRule();
                var prefabSwitch = Catalogue.Prefab(item).Switch!;
                var label = FurnishingInteraction.Action(Catalogue.Get(item).Food, housing.SwitchOn(item), prefabSwitch.DefaultOn, true, prefabSwitch.Verb, prefabSwitch.ClosedOn);
                if (HavenKit.Button(label + "##pose-switch", ButtonKind.Secondary, new(Px(260), 0))) { housing.Run(() => housing.ToggleSwitch(item.Id)); poseMenu = null; }
            }
        }
        ImGui.End();
        ImGui.PopStyleVar(2);
        HavenKit.PopTheme();
    }

    // The player's own ball: the panel (PoseHudUi), filled from the ball and carried out here.
    private readonly PoseHudState poseHud = new();
    private void DrawPoseHud()
    {
        // The player's own ball, or the possessed NPC's (the controls then act for it).
        var subject = housing.PoseSubject;
        var npc = PoseNpcs.IsNpc(subject);
        if (housing.SeatOf(subject) is not { } bound || housing.FindSet(bound.Furnishing, bound.Set) is not { } found) { poseHudBounds = default; return; }
        var set = found.Set;
        var holders = PoseSessions.Holders(housing.PoseEntries, bound.Furnishing, set);
        var others = holders.Where(h => h != 0 && h != subject).Select(housing.PoseName).ToArray();
        var status = bound.Positioning ? "Placing this ball: move yourself where it belongs"
            : bound.Playing ? bound.Status.Length > 0 ? bound.Status : others.Length > 0 ? "Playing with " + string.Join(", ", others) : "Playing" : bound.Status;
        // Partners on the set can be adjusted from here too: one at a time, or everyone together.
        var partners = housing.PosePartners();
        if (poseTarget != 0 && poseTarget != Everyone && !partners.Contains(poseTarget) || poseTarget == Everyone && partners.Length == 0) poseTarget = 0;
        var self = npc ? housing.PoseName(subject) : "You";
        string[] targets = partners.Length == 0 ? [] : [self, .. partners.Select(p => housing.PoseName(p)), "Everyone"];
        var targetIndex = poseTarget == 0 ? 0 : poseTarget == Everyone ? targets.Length - 1 : Array.IndexOf(partners, poseTarget) + 1;
        var shown = housing.FitOf(PoseTargetCharacter()) ?? bound.Fit;
        var title = (npc ? self + " · " : "") + set.Name + " · " + RoleLabel(set, bound.Role);
        var command = PoseHudUi.Draw(new(title, status, bound.Playing, bound.Positioning, shown.Move, shown.Turn, shown.Pitch, shown.Roll,
            shown.Scale, housing.PoseSounds, housing.PoseRestOnSurface) { Targets = targets, Target = targetIndex, LetPartners = housing.PoseLetPartnersAdjust }, poseHud, out poseHudBounds);
        poseHudCollapsed = poseHud.Collapsed;
        switch (command.Action)
        {
            case PoseHudAction.Target:
                poseTarget = command.Target <= 0 ? 0 : command.Target >= targets.Length - 1 ? Everyone : partners[command.Target - 1];
                poseAxisDrag = null;
                break;
            case PoseHudAction.LetPartners: housing.Run(() => housing.PoseLetPartnersAdjust = command.Flag); break;
            case PoseHudAction.Fit: ApplyPoseFit(shown with { Move = command.Move, Turn = command.Turn, Pitch = command.Pitch, Roll = command.Roll }, ImGui.GetIO().KeyCtrl); break;
            case PoseHudAction.Scale: ApplyPoseFit(shown with { Scale = command.Scale }); break;
            case PoseHudAction.Sounds: housing.Run(() => housing.PoseSounds = command.Flag); break;
            case PoseHudAction.Rest: housing.Run(() => housing.PoseRestOnSurface = command.Flag); break;
            case PoseHudAction.SaveFit: housing.Run(housing.SavePoseFit); break;
            case PoseHudAction.Reset:
                var target = poseTarget;
                housing.Run(() =>
                {
                    if (target is 0 or Everyone) housing.ResetPoseFit();
                    foreach (var partner in target == Everyone ? housing.PosePartners() : target == 0 ? [] : [target])
                        housing.SetFitFor(partner, new(Vector3.Zero, 0, 0, 0, 1));
                });
                break;
            case PoseHudAction.Restart: housing.Run(housing.RestartPoseAnimation); break;
            case PoseHudAction.StandUp: housing.Run(housing.LeaveSubjectBall); break;
            case PoseHudAction.Done: housing.Run(() => housing.FinishPositioning(true)); break;
            case PoseHudAction.Cancel: housing.Run(() => housing.FinishPositioning(false)); break;
        }
    }

    // The player the handles sit on: the one adjusted (the player themself when adjusting everyone).
    private ulong PoseTargetCharacter() => poseTarget is 0 or Everyone ? housing.PoseSubject : poseTarget;

    // Where the one taking balls stands: the possessed NPC, else the player.
    private Vector3? PoseActorPosition() =>
        housing.PossessedNpc?.Invoke() is { } npc && housing.NpcAt?.Invoke(npc) is { } at ? at : engine.HousingPlayerPosition;

    // A fit from the HUD or a handle, for whoever is adjusted: the player, a partner, or everyone together.
    // Ctrl held lifts the 2 m cap (up to PoseWire.FarMove).
    private void ApplyPoseFit(PoseFitValues fit, bool far = false)
    {
        var target = poseTarget;
        housing.Run(() =>
        {
            if (target == Everyone) housing.SetFitForEveryone(fit, far);
            else housing.SetFitFor(target == 0 ? housing.PoseSubject : target, fit, far);
        });
    }
}
