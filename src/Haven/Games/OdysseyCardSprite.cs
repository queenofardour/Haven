using System.Numerics;
using Dalamud.Bindings.ImGui;
using Haven.Core;
using Haven.Uld;

namespace Haven.Games;

/// <summary>
/// The game's own Triple Triad card, drawn anywhere: one hand slot of TripleTriad.uld (its art, numbers, stars, type
/// icon and blue or red tint), everything else in that layout hidden. Each Draw fills the slot with a card and draws
/// it at a screen position and size, so one sprite draws any number of cards in a frame.
/// </summary>
public sealed class OdysseyCardSprite
{
    // A hand slot of the match window (the DragDrop component the match fills), and its size in layout pixels.
    private const uint Slot = 121;
    private readonly UldView view;
    private readonly UldElement slot;
    private readonly Vector2 offset;
    /// <summary>A card's size in layout pixels at scale 1.</summary>
    public Vector2 Size { get; }

    public OdysseyCardSprite(IUldHost host)
    {
        view = new UldView(host.Layout("TripleTriad"), host);
        slot = view.Node(Slot) ?? throw new InvalidOperationException("TripleTriad has no card slot.");
        Hide(view.Root);
        // Only the slot and the nodes above it show.
        Show(slot);
        for (var up = slot.Parent; up != null; up = up.Parent) up.Visible = true;
        // The slot's place in the window, so it can be drawn anywhere by moving the window under it.
        for (var at = slot; at != null && at != view.Root; at = at.Parent) offset += at.Position;
        Size = slot.Size;
    }

    private static void Hide(UldElement e) { e.Visible = false; foreach (var c in e.Children) Hide(c); }
    private static void Show(UldElement e) { e.Visible = true; foreach (var c in e.Children) Show(c); }

    /// <summary>Advances the card's own animations (the tint and star parts are set by playing labels).</summary>
    public void Update(float seconds) => view.Update(seconds);

    /// <summary>
    /// Draws a card with its top-left corner at a screen point, scale layout pixels per screen pixel. Numbers are the
    /// card's own unless given (top, right, bottom, left, as the card plays with its bonuses); badge shows a +n/-n
    /// change in the corner (0 hides it). Face down shows the back; dim darkens it (a card that can't be chosen).
    /// </summary>
    public void Draw(ImDrawListPtr draw, TriadCard card, Vector2 at, float scale, bool blue = true, bool faceUp = true, int[]? numbers = null,
        int badge = 0, bool dim = false, bool highlight = false, float hover = 0)
    {
        Show(slot);
        var icon = slot.Find(3)?.Children.FirstOrDefault();
        if (icon?.Find(2) is { } back) back.Visible = !faceUp;
        if (icon?.Find(3) is { } faceComponent) faceComponent.Visible = faceUp;
        slot.Multiply = dim ? new Vector3(.5f) : Vector3.One;
        // The slot's frame: full for a chosen card, part-way for a hovered one (as the match shows its hand).
        if (slot.Find(2) is { } frame) frame.Alpha = highlight ? 1 : hover;
        var root = icon?.Find(3)?.Children.FirstOrDefault();
        if (faceUp && root != null)
        {
            if (root.Find(20) is { } art) art.Icon = card.Icon;
            if (root.Find(21) is { } colour) view.Play(blue ? (ushort)101 : (ushort)102, colour);
            var sides = new[] { (14u, 0), (15u, 2), (16u, 3), (17u, 1) };
            foreach (var (id, d) in sides)
                if (root.Find(id) is { } text) { var v = numbers?[d] ?? card.Side(d); text.Text = v >= 10 ? "A" : Math.Max(1, v).ToString(); }
            if (root.Find(2) is { } stars) TripleTriadMatch.Stars(stars, card.Stars);
            if (root.Find(8) is { } type) { type.Visible = card.Type is >= 1 and <= 4; if (type.Visible) view.Play((ushort)(100 + card.Type), type); }
            if (root.Find(10) is { } shown) { shown.Visible = badge != 0; if (shown.Find(11) is { } mod) mod.Text = badge > 0 ? $"+{badge}" : badge.ToString(); }
        }
        // Played labels set their parts at once; a zero-length update applies them before drawing.
        view.Update(0);
        view.Draw(draw, at - offset * scale, scale);
    }

    /// <summary>Whether a screen point is over a card drawn at a point and scale.</summary>
    public bool Contains(Vector2 point, Vector2 at, float scale) =>
        point.X >= at.X && point.Y >= at.Y && point.X < at.X + Size.X * scale && point.Y < at.Y + Size.Y * scale;
}
