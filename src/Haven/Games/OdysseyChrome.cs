using System.Numerics;
using Dalamud.Bindings.ImGui;
using Haven.Core;
using Haven.Core.Odyssey;
using Haven.Uld;

namespace Haven.Games;

/// <summary>A rectangle in a screen's layout pixels (the Odyssey lays its screens out at scale 1 and draws them scaled).</summary>
public readonly record struct OdRect(Vector2 Min, Vector2 Size)
{
    public Vector2 Max => Min + Size;
    public Vector2 Centre => Min + Size / 2;
    public float X => Min.X;
    public float Y => Min.Y;
    public float Width => Size.X;
    public float Height => Size.Y;
    public bool Contains(Vector2 p) => p.X >= Min.X && p.Y >= Min.Y && p.X < Max.X && p.Y < Max.Y;
    public static OdRect At(float x, float y, float w, float h) => new(new(x, y), new(w, h));
    public OdRect Inset(float by) => new(Min + new Vector2(by), Size - new Vector2(by * 2));
}

/// <summary>
/// The Odyssey's drawing kit for every screen outside a duel (0.52.3): Haven's own refined-modern look (HavenTokens) drawn
/// with ImGui in Inter, at the size each screen is zoomed to, so it stays sharp at any resolution. Until 0.52.3 these
/// were the game's TripleTriadResult window and buttons stretched to each screen, with text in the game's bitmap fonts,
/// which looked blurry and low-resolution. Cards are still the game's own (OdysseyCardSprite); duels are the game's board.
/// </summary>
public sealed class OdysseyChrome
{
    private const float ButtonHeight = 30;
    private readonly IUldHost host;

    public OdysseyChrome(IUldHost host) => this.host = host;

    /// <summary>Nothing animates in the kit; kept for the screens' update loop.</summary>
    public void Update(float seconds) { }

    // ---- Colours (ImGui ABGR): Haven's tokens, plus the Odyssey's gold and its rarity and Materia hues ----
    // White, Grey and Dark are HavenTokens.Text, TextSecondary and TextTertiary (at 60 %), as constants for default arguments.
    public const uint White = 0xFFF5F1EE, Grey = 0xFFB2A49A, Dark = 0x99AB9C92;
    public const uint Gold = 0xFF7CCDF0, DimGold = 0xFF4E8CB4, Green = 0xFF78DC78, Blue = 0xFFFAAA6E,
        Purple = 0xFFE678C8, Red = 0xFF6464F0, Yellow = 0xFF50D2F0, Edge = 0xE0000000;
    private static uint Surface(Vector4 colour, float alpha = 1) => ImGui.ColorConvertFloat4ToU32(colour with { W = colour.W * alpha });
    public static uint RarityColour(Rarity rarity) => rarity switch { Rarity.Uncommon => Green, Rarity.Rare => Blue, Rarity.Legendary => 0xFF50B4F0, _ => White };
    public static uint MateriaTint(MateriaColour colour) => colour switch
    {
        MateriaColour.Green => Green, MateriaColour.Yellow => Yellow, MateriaColour.Blue => Blue, MateriaColour.Purple => Purple, _ => Red,
    };
    public static uint Alpha(uint colour, float alpha) => colour & 0x00FFFFFF | (uint)(Math.Clamp(alpha, 0, 1) * (colour >> 24)) << 24;

    // ---- Text in Inter ----
    /// <summary>Text styles, named after the game fonts the screens first used: Axis is body text (Inter Regular); the others are headings (Inter SemiBold).</summary>
    public const byte Axis = 0, Miedinger = 1, Trump = 3, Jupiter = 4;
    private ImFontPtr? Font(byte style, float size) => host.Interface(style != Axis, size);
    /// <summary>Whether the typeface is ready: until it is, a screen's text would measure as nothing, so only its panel is drawn.</summary>
    public bool Ready => host.Interface(false, 14) != null && host.Interface(true, 24) != null;

    public Vector2 Measure(byte font, float size, string text, float wrap = 0)
    {
        if (text.Length == 0 || Font(font, size) is not { } f) return Vector2.Zero;
        // Measured in the font at its own size (times the global font scale, which CalcTextSize applies), then scaled to the size drawn.
        var factor = size / Math.Max(1f, f.FontSize * ImGui.GetIO().FontGlobalScale);
        ImGui.PushFont(f); var measured = ImGui.CalcTextSize(text, false, wrap > 0 ? wrap / factor : -1f) * factor; ImGui.PopFont();
        return measured;
    }

