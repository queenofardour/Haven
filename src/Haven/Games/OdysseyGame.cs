using System.Numerics;
using Dalamud.Bindings.ImGui;
using Haven.Core;
using Haven.Core.Odyssey;
using Haven.Uld;

namespace Haven.Games;

/// <summary>How the Odyssey opens: at its title, continuing the saved run, with a new run at a Crystal Rank, or at the records.</summary>
public enum OdysseyStart { Title, Continue, NewRun, Records }

/// <summary>
/// What the Odyssey needs from Haven: the director (the plugin loads and saves it), every card, the player's name, rule
/// names, an item icon by its FFXIV item name (0 when unknown), and how to open.
/// </summary>
public sealed record OdysseySetup(Odyssey Director, IReadOnlyList<TriadCard> Cards, string PlayerName, Func<TriadRule, string> RuleName, Func<string, uint> IconOf,
    OdysseyStart Start = OdysseyStart.Title, int Rank = 0);

/// <summary>
/// The Crystal Odyssey: a Triple Triad roguelite played as one minigame over the director's screens (design §7). Every
/// screen outside a duel is drawn by Haven in its own look (OdysseyChrome: panels, buttons and Inter text) with the
/// game's own cards (OdysseyCardSprite); duels are a TripleTriadMatch child (the game's board) with the run's decks,
/// Materia and music. The screens are laid out in layout pixels and drawn scaled; widgets are rebuilt every frame from the
/// director's views (OdysseyGame.Screens.cs), so the screen never disagrees with the run.
/// </summary>
public sealed partial class OdysseyGame : MiniGame
{
    // Sounds: the Triple Triad bank (pick, place, return, invalid, coin, a card turning, the turn passing) and its jingles.
    private const uint PickSound = 2001, PlaceSound = 2002, ReturnSound = 2003, InvalidSound = 2015, CoinSound = 2007, FlipSound = 2009, PageSound = 2013,
        OpenJingle = 1000053, WinJingle = 1000055;
    private const float Margin = 24, ButtonY = 52;

    private readonly OdysseySetup setup;
    private readonly Odyssey odyssey;
    private readonly IUldHost host;
    private readonly OdysseyChrome chrome;
    private readonly OdysseyCardSprite sprite;
    private readonly List<Widget> widgets = [], modalWidgets = [];
    private Widget? hovered;
    private Vector2 screen, framePos, frameSize = new(1000, 560), lastOffset;
    private float drawScale = 1, uiScale = 1;
    private uint track;
    // The fanfare of the duel just won, until when it plays, and whether this duel's has started.
    private uint fanfare;
    private float fanfareUntil;
    private bool fanfareStarted;
    private bool pressedOnHovered;
    private float age;
    // A screen other than the director's: the title (also where a finished run's summary leads) and the records.
    private enum Mode { Run, Title, Records }
    private Mode mode;
    private OdysseyScreen lastScreen = (OdysseyScreen)(-1);
    // The duel in play, and its tally of the player's captures.
    private TripleTriadMatch? match;
    private DuelTally tally = new();
    // A question over the screen (leave, abandon, confirm an enhancement): its text, buttons and what they do.
    private sealed record Modal(string Title, string Text, string Ok, string Cancel, Action OnOk, Action? OnCancel = null, bool Danger = false);
    private Modal? modal;
    private string? error;
    private float errorAge;

    public OdysseyGame(IMiniGameEffects effects, IUldHost host, OdysseySetup setup) : base(effects)
    {
        this.setup = setup; this.host = host;
        odyssey = setup.Director;
        chrome = new OdysseyChrome(host);
        sprite = new OdysseyCardSprite(host);
        switch (setup.Start)
        {
            case OdysseyStart.NewRun: odyssey.NewRun(Random.Shared.Next(), Math.Clamp(setup.Rank, 0, odyssey.Records.UnlockedRank)); break;
            case OdysseyStart.Title: mode = Mode.Title; break;
            case OdysseyStart.Records: mode = Mode.Records; break;
        }
        // Continuing with nothing to continue (and no finished run's summary to show) opens the title.
        if (setup.Start == OdysseyStart.Continue && !odyssey.CanContinue && odyssey.Screen is not (OdysseyScreen.Victory or OdysseyScreen.GameOver)) mode = Mode.Title;
    }

    public override string Name => "The Crystal Odyssey";
    /// <summary>The director, for the preview and the Board tab.</summary>
    public Odyssey Director => odyssey;
    /// <summary>For the preview: the duel plays itself (both sides) and finishes at once.</summary>
    public bool AutoPlayDuels { get; set; }
    /// <summary>For the preview: the Deck screen opens with this item picked (its target choice showing).</summary>
    public int PreviewPickItem { get; set; } = -1;
    /// <summary>For the preview: the shop opens on selling a card.</summary>
    public bool PreviewSelling { get; set; }

