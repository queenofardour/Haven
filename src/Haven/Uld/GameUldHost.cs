using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;

namespace Haven.Uld;

/// <summary>
/// The game's own assets for drawn layouts: textures (the high-resolution _hr1 copies when present, as the game's UI
/// uses them), icons, the game fonts at the size each text is drawn, and Addon sheet texts.
/// </summary>
internal sealed class GameUldHost(IDataManager data, ITextureProvider textures, IFontAtlas fonts) : IUldHost, IDisposable
{
    private readonly Dictionary<string, (string Path, float Scale)> resolved = [];
    private readonly Dictionary<(GameFontFamily, int), IFontHandle> handles = [];
    private readonly Dictionary<uint, string> texts = [];
    private readonly List<ILockedImFont> locks = [];
    private int lockedFrame = -1;

    public (ImTextureID Texture, Vector2 Size, float Scale)? Texture(string path)
    {
        if (!resolved.TryGetValue(path, out var source))
        {
            var high = path.Replace(".tex", "_hr1.tex", StringComparison.Ordinal);
            resolved[path] = source = data.FileExists(high) ? (high, 2f) : (path, 1f);
        }
        return textures.GetFromGame(source.Path).GetWrapOrDefault() is { } wrap ? (wrap.Handle, new Vector2(wrap.Width, wrap.Height), source.Scale) : null;
    }

    // Language icons (banners, result images) first in the client's language, then the plain icon.
    public (ImTextureID Texture, Vector2 Size)? Icon(uint icon)
    {
        var shared = textures.TryGetFromGameIcon(new GameIconLookup(icon, false, true, data.Language), out var localized) ? localized : textures.GetFromGameIcon(new GameIconLookup(icon, false, true));
        return shared.GetWrapOrDefault() is { } wrap ? (wrap.Handle, new Vector2(wrap.Width, wrap.Height)) : null;
    }

    // The sizes each game font ships at (GameFontFamilyAndSize), in points (a point is 4/3 of a pixel).
    private static readonly float[] AxisSizes = [9.6f, 12, 14, 18, 36], MiedingerSizes = [10, 12, 14, 18, 36], TrumpSizes = [18.4f, 23, 34, 68],
        JupiterSizes = [16, 20, 23, 46, 90];
    private static readonly (GameFontFamily, float[])[] Natives =
        [(GameFontFamily.Axis, AxisSizes), (GameFontFamily.MiedingerMid, MiedingerSizes), (GameFontFamily.TrumpGothic, TrumpSizes), (GameFontFamily.Jupiter, JupiterSizes)];
    // A game font's handle at one of its native sizes (points), created once.
    private IFontHandle GameFont(GameFontFamily family, float native)
    {
        var key = (family, (int)MathF.Round(native * 10));
        if (!handles.TryGetValue(key, out var handle)) handles[key] = handle = fonts.NewGameFontHandle(new GameFontStyle(family, native * UldView.PixelsPerPoint));
        return handle;
    }

    public ImFontPtr? Font(byte font, float size)
    {
        var family = font switch { 1 or 2 => GameFontFamily.MiedingerMid, 3 => GameFontFamily.TrumpGothic, 4 or 5 => GameFontFamily.Jupiter, _ => GameFontFamily.Axis };
        // The game's fonts are bitmaps drawn at a few native sizes; built at any other size they are stretched, and
        // stretched up they blur. So a text gets the smallest native size at least its own (a little under is let
        // through), built 1:1, and is drawn at its size from there: the same or scaled down, never blown up.
        var natives = family switch
        {
            GameFontFamily.MiedingerMid => MiedingerSizes, GameFontFamily.TrumpGothic => TrumpSizes, GameFontFamily.Jupiter => JupiterSizes, _ => AxisSizes,
        };
        // Sizes asked for are in pixels.
        var native = natives.FirstOrDefault(n => n * UldView.PixelsPerPoint * 1.08f >= size, natives[^1]);
        var handle = GameFont(family, native);
        if (!handle.Available) return null;
        // A font is used while it stays locked: locks are kept until the next frame's first request.
        var frame = ImGui.GetFrameCount();
        if (frame != lockedFrame) { foreach (var held in locks) held.Dispose(); locks.Clear(); lockedFrame = frame; }
        var locked = handle.Lock();
        locks.Add(locked);
        return locked.ImFont;
    }