    /// <summary>
    /// Draws text at a screen point in a style at a screen size, optionally wrapped at a width and aligned (0 left,
    /// 1 centre, 2 right) within it. Returns the text's size. (Edge is ignored: the panels are dark and the text is sharp.)
    /// </summary>
    public Vector2 Text(ImDrawListPtr draw, Vector2 at, string text, byte font, float size, uint colour, float wrap = 0, int align = 0, bool edge = true)
    {
        if (text.Length == 0 || Font(font, size) is not { } f) return Vector2.Zero;
        var measured = Measure(font, size, text, wrap);
        if (align > 0 && wrap > 0) at.X += align == 1 ? (wrap - measured.X) / 2 : wrap - measured.X;
        at = new(MathF.Round(at.X), MathF.Round(at.Y));
        draw.AddText(f, size, at, colour, text, wrap);
        return measured;
    }

    // ---- Buttons ----
    public enum ButtonState { Normal, Hover, Pressed, Disabled }

    /// <summary>A button in Haven's look at a screen point, width in layout pixels: primary in the accent, the rest raised.</summary>
    public void Button(ImDrawListPtr draw, Vector2 at, float width, float scale, string label, ButtonState state, bool primary = false)
    {
        var size = new Vector2(width, ButtonHeight) * scale; var rounding = 8 * scale;
        var (fill, ink, edge) = (state, primary) switch
        {
            (ButtonState.Disabled, _) => (HavenTokens.Card, HavenTokens.TextTertiary with { W = .6f }, HavenTokens.Border),
            (ButtonState.Pressed, true) => (HavenTokens.AccentPressed, HavenTokens.OnAccent, Vector4.Zero),
            (ButtonState.Hover, true) => (HavenTokens.AccentHover, HavenTokens.OnAccent, Vector4.Zero),
            (_, true) => (HavenTokens.Accent, HavenTokens.OnAccent, Vector4.Zero),
            (ButtonState.Pressed, false) => (HavenTokens.Divider, HavenTokens.Text, HavenTokens.Divider),
            (ButtonState.Hover, false) => (HavenTokens.Hover, HavenTokens.Text, HavenTokens.Divider),
            _ => (HavenTokens.Raised, HavenTokens.Text, HavenTokens.Divider),
        };
        draw.AddRectFilled(at, at + size, Surface(fill), rounding);
        if (edge.W > 0) draw.AddRect(at, at + size, Surface(edge), rounding, ImDrawFlags.None, Math.Max(1, scale));
        var textSize = 15 * scale;
        var measured = Measure(Miedinger, textSize, label);
        Text(draw, at + (size - measured) / 2, label, Miedinger, textSize, Surface(ink));
    }
    public static float ButtonHeightPx => ButtonHeight;

    // ---- Panels ----
    /// <summary>A screen's frame: Haven's window surface with its heading centred across the top and a rule under it.</summary>
    public void Frame(ImDrawListPtr draw, Vector2 at, Vector2 size, float scale, string heading, float headingSize = 24)
    {
        var rounding = HavenTokens.RadiusWindow * scale;
        // A soft shadow, the surface and its border.
        for (var i = 3; i >= 1; i--)
            draw.AddRectFilled(at + new Vector2(0, 2 * i * scale), at + size + new Vector2(0, 2 * i * scale), Surface(HavenTokens.Shadow, .25f), rounding + i * scale);
        draw.AddRectFilled(at, at + size, Surface(HavenTokens.Window, .97f), rounding);
        draw.AddRect(at, at + size, Surface(HavenTokens.BorderStrong), rounding, ImDrawFlags.None, Math.Max(1, scale));
        if (heading.Length == 0) return;
        var measured = Measure(Miedinger, headingSize * scale, heading);
        Text(draw, at + new Vector2((size.X - measured.X) / 2, 16 * scale), heading, Miedinger, headingSize * scale, Gold);
        var rule = MathF.Round(at.Y + 16 * scale + measured.Y + 10 * scale);
        draw.AddLine(new(at.X + 24 * scale, rule), new(at.X + size.X - 24 * scale, rule), Surface(HavenTokens.Divider), Math.Max(1, scale));
    }