    public override void Layout(Vector2 screen, float scale)
    {
        this.screen = screen; uiScale = scale;
        if (match != null)
        {
            match.Layout(screen, scale);
            Windows.Clear(); Windows.AddRange(match.Windows);
            return;
        }
        frameSize = SizeFor();
        // The screens are laid out for a 1280 x 800 screen and zoomed to the real one (never below the UI scale), so cards,
        // their numbers and text stay as large on a big screen as they look there; text is then built at its drawn size,
        // which is also what keeps the game's fonts sharp. A frame that wouldn't fit shrinks to the screen.
        var zoom = Math.Max(scale, Math.Min(screen.X / 1280, screen.Y / 800));
        drawScale = Math.Min(zoom, Math.Min((screen.X - 16) / frameSize.X, (screen.Y - 16) / frameSize.Y));
        framePos = new(MathF.Round((screen.X - frameSize.X * drawScale) / 2), MathF.Round((screen.Y - frameSize.Y * drawScale) / 2));
        // Haven draws the screens itself (Draw); the game windows list is only the duel's.
        Windows.Clear();
    }

    public override void Update(MiniGameInput input)
    {
        age += input.Seconds;
        if (errorAge > 0) errorAge -= input.Seconds;
        if (match != null) { UpdateDuel(input); return; }
        if (odyssey.Screen == OdysseyScreen.Duel && mode == Mode.Run) { StartDuel(); return; }
        chrome.Update(input.Seconds); sprite.Update(input.Seconds);
        var screenNow = odyssey.Screen;
        if (screenNow != lastScreen) { lastScreen = screenNow; ScreenChanged(); }
        // A won duel's fanfare plays out before the screen's own music.
        Music(age < fanfareUntil ? fanfare : mode == Mode.Run ? ScreenMusic() : OdysseyContent.TitleMusic);
        // The widgets of this frame, composed from the director's views, then the pointer over them.
        var mouse = (input.Mouse - (framePos + lastOffset)) / Math.Max(.01f, drawScale);
        widgets.Clear(); modalWidgets.Clear();
        try { Compose(); if (modal != null) ComposeModal(); }
        catch (Exception e) when (e is not OutOfMemoryException) { Fail(e.Message); }
        var active = modal != null ? modalWidgets : widgets;
        hovered = null;
        for (var i = active.Count - 1; i >= 0; i--)
            if (active[i].Interactive && active[i].Bounds.Contains(mouse)) { hovered = active[i]; break; }
        if (input.Cancel) { Escape(); return; }
        if (input.Confirm)
        {
            // Enter presses the screen's primary button, or its only button; among equal choices (rewards, opponents,
            // an event's answers) nothing is preselected, so Enter picks none of them.
            var enabled = active.OfType<ButtonW>().Where(b => b.Enabled).ToList();
            var primary = enabled.FirstOrDefault(b => b.Primary) ?? (enabled.Count == 1 ? enabled[0] : null);
            if (primary != null) Press(primary);
            return;
        }
        if (input.Pressed) pressedOnHovered = hovered != null;
        if (input.Pressed && hovered != null) Press(hovered);
        if (!input.Held) pressedOnHovered = false;
    }

