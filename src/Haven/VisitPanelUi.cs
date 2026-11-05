using System.Numerics;
using Dalamud.Bindings.ImGui;
using static Haven.HavenTokens;

namespace Haven;

internal enum VisitMode { Solo, Connecting, Hosting, Guest }
internal readonly record struct VisitMember(ulong ContentId, string Name, string Detail, bool Online, bool Owner = false);
internal readonly record struct VisitProposal(Guid Id, string Author, string Detail);
internal sealed record VisitPanelModel(VisitMode Mode, string Title, string Detail, string Code, IReadOnlyList<VisitMember> Members,
    bool IsOwner, bool CanDecorate, bool DecoratingEnabled, IReadOnlyCollection<ulong> Decorators, bool CanManageDecorators,
    bool HasDraft, bool HasArchivedDraft, bool CanPublish, string PublishLabel, bool Busy, string? Conflict,
    IReadOnlyList<VisitProposal> Proposals, bool CanReview, string EditStatus, string Recovery, bool RecoveryBlocked,
    bool CanSaveCopy, bool CanRetry, bool Leaving, string LeaveLabel,
    IReadOnlyList<VisitPicture>? Pictures = null, bool PicturesLoaded = false, Func<string, ImTextureID?>? Thumbnail = null)
{
    /// <summary>How many of the owner's own songs the home's playlist has (custom content, loaded with the pictures).</summary>
    public int Songs { get; init; }
    /// <summary>The owner's edits publish themselves (HousingController.AutoPublishing): Publish shows only for a conflict.</summary>
    public bool AutoPublish { get; init; }
    /// <summary>How many of the owner's own furnishings the home has (custom content too).</summary>
    public int Furniture { get; init; }
    /// <summary>How many animation packages the home's pose sets play (custom content too).</summary>
    public int Animations { get; init; }
    /// <summary>The owner's invitations while hosting; null otherwise.</summary>
    public VisitInvites? Invites { get; init; }
}
// Hosting: automatic invitations, the invite-only lock (and whether the relay keeps it), and the friends nearby.
internal sealed record VisitInvites(bool AutoInvite, bool InviteOnly, bool LockKept, IReadOnlyList<InviteFriend> Friends);
internal readonly record struct InviteFriend(ulong ContentId, string Name, bool Invited);
// A custom picture the home shows, for a guest to hide on their own PC.
internal readonly record struct VisitPicture(string Hash, bool Hidden, float Aspect = 5 / 3f);
internal enum VisitAction { None, CopyCode, Leave, SaveCopy, RetryEntry, RetryRecovery, Publish, Discard, RestoreDraft, KeepDraft,
    ApproveAll, RejectAll, Inspect, Approve, Decline, ApplyDecorators, LoadPictures, HidePicture, AutoInvite, InviteOnly, Invite, Uninvite }
internal readonly record struct VisitCommand(VisitAction Action, Guid Proposal = default, bool Enabled = false, ulong[]? Decorators = null, string? Picture = null, ulong Friend = 0);

// The decorator choices being edited, reset whenever the room's access changes underneath them.
internal sealed class VisitPanelState
{
    public string Key = "";
    public bool AllowDecorators;
    public readonly HashSet<ulong> Chosen = [];
    public DateTime CopiedUntil;
}

