using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Haven.Native;

namespace Haven.Games;

/// <summary>
/// A minigame's effects in the game: UI sounds by the game's own ids, the player's pose (a base idle, as the Gold Saucer
/// sets for the card game) and actions, and music by BGM row in place of the home's. One-shot sound files and machine
/// timelines aren't played yet.
/// </summary>
internal sealed unsafe class GameEffects(NativeArcade native, IObjectTable objects, IDataManager data, Action<string?> music, IPluginLog log) : IMiniGameEffects
{
    // Sounds asked for while the UI draws are played on the next framework update, on the game's own thread, as the
    // game's agents play them. Each id is logged the first time, to tell a silent sound from one never asked for.
    private readonly Queue<uint> sounds = new();
    private readonly HashSet<uint> logged = [];
    public void Sound(uint effect) { lock (sounds) sounds.Enqueue(effect); }
    public void Flush()
    {
        while (true)
        {
            uint effect;
            lock (sounds) { if (!sounds.TryDequeue(out effect)) return; }
            if (logged.Add(effect)) log.Information($"[GAMES] UI sound {effect} (player {(native.CanPlaySounds ? "found" : "missing")}).");
            native.Sound(effect);
        }
    }
    public void SoundFile(string path, int index = 0) { }
    public void Machine(string timeline) { }

    /// <summary>A loop sets the player's idle pose (0 back to normal); a one-shot plays once.</summary>
    public void Animate(ushort timeline, ushort loop = 0)
    {
        if (objects.LocalPlayer is not { } player) return;
        var character = (Character*)player.Address;
        if (loop != 0 || timeline == 0) native.BaseIdle(character, loop != 0 ? loop : (ushort)3);
        else character->Timeline.PlayActionTimeline(timeline);
    }

    // An orchestrion roll, or a BGM row offset by OdysseyContent.Bgm (a track that isn't a roll).
    public void Track(uint roll)
    {
        if (roll >= Core.Odyssey.OdysseyContent.Bgm) { Music((ushort)(roll - Core.Odyssey.OdysseyContent.Bgm)); return; }
        music(roll == 0 ? null : data.GetExcelSheet<Lumina.Excel.Sheets.OrchestrionPath>().GetRowOrDefault(roll)?.File.ExtractText() is { Length: > 0 } file ? file : null);
    }

    public void Music(ushort bgm) =>
        music(bgm == 0 ? null : data.GetExcelSheet<Lumina.Excel.Sheets.BGM>().GetRowOrDefault(bgm)?.File.ExtractText() is { Length: > 0 } file ? file : null);
}