    private void Press(Widget widget)
    {
        try
        {
            switch (widget)
            {
                case ButtonW button when !button.Enabled: Effects.Sound(InvalidSound); break;
                case ButtonW button: Effects.Sound(button.Sound); button.Click?.Invoke(); break;
                case CardW { Click: { } click } card: Effects.Sound(card.Dim ? InvalidSound : PickSound); click(); break;
                case PillW { Click: { } click }: Effects.Sound(PickSound); click(); break;
                case IconW { Click: { } click }: Effects.Sound(PickSound); click(); break;
                case BoxW { Click: { } click }: Effects.Sound(PickSound); click(); break;
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException) { Fail(e.Message); }
        // A click can open another screen: its frame size and zoom apply from this frame, not the next.
        if (match == null) Layout(screen, uiScale);
    }

    // A refused command (the views should have greyed it out): a line under the heading for a few seconds.
    private void Fail(string message) { error = message; errorAge = 4; Effects.Sound(InvalidSound); }

    // Escape: closes a question, backs out of a sub-choice, or asks to leave (the run is saved as it stands).
    private void Escape()
    {
        if (modal != null) { Effects.Sound(ReturnSound); var cancel = modal.OnCancel; modal = null; cancel?.Invoke(); return; }
        if (Back()) { Effects.Sound(ReturnSound); return; }
        if (mode == Mode.Title || odyssey.Run == null) { Done = true; return; }
        if (mode == Mode.Records) { mode = Mode.Title; Effects.Sound(ReturnSound); return; }
        modal = new("Leave the Odyssey?", "Your run is saved where it stands. Come back to it from the Triple Triad Board.", "Leave", "Keep playing", () => Done = true);
    }

    private void Music(uint roll)
    {
        if (roll == track) return;
        track = roll;
        Effects.Track(roll);
    }

    public override void Stop()
    {
        // A duel already decided whose result window is still up (the game left, an invitation, an unload) counts as it
        // ended: the win's reward or the loss is kept, rather than the duel being dealt again next time.
        var current = match;
        if (current is { Outcome: TriadOutcome.Won or TriadOutcome.Lost or TriadOutcome.Drawn, Done: false }) Settle(current);
        current?.Stop();
        Effects.Track(0);
        Effects.Animate(0, 0);
    }

    // ---- Duels ----

    private void StartDuel()
    {
        var plan = odyssey.Plan();
        tally = new DuelTally();
        fanfareStarted = false; fanfareUntil = 0;
        var opponent = new TriadOpponent(plan.Opponent.Name, plan.Rules);
        var duel = new TriadDuel(plan.Blue, plan.Red, plan.First, plan.BlueEdge, plan.RedEdge, plan.Brain, plan.Music, 45, plan.Seed, plan.LossNote, plan.RouletteSkips);
        match = new TripleTriadMatch(Effects, host, new TriadSetup(setup.Cards, _ => true, setup.PlayerName, opponent, setup.RuleName, null, null, duel))
        {
            Placed = (player, placement, card) => { if (player == TriadPlayer.Blue) tally.Note(card, placement); },
            AutoPlay = AutoPlayDuels,
        };
        track = plan.Music;
        match.Layout(screen, uiScale);
        Windows.Clear(); Windows.AddRange(match.Windows);
    }

    private void UpdateDuel(MiniGameInput input)
    {
        duelPointer = input.Mouse;
        var playing = match!;
        playing.Update(input);
        // The fanfare starts as the win shows (the result window is still up), once.
        if (!fanfareStarted && playing.Finished > 0 && playing.Game.Winner == TriadPlayer.Blue)
        {
            fanfareStarted = true;
            (fanfare, var seconds) = OdysseyContent.Fanfare(Math.Max(1, odyssey.Run?.World ?? 1));
            fanfareUntil = age + seconds;
            Music(fanfare);
        }
        if (!playing.Done) return;
        Settle(playing);
        if (odyssey.Screen == OdysseyScreen.Victory) Effects.Sound(WinJingle);
        Layout(screen, uiScale);
    }

    // The duel's result goes to the run, once.
    private void Settle(TripleTriadMatch playing)
    {
        var outcome = playing.Outcome;
        var game = playing.Game;
        match = null;
        Effects.Animate(0, 0);
        if (age >= fanfareUntil) track = 0;   // a fanfare still playing carries on (Music skips the same track)
        try
        {
            if (outcome == TriadOutcome.Forfeit) odyssey.Forfeit();
            else odyssey.Resolve(tally.Report(game, outcome switch { TriadOutcome.Won => DuelOutcome.Won, TriadOutcome.Lost => DuelOutcome.Lost, _ => DuelOutcome.Drawn }));
        }
        catch (Exception e) when (e is not OutOfMemoryException) { Fail(e.Message); }
    }

    // Over the duel's board: each side's Materia at the end of its name band, perk pills over the cards, and tooltips
    // for what's under the pointer (a rule, a Materia, a card's perk and type), since the board itself explains none.
    private Vector2 duelPointer;
    private void DrawDuelOverlay(ImDrawListPtr draw, float scale)
    {
        var playing = match!;
        if (playing.Prompting || odyssey.Run?.Dealt is not { } dealt) return;
        var tips = new List<(Vector2 Min, Vector2 Max, string Title, string? Text, uint Colour)>();
        if (playing.BoardFrame() is { } board)
        {
            MateriaPanel(draw, board, dealt.BlueMateria, "Your Materia", mine: true, scale, tips);
            MateriaPanel(draw, board, dealt.RedMateria, $"{playing.OpponentName}'s Materia", mine: false, scale, tips);
        }
        foreach (var (card, min, max) in playing.TheirHandSlots().Concat(playing.BoardSlots()))
        {
            if (card is not { Perk: not TriadPerk.None }) continue;
            var name = OdysseyChrome.PerkName(card.Perk);
            var width = chrome.Measure(OdysseyChrome.Axis, 11 * scale, name).X + 14 * scale;
            chrome.Pill(draw, new((min.X + max.X - width) / 2, min.Y + 6 * scale), name, scale, OdysseyChrome.Gold, 0xD0101010, 11);
        }
        foreach (var (rule, rolled, more, min, max) in playing.RuleSlots())
            if (rule is { } shown) tips.Add((min, max, setup.RuleName(shown) + (rolled ? " · rolled by Roulette" : ""), OdysseyChrome.RuleText(shown), OdysseyChrome.Gold));
            else tips.Add((min, max, $"{more.Count} more rules", string.Join("\n", more.Select(r => $"{setup.RuleName(r)}: {OdysseyChrome.RuleText(r)}")), OdysseyChrome.Gold));
        // Cards: yours, theirs once face up, and the board's.
        foreach (var (card, min, max) in playing.MyHandSlots().Concat(playing.TheirHandSlots().Where(s => s.Card != null && playing.IsOpen(s.Card))).Concat(playing.BoardSlots()))
            if (card != null && DuelCardTip(card) is { } text) tips.Add((min, max, $"{card.Name} · {card.Stars} star{(card.Stars == 1 ? "" : "s")}", text, OdysseyChrome.Gold));
        // The last one added under the pointer wins (Materia and rules over the panels behind them).
        foreach (var tip in tips.AsEnumerable().Reverse())
        {
            if (duelPointer.X < tip.Min.X || duelPointer.Y < tip.Min.Y || duelPointer.X >= tip.Max.X || duelPointer.Y >= tip.Max.Y) continue;
            var viewport = ImGui.GetMainViewport();
            chrome.Tooltip(draw, duelPointer, tip.Title, tip.Text, scale, viewport.Pos, viewport.Pos + viewport.Size, tip.Colour);
            break;
        }
    }

    /// <summary>Shows each side's Materia in a strip above the board even where a column beside it fits (the preview's comparison).</summary>
    public static bool MateriaAbove { get; set; }

    // A side's Materia in a small panel of its own outside the board (yours on the left, theirs on the right), each one
    // its icon and name, with its tooltip: a column beside the board, or a row above it where the screen has no room.
    private void MateriaPanel(ImDrawListPtr draw, (Vector2 Min, Vector2 Max) board, IEnumerable<string> keys, string caption, bool mine, float scale,
        List<(Vector2 Min, Vector2 Max, string Title, string? Text, uint Colour)> tips)
    {
        var materia = keys.Select(OdysseyContent.MateriaOf).Where(m => m != null).Select(m => m!).ToList();
        if (materia.Count == 0) return;
        const float Pad = 8, Icon = 22, Gap = 6, Row = 26, Caption = 12, Text = 13, Away = 8;
        var captionSize = chrome.Measure(OdysseyChrome.Miedinger, Caption * scale, caption);
        var names = materia.Select(m => chrome.Measure(OdysseyChrome.Axis, Text * scale, m.Name).X).ToList();
        var item = names.Select(w => Icon * scale + Gap * scale + w).ToList();
        var column = new Vector2(Math.Max(captionSize.X, item.Max()) + Pad * 2 * scale, captionSize.Y + Gap * scale + materia.Count * Row * scale + Pad * 2 * scale - (Row - Icon) * scale);
        var columnAt = new Vector2(mine ? board.Min.X - Away * scale - column.X : board.Max.X + Away * scale, board.Min.Y + (board.Max.Y - board.Min.Y) * .18f);
        var viewport = ImGui.GetMainViewport();
        var beside = !MateriaAbove && columnAt.X >= viewport.Pos.X && columnAt.X + column.X <= viewport.Pos.X + viewport.Size.X;
        var size = beside ? column : new Vector2(captionSize.X + Pad * 2 * scale + item.Sum(w => w + 12 * scale), Row * scale + Pad * 2 * scale - (Row - Icon) * scale);
        var at = beside ? columnAt : new Vector2(mine ? board.Min.X : board.Max.X - size.X, board.Min.Y - Away * scale - size.Y);
        at = new(MathF.Round(at.X), MathF.Round(at.Y));
        chrome.Box(draw, at, size / scale, scale, .94f);
        Vector2 cursor;
        if (beside)
        {
            chrome.Text(draw, at + new Vector2(Pad, Pad) * scale, caption, OdysseyChrome.Miedinger, Caption * scale, OdysseyChrome.Gold);
            cursor = at + new Vector2(Pad * scale, Pad * scale + captionSize.Y + Gap * scale);
        }
        else
        {
            chrome.Text(draw, at + new Vector2(Pad * scale, (size.Y - captionSize.Y) / 2), caption, OdysseyChrome.Miedinger, Caption * scale, OdysseyChrome.Gold);
            cursor = at + new Vector2(Pad * scale + captionSize.X + 12 * scale, Pad * scale);
        }
        for (var i = 0; i < materia.Count; i++)
        {
            var m = materia[i]; var tint = OdysseyChrome.MateriaTint(m.Colour);
            chrome.Icon(draw, setup.IconOf(m.Icon), cursor, cursor + new Vector2(Icon * scale), tint);
            var nameSize = chrome.Measure(OdysseyChrome.Axis, Text * scale, m.Name);
            chrome.Text(draw, cursor + new Vector2((Icon + Gap) * scale, (Icon * scale - nameSize.Y) / 2), m.Name, OdysseyChrome.Axis, Text * scale, tint);
            tips.Add((cursor, cursor + new Vector2(item[i], Icon * scale), $"{m.Name} · {m.Rarity}", m.Description, tint));
            if (beside) cursor.Y += Row * scale; else cursor.X += item[i] + 12 * scale;
        }
    }

    // A duel card's tooltip: its type and perk (what the numbers don't say), or null when there's nothing to explain.
    private static string? DuelCardTip(TriadCard card)
    {
        var lines = new List<string>();
        if (card.Type != 0) lines.Add(OdysseyChrome.TypeName(card.Type));
        if (card.Perk != TriadPerk.None) lines.Add($"{OdysseyChrome.PerkName(card.Perk)}: {OdysseyChrome.PerkText(card.Perk)}");
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    // The frame is drawn at its own zoom, not the UI scale, so the area that takes the mouse is the frame as drawn
    // (during a duel, the match's own windows).
    public override (Vector2 Min, Vector2 Max) Bounds(float scale) =>
        match != null ? base.Bounds(scale) : (framePos, framePos + frameSize * drawScale);

    public override void Draw(ImDrawListPtr draw, float scale, Vector2 offset = default)
    {
        lastOffset = offset;
        if (match is { } playing)
        {
            playing.Draw(draw, scale, offset);
            DrawDuelOverlay(draw, scale);
            return;
        }
        var origin = offset + framePos;
        if (!chrome.Ready) { chrome.Frame(draw, origin, frameSize * drawScale, drawScale, ""); return; }
        chrome.Frame(draw, origin, frameSize * drawScale, drawScale, Heading());
        foreach (var widget in widgets) DrawWidget(draw, widget, origin, drawScale, ReferenceEquals(widget, hovered) && modal == null);
        if (error != null && errorAge > 0)
            chrome.Text(draw, origin + new Vector2(0, 54 * drawScale), error, OdysseyChrome.Axis, 13 * drawScale, OdysseyChrome.Red, frameSize.X * drawScale, 1);
        if (modal != null)
        {
            // The screen dims behind the question, drawn as a smaller frame in the middle.
            draw.AddRectFilled(origin, origin + frameSize * drawScale, 0x90000000);
            var size = modalSize;
            var at = origin + (frameSize - size) / 2 * drawScale;
            chrome.Frame(draw, at, size * drawScale, drawScale, modal.Title, 20);
            foreach (var widget in modalWidgets) DrawWidget(draw, widget, origin, drawScale, ReferenceEquals(widget, hovered));
        }
        if (hovered is { TipTitle: { Length: > 0 } title })
            chrome.Tooltip(draw, origin + hovered.Bounds.Max * drawScale - new Vector2(hovered.Bounds.Width, hovered.Bounds.Height) * drawScale / 2, title, hovered.Tip, drawScale,
                offset, offset + screen, hovered.TipColour);
    }

    // ---- Widgets: what a screen is made of, built every frame in layout pixels from the frame's corner ----

    private abstract record Widget(OdRect Bounds)
    {
        public string? Tip { get; init; }
        public string? TipTitle { get; init; }
        public uint TipColour { get; init; } = OdysseyChrome.Gold;
        public virtual bool Interactive => TipTitle != null;
    }
    private sealed record ButtonW(OdRect Bounds, string Label, Action? Click, bool Enabled = true, bool Primary = false, uint Sound = PlaceSound) : Widget(Bounds)
    {
        public override bool Interactive => true;
    }
    private sealed record CardW(OdRect Bounds, TriadCard Card, float Scale, bool Blue = true, bool FaceUp = true, int Badge = 0, bool Dim = false, bool Selected = false,
        Action? Click = null, string? Caption = null, uint CaptionColour = OdysseyChrome.Grey) : Widget(Bounds)
    {
        public override bool Interactive => Click != null || TipTitle != null;
    }
    private sealed record TextW(Vector2 At, string Text, byte Font, float Size, uint Colour, float Wrap = 0, int Align = 0) : Widget(new OdRect());
    private sealed record PillW(OdRect Bounds, string Text, uint Colour, Action? Click = null, bool Selected = false, float Size = 12) : Widget(Bounds)
    {
        public override bool Interactive => Click != null || TipTitle != null;
    }
    private sealed record IconW(OdRect Bounds, uint Icon, uint Tint = 0xFFFFFFFF, Action? Click = null) : Widget(Bounds)
    {
        public override bool Interactive => Click != null || TipTitle != null;
    }
    private sealed record BoxW(OdRect Bounds, float Alpha = 1, bool Highlight = false, Action? Click = null) : Widget(Bounds)
    {
        public override bool Interactive => Click != null;
    }
    private sealed record RectW(OdRect Bounds, uint Fill, uint Border = 0, float Rounding = 0) : Widget(Bounds);
    private sealed record CircleW(Vector2 Centre, float Radius, uint Fill, uint Border = 0) : Widget(new OdRect());

    private void DrawWidget(ImDrawListPtr draw, Widget widget, Vector2 origin, float scale, bool hover)
    {
        Vector2 S(Vector2 p) => origin + p * scale;
        switch (widget)
        {
            case ButtonW b:
                chrome.Button(draw, S(b.Bounds.Min), b.Bounds.Width, scale, b.Label, !b.Enabled ? OdysseyChrome.ButtonState.Disabled : hover && pressedOnHovered ? OdysseyChrome.ButtonState.Pressed
                    : hover ? OdysseyChrome.ButtonState.Hover : OdysseyChrome.ButtonState.Normal, b.Primary);
                break;
            case CardW c:
                {
                    var at = S(c.Bounds.Min); var cardScale = c.Scale * scale;
                    sprite.Draw(draw, c.Card, at, cardScale, c.Blue, c.FaceUp, null, c.Badge, c.Dim, c.Selected, hover && c.Click != null ? .5f : 0);
                    if (c.FaceUp && c.Card.Perk != TriadPerk.None)
                    {
                        var name = OdysseyChrome.PerkName(c.Card.Perk);
                        var pillSize = Math.Max(9, 11 * c.Scale);
                        var width = chrome.Measure(OdysseyChrome.Axis, pillSize * scale, name).X + 14 * scale;
                        chrome.Pill(draw, new(at.X + (sprite.Size.X * cardScale - width) / 2, at.Y + 8 * cardScale), name, scale, OdysseyChrome.Gold, 0xD0101010, pillSize);
                    }
                    if (c.Caption != null)
                        chrome.Text(draw, at + new Vector2(0, sprite.Size.Y * cardScale + 3 * scale), c.Caption, OdysseyChrome.Axis, 12 * scale, c.CaptionColour, sprite.Size.X * cardScale, 1);
                    break;
                }
            case TextW t:
                chrome.Text(draw, S(t.At), t.Text, t.Font, t.Size * scale, t.Colour, t.Wrap * scale, t.Align);
                break;
            case PillW p:
                chrome.Pill(draw, S(p.Bounds.Min), p.Text, scale, p.Selected || hover && p.Click != null ? OdysseyChrome.White : p.Colour, p.Selected ? 0xD0404040 : hover && p.Click != null ? 0xD0303030 : 0xB4101010, p.Size);
                break;
            case IconW i:
                chrome.Icon(draw, i.Icon, S(i.Bounds.Min), S(i.Bounds.Max), i.Tint);
                if (hover && i.Click != null) draw.AddRect(S(i.Bounds.Min), S(i.Bounds.Max), OdysseyChrome.Gold, 3 * scale, ImDrawFlags.None, Math.Max(1, scale));
                break;
            case BoxW x:
                chrome.Box(draw, S(x.Bounds.Min), x.Bounds.Size, scale, x.Alpha);
                if (x.Highlight || hover && x.Click != null)
                    draw.AddRect(S(x.Bounds.Min) + new Vector2(2 * scale), S(x.Bounds.Max) - new Vector2(2 * scale), x.Highlight ? OdysseyChrome.Gold : OdysseyChrome.DimGold, 4 * scale, ImDrawFlags.None, Math.Max(1, scale));
                break;
            case RectW r:
                draw.AddRectFilled(S(r.Bounds.Min), S(r.Bounds.Max), r.Fill, r.Rounding * scale);
                if (r.Border != 0) draw.AddRect(S(r.Bounds.Min), S(r.Bounds.Max), r.Border, r.Rounding * scale, ImDrawFlags.None, Math.Max(1, scale));
                break;
            case CircleW o:
                draw.AddCircleFilled(S(o.Centre), o.Radius * scale, o.Fill);
                if (o.Border != 0) draw.AddCircle(S(o.Centre), o.Radius * scale, o.Border, 0, Math.Max(1, scale));
                break;
        }
    }

    // ---- Composition helpers ----

    private ButtonW Button(float x, float y, float width, string label, Action? click, bool enabled = true, bool primary = false, string? reason = null, uint sound = PlaceSound)
    {
        var button = new ButtonW(OdRect.At(x, y, width, OdysseyChrome.ButtonHeightPx), label, click, enabled, primary, sound) { TipTitle = !enabled && reason != null ? reason : null, TipColour = OdysseyChrome.Grey };
        widgets.Add(button);
        return button;
    }
    private void Label(float x, float y, string text, byte font = OdysseyChrome.Axis, float size = 14, uint colour = OdysseyChrome.White, float wrap = 0, int align = 0) =>
        widgets.Add(new TextW(new(x, y), text, font, size, colour, wrap, align));
    private float LabelHeight(string text, float size, float wrap) => chrome.Measure(OdysseyChrome.Axis, size, text, wrap).Y;
    private void Box(OdRect bounds, float alpha = 1, bool highlight = false, Action? click = null) => widgets.Add(new BoxW(bounds, alpha, highlight, click));
    private CardW Card(Vector2 at, CardView view, float scale, bool blue = true, Action? click = null, bool selected = false, bool dim = false, string? caption = null, uint captionColour = OdysseyChrome.Grey, string? tip = null)
    {
        var card = new CardW(new(at, sprite.Size * scale), view.Card, scale, blue, true, view.Delta, dim, selected, click, caption, captionColour) { TipTitle = CardTitle(view), Tip = tip ?? CardTip(view) };
        widgets.Add(card);
        return card;
    }
    private static string CardTitle(CardView view) => $"{view.Card.Name} · {view.Card.Stars} star{(view.Card.Stars == 1 ? "" : "s")}";
    private string CardTip(CardView view)
    {
        var lines = new List<string> { $"Power {view.Power:0.00}" };
        // What was done to it: each raised side (from its printed number), a new type, a perk and what it does.
        var sides = new[] { ("Top", view.Base.Top, view.Card.Top), ("Right", view.Base.Right, view.Card.Right), ("Bottom", view.Base.Bottom, view.Card.Bottom), ("Left", view.Base.Left, view.Card.Left) }
            .Where(s => s.Item2 != s.Item3).Select(s => $"{s.Item1} {N(s.Item2)} → {N(s.Item3)}").ToList();
        if (sides.Count > 0) lines.Add("Upgraded: " + string.Join(", ", sides));
        if (view.Card.Type != view.Base.Type)
            lines.Add($"Now {(view.Card.Type == 0 ? "untyped" : OdysseyChrome.TypeName(view.Card.Type))} (was {(view.Base.Type == 0 ? "untyped" : OdysseyChrome.TypeName(view.Base.Type))}): Ascension, Descension and type Materia count it as that.");
        else if (view.Card.Type != 0) lines.Add(OdysseyChrome.TypeName(view.Card.Type));
        if (view.Card.Perk != TriadPerk.None) lines.Add($"{OdysseyChrome.PerkName(view.Card.Perk)}: {OdysseyChrome.PerkText(view.Card.Perk)}");
        if (view.Enhancements > 0)
            lines.Add(view.Enhancements >= OdysseyBalance.MaxEnhancements ? $"Enhancements {view.Enhancements} of {OdysseyBalance.MaxEnhancements}: it takes no more" : $"Enhancements {view.Enhancements} of {OdysseyBalance.MaxEnhancements}");
        if (view.Reason != null) lines.Add(view.Reason);
        return string.Join("\n", lines);
    }
    /// <summary>A row of pills from a point, wrapping within a width; returns the height used.</summary>
    private float Pills(float x, float y, float width, IEnumerable<(string Text, uint Colour, string? TipTitle, string? Tip, Action? Click, bool Selected)> pills, float size = 12)
    {
        float cx = x, cy = y, rowHeight = size + 6;
        foreach (var (text, colour, tipTitle, tip, click, selected) in pills)
        {
            var w = chrome.Measure(OdysseyChrome.Axis, size, text).X + 14;
            if (cx > x && cx + w > x + width) { cx = x; cy += rowHeight + 4; }
            widgets.Add(new PillW(OdRect.At(cx, cy, w, rowHeight), text, colour, click, selected, size) { TipTitle = tipTitle, Tip = tip, TipColour = colour });
            cx += w + 6;
        }
        return cy + rowHeight - y;
    }
    private (string, uint, string?, string?, Action?, bool) RulePill(TriadRule rule, Action? click = null, bool selected = false) =>
        (setup.RuleName(rule), OdysseyChrome.Gold, setup.RuleName(rule), OdysseyChrome.RuleText(rule), click, selected);
    private static (string, uint, string?, string?, Action?, bool) MateriaPill(Materia materia) =>
        (materia.Name, OdysseyChrome.MateriaTint(materia.Colour), $"{materia.Name} · {materia.Rarity}", materia.Description, null, false);
    private void MateriaIcon(float x, float y, float size, Materia materia, Action? click = null) =>
        widgets.Add(new IconW(OdRect.At(x, y, size, size), setup.IconOf(materia.Icon), OdysseyChrome.MateriaTint(materia.Colour), click) { TipTitle = $"{materia.Name} · {materia.Rarity}", Tip = materia.Description, TipColour = OdysseyChrome.MateriaTint(materia.Colour) });
    private void ItemIcon(float x, float y, float size, string name, string? description, string iconName, uint tint = 0xFFFFFFFF, Action? click = null) =>
        widgets.Add(new IconW(OdRect.At(x, y, size, size), setup.IconOf(iconName), tint, click) { TipTitle = name, Tip = description });

    /// <summary>The run's standing, top right of every run screen.</summary>
    private void Status()
    {
        if (odyssey.Run is not { } run || mode != Mode.Run) return;
        var world = OdysseyContent.WorldAt(Math.Max(1, run.World));
        var duel = run.Duel >= OdysseyBalance.BossDuel ? "Boss" : $"Duel {Math.Max(1, run.Duel)} of {OdysseyBalance.DuelsPerWorld}";
        var text = run.World == 0 ? $"{run.Mgp} gil" : $"World {world.Numeral} · {duel} · {run.Mgp} gil" + (run.Rank > 0 ? $" · Crystal Rank {run.Rank}" : "");
        Label(frameSize.X - Margin - 420, 22, text, OdysseyChrome.Axis, 13, OdysseyChrome.Grey, 420, 2);
    }
    /// <summary>Page controls under a grid: the page shown of count, with arrows; returns the page clamped.</summary>
    private int Pager(float x, float y, float width, int page, int pages, Action<int> set)
    {
        page = Math.Clamp(page, 0, Math.Max(0, pages - 1));
        if (pages <= 1) return page;
        var centre = x + width / 2;
        widgets.Add(new ButtonW(OdRect.At(centre - 110, y, 36, OdysseyChrome.ButtonHeightPx), "<", () => set(page - 1), page > 0, false, PageSound));
        Label(centre - 60, y + 6, $"Page {page + 1} of {pages}", OdysseyChrome.Axis, 13, OdysseyChrome.Grey, 120, 1);
        widgets.Add(new ButtonW(OdRect.At(centre + 74, y, 36, OdysseyChrome.ButtonHeightPx), ">", () => set(page + 1), page < pages - 1, false, PageSound));
        return page;
    }
    /// <summary>A paged grid of binder cards (the screen's page); returns the height used (grid plus pager).</summary>
    private float BinderGrid(float x, float y, float width, int rows, float cardScale, IReadOnlyList<CardView> cards, Func<CardView, Action?> click,
        Func<CardView, bool>? selected = null, Func<CardView, bool>? dim = null, Func<CardView, string?>? caption = null)
    {
        var cell = sprite.Size * cardScale; var gap = 8f; var captionRoom = caption != null ? 16 : 0;
        var columns = Math.Max(1, (int)((width + gap) / (cell.X + gap)));
        var perPage = columns * rows;
        var pages = Math.Max(1, (cards.Count + perPage - 1) / perPage);
        page = Math.Clamp(page, 0, pages - 1);
        var gridWidth = columns * cell.X + (columns - 1) * gap;
        var left = x + (width - gridWidth) / 2;
        for (var i = 0; i < perPage; i++)
        {
            var index = page * perPage + i;
            if (index >= cards.Count) break;
            var view = cards[index];
            var at = new Vector2(left + i % columns * (cell.X + gap), y + i / columns * (cell.Y + gap + captionRoom));
            Card(at, view, cardScale, true, click(view), selected?.Invoke(view) ?? false, dim?.Invoke(view) ?? false, caption?.Invoke(view));
        }
        var height = rows * (cell.Y + gap + captionRoom) - gap;
        if (pages > 1) { Pager(x, y + height + 4, width, page, pages, n => page = n); height += OdysseyChrome.ButtonHeightPx + 8; }
        return height;
    }
    private int page;

    private Vector2 modalSize = new(480, 190);
    private void Ask(string title, string text, string ok, Action onOk, string cancel = "Cancel", bool danger = false) => modal = new(title, text, ok, cancel, onOk, Danger: danger);

    private void ComposeModal()
    {
        var m = modal!;
        var width = 480 - Margin * 2;
        // The question grows to fit its text.
        var size = modalSize = new Vector2(480, Math.Max(190, 64 + LabelHeight(m.Text, 14, width) + 24 + ButtonY));
        var at = (frameSize - size) / 2;
        modalWidgets.Add(new TextW(at + new Vector2(Margin, 64), m.Text, OdysseyChrome.Axis, 14, OdysseyChrome.White, width, 1));
        var y = at.Y + size.Y - ButtonY;
        modalWidgets.Add(new ButtonW(OdRect.At(at.X + size.X / 2 - 150, y, 140, OdysseyChrome.ButtonHeightPx), m.Ok, () => { var ok = m.OnOk; modal = null; ok(); }, true, true, m.Danger ? ReturnSound : PlaceSound));
        modalWidgets.Add(new ButtonW(OdRect.At(at.X + size.X / 2 + 10, y, 140, OdysseyChrome.ButtonHeightPx), m.Cancel, () => { var cancel = m.OnCancel; modal = null; cancel?.Invoke(); }, true, false, ReturnSound));
    }
}