// The Visit panel: where you are, the visit code, who is here, decorating access,
// your draft, proposals to review and leaving. Replaces the studio window's Visits page and shared-editing block.
internal static class VisitPanelUi
{
    public static VisitCommand Draw(VisitPanelState state, VisitPanelModel model, ref bool open, float top)
    {
        var command = default(VisitCommand);
        if (SidePanelUi.Begin("Visit", "Haven.VisitPanel", ref open, top))
        {
            Status(model);
            if (model.Recovery.Length > 0)
            {
                HavenKit.Banner(BannerKind.Warning, "Reconnecting", model.Recovery + " The house stays as it is; chat returns when the connection does.");
                if (model.RecoveryBlocked && HavenKit.Button("Retry connection", ButtonKind.Secondary, new(-1, 0))) command = new(VisitAction.RetryRecovery);
            }
            if (model.Mode == VisitMode.Solo)
                HavenKit.Caption("To share this home, leave it, then choose Host a gathering from its card in the launcher.");
            if (model.Mode == VisitMode.Hosting && model.Code.Length > 0) Code(state, model, ref command);
            if (model.Invites is { } invites) Invites(invites, ref command);
            if (model.Members.Count > 0) Members(state, model, ref command);
            if (model.CanDecorate && model.Mode != VisitMode.Solo) Draft(model, ref command);
            if (model.IsOwner && model.Proposals.Count > 0) Proposals(model, ref command);
            if (!model.IsOwner && (model.Pictures is { Count: > 0 } || model.Songs > 0 || model.Furniture > 0 || model.Animations > 0)) Pictures(model, ref command);
            if (model.EditStatus.Length > 0) HavenKit.Caption(model.EditStatus);
            ImGui.Dummy(Px(0, Space2)); HavenKit.Rule();
            if (model.CanSaveCopy && HavenKit.Button("Save a copy of this home", ButtonKind.Secondary, new(-1, 0))) command = new(VisitAction.SaveCopy);
            if (model.CanRetry && HavenKit.Button("Retry entering the home", ButtonKind.Secondary, new(-1, 0))) command = new(VisitAction.RetryEntry);
            ImGui.BeginDisabled(model.Leaving);
            if (HavenKit.Button(model.LeaveLabel + "##visit-leave", model.Mode == VisitMode.Hosting ? ButtonKind.Danger : ButtonKind.Secondary, new(-1, 0))) command = new(VisitAction.Leave);
            ImGui.EndDisabled();
        }
        SidePanelUi.End(open);
        return command;
    }

    private static void Status(VisitPanelModel model)
    {
        var width = ImGui.GetContentRegionAvail().X; var min = ImGui.GetCursorScreenPos(); var draw = ImGui.GetWindowDrawList(); var height = Px(64);
        var tone = model.Mode switch { VisitMode.Hosting => Accent, VisitMode.Connecting => Warning, _ => Success };
        draw.AddRectFilled(min, min + new Vector2(width, height), HavenUi.U(model.Mode == VisitMode.Hosting ? AccentTint : HavenTokens.Card), Px(RadiusCard));
        draw.AddCircleFilled(min + new Vector2(Px(Space4 + 4), height / 2), Px(4), HavenUi.U(tone));
        var x = min.X + Px(Space4 + 16); float titleHeight;
        draw.PushClipRect(min, min + new Vector2(width - Px(Space3), height), true);
        using (HavenFonts.Use(HavenText.Strong)) { titleHeight = ImGui.GetTextLineHeight(); draw.AddText(new(x, min.Y + Px(12)), HavenUi.U(Text), model.Title); }
        using (HavenFonts.Use(HavenText.Small)) draw.AddText(new(x, min.Y + Px(14) + titleHeight), HavenUi.U(TextSecondary), model.Detail);
        draw.PopClipRect();
        ImGui.Dummy(new(width, height + Px(Space1)));
    }

