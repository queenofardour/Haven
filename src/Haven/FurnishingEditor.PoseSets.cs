using System.Numerics;
using Dalamud.Bindings.ImGui;
using Haven.Core;
using Haven.Core.Animation;
using static Haven.HavenTokens;

namespace Haven;

// Setting up pose balls, in the inspector while decorating: a furnishing's animation sets, an
// editor for one set (its name, its balls, what each plays and where it stands, previewed live in the room), and
// importing animations from a Penumbra animation mod.
internal sealed partial class FurnishingEditor
{
    private (Guid Furnishing, PoseSet Draft, bool New)? poseEdit;
    private bool poseEditShown, poseEditDeleting, poseEditCustom;
    private string? poseEditPackage;
    private PoseAnimationManifest? poseEditManifest;
    public Action<bool, Action<string>>? PickAnimationMod { get; set; }
    private Task<HousingController.AnimationImportChoice>? animationRead;
    private HousingController.AnimationImportChoice? animationChoice;
    private bool animationImportShown;
    private bool[] animationChosen = [];
    private string[] animationNames = [];
    private string animationPackageName = "";
    // The prop kept with the animations (an index into the mod's props), or -1 for none.
    private int animationProp = -1;
    private Task<string>? animationImport;
    private string poseEmoteSearch = "";

    // The pose balls section of a furnishing's inspector.
    private void DrawPoseSets(Furnishing item)
    {
        UpdateAnimationImport();
        if (item.BuildingPart != null) return;
        var canEdit = editing && housing.CanFurnish && !housing.IsPlacing;
        if (!canEdit && item.Poses == null) return;
        ImGui.Dummy(Px(0, Space1));
        HavenKit.SectionTitle("Pose balls", item.Poses == null ? "animations players take here" : $"{item.Poses.Length} animation {(item.Poses.Length == 1 ? "set" : "sets")}");
        if (editing) housing.PosePreview = (item.Id, null);
        foreach (var set in item.Poses ?? [])
        {
            var detail = $"{set.Roles.Length} {(set.Roles.Length == 1 ? "place" : "places")} · " + (set.Package != null ? housing.Animations.Name(set.Package) ?? "custom animations" : "game emotes");
            if (HavenKit.ListRow("##pose-edit-" + set.Id, set.Name, detail, poseEdit?.Draft.Id == set.Id, HavenIcon.Custom) && canEdit) BeginPoseEdit(item, set, false);
        }
        if (!canEdit) return;
        if (poseEdit is { } pending && pending.Furnishing == item.Id && !poseEditShown && animationChoice == null && animationRead == null && animationImport == null)
        {
            if (HavenKit.Button($"Back to editing {pending.Draft.Name}##pose-back", ButtonKind.Primary, new(-1, 0))) poseEditOpenRequested = true;
            else if (HavenKit.Button("Discard that set##pose-discard", ButtonKind.Ghost, new(-1, 0))) poseEdit = null;
        }
        else if ((item.Poses?.Length ?? 0) < PoseSets.MaxSetsPerFurnishing && HavenKit.Button("Add an animation set##pose-add", ButtonKind.Secondary, new(-1, 0), HavenIcon.Custom))
        {
            var balls = housing.DefaultPoseBalls(item, 2);
            BeginPoseEdit(item, new PoseSet
            {
                Name = "Cuddle",
                Roles = [.. balls.Select((p, i) => new PoseRole { Name = i == 0 ? "Pink" : "Blue", Position = p, Color = PoseSets.DefaultColors[i], Emote = DefaultEmote })],
            }, true);
        }
        if (poseEdit is { } edit && edit.Furnishing == item.Id && !poseEditShown && animationChoice == null && animationRead == null && animationImport == null && poseEditOpenRequested)
        { ImGui.OpenPopup("##pose-set"); poseEditShown = true; poseEditOpenRequested = false; }
        DrawPoseSetDialog(item);
        DrawAnimationImport();
    }
    private bool poseEditOpenRequested;
    // A looping emote a new ball plays until the owner picks something: /doze where it exists, else the first.
    private uint DefaultEmote => housing.PoseEmotes.FirstOrDefault(e => e.Id == 88).Id is var doze && doze != 0 ? doze : housing.PoseEmotes.FirstOrDefault().Id;

