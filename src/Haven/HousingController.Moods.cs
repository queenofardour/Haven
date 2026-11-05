using Haven.Core;

namespace Haven;

// Moods (HomeMoods): the room's brightness, weather, time of day, weather heard outside and optionally its music,
// saved under a name and switched with one click. They live in the home's interior, so they travel and stage like a
// change of wallpaper; a mood's music is set through the music controls, which only the home's owner holds.
internal sealed partial class HousingController
{
    public IReadOnlyList<HomeMood> Moods => active?.Interior.Moods ?? [];
    /// <summary>A time of day being dragged in the Room panel (shown, not saved), or null.</summary>
    public int? TimePreview { get; set; }
    /// <summary>Whether weather heard outside can play on this PC.</summary>
    public bool OutsideAvailable => engine.HousingOutsideAvailable;
    /// <summary>How loud the weather heard outside plays on this PC.</summary>
    public float OutsideVolume { get => engine.HousingOutsideVolume; set => engine.HousingOutsideVolume = value; }

    public void SetAtmosphere(uint? weather, int? timeOfDay, string? outside)
    {
        if (active == null) return;
        SetInterior(active.Interior with { Weather = weather, TimeOfDay = timeOfDay, Outside = outside });
    }

    public void SaveMood(string name, bool music)
    {
        if (active == null) return;
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("A mood needs a name.");
        var moods = Moods;
        if (moods.Count >= HomeMoods.MaxMoods) throw new InvalidOperationException($"A home keeps up to {HomeMoods.MaxMoods} moods. Delete one to save another.");
        var mood = HomeMoods.Capture(active, HomeMoods.UniqueName(moods, name), music);
        SetInterior(active.Interior with { Moods = [.. moods, mood] });
        Status = $"Saved the mood \"{mood.Name}\".";
    }

    public void ApplyMood(Guid id)
    {
        if (active == null) return;
        var mood = Moods.FirstOrDefault(m => m.Id == id) ?? throw new InvalidOperationException("That mood is no longer saved.");
        SetInterior(HomeMoods.Apply(active.Interior, mood));
        if (mood.Music is { } music)
        {
            if (CanControlMusic) SetMusic(music with { StartedAtUnixMs = music.Playing ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : 0 });
            else { Status = $"\"{mood.Name}\" is on. Its music is the owner's to change."; return; }
        }
        Status = $"\"{mood.Name}\" is on.";
    }

    /// <summary>Saves the room as it is now into an existing mood (keeping whether it carries music).</summary>
    public void UpdateMood(Guid id)
    {
        if (active == null) return;
        var mood = Moods.FirstOrDefault(m => m.Id == id) ?? throw new InvalidOperationException("That mood is no longer saved.");
        var updated = HomeMoods.Capture(active, mood.Name, mood.Music != null) with { Id = mood.Id };
        SetInterior(active.Interior with { Moods = [.. Moods.Select(m => m.Id == id ? updated : m)] });
        Status = $"\"{mood.Name}\" now keeps the room as it is.";
    }

    public void RenameMood(Guid id, string name)
    {
        if (active == null) return;
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("A mood needs a name.");
        var unique = HomeMoods.UniqueName(Moods, name, id);
        SetInterior(active.Interior with { Moods = [.. Moods.Select(m => m.Id == id ? m with { Name = unique } : m)] });
    }

    public void DeleteMood(Guid id)
    {
        if (active == null) return;
        var left = Moods.Where(m => m.Id != id).ToArray();
        SetInterior(active.Interior with { Moods = left.Length == 0 ? null : left });
    }
}