    /// <summary>A panel inside a screen (a group of cards, a list, an offer), at a screen point, size in layout pixels.</summary>
    public void Box(ImDrawListPtr draw, Vector2 at, Vector2 size, float scale, float alpha = 1)
    {
        var max = at + size * scale; var rounding = HavenTokens.RadiusCard * scale;
        draw.AddRectFilled(at, max, Surface(HavenTokens.Card, alpha), rounding);
        draw.AddRect(at, max, Surface(HavenTokens.Border, alpha), rounding, ImDrawFlags.None, Math.Max(1, scale));
    }

    /// <summary>A pill: a small rounded label (rules, Materia, perks) at a screen point; returns its width in screen pixels.</summary>
    public float Pill(ImDrawListPtr draw, Vector2 at, string text, float scale, uint colour, uint fill = 0xB4101010, float size = 12)
    {
        var padding = 7 * scale;
        var measured = Measure(Axis, size * scale, text);
        var box = new Vector2(measured.X + padding * 2, MathF.Round((size + 6) * scale));
        draw.AddRectFilled(at, at + box, fill, box.Y / 2);
        draw.AddRect(at, at + box, Alpha(colour, .8f), box.Y / 2, ImDrawFlags.None, Math.Max(1, scale));
        Text(draw, at + new Vector2(padding, (box.Y - measured.Y) / 2), text, Axis, size * scale, colour);
        return box.X;
    }

    /// <summary>A game icon (an item's) filling a screen rectangle, or a tinted placeholder when there is none.</summary>
    public void Icon(ImDrawListPtr draw, uint icon, Vector2 min, Vector2 max, uint tint = 0xFFFFFFFF)
    {
        if (icon != 0 && host.Icon(icon) is { } image) { draw.AddImage(image.Texture, min, max, Vector2.Zero, Vector2.One, tint); return; }
        var centre = (min + max) / 2; var radius = (max.X - min.X) * .36f;
        draw.AddCircleFilled(centre, radius, Alpha(tint, .85f));
        draw.AddCircle(centre, radius, Edge, 0, 2);
    }

    /// <summary>A tooltip: a framed box of wrapped text beside a screen point, kept inside a screen area.</summary>
    public void Tooltip(ImDrawListPtr draw, Vector2 at, string title, string? text, float scale, Vector2 screenMin, Vector2 screenMax, uint titleColour = Gold)
    {
        var width = 300 * scale; var padding = 12 * scale;
        var titleSize = Measure(Miedinger, 15 * scale, title, width);
        var bodySize = text is { Length: > 0 } ? Measure(Axis, 14 * scale, text, width) : Vector2.Zero;
        var size = new Vector2(Math.Max(titleSize.X, bodySize.X) + padding * 2, titleSize.Y + (bodySize.Y > 0 ? bodySize.Y + 6 * scale : 0) + padding * 2);
        var min = at + new Vector2(16, 18) * scale;
        if (min.X + size.X > screenMax.X) min.X = Math.Max(screenMin.X, at.X - size.X - 8 * scale);
        if (min.Y + size.Y > screenMax.Y) min.Y = Math.Max(screenMin.Y, screenMax.Y - size.Y);
        draw.AddRectFilled(min, min + size, Surface(HavenTokens.Overlay), 10 * scale);
        draw.AddRect(min, min + size, Surface(HavenTokens.Divider), 10 * scale, ImDrawFlags.None, Math.Max(1, scale));
        Text(draw, min + new Vector2(padding, padding), title, Miedinger, 15 * scale, titleColour, width);
        if (bodySize.Y > 0) Text(draw, min + new Vector2(padding, padding + titleSize.Y + 6 * scale), text!, Axis, 14 * scale, White, width);
    }

