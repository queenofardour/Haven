using Haven.Core;
using Haven.Core.World;

namespace Haven;

// Placing the world's effects and ambient sounds: the drawer's sound preview.
internal sealed partial class HousingController
{
    // One preview at a time, heard from no position (the system player the weather heard outside uses), stopped after a
    // while (longer for a cycle of random variants, which can start with a gap), on another preview, on closing the
    // drawer, on leaving and on unload. Its slot goes back to the game when stopped (HousingAmbientSound.Stop).
    private HavenNative.HousingAmbientSound? preview;
    private long previewUntil;
    /// <summary>The sound being previewed, or null.</summary>
    public string? PreviewingSound { get; private set; }

    /// <summary>Plays a catalogue sound for a few seconds so the player can hear it before placing it; again stops it.</summary>
    public void PreviewSound(string path)
    {
        if (PreviewingSound == path) { StopPreview(); return; }
        StopPreview();
        if (!Haven.Core.World.WorldAmbient.IsSoundPath(path) || Ambient.Find(path) is not { } entry) return;
        preview ??= new HavenNative.HousingAmbientSound(log);
        if (!preview.Play(path, .8f)) { Status = "The game didn't play that sound. Try again in a moment."; return; }
        PreviewingSound = path;
        previewUntil = Environment.TickCount64 + (entry.Repeat == AmbientRepeat.Cycle ? 12_000 : 8_000);
    }

    public void StopPreview()
    {
        preview?.Stop(400);
        PreviewingSound = null;
    }

    /// <summary>
    /// Whether gatherings can carry world effects and sounds. The hosted relay checks every home it's given with the house
    /// format it was built with; relays from 0.47.0 (housing protocol 41) know these pieces. Set false, they stay in Just
    /// me visits.
    /// </summary>
    public const bool AmbientShareable = true;

    /// <summary>
    /// What Haven's hosted server can't take yet (it checks every home with the format it was built with, housing
    /// protocol 40): a world effect or sound, or a sound volume on any building piece (a world set's).
    /// </summary>
    public static bool NeedsNewerServer(Furnishing f) => !AmbientShareable && f.BuildingPart is { } path && (BuildingParts.IsAmbient(path) || f.SoundVolume != null);

    /// <summary>Whether a piece's sound volume can be changed here (not a world set's in a gathering, for now).</summary>
    public bool SoundVolumeEditable(Furnishing item) => item.BuildingPart == null || AmbientShareable || !IsSharedVisit;

    private static void RequireAmbientShareable(House house)
    {
        if (house.Furnishings.Any(NeedsNewerServer))
            throw new InvalidOperationException("This home has world effects or sounds, or a world set whose sound volume was changed, which gatherings can't carry yet (Haven's server needs an update first). Visit it Just me, or take them out (or set those volumes back to 100%) to host it.");
    }

    // A change in a gathering mustn't bring what the server can't take: the whole draft would be refused, and nothing
    // after it published (saved groups, copies and pastes reach here without the drawer's own check). Checked piece by
    // piece: one the home already held as it is (moved, turned) passes.
    private void RequireShareableChange(House house)
    {
        if (!IsSharedVisit || active == null) return;
        foreach (var piece in house.Furnishings)
            if (NeedsNewerServer(piece) && !(active.Furnishings.FirstOrDefault(f => f.Id == piece.Id) is { } before && SameForServer(before, piece)))
                throw new InvalidOperationException("World effects and sounds, and a world set's sound volume, can be placed and set in Just me visits for now: gatherings can carry them once Haven's server is updated.");
    }

    /// <summary>
    /// Whether a world effect piece is left out because the home already shows as many world effects as Haven draws at
    /// once (<see cref="AmbientBudget.MaxWorldEffects"/>, the world sets' and these together); false while unknown.
    /// </summary>
    public bool EffectOverBudget(Furnishing item) => rendered && HavenEffects && engine.HousingAmbientEffects && placement?.Id != item.Id
        && Catalogue.Prefab(item).WorldEffects.Length > 0 && !effects.Draws($"wf-{item.Id:N}-") && effects.Count >= AmbientBudget.MaxWorldEffects;

    // Whether a piece is, for the server's format, what it was: the same path and sound volume.
    private static bool SameForServer(Furnishing before, Furnishing after) => before.BuildingPart == after.BuildingPart && before.SoundVolume == after.SoundVolume;

    // The catalogue read after a home was shown: its effects and sounds are drawn again (in place and tinted), and the
    // room list renamed.
    private bool ambientWasReady;
    public int AmbientVersion { get; private set; }
    private void UpdateAmbientReady()
    {
        if (ambientWasReady || !Ambient.Ready) return;
        ambientWasReady = true; AmbientVersion++;
        if (rendered) needsRender = true;
    }

    // Each frame: a world effect being placed or moved is drawn where it would go, at its scale, so a big or offset one
    // can be set down where it's wanted (its marker alone says little).
    // Its own effects are moved without starting again (a drag would never get past the start); once the piece has
    // stood still a moment they start again where it is (some effects only take a new place on a start).
    private string previewPlace = "";
    private long previewStill;
    private bool previewRestarted;
    private void UpdatePlacementEffect(bool ready)
    {
        if (!ready || active == null || !HavenEffects || !engine.HousingAmbientEffects || placement is not { } leader) { if (previewEffect.Count > 0) previewEffect.Clear(); return; }
        List<AmbientEmitter> wanted = [];
        var house = FurnitureCatalogue.HouseMatrix(active);
        foreach (var piece in FollowerPreviews.Prepend(leader))
        {
            if (piece.BuildingPart is not { } path || !Haven.Core.World.WorldAmbient.IsEffectPath(path)) continue;
            var prefab = Catalogue.Prefab(piece); var world = FurnitureCatalogue.ItemMatrix(piece) * house;
            for (var i = 0; i < prefab.WorldEffects.Length; i++)
                wanted.Add(new($"placing-{piece.Id:N}-{i}", prefab.WorldEffects[i].Path, prefab.WorldEffects[i].Transform * world, prefab.WorldEffects[i].Color));
        }
        if (wanted.Count == 0) { if (previewEffect.Count > 0) previewEffect.Clear(); return; }
        var place = string.Join(";", wanted.Select(w => w.World.ToString()));
        var now = Environment.TickCount64;
        if (place != previewPlace) { previewPlace = place; previewStill = now; previewRestarted = false; }
        var restart = !previewRestarted && now - previewStill > 250;
        if (restart) { previewRestarted = true; previewEffect.Clear(); }
        previewEffect.Show(wanted, restartOnMove: false);
    }

    // Each frame: a preview ends on its own, or when the visit does.
    private void UpdatePreview()
    {
        if (PreviewingSound != null && (Environment.TickCount64 >= previewUntil || active == null || !engine.HousingSessionActive)) StopPreview();
    }
}