    private static void Code(VisitPanelState state, VisitPanelModel model, ref VisitCommand command)
    {
        HavenKit.SectionTitle("Visit code");
        var copy = DateTime.UtcNow < state.CopiedUntil ? "Copied" : "Copy";
        var buttonWidth = HavenKit.ButtonWidth("Copied");
        var width = ImGui.GetContentRegionAvail().X - buttonWidth - Px(Space2); var min = ImGui.GetCursorScreenPos(); var height = Px(ControlHeight);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, min + new Vector2(width, height), HavenUi.U(Inset), Px(RadiusField));
        draw.AddRect(min, min + new Vector2(width, height), HavenUi.U(BorderStrong), Px(RadiusField));
        draw.PushClipRect(min, min + new Vector2(width - Px(Space2), height), true);
        draw.AddText(min + new Vector2(Px(Space3), (height - ImGui.GetTextLineHeight()) / 2), HavenUi.U(Text), model.Code);
        draw.PopClipRect();
        ImGui.Dummy(new(width, height)); ImGui.SameLine(0, Px(Space2));
        if (HavenKit.Button(copy + "##visit-copy", ButtonKind.Primary, new(buttonWidth, 0))) { command = new(VisitAction.CopyCode); state.CopiedUntil = DateTime.UtcNow.AddSeconds(2); }
        HavenKit.Caption(model.Invites is { InviteOnly: true }
            ? "Only people you invite can join, with this code or from their invitation."
            : model.Invites is { AutoInvite: true }
            ? "Friends nearby with Haven are asked to join by themselves. Anyone else can paste this under Join a friend in their launcher, standing near you."
            : "Friends paste this under Join a friend in their launcher, standing near you in the same zone.");
    }

    // Who is asked to join: friends nearby automatically (or only those chosen), and whether the code alone lets people in.
    private static void Invites(VisitInvites invites, ref VisitCommand command)
    {
        ImGui.Dummy(Px(0, Space1));
        HavenKit.SectionTitle("Invitations", invites.Friends.Count switch { 0 => "", 1 => "1 friend nearby", var n => $"{n} friends nearby" });
        var auto = invites.AutoInvite;
        if (HavenKit.Toggle("Invite friends nearby automatically", ref auto)) command = new(VisitAction.AutoInvite, Enabled: auto);
        var only = invites.InviteOnly;
        if (HavenKit.Toggle("Only people I invite can join", ref only)) command = new(VisitAction.InviteOnly, Enabled: only);
        HavenKit.Caption(!invites.InviteOnly ? "Anyone standing near you with your visit code can join too."
            : invites.LockKept ? "Others can't join, even with your visit code. People already here can come back."
            : "Waiting for the relay to confirm. If this stays, the relay needs an update and the code still works.");
        if (invites.Friends.Count == 0)
        {
            HavenKit.Caption("Friends on your friend list show here when they come near you.");
            return;
        }
        foreach (var friend in invites.Friends)
        {
            var label = friend.Invited ? "Uninvite" : "Invite";
            var top = ImGui.GetCursorPosY();
            using (HavenFonts.Use(HavenText.Body))
            {
                ImGui.SetCursorPosY(top + (Px(ControlHeight) - ImGui.GetTextLineHeight()) / 2);
                ImGui.TextUnformatted(friend.Name);
                if (friend.Invited) { ImGui.SameLine(0, Px(Space2)); ImGui.TextColored(TextTertiary, "Invited"); }
            }
            ImGui.SameLine(); ImGui.SetCursorPosY(top);
            HavenKit.AlignRight(HavenKit.ButtonWidth(label));
            if (HavenKit.Button(label + "##invite-" + friend.ContentId, friend.Invited ? ButtonKind.Ghost : ButtonKind.Secondary))
                command = new(friend.Invited ? VisitAction.Uninvite : VisitAction.Invite, Friend: friend.ContentId);
        }
        if (!invites.AutoInvite) HavenKit.Caption("Only the friends you invite are asked to join.");
    }

    private static void Members(VisitPanelState state, VisitPanelModel model, ref VisitCommand command)
    {
        ImGui.Dummy(Px(0, Space1));
        var key = string.Join(',', model.Decorators.Order()) + "|" + model.DecoratingEnabled;
        if (key != state.Key) { state.Key = key; state.AllowDecorators = model.DecoratingEnabled; state.Chosen.Clear(); state.Chosen.UnionWith(model.Decorators); }
        HavenKit.SectionTitle("In this home", model.Members.Count == 1 ? "1 person" : $"{model.Members.Count} people");
        if (!model.CanManageDecorators)
        {
            foreach (var member in model.Members)
                HavenKit.ListRow("##member-" + member.ContentId, member.Name, member.Online ? member.Detail : "Reconnecting", false, HavenIcon.Friends);
            return;
        }
        HavenKit.Toggle("Let guests decorate", ref state.AllowDecorators);
        HavenKit.Caption("You approve their changes and keep the final say, even if hosting moves to someone else.");
        ImGui.BeginDisabled(!state.AllowDecorators);
        foreach (var member in model.Members)
        {
            if (member.ContentId == 0 || member.Owner) continue;
            var chosen = state.Chosen.Contains(member.ContentId);
            if (HavenKit.Toggle(member.Name + (member.Online ? "" : " · reconnecting") + "##decorator-" + member.ContentId, ref chosen))
            { if (chosen) state.Chosen.Add(member.ContentId); else state.Chosen.Remove(member.ContentId); }
        }
        ImGui.EndDisabled();
        var dirty = state.AllowDecorators != model.DecoratingEnabled || !state.Chosen.SetEquals(model.Decorators);
        ImGui.BeginDisabled(!dirty);
        if (HavenKit.Button("Apply decorating access", ButtonKind.Secondary, new(-1, 0)))
            command = new(VisitAction.ApplyDecorators, Enabled: state.AllowDecorators, Decorators: state.Chosen.Where(id => model.Members.Any(m => m.ContentId == id)).ToArray());
        ImGui.EndDisabled();
    }

    // A guest's choice to load the owner's custom content (pictures and songs), and any single picture hidden for this
    // PC alone.
    private static void Pictures(VisitPanelModel model, ref VisitCommand command)
    {
        ImGui.Dummy(Px(0, Space1));
        var pictures = model.Pictures ?? [];
        var counts = new[] { pictures.Count switch { 0 => "", 1 => "1 picture", var n => $"{n} pictures" }, model.Songs switch { 0 => "", 1 => "1 song", var n => $"{n} songs" },
            model.Furniture switch { 0 => "", 1 => "1 furnishing", var n => $"{n} furnishings" },
            model.Animations switch { 0 => "", 1 => "1 animation pack", var n => $"{n} animation packs" } };
        HavenKit.SectionTitle("Custom content", string.Join(" · ", counts.Where(c => c.Length > 0)));
        var load = model.PicturesLoaded;
        if (HavenKit.Toggle("Load this home's custom content", ref load)) command = new(VisitAction.LoadPictures, Enabled: load);
        if (!model.PicturesLoaded)
        {
            HavenKit.Caption(string.Join(" ", new[] { pictures.Count > 0 ? "Frames with the owner's own pictures stay blank." : "",
                model.Songs > 0 ? "The owner's own songs are silent." : "", model.Furniture > 0 ? "Their own furniture shows as the original pieces." : "",
                model.Animations > 0 ? "The owner's own animations show as ordinary emotes." : "" }.Where(s => s.Length > 0)));
            return;
        }
        if (pictures.Count == 0) return;
        HavenKit.Caption("Hide any picture you'd rather not see. Only you stop seeing it, in every home.");
        foreach (var picture in pictures)
        {
            // The picture in its own shape, within 80 x 48.
            var fit = Math.Min(Px(80) / Math.Max(.01f, picture.Aspect), Px(48));
            var size = new Vector2(fit * picture.Aspect, fit);
            var min = ImGui.GetCursorScreenPos(); var draw = ImGui.GetWindowDrawList();
            if (!picture.Hidden && model.Thumbnail?.Invoke(picture.Hash) is {} texture)
                draw.AddImageRounded(texture, min, min + size, Vector2.Zero, Vector2.One, HavenUi.U(Vector4.One), Px(RadiusField));
            else
            {
                draw.AddRectFilled(min, min + size, HavenUi.U(Inset), Px(RadiusField));
                var text = picture.Hidden ? "Hidden" : "…";
                using (HavenFonts.Use(HavenText.Small)) draw.AddText(min + (size - ImGui.CalcTextSize(text)) / 2, HavenUi.U(TextTertiary), text);
            }
            ImGui.Dummy(size);
            var label = (picture.Hidden ? "Show" : "Hide") + "##picture-" + picture.Hash;
            ImGui.SameLine(); HavenKit.AlignRight(HavenKit.ButtonWidth(picture.Hidden ? "Show" : "Hide"));
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (size.Y - Px(ControlHeight)) / 2);
            if (HavenKit.Button(label, ButtonKind.Ghost)) command = new(VisitAction.HidePicture, Enabled: !picture.Hidden, Picture: picture.Hash);
        }
    }

    // "3 furnishings · 1 room change": only what a proposal touches, counted in words people use.
    internal static string ProposalSummary(int furnishings, int other)
    {
        var parts = new List<string>();
        if (furnishings > 0) parts.Add(furnishings == 1 ? "1 furnishing" : $"{furnishings} furnishings");
        if (other > 0) parts.Add(other == 1 ? "1 room change" : $"{other} room changes");
        return parts.Count == 0 ? "No changes" : string.Join(" · ", parts);
    }

    private static void Draft(VisitPanelModel model, ref VisitCommand command)
    {
        ImGui.Dummy(Px(0, Space1));
        HavenKit.SectionTitle("Your changes");
        HavenKit.Caption(model.IsOwner ? model.AutoPublish
                ? "Everyone sees your edits a moment after you make them. Blue previews are proposals from your decorators."
                : "Your edits stay private until you publish them. Blue previews are proposals from your decorators."
            : "Your edits are sent to the owner for approval when you finish placing or adjusting an object.");
        // With automatic publishing the button is only needed to settle a conflict.
        if (model.IsOwner && (!model.AutoPublish || model.Conflict != null))
        {
            ImGui.BeginDisabled(!model.CanPublish);
            if (HavenKit.Button(model.PublishLabel + "##visit-publish", ButtonKind.Primary, new(-1, 0))) command = new(VisitAction.Publish);
            ImGui.EndDisabled();
        }
        ImGui.BeginDisabled(!model.HasDraft || model.Busy);
        if (HavenKit.Button("Discard my draft…", ButtonKind.Ghost, new(-1, 0))) ImGui.OpenPopup("##visit-discard");
        ImGui.EndDisabled();
        if (model.HasArchivedDraft && !model.HasDraft && HavenKit.Button("Restore my local draft", ButtonKind.Secondary, new(-1, 0))) command = new(VisitAction.RestoreDraft);
        if (model.Conflict is { } conflict)
        {
            HavenKit.Banner(BannerKind.Warning, "Your draft conflicts with the shared home", conflict + " Discard to use the shared layout.");
            if (model.IsOwner && HavenKit.Button("Keep my draft values", ButtonKind.Secondary, new(-1, 0))) command = new(VisitAction.KeepDraft);
        }
        if (HavenKit.BeginDialog("##visit-discard", "Discard your draft?"))
        {
            HavenKit.Caption("You return to the published layout. A local backup is kept.");
            HavenKit.AlignRight(HavenKit.ButtonWidth("Keep editing") + HavenKit.ButtonWidth("Discard") + Px(Space2));
            if (HavenKit.Button("Keep editing", ButtonKind.Ghost)) ImGui.CloseCurrentPopup();
            ImGui.SameLine(0, Px(Space2));
            if (HavenKit.Button("Discard", ButtonKind.Danger)) { command = new(VisitAction.Discard); ImGui.CloseCurrentPopup(); }
            HavenKit.EndDialog();
        }
    }

    private static void Proposals(VisitPanelModel model, ref VisitCommand command)
    {
        ImGui.Dummy(Px(0, Space1));
        HavenKit.SectionTitle("Proposals", $"{model.Proposals.Count} waiting");
        ImGui.BeginDisabled(!model.CanReview);
        var half = (ImGui.GetContentRegionAvail().X - Px(Space2)) / 2;
        if (HavenKit.Button("Approve all", ButtonKind.Primary, new(half, 0))) command = new(VisitAction.ApproveAll);
        ImGui.SameLine(0, Px(Space2)); if (HavenKit.Button("Reject all", ButtonKind.Danger, new(half, 0))) command = new(VisitAction.RejectAll);
        foreach (var proposal in model.Proposals)
        {
            ImGui.PushID(proposal.Id.ToString());
            var min = ImGui.GetCursorScreenPos(); var width = ImGui.GetContentRegionAvail().X; var draw = ImGui.GetWindowDrawList();
            float titleHeight;
            using (HavenFonts.Use(HavenText.Strong)) { titleHeight = ImGui.GetTextLineHeight(); draw.AddText(min, HavenUi.U(Text), proposal.Author); }
            using (HavenFonts.Use(HavenText.Caption)) draw.AddText(min + new Vector2(0, titleHeight + Px(2)), HavenUi.U(TextTertiary), proposal.Detail);
            ImGui.Dummy(new(width, titleHeight + Px(20)));
            var third = (width - Px(Space2) * 2) / 3;
            if (HavenKit.Button("Inspect", ButtonKind.Ghost, new(third, 0))) command = new(VisitAction.Inspect, proposal.Id);
            ImGui.SameLine(0, Px(Space2)); if (HavenKit.Button("Approve", ButtonKind.Secondary, new(third, 0))) command = new(VisitAction.Approve, proposal.Id);
            ImGui.SameLine(0, Px(Space2)); if (HavenKit.Button("Decline", ButtonKind.Danger, new(third, 0))) command = new(VisitAction.Decline, proposal.Id);
            ImGui.PopID();
        }
        ImGui.EndDisabled();
        if (!model.CanReview) HavenKit.Caption("Publish or discard your own draft, and finish placing, before reviewing.");
    }
}