    // ---- Names for the engine's rules and perks ----
    // A card's perk may be two traits from one scroll (Bastion: Aegis and Cornerstone): names and texts join each trait's.
    private static IEnumerable<TriadPerk> Traits(TriadPerk perk) => Enum.GetValues<TriadPerk>().Where(p => p != TriadPerk.None && (perk & p) == p);
    public static string PerkName(TriadPerk perk) => string.Join(" + ", Traits(perk).Select(TraitName));
    public static string PerkText(TriadPerk perk) => string.Join(" ", Traits(perk).Select(TraitText));
    private static string TraitName(TriadPerk perk) => perk switch
    {
        TriadPerk.Steadfast => "Steadfast", TriadPerk.Aegis => "Aegis", TriadPerk.AceKiller => "Ace Killer", TriadPerk.Avalanche => "Avalanche",
        TriadPerk.Reckless => "Reckless", TriadPerk.Guardian => "Guardian", TriadPerk.Cornerstone => "Cornerstone", TriadPerk.Crown => "Crown",
        TriadPerk.Wild => "Wild", TriadPerk.Vanguard => "Vanguard", TriadPerk.Desperate => "Desperate", TriadPerk.Keystone => "Keystone",
        TriadPerk.Frostbite => "Frostbite", TriadPerk.Rally => "Rally", TriadPerk.Drain => "Drain", TriadPerk.Contrary => "Contrary",
        TriadPerk.Masked => "Masked", TriadPerk.Mimic => "Mimic", _ => "",
    };
    private static string TraitText(TriadPerk perk) => perk switch
    {
        TriadPerk.Steadfast => "A higher number can never take this card (Same, Plus and combos still can).",
        TriadPerk.Aegis => "Same, Plus and combos can never take this card (a higher number still can).",
        TriadPerk.AceKiller => "This card's 1s take an A, as under Fallen Ace, in every duel.",
        TriadPerk.Avalanche => "Every card this card takes goes on to take its weaker neighbours, like a combo.",
        TriadPerk.Reckless => "+2 on every side when you play it, -1 while it sits on the board.",
        TriadPerk.Guardian => "+2 on every side while it sits on the board, -1 when you play it.",
        TriadPerk.Cornerstone => "+2 on every side while it is in a corner.",
        TriadPerk.Crown => "If you own this card when the duel ends, it counts as two cards.",
        TriadPerk.Wild => "Counts as Primal, Scion, Society and Garlean at once.",
        TriadPerk.Vanguard => "+2 on every side if it is the first card played by its side in a duel, for the rest of the duel.",
        TriadPerk.Desperate => "+2 on every side if it is played while its side is behind, for the rest of the duel.",
        TriadPerk.Keystone => "+2 on every side while it is in the centre.",
        TriadPerk.Frostbite => "When it is played, enemy cards beside it lose 1 on every side, for good.",
        TriadPerk.Rally => "When it is played, its side's cards beside it gain +1 on every side, for good.",
        TriadPerk.Drain => "When it is played, it gains +1 on every side for each card that placement takes, for good.",
        TriadPerk.Contrary => "Takes with lower numbers instead of higher (higher under Reverse); it defends as normal.",
        TriadPerk.Masked => "Counts as one star lower for the deck limits, but plays -1 on every side.",
        TriadPerk.Mimic => "When it is played, each side facing a card copies that card's number if it is higher, for the rest of the duel.",
        _ => "",
    };
    public static string RuleText(TriadRule rule) => rule switch
    {
        TriadRule.AllOpen => "Both hands are shown.", TriadRule.ThreeOpen => "Three cards of each hand are shown.",
        TriadRule.Same => "A card with two or more equal touching numbers takes them all.", TriadRule.Plus => "Two touching pairs with the same sum take both.",
        TriadRule.SuddenDeath => "A draw is played again with the cards each side owns.", TriadRule.Reverse => "Lower numbers take.",
        TriadRule.FallenAce => "A 1 takes an A.", TriadRule.Ascension => "Typed cards gain +1 per card of their type on the board.",
        TriadRule.Descension => "Typed cards lose 1 per card of their type on the board.", TriadRule.Order => "Cards must be played in hand order.",
        TriadRule.Chaos => "The card to play is chosen for you.", TriadRule.Swap => "One card of each hand is swapped before play.",
        TriadRule.Roulette => "The rules are rolled at the start.", TriadRule.Random => "Hands are dealt at random.", TriadRule.Draft => "Hands are drafted.", _ => "",
    };
    public static string TypeName(int type) => type switch { 1 => "Primal", 2 => "Scion", 3 => "Society", 4 => "Garlean", _ => "" };
}