    // Inter for what Haven draws itself (the Odyssey's screens), on a fixed ladder of pixel sizes built once, when the
    // plugin starts: a text is drawn from the smallest size at least its own, scaled down a little, so it stays sharp.
    // 0.52.3 built each exact size the moment a screen asked for it; every screen has its own zoom, so each one opened
    // with fonts still building in the background (missing text, pills and wraps measured as nothing) and then snapped
    // into place.
    private static readonly int[] InterfaceSizes = [12, 15, 19, 24, 30, 38, 48, 60, 76];
    private Dictionary<(bool, int), IFontHandle>? interfaceHandles;

    /// <summary>Creates the fonts Haven's minigame screens use up front, so none is built while a screen is showing.</summary>
    public void Prepare()
    {
        interfaceHandles ??= InterfaceSizes.SelectMany(px => new[] { false, true }.Select(strong => (strong, px)))
            .ToDictionary(key => key, key => Plugin.InterFont(fonts, key.strong, key.px));
        foreach (var (family, natives) in Natives)
            foreach (var native in natives) GameFont(family, native);
    }

    public ImFontPtr? Interface(bool strong, float size)
    {
        Prepare();
        var px = InterfaceSizes.FirstOrDefault(n => n * 1.03f >= size, InterfaceSizes[^1]);
        var handle = interfaceHandles![(strong, px)];
        if (!handle.Available) return null;
        var frame = ImGui.GetFrameCount();
        if (frame != lockedFrame) { foreach (var held in locks) held.Dispose(); locks.Clear(); lockedFrame = frame; }
        var locked = handle.Lock();
        locks.Add(locked);
        return locked.ImFont;
    }

    private readonly Dictionary<(string, UldPart, Vector3), Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap?> added = [];
    public (ImTextureID Texture, Vector2 Size)? Added(string path, UldPart part, Vector3 add)
    {
        var key = (path, part, add);
        if (!added.TryGetValue(key, out var wrap))
        {
            Texture(path);
            var source = resolved[path];
            var file = data.GetFile<Lumina.Data.Files.TexFile>(source.Path);
            if (file != null)
            {
                var pixels = IUldHost.AddColour(file.ImageData, file.Header.Width, file.Header.Height, part, source.Scale, add, out var w, out var h);
                wrap = textures.CreateFromRaw(RawImageSpecification.Bgra32(w, h), pixels, "Haven.Uld.Added");
            }
            added[key] = wrap;
        }
        return wrap is { } made ? (made.Handle, new Vector2(made.Width, made.Height)) : null;
    }

    private readonly Dictionary<string, UldDocument> layouts = [];
    public UldDocument Layout(string name)
    {
        if (layouts.TryGetValue(name, out var known)) return known;
        var file = data.GetFile<Lumina.Data.Files.UldFile>($"ui/uld/{name}.uld") ?? throw new InvalidDataException("The game has no window layout " + name + ".");
        return layouts[name] = UldDocument.Load(name, file);
    }

    // UIColor rows hold 0xRRGGBBAA; ImGui wants 0xAABBGGRR.
    private static uint Abgr(uint rgba) => (rgba & 0xFF) << 24 | (rgba >> 8 & 0xFF) << 16 | (rgba >> 16 & 0xFF) << 8 | rgba >> 24;
    public uint UiColor(uint row, bool edge) => data.GetExcelSheet<Lumina.Excel.Sheets.UIColor>().GetRowOrDefault(row) is { } color ? Abgr(color.Dark) : 0xFFFFFFFF;

    public string Text(uint addonRow)
    {
        if (texts.TryGetValue(addonRow, out var text)) return text;
        return texts[addonRow] = data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRowOrDefault(addonRow)?.Text.ExtractText() ?? "";
    }

    public void Dispose()
    {
        foreach (var held in locks) held.Dispose();
        locks.Clear();
        foreach (var handle in handles.Values) handle.Dispose();
        handles.Clear();
        if (interfaceHandles != null) foreach (var handle in interfaceHandles.Values) handle.Dispose();
        interfaceHandles = null;
        foreach (var wrap in added.Values) wrap?.Dispose();
        added.Clear();
    }
}
