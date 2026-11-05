using System.Numerics;
using Dalamud.Bindings.ImGui;
using Haven.Uld;

namespace Haven.Games;

// Haven's Gold Saucer minigames: each draws the game's own windows from their layouts (UldView) and plays them the
// way the Gold Saucer does, locally. Nothing reaches the server; no MGP is spent or won.

/// <summary>One frame's input for a minigame: the pointer (in the same screen pixels the windows are drawn at), and the
/// confirm/cancel keys or buttons pressed this frame.</summary>
public readonly record struct MiniGameInput(Vector2 Mouse, bool Pressed, bool Released, bool Held, bool Confirm, bool Cancel,
    Vector2 Move = default, float Seconds = 1 / 60f);

/// <summary>The game-side effects a minigame asks for; the plugin plays them natively, the preview ignores them.</summary>
public interface IMiniGameEffects
{
    /// <summary>A UI sound effect, by the game's own sound effect id (UIGlobals.PlaySoundEffect).</summary>
    void Sound(uint effect);
    /// <summary>A sound file played once (a minigame's one-shot sounds), optionally one entry of a multi-sound bank.</summary>
    void SoundFile(string path, int index = 0);
    /// <summary>The local player's animation (ActionTimeline rows), e.g. a punch or a hammer swing; 0 stops.</summary>
    void Animate(ushort timeline, ushort loop = 0);
    /// <summary>The machine's own timeline, by its name in the furnishing's layout (a hit reaction, lights).</summary>
    void Machine(string timeline);
    /// <summary>Background music by BGM row while the game plays; 0 puts the home's music back.</summary>
    void Music(ushort bgm);
    /// <summary>Background music by orchestrion roll (its own file, music/ffxiv/Orchestrion/BGM_ORCH_###.scd); 0 puts the home's music back.</summary>
    void Track(uint roll);
}

/// <summary>A minigame drawn from game layouts.</summary>
public abstract class MiniGame
{
    protected MiniGame(IMiniGameEffects effects) => Effects = effects;
    protected IMiniGameEffects Effects { get; }
    /// <summary>The windows drawn, back to front, each at its screen position.</summary>
    public List<(UldView View, Vector2 Position)> Windows { get; } = [];
    /// <summary>The game has finished and its windows can close.</summary>
    public bool Done { get; protected set; }
    public abstract string Name { get; }
    /// <summary>Lays the windows out for this screen size and drawing scale.</summary>
    public abstract void Layout(Vector2 screen, float scale);
    public abstract void Update(MiniGameInput input);
    /// <summary>Runs once however the game ends (finished, left, the home left, unloading): puts music and poses back.</summary>
    public virtual void Stop() { }

    /// <summary>Draws the windows; positions are from the screen's corner, at offset (the main viewport's position).</summary>
    public virtual void Draw(ImDrawListPtr draw, float scale, Vector2 offset = default)
    {
        foreach (var (view, position) in Windows) view.Draw(draw, offset + position, scale);
    }
    /// <summary>The screen area the windows cover, which takes the mouse from the game while the minigame is open.</summary>
    public virtual (Vector2 Min, Vector2 Max) Bounds(float scale)
    {
        if (Windows.Count == 0) return default;
        var min = new Vector2(float.MaxValue); var max = new Vector2(float.MinValue);
        foreach (var (view, position) in Windows)
        {
            if (!view.Root.Visible) continue;
            min = Vector2.Min(min, position); max = Vector2.Max(max, position + view.Root.Size * scale);
        }
        return min.X > max.X ? default : (min, max);
    }

    /// <summary>Where a layout's widget goes on screen: its authored alignment and offset, as the game places addons.</summary>
    protected static Vector2 Place(UldView view, Vector2 screen, float scale)
    {
        var size = view.Root.Size * scale; var offset = view.Document.WidgetPosition * scale;
        var align = (int)view.Document.Alignment;
        var x = (align % 3) switch { 0 => offset.X, 1 => (screen.X - size.X) / 2, _ => screen.X - size.X - offset.X };
        var y = (align / 3) switch { 0 => offset.Y, 1 => (screen.Y - size.Y) / 2, _ => screen.Y - size.Y - Math.Max(0, screen.Y - offset.Y - size.Y) };
        return new(MathF.Round(x), MathF.Round(y));
    }
}
