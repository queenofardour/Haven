using Haven.Core;
using System.Text.Json;

namespace Haven;

internal sealed partial class HousingController
{
    private HostedDraft? sharedDraft;
    private readonly HouseStore draftStore, draftBaseStore;
    private readonly Dictionary<Guid, House> submittedDrafts = [];
    private readonly Dictionary<Guid, EditProposal> editProposals = [];
    private House? acknowledgedDraft;
    private bool editBusy;
    private DateTimeOffset editDeadline;
    private House? stagedBaseline, stagingSnapshot;
    private House? stageCheckedWorking, stageCheckedPublished;
    private Guid? stagingId;
    private DateTimeOffset nextStage;
    private int stageFailures, publishFailures;
    private StagedFurniture[] stagedFurniture = [];
    private StagedImported[] stagedImported = [];
    private House? stagedScene;
    public IReadOnlyList<StagedFurniture> StagedFurniture => stagedFurniture;
    public IReadOnlyList<StagedImported> StagedImported => stagedImported;
    public bool AutoStaging => IsSharedVisit && !OwnsActiveHouse && engine.HousingRelay.Editing?.Protocol >= 2;
    // The owner's own changes while hosting publish by themselves, as a guest's stage: 0.3 s after an edit, at most
    // every 1.6 s, one batch at a time, never mid-placement or while a conflict waits for the owner. (An explicit
    // Publish step lost edits and raced a picture still being prepared.)
    public bool AutoPublishing => IsSharedVisit && OwnsActiveHouse && engine.HousingRelay.Editing?.Protocol >= 2;
    private DateTimeOffset nextAutoPublish;
    public bool CanReview => OwnsActiveHouse && engine.HousingRelay.Editing?.Protocol >= 2 && engine.HousingRelay.IsConnected && engine.HousingRelay.HousingSceneReady && !HasSharedDraft && !IsPlacing && !editBusy;
    public bool HasSharedDraft => sharedDraft?.Dirty == true;
    public string? DraftConflict => sharedDraft?.Conflict;
    public string EditStatus { get; private set; } = "";
    public bool EditBusy => editBusy;
    public bool CanSaveShared => engine.HousingRelay.IsConnected && engine.HousingRelay.HousingSceneReady && CanFurnish && HasSharedDraft && !IsPlacing && !editBusy && DraftConflict == null;
    public string SaveLabel => editBusy ? "Saving…" : OwnsActiveHouse ? "Publish changes" : "Submit to owner";
    public IReadOnlyCollection<EditProposal> EditProposals => editProposals.Values;
    public bool HasArchivedDraft { get; private set; }
    private void ClearSharedEditing()
    {
        if (sharedDraft?.Dirty == true) ArchiveDraft();
        sharedDraft = null; acknowledgedDraft = null; editBusy = false;
        submittedDrafts.Clear(); editProposals.Clear(); EditStatus = ""; HasArchivedDraft = false;
        stagedBaseline = stagingSnapshot = stagedScene = null; stagingId = null; stagedFurniture = []; stagedImported = [];
        stageCheckedWorking = stageCheckedPublished = null;
        stageFailures = 0;
    }
    private void ArchiveDraft()
    {
        if (sharedDraft == null) return;
        draftBaseStore.Save(sharedDraft.Origin); draftStore.Save(sharedDraft.Working);
        HasArchivedDraft = sharedDraft.Dirty;
    }
    private void OnEditingRecovered() => Run(() =>
    {
        editBusy = false;
        stagedBaseline = null; stagingId = null;
        stageFailures = 0;
        stageCheckedWorking = null;
        if (HasSharedDraft) EditStatus = "Connection restored. Staged changes will resume automatically.";
    });
    private void OnEditing(EditingState access) => Run(() =>
    {
        foreach (var id in editProposals.Keys.Except(access.Pending ?? []).ToArray()) editProposals.Remove(id);
        RefreshStagedFurniture();
        if (active != null && !access.CanEdit(engine.HousingRelay.LocalContentId) && sharedDraft?.Dirty == true)
        {
            ArchiveDraft(); CancelPlacement(); sharedDraft.Discard(); active = sharedDraft.Working;
            undo.Clear(); redo.Clear(); needsRender = true;
            EditStatus = "Decorating access ended. Your draft is backed up locally; restore it when invited again.";
        }
        if (!access.CanEdit(engine.HousingRelay.LocalContentId))
        { stagedBaseline = stagingSnapshot = null; stagingId = null; stageCheckedWorking = null; }
    });
    private static string Clean(string? text, int max) => SafeText.Clean(text, max);
    private void OnProposal(EditProposal proposal) => Run(() =>
    {
        if (engine.HousingRelay.Editing?.Owner != engine.HousingRelay.LocalContentId) return;
        HostedEditing.Validate(proposal.Batch);
        // A collaborator's batch is previewed every frame, so it must describe a valid home on its own: an unknown
        // item or dye would otherwise fail the renderer over and over.
        if (sharedDraft != null)
        {
            try { var proposed = HostedEditing.Apply(sharedDraft.Published, proposal.Batch, overwriteConflicts: true); HouseFormat.Validate(proposed); Catalogue.Validate(proposed); }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or KeyNotFoundException or ArgumentException or JsonException or IndexOutOfRangeException)
            { log.Warning("[EDIT] A staged change from {Name} was not valid here and was ignored: {Message}", Clean(proposal.Name, 32), ex.Message); return; }
        }
        var paths = proposal.Batch.Changes.Select(c => c.Path).ToArray();
        foreach (var old in editProposals.Values.Where(p => p.Author == proposal.Author && p.Batch.Changes.Any(c => paths.Any(n => HostedEditing.Overlaps(c.Path, n)))).ToArray()) editProposals.Remove(old.Batch.Id);
        if (!editProposals.ContainsKey(proposal.Batch.Id)
            && (editProposals.Count >= HostedEditing.MaxStagedPerHome || editProposals.Values.Count(p => p.Author == proposal.Author) >= HostedEditing.MaxStagedPerAuthor))
        { log.Warning("[EDIT] Too many staged changes are waiting; a new one from {Name} was ignored.", Clean(proposal.Name, 32)); return; }
        proposal = proposal with { Name = Clean(proposal.Name, 32) };
        editProposals[proposal.Batch.Id] = proposal;
        RefreshStagedFurniture();
        EditStatus = proposal.Name + " staged changes · blue previews are awaiting your approval.";
    });
    private void OnEditApplied(EditApplied applied) => Run(() =>
    {
        if (applied.Author == engine.HousingRelay.LocalContentId && submittedDrafts.Remove(applied.Batch.Id, out var saved)) { acknowledgedDraft = saved; publishFailures = 0; }
        if (sharedDraft != null)
            foreach (var contribution in applied.Contributions ?? [])
                if (contribution.Author == engine.HousingRelay.LocalContentId)
                    acknowledgedDraft = HostedEditing.Apply(acknowledgedDraft ?? sharedDraft.Origin, contribution.Batch, overwriteConflicts: true);
    });
    private void OnEditResult(EditResult result) => Run(() =>
    {
        if (result.Outcome == "staged" && result.Id == stagingId)
        { stagedBaseline = stagingSnapshot; stagingSnapshot = null; stagingId = null; stageFailures = 0; }
        else if (result.Outcome == "error")
        { stagingId = null; stagingSnapshot = null; stagedBaseline = null; stageCheckedWorking = null; nextStage = DateTimeOffset.UtcNow.AddSeconds(Math.Min(60, 4 * Math.Pow(2, Math.Min(stageFailures++, 4)))); }
        editBusy = false; EditStatus = result.Message;
        // The owner's own publish refused: said in the log, and tried again more slowly (it was retried every 1.6 s,
        // silently, when every publish of a home was refused).
        if (result.Outcome == "error" && submittedDrafts.ContainsKey(result.Id))
        {
            log.Warning("[PUBLISH] the relay refused the owner's changes: {Message}", result.Message);
            nextAutoPublish = DateTimeOffset.UtcNow.AddSeconds(Math.Min(60, 4 * Math.Pow(2, Math.Min(publishFailures++, 4))));
        }
        if (result.Rejected != null && sharedDraft != null)
        {
            if (stagedBaseline != null) stagedBaseline = HostedEditing.Reject(stagedBaseline, sharedDraft.Published, result.Rejected);
            sharedDraft.Reject(result.Rejected); active = sharedDraft.Working;
            needsRender = true; ArchiveDraft();
        }
        if (result.Outcome == "error") _ = engine.HousingRelay.RequestSharedScene();
        if (result.Outcome is "rejected" or "withdrawn") submittedDrafts.Remove(result.Id);
    });
    private void ReceiveSharedHouse(House house)
    {
        RequireHousing(house.TerritoryId); Catalogue.Validate(house);
        if (sharedDraft != null && sharedDraft.Published.Id != house.Id) throw new InvalidDataException("A gathering cannot replace the home identity.");
        var before = active;
        if (sharedDraft == null) sharedDraft = new(house);
        else
        {
            if (stagedBaseline != null)
            {
                var baseline = new HostedDraft(sharedDraft.Published); baseline.Change(stagedBaseline); baseline.Receive(house, acknowledgedDraft);
                stagedBaseline = baseline.Conflict == null ? baseline.Working : null;
            }
            sharedDraft.Receive(house, acknowledgedDraft);
        }
        acknowledgedDraft = null;
        publishedScene = new(house);
        active = sharedDraft.Working;
        RefreshStagedFurniture();
        if (!OwnsActiveHouse) visitorHouse = house;
        else { Store.Save(house); Remember(house); }
        if (before == null || !HostedEditing.Same(before, active))
        {
            // Someone else's change (the owner publishes every 1.6 s while decorating) keeps a placement in progress,
            // unless it changed or removed the very piece being moved. Undo history holds whole homes from before the
            // change, so it cannot be kept.
            if (!PlacementSurvives(before, active)) CancelPlacement();
            undo.Clear(); redo.Clear(); needsRender = true;
            runtime.RefreshLighting();
        }
        if (sharedDraft.Dirty) ArchiveDraft();
        else
        {
            try { HasArchivedDraft = File.Exists(draftStore.PathFor(house.Id)) && !HostedEditing.Same(HouseStore.Read(draftStore.PathFor(house.Id)), house); }
            catch (Exception ex) { log.Warning(ex, "Local decorating backup could not be read."); HasArchivedDraft = false; }
        }
        if (sharedDraft.Conflict != null) EditStatus = sharedDraft.Conflict;
    }
    private bool PlacementSurvives(House? before, House? after)
    {
        if (placement == null || importedPlacement != null || before == null || after == null) return false;
        if (!movingExisting) return true;
        var old = before.Furnishings.FirstOrDefault(f => f.Id == placement.Id);
        var now = after.Furnishings.FirstOrDefault(f => f.Id == placement.Id);
        return old != null && now != null && JsonSerializer.Serialize(old) == JsonSerializer.Serialize(now);
    }
    public void SaveSharedChanges()
    {
        if (AutoStaging) return;
        if (!CanSaveShared || sharedDraft == null) throw new InvalidOperationException("Finish placing objects and resolve any draft conflict before saving.");
        var batch = sharedDraft.Batch();
        _ = HostedEditing.Pack(batch); // Reject locally before marking a save in flight.
        if (submittedDrafts.Count >= 32) submittedDrafts.Clear();
        submittedDrafts[batch.Id] = sharedDraft.Working;
        editBusy = true; editDeadline = DateTimeOffset.UtcNow.AddSeconds(20);
        EditStatus = OwnsActiveHouse ? "Publishing saved changes…" : "Sending proposal to the home owner…";
        _ = engine.HousingRelay.SubmitEdits(batch);
    }
    public void DiscardSharedDraft()
    {
        if (sharedDraft == null || editBusy) return;
        _ = engine.HousingRelay.WithdrawEdits();
        ArchiveDraft(); CancelPlacement(); sharedDraft.Discard(); active = sharedDraft.Working;
        undo.Clear(); redo.Clear(); needsRender = true; runtime.RefreshLighting();
        stagedBaseline = sharedDraft.Published; stagingSnapshot = null; stagingId = null;
        EditStatus = "Shared layout restored. The discarded draft is backed up locally.";
    }
    public void RestoreSharedDraft()
    {
        if (!CanFurnish || sharedDraft == null || !HasArchivedDraft || HasSharedDraft || editBusy) return;
        var saved = HouseStore.Read(draftStore.PathFor(sharedDraft.Published.Id));
        var origin = HouseStore.Read(draftBaseStore.PathFor(saved.Id));
        // A draft kept by 0.67.0 may hold what Haven's server can't take yet (a world set's sound volume): set aside.
        var held = saved.Furnishings.Count(f => NeedsNewerServer(f) && !(origin.Furnishings.FirstOrDefault(o => o.Id == f.Id) is { } before && SameForServer(before, f)));
        if (held > 0) saved = saved with { Furnishings = [.. saved.Furnishings.Where(f => !NeedsNewerServer(f) || origin.Furnishings.Any(o => o.Id == f.Id && SameForServer(o, f)))
            .Concat(origin.Furnishings.Where(o => NeedsNewerServer(o) && saved.Furnishings.Any(f => f.Id == o.Id && !SameForServer(o, f))))] };
        sharedDraft = new(origin); sharedDraft.Change(saved); sharedDraft.Receive(active!);
        active = sharedDraft.Working; needsRender = true;
        EditStatus = held > 0 ? $"Local draft restored, without {held} world effect, sound or sound volume gatherings can't carry yet. Review it before saving." : "Local draft restored. Review it before saving.";
    }
    public void KeepDraftValues()
    {
        if (!CanFurnish || !OwnsActiveHouse || sharedDraft == null || editBusy) return;
        sharedDraft.KeepLocalChanges(); Catalogue.Validate(sharedDraft.Working); Catalogue.BuildStage(sharedDraft.Working);
        active = sharedDraft.Working; needsRender = true; ArchiveDraft(); EditStatus = "Your draft values are selected. Publish explicitly to share them.";
    }
    public void SetDecorators(bool enabled, ulong[] editors)
    {
        if (!OwnsActiveHouse || !CanFurnish || !engine.HousingRelay.HousingSceneReady || !engine.HousingRelay.IsConnected) throw new InvalidOperationException("Only the original home owner can choose decorators.");
        _ = engine.HousingRelay.SetEditing(new(enabled, editors));
    }
    public void ReviewProposal(Guid id, bool accept)
    {
        if (!OwnsActiveHouse || !engine.HousingRelay.IsConnected || !engine.HousingRelay.HousingSceneReady || editBusy) throw new InvalidOperationException("Wait for the original owner's review connection to be ready.");
        if (accept)
        {
            if (HasSharedDraft || IsPlacing || editBusy) throw new InvalidOperationException("Save or discard your own draft before approving this proposal.");
            var candidate = HostedEditing.Apply(sharedDraft!.Published, editProposals[id].Batch);
            Catalogue.Validate(candidate); Catalogue.BuildStage(candidate);
        }
        editBusy = true; editDeadline = DateTimeOffset.UtcNow.AddSeconds(20);
        _ = engine.HousingRelay.ReviewEdits(new EditDecision(id, accept));
    }
    public void ReviewAllProposals(bool accept = true)
    {
        if (!CanReview || editProposals.Count == 0) return;
        var house = sharedDraft!.Published;
        if (accept)
        {
            foreach (var proposal in editProposals.Values) house = HostedEditing.Apply(house, proposal.Batch);
            Catalogue.Validate(house); Catalogue.BuildStage(house);
        }
        editBusy = true; editDeadline = DateTimeOffset.UtcNow.AddSeconds(20);
        _ = engine.HousingRelay.ReviewStaged(new EditReview(editProposals.Keys.ToArray(), accept));
    }
    private void RefreshStagedFurniture()
    {
        if (!OwnsActiveHouse || sharedDraft == null) { stagedFurniture = []; stagedImported = []; stagedScene = null; return; }
        var previewScene = active ?? sharedDraft.Working;
        List<StagedFurniture> previews = [];
        foreach (var proposal in editProposals.Values)
        foreach (var change in proposal.Batch.Changes.Where(c => c.Path[0] == "furnishings"))
        {
            var value = change.AfterExists ? change.After : change.Before;
            if (value == null) continue;
            try
            {
                var item = value.Value.Deserialize<Furnishing>(HostedEditing.Json)!;
                // Checked as a piece of this home (a deletion's "before" reaches here unchecked by the relay): a
                // malformed one would throw from every frame's drawing.
                HouseFormat.Validate(previewScene with { Furnishings = [item] });
                Catalogue.Get(item); Catalogue.Prefab(item);
                previews.Add(new(proposal.Batch.Id, proposal.Name, item, !change.AfterExists));
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or KeyNotFoundException or ArgumentException or JsonException or NullReferenceException)
            { log.Warning("[EDIT] A staged preview could not be shown: {Message}", ex.Message); }
        }
        stagedFurniture = previews.ToArray();
        List<StagedImported> importedPreviews = [];
        foreach (var proposal in editProposals.Values)
        {
            var sceneChanges = proposal.Batch with { Changes = proposal.Batch.Changes.Where(c => c.Path[0] != "furnishings").ToArray() };
            if (sceneChanges.Changes.Length == 0) continue;
            try
            {
                var previous = previewScene;
                previewScene = HostedEditing.Apply(previewScene, sceneChanges);
                if (sceneChanges.Changes.Any(c => c.Path[0] == "stage"))
                {
                    var after = Imported.Objects(previewScene.Stage);
                    var before = Imported.Objects(previous.Stage);
                    foreach (var item in after.Concat(before.Where(b => !after.Any(a => a.Definition.Key == b.Definition.Key))))
                    {
                        if (item.Definition.Type == "Group") continue;
                        var path = new[] { "stage" }.Concat(item.Definition.Path.SelectMany(p => new[] { "Objects", p })).ToArray();
                        if (sceneChanges.Changes.Any(c => HostedEditing.Overlaps(c.Path, path)))
                            importedPreviews.Add(new(proposal.Batch.Id, proposal.Name, item, !after.Any(a => a.Definition.Key == item.Definition.Key)));
                    }
                }
            }
            catch (InvalidOperationException) { continue; } // A conflicting proposal remains available for rejection.
        }
        stagedImported = importedPreviews.ToArray();
        stagedScene = previewScene with { Stage = ImportedScene.PreviewStage(previewScene.Stage, (active ?? sharedDraft.Working).Stage, stagedImported) };
        needsRender = true;
    }
    private void UpdateAutoPublishing()
    {
        var now = DateTimeOffset.UtcNow;
        if (!AutoPublishing || !CanSaveShared || now < nextAutoPublish) return;
        nextAutoPublish = now.AddMilliseconds(1600);
        log.Information("[PUBLISH] publishing the owner's changes automatically.");
        // A batch refused before sending (too large, say) is reported, then tried again only after 30 s.
        try { SaveSharedChanges(); }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException) { nextAutoPublish = now.AddSeconds(30); throw; }
    }

    private void UpdateAutoStaging()
    {
        if (!AutoStaging || sharedDraft == null || !CanFurnish || IsPlacing || sharedDraft.Conflict != null
            || !engine.HousingRelay.IsConnected || !engine.HousingRelay.HousingSceneReady) return;
        var now = DateTimeOffset.UtcNow;
        var owner = engine.HousingRelay.HousingMembers.FirstOrDefault(p => p.Value.ContentId == engine.HousingRelay.Editing?.Owner);
        if (owner.Key != null && engine.HousingRelay.PeerConnections.TryGetValue(owner.Key, out var online) && !online) return;
        if (stagingId != null)
        {
            if (now > editDeadline) { stagingId = null; stagedBaseline = null; stageCheckedWorking = null; nextStage = now.AddSeconds(4); }
            return;
        }
        if (now < nextStage) return;
        if (ReferenceEquals(stageCheckedWorking, sharedDraft.Working) && ReferenceEquals(stageCheckedPublished, sharedDraft.Published)) return;
        var batch = HostedEditing.Stage(sharedDraft.Published, stagedBaseline ?? sharedDraft.Published, sharedDraft.Working);
        stageCheckedWorking = sharedDraft.Working; stageCheckedPublished = sharedDraft.Published;
        if (batch.Changes.Length == 0) return;
        _ = HostedEditing.Pack(batch);
        stagingId = batch.Id; stagingSnapshot = sharedDraft.Working;
        nextStage = now.AddMilliseconds(1600); editDeadline = now.AddSeconds(20);
        EditStatus = "Staging changes for the owner…";
        _ = engine.HousingRelay.StageEdits(batch);
    }
}

internal sealed record StagedFurniture(Guid Proposal, string Author, Furnishing Item, bool Deleted);
internal sealed record StagedImported(Guid Proposal, string Author, ImportedObject Item, bool Deleted);