    private void BeginPoseEdit(Furnishing item, PoseSet set, bool isNew)
    {
        poseEdit = (item.Id, set, isNew); poseEditShown = false; poseEditOpenRequested = true; poseEditDeleting = false; poseEditCustom = set.Package != null;
        poseEditPackage = null; poseEditManifest = null; poseEmoteSearch = "";
    }
    private void ReopenPoseEdit() { if (poseEdit != null) { poseEditShown = false; poseEditOpenRequested = true; } }

    private void DrawPoseSetDialog(Furnishing item)
    {
        if (poseEdit is not { } edit || edit.Furnishing != item.Id || !poseEditShown) return;
        if (!HavenKit.BeginDialog("##pose-set", poseEditDeleting ? "Delete this animation set?" : edit.New ? "New animation set" : "Animation set", 560, onScreen: true))
        {
            // Closed by a click outside: the set waits in the inspector ("Back to editing").
            poseEditShown = false; return;
        }
        var draft = edit.Draft;
        var width = ImGui.GetContentRegionAvail().X;
        if (poseEditDeleting)
        {
            HavenKit.Caption($"{draft.Name} and its balls leave this furnishing. Anyone on one of its balls stands up.", width);
            HavenKit.AlignRight(HavenKit.ButtonWidth("Cancel") + HavenKit.ButtonWidth("Delete") + Px(Space2));
            if (HavenKit.Button("Cancel##pose-delete", ButtonKind.Ghost)) poseEditDeleting = false;
            ImGui.SameLine(0, Px(Space2));
            if (HavenKit.Button("Delete##pose-delete", ButtonKind.Danger))
            { var id = draft.Id; housing.Run(() => housing.RemovePoseSet(item.Id, id)); poseEdit = null; ImGui.CloseCurrentPopup(); }
            HavenKit.EndDialog();
            return;
        }
        var name = draft.Name;
        if (HavenKit.Field("##pose-set-name", "Name", ref name, "Cuddle, Slow dance…", width, 40)) draft = draft with { Name = name };
        // What the balls play: game emotes, or the animations of one imported package.
        var source = poseEditCustom || draft.Package != null ? 1 : 0;
        HavenKit.Caption("The balls play");
        if (HavenKit.Segmented("##pose-source", ["Game emotes", "Custom animations"], ref source))
        {
            poseEditCustom = source == 1;
            if (source == 0) draft = draft with { Package = null, Roles = [.. draft.Roles.Select(r => r with { Emote = r.Emote != 0 ? r.Emote : DefaultEmote, Animation = 0, Carrier = 0 })] };
            else if (housing.Animations.Library.FirstOrDefault().Hash is { } first) draft = WithPackage(draft, first);
        }
        if (source == 1)
        {
            var library = housing.Animations.Library;
            var hashes = library.Select(l => l.Hash).ToList();
            var names = library.Select(l => l.Item.Name).ToList();
            if (draft.Package != null && !hashes.Contains(draft.Package)) { hashes.Insert(0, draft.Package); names.Insert(0, housing.Animations.Name(draft.Package) ?? "This home's animations"); }
            var index = draft.Package == null ? -1 : hashes.IndexOf(draft.Package);
            var half = (width - Px(Space2)) / 2;
            if (hashes.Count > 0 && HavenKit.Select("##pose-package", "Animations", names, ref index, width)) draft = WithPackage(draft, hashes[index]);
            if (HavenKit.Button("Import an animation mod##pose-import-file", ButtonKind.Secondary, new(half, 0))) { poseEdit = edit with { Draft = draft }; PickAnimationModFromDialog(false); }
            ImGui.SameLine(0, Px(Space2));
            if (HavenKit.Button("Import a mod folder##pose-import-folder", ButtonKind.Secondary, new(half, 0))) { poseEdit = edit with { Draft = draft }; PickAnimationModFromDialog(true); }
            HavenKit.Caption(hashes.Count == 0
                ? "Import a Penumbra animation mod (.pmp or its folder). Couple animations usually come as two animations, one for each partner."
                : "Imported animations replace nothing in your game. While someone is on a ball, Haven plays its animation through a little-used emote, only in this home, and gives the emote back after.", width);
        }
        var manifest = draft.Package != null ? Manifest(draft.Package) : null;
        // The balls.
        ImGui.Dummy(Px(0, Space1));
        HavenKit.SectionTitle("Balls", "the room shows them as you edit", width);
        var roles = draft.Roles.ToList();
        var emotes = housing.PoseEmotes;
        var emoteNames = emotes.Select(e => e.Name).ToArray();
        var tryBall = -1;
        for (var r = 0; r < roles.Count; r++)
        {
            ImGui.PushID("pose-role-" + r);
            var role = roles[r];
            // The colour, the ball's name and what it plays, side by side, each with its label above (a group each,
            // so the row stays one line).
            var nameWidth = (width - Px(26 + ControlHeight) - Px(Space2) * 3) * .45f;
            var playsWidth = width - Px(26 + ControlHeight) - Px(Space2) * 3 - nameWidth;
            ImGui.BeginGroup();
            using (HavenFonts.Use(HavenText.Small)) ImGui.TextColored(TextSecondary, " ");
            if (HavenKit.Swatch("##colour", ColourOf(role.Color), false, "Change colour"))
                role = role with { Color = PoseColours[(Array.IndexOf(PoseColours, role.Color) + 1) % PoseColours.Length] };
            ImGui.EndGroup();
            ImGui.SameLine(0, Px(Space2));
            ImGui.BeginGroup();
            var roleName = role.Name;
            if (HavenKit.Field("##role-name", "Ball", ref roleName, $"Ball {r + 1}", nameWidth, 30)) role = role with { Name = roleName };
            ImGui.EndGroup();
            ImGui.SameLine(0, Px(Space2));
            ImGui.BeginGroup();
            if (manifest != null)
            {
                var animation = Math.Clamp(role.Animation, 0, manifest.Animations.Length - 1);
                if (HavenKit.Select("##role-animation", "Plays", manifest.Animations.Select(a => a.Name).ToArray(), ref animation, playsWidth)) role = role with { Animation = animation, Emote = 0 };
                if (ImGui.IsItemHovered() && manifest.Animations[animation].Source.Length > 0)
                    HavenKit.Tooltip($"In its mod, this animation replaced {manifest.Animations[animation].Source}. Here it replaces nothing.");
            }
            else
            {
                var emote = Array.FindIndex(emotes.ToArray(), e => e.Id == role.Emote);
                if (HavenKit.Select("##role-emote", "Plays", emoteNames, ref emote, playsWidth)) role = role with { Emote = emotes[emote].Id };
            }
            ImGui.EndGroup();
            ImGui.SameLine(0, Px(Space2));
            ImGui.BeginGroup();
            using (HavenFonts.Use(HavenText.Small)) ImGui.TextColored(TextSecondary, " ");
            ImGui.BeginDisabled(roles.Count <= 1);
            var remove = HavenKit.IconButton("##remove", HavenIcon.Trash, "Remove this ball");
            ImGui.EndDisabled();
            ImGui.EndGroup();
            // Where the player on it stands, and the way they face; or stand there yourself (Try it).
            var p = role.Offset; var turn = role.Rotation;
            var fields = (width - Px(Space2) * 5 - HavenKit.ButtonWidth("Here") - HavenKit.ButtonWidth("Try it")) / 4;
            var moved = HavenKit.DragField("##x", "X", AxisX, ref p.X, .005f, "%.2f", fields, -PoseSets.MaxOffset, PoseSets.MaxOffset);
            ImGui.SameLine(0, Px(Space2)); moved |= HavenKit.DragField("##y", "Y", AxisY, ref p.Y, .005f, "%.2f", fields, -PoseSets.MaxOffset, PoseSets.MaxOffset);
            ImGui.SameLine(0, Px(Space2)); moved |= HavenKit.DragField("##z", "Z", AxisZ, ref p.Z, .005f, "%.2f", fields, -PoseSets.MaxOffset, PoseSets.MaxOffset);
            ImGui.SameLine(0, Px(Space2)); moved |= HavenKit.DragField("##turn", "°", TextSecondary, ref turn, .5f, "%.0f", fields, -180, 180);
            if (moved) role = role with { Position = [p.X, p.Y, p.Z], Rotation = turn };
            ImGui.SameLine(0, Px(Space2));
            if (HavenKit.Button("Here##here", ButtonKind.Secondary) && housing.PoseBallHere(item) is { } here) role = role with { Position = here.Position, Rotation = here.Rotation };
            if (ImGui.IsItemHovered()) HavenKit.Tooltip("Put this ball where you stand, facing the way you face.");
            ImGui.SameLine(0, Px(Space2));
            if (HavenKit.Button("Try it##try", ButtonKind.Primary)) tryBall = r;
            if (ImGui.IsItemHovered()) HavenKit.Tooltip("Save the set and stand on this ball yourself, playing its animation. Move into place with the handles and the panel, then Done moves the ball there.");
            if (role.Pitch != 0 || role.Roll != 0) HavenKit.Caption($"Tilted {role.Pitch:0}°, leaning {role.Roll:0}° (set with Try it).", width);
            roles[r] = role;
            ImGui.PopID();
            if (remove) { roles.RemoveAt(r); r--; }
            ImGui.Dummy(Px(0, Space1));
        }
        if (roles.Count < PoseSets.MaxRoles && HavenKit.Button("Add a ball##pose-add-role", ButtonKind.Ghost))
        {
            var spot = housing.DefaultPoseBalls(item, 1)[0];
            roles.Add(new PoseRole { Name = "", Position = spot, Color = PoseSets.DefaultColors[roles.Count % PoseSets.DefaultColors.Length], Emote = draft.Package == null ? DefaultEmote : 0, Animation = Math.Min(roles.Count, (manifest?.Animations.Length ?? 1) - 1) });
        }
        draft = draft with { Roles = [.. roles] };
        // Try it: the set is saved as it stands, and the player stands on that ball to place it (FinishPositioning).
        if (tryBall >= 0 && draft.Name.Trim().Length > 0 && (draft.Package == null || manifest != null))
        {
            var saved = draft with { Name = draft.Name.Trim() }; var which = tryBall; var furnishing = item.Id;
            housing.Run(() => { housing.SavePoseSet(furnishing, saved); housing.BeginPositioning(furnishing, saved.Id, which); });
            poseEdit = null; ImGui.CloseCurrentPopup(); HavenKit.EndDialog(); return;
        }
        HavenKit.Caption("Players click the furnishing, choose this set, and take a ball each. The animation starts once every ball is taken; each player can then fine-tune their place from their own HUD.", width);
        poseEdit = edit with { Draft = draft };
        // Delete, Cancel, Save.
        if (!edit.New && HavenKit.Button("Delete set##pose-set", ButtonKind.Danger)) poseEditDeleting = true;
        if (!edit.New) ImGui.SameLine();
        HavenKit.AlignRight(HavenKit.ButtonWidth("Cancel") + HavenKit.ButtonWidth("Save") + Px(Space2));
        if (HavenKit.Button("Cancel##pose-set", ButtonKind.Ghost)) { poseEdit = null; ImGui.CloseCurrentPopup(); }
        ImGui.SameLine(0, Px(Space2));
        ImGui.BeginDisabled(draft.Name.Trim().Length == 0 || draft.Package != null && manifest == null);
        if (HavenKit.Button("Save##pose-set", ButtonKind.Primary))
        {
            var saved = draft with { Name = draft.Name.Trim() };
            housing.Run(() => housing.SavePoseSet(item.Id, saved));
            poseEdit = null; ImGui.CloseCurrentPopup();
        }
        ImGui.EndDisabled();
        HavenKit.EndDialog();
    }

    private static readonly uint[] PoseColours = [0xF29CC6, 0x8CC8F2, 0xB4E68C, 0xF2D27A, 0xC6A2F2, 0xF2A27A, 0x7AF2D8, 0xF2F2F2];
    private static Vector4 ColourOf(uint rgb) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1);
    private static readonly Vector4 AxisX = new(.93f, .53f, .53f, 1), AxisY = new(.6f, .8f, .6f, 1), AxisZ = new(.53f, .73f, .93f, 1);

    // A package's manifest for the editor, read once (the player's own packages are read on this PC when first chosen).
    private PoseAnimationManifest? Manifest(string package)
    {
        if (poseEditPackage == package && poseEditManifest != null) return poseEditManifest;
        poseEditPackage = package;
        try { poseEditManifest = housing.Animations.Item(package) != null ? housing.Animations.Load(package) : housing.Animations.Manifest(package); }
        catch (Exception ex) { poseEditManifest = null; housing.Run(() => throw new InvalidOperationException("Those animations couldn't be read: " + ex.Message, ex)); }
        return poseEditManifest;
    }

    // A set on another package: each ball plays the package's animation in its order (the first ball the first).
    private PoseSet WithPackage(PoseSet set, string package)
    {
        var count = Manifest(package)?.Animations.Length ?? 1;
        return set with { Package = package, Roles = [.. set.Roles.Select((r, i) => r with { Emote = 0, Carrier = 0, Animation = Math.Min(i, count - 1) })] };
    }

    // The file dialog would close the set editor (a click outside it), so it closes first and comes back after.
    private void PickAnimationModFromDialog(bool folder)
    {
        ImGui.CloseCurrentPopup(); poseEditShown = false;
        PickAnimationMod?.Invoke(folder, path => housing.Run(() => animationRead = housing.ReadAnimationMod(path)));
    }

    private void UpdateAnimationImport()
    {
        if (animationRead is { IsCompleted: true } read)
        {
            animationRead = null;
            if (read.IsCompletedSuccessfully)
            {
                animationChoice = read.Result; animationImportShown = false;
                animationChosen = [.. animationChoice.Candidates.Select(_ => true)];
                // One animation takes the mod's own name; several take the names of what they replaced, to rename.
                animationNames = animationChoice.Candidates.Count == 1 ? [SafeText.Clean(animationChoice.ModName, 40)] : [.. animationChoice.Candidates.Select((c, i) => Label(c, i))];
                animationPackageName = animationChoice.ModName;
                animationProp = animationChoice.Props.Count > 0 ? 0 : -1;
            }
            else { housing.Run(() => throw new InvalidOperationException("That mod couldn't be read: " + read.Exception!.GetBaseException().Message, read.Exception)); ReopenPoseEdit(); }
        }
        if (animationImport is { IsCompleted: true } import)
        {
            animationImport = null;
            if (import.IsCompletedSuccessfully)
            {
                if (poseEdit is { } edit) poseEdit = edit with { Draft = WithPackage(edit.Draft, import.Result) };
            }
            else housing.Run(() => throw new InvalidOperationException("Those animations couldn't be imported: " + import.Exception!.GetBaseException().Message, import.Exception));
            ReopenPoseEdit();
        }
    }
    private static string Label(PoseAnimationCandidate candidate, int index)
    {
        var name = candidate.Label != candidate.Key ? candidate.Label : candidate.FileName;
        var cut = name.IndexOf(" (", StringComparison.Ordinal);
        if (cut > 0) name = name[..cut];
        // A mod's variant (an option such as "Leg Lock") is named after its option.
        if (candidate.Option.Length > 0) name = $"{name} ({candidate.Option})";
        return SafeText.Clean(name, 40) is { Length: > 0 } clean ? clean : $"Animation {index + 1}";
    }

    // The animations a mod offers: each can be kept (with a name), and they become one package.
    private void DrawAnimationImport()
    {
        if (animationChoice is not { } choice) return;
        if (!animationImportShown) { ImGui.OpenPopup("##animation-import"); animationImportShown = true; }
        if (!HavenKit.BeginDialog("##animation-import", "Import animations", 520, onScreen: true)) { animationChoice = null; ReopenPoseEdit(); return; }
        var width = ImGui.GetContentRegionAvail().X;
        HavenKit.Caption($"{choice.ModName} changes {choice.Candidates.Count} {(choice.Candidates.Count == 1 ? "animation" : "animations")}. Keep the ones your balls will play and name them; " +
            "they become animations of your own and replace nothing in the game. Guests who load your home's custom content receive them.", width);
        ImGui.BeginChild("##animation-candidates", new(width, Math.Min(Px(320), Px(48) * choice.Candidates.Count + Px(8))));
        for (var i = 0; i < choice.Candidates.Count; i++)
        {
            ImGui.PushID("candidate-" + i);
            var keep = animationChosen[i];
            var candidate = choice.Candidates[i];
            var top = ImGui.GetCursorPosY();
            if (HavenKit.Toggle("##keep", ref keep, Px(48))) animationChosen[i] = keep;
            ImGui.SameLine(0, Px(Space2)); ImGui.SetCursorPosY(top);
            var name = animationNames[i];
            ImGui.BeginDisabled(!keep);
            if (HavenKit.Field("##name", "", ref name, "Animation name", width - Px(48 + Space2 * 2), 40)) animationNames[i] = name;
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                HavenKit.Tooltip($"Replaces {candidate.Label} in the mod{(candidate.Option.Length > 0 ? $" (option {candidate.Option})" : "")} · made for {candidate.Races.Count} of 18 body types");
            ImGui.PopID();
        }
        ImGui.EndChild();
        var package = animationPackageName;
        var submitted = HavenKit.SubmitField("##animation-package", "Name these animations", ref package, "Mod name", width, 40);
        animationPackageName = package;
        if (choice.Props.Count > 0)
        {
            ImGui.Dummy(Px(0, Space1));
            var keepProp = animationProp >= 0;
            var top = ImGui.GetCursorPosY();
            if (HavenKit.Toggle("##keep-prop", ref keepProp, Px(48))) animationProp = keepProp ? Math.Max(0, animationProp) : -1;
            ImGui.SameLine(0, Px(Space2)); ImGui.SetCursorPosY(top);
            ImGui.BeginGroup();
            var shown = Math.Max(0, animationProp);
            if (choice.Props.Count == 1) ImGui.TextUnformatted($"Include its prop: {choice.Props[0].Item} ({PoseProps.Label(choice.Props[0].Prop.Slot)})");
            else
            {
                ImGui.TextUnformatted("Include a prop:");
                ImGui.BeginDisabled(!keepProp);
                if (HavenKit.Select("##prop", "", [.. choice.Props.Select(p => $"{p.Item} ({PoseProps.Label(p.Prop.Slot)})")], ref shown, width - Px(48 + Space2 * 2))) animationProp = shown;
                ImGui.EndDisabled();
            }
            HavenKit.Caption("The mod draws its props as a piece of gear. Whoever takes one of these balls is asked whether to wear it; Haven puts it on with Glamourer while the animation plays and gives back what they wore after.", width - Px(48 + Space2 * 2));
            ImGui.EndGroup();
        }
        var chosen = Enumerable.Range(0, choice.Candidates.Count).Where(i => animationChosen[i]).ToArray();
        if (chosen.Length > PoseSets.MaxAnimations) HavenKit.Caption($"Keep at most {PoseSets.MaxAnimations}.", width);
        HavenKit.AlignRight(HavenKit.ButtonWidth("Cancel") + HavenKit.ButtonWidth("Import") + Px(Space2));
        if (HavenKit.Button("Cancel##animation-import", ButtonKind.Ghost)) { animationChoice = null; ImGui.CloseCurrentPopup(); ReopenPoseEdit(); }
        ImGui.SameLine(0, Px(Space2));
        ImGui.BeginDisabled(chosen.Length is 0 or > PoseSets.MaxAnimations || package.Trim().Length == 0 || animationImport != null);
        if (HavenKit.Button("Import##animation-import", ButtonKind.Primary) || submitted && chosen.Length is > 0 and <= PoseSets.MaxAnimations && package.Trim().Length > 0)
        {
            var picks = chosen.Select(i => (choice.Candidates[i], animationNames[i].Trim().Length > 0 ? animationNames[i].Trim() : Label(choice.Candidates[i], i))).ToArray();
            var title = package.Trim();
            var prop = animationProp >= 0 && animationProp < choice.Props.Count ? choice.Props[animationProp].Prop : null;
            housing.Run(() => animationImport = housing.ImportAnimations(choice, title, picks, prop));
            animationChoice = null; ImGui.CloseCurrentPopup();
        }
        ImGui.EndDisabled();
        HavenKit.EndDialog();
    }
}
