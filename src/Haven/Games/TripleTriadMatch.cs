using System.Collections.Concurrent;
using System.Numerics;
using Haven.Core;
using Haven.Uld;

namespace Haven.Games;

/// <summary>An opponent: name, the rules they play, and how their deck is drawn (an NPC's fixed and variable cards).</summary>
public sealed record TriadOpponent(string Name, IReadOnlyList<TriadRule> Rules, Func<Random, TriadCard[]>? Deck = null, string Place = "");

/// <summary>What a Triple Triad match needs from Haven: every card, which the player owns, names and rule names.</summary>
public sealed record TriadSetup(IReadOnlyList<TriadCard> Cards, Func<uint, bool> Owned, string PlayerName, TriadOpponent Opponent,
    Func<TriadRule, string>? RuleName = null, IReadOnlyList<TriadCard>? Deck = null, TriadRemote? Remote = null, TriadDuel? Duel = null);

/// <summary>
/// An Odyssey duel: both decks as they play (enhanced cards included), who starts, each side's Materia, the opponent's
/// brain, the music (an orchestrion roll), the turn length and the deal's seed. The match then ends instead of offering
/// a rematch, and says how (TripleTriadMatch.Outcome); LossNote is the result window's line on a loss.
/// </summary>
public sealed record TriadDuel(IReadOnlyList<TriadCard> Blue, IReadOnlyList<TriadCard> Red, TriadPlayer First, TriadEdge? BlueEdge = null,
    TriadEdge? RedEdge = null, ITriadBrain? Brain = null, uint Music = 0, float TurnSeconds = 45, int Seed = 0, string? LossNote = null,
    IReadOnlyList<TriadRule>? RouletteSkips = null);

/// <summary>How a duel ended (None while it plays). A draw is one that Sudden Death couldn't settle.</summary>
public enum TriadOutcome { None, Won, Lost, Drawn, Forfeit }

/// <summary>
/// The other player in a two-player match, over the relay: the deal both PCs play from, their moves as they arrive, and
/// how to send ours. Its state is set from the relay's thread and read by the match each frame.
/// </summary>
public sealed class TriadRemote(bool host, TriadDeal deal, Action<int, int, int> sendMove, Action askRematch, Action leave)
{
    /// <summary>Whether this PC invited (its deck plays the game's blue side).</summary>
    public bool Host { get; } = host;
    public TriadDeal Deal { get; private set; } = deal;
    public readonly ConcurrentQueue<(int Number, int Hand, int Cell)> Moves = new();
    private volatile bool left, wantsRematch;
    private TriadDeal? next;
    /// <summary>The other player left the match (quit, left the gathering, or stopped answering).</summary>
    public bool Left { get => left; set => left = value; }
    /// <summary>The other player asked for a rematch.</summary>
    public bool WantsRematch { get => wantsRematch; set => wantsRematch = value; }
    /// <summary>A rematch's deal, once both asked (the host deals it).</summary>
    public void Rematch(TriadDeal deal) { Interlocked.Exchange(ref next, deal); }
    public TriadDeal? TakeRematch()
    {
        var taken = Interlocked.Exchange(ref next, null);
        if (taken != null) { Deal = taken; wantsRematch = false; Moves.Clear(); }
        return taken;
    }
    public void SendMove(int number, int hand, int cell) => sendMove(number, hand, cell);
    public void AskRematch() => askRematch();
    public void Leave() => leave();
}

/// <summary>
/// Triple Triad in the game's own TripleTriad window: the board, both hands, the score cards, the turn medallion, the
/// card and cell animations and the banners (START!, turns, rules, COMBO/PLUS/SAME, the result), with the match music
/// and the card-game pose, played against an opponent (an NPC's deck and rules, or Haven's) locally, or against another
/// player in the gathering (TriadRemote: both PCs play the same game from one deal; the local player is always shown
/// blue, on the left, as in the game). A finished match
/// shows the game's TripleTriadResult window (Rematch or Quit); Escape asks there whether to leave a match in play.
/// </summary>
public sealed class TripleTriadMatch : MiniGame
{
    // Node ids in TripleTriad.uld: board card slots and cells in reading order, the hands (reading order), score cards.
    private static readonly uint[] BoardCards = [156, 155, 154, 153, 152, 151, 150, 149, 148];
    private static readonly uint[] BoardCells = [232, 233, 234, 235, 236, 237, 238, 239, 240];
    private static readonly uint[] BlueHand = [121, 120, 119, 118, 117];
    private static readonly uint[] RedHand = [163, 162, 161, 160, 159];
    private static readonly uint[] ScoreCards = [208, 207, 206, 205, 204, 203, 202, 201, 200, 199];
    private static readonly uint[] RuleLines = [192, 193, 194, 195];
    private const uint Coin = 189, Rules = 191, Banners = 3;
    // Each side's turn clock text (seconds:hundredths, as the game's StartTimer writes it) and the band under its bar
    // that holds the name: one line in the game's Miedinger lettering (#141 / #183) when there is no title; the other
    // lines stay empty.
    private const uint BlueClock = 128, RedClock = 170, BlueNameBand = 135, RedNameBand = 177, BlueNameLine = 141, RedNameLine = 183;
    // Banner slots (each its own animation) and the game's banner images (ui/icon/121000/<language>/).
    private const uint StartBanner = 38, BurstBanner = 57, TurnBanner = 99, RuleBanner = 103, ResultBanner = 107;
    private const uint StartImage = 121601, BlueTurnImage = 121602, RedTurnImage = 121603, BlueWinsImage = 121604, RedWinsImage = 121605, DrawImage = 121606;
    private const uint ComboImage = 121618, PlusImage = 121619, SameImage = 121620;
    // UI sounds: ids 2000+ are the Triple Triad bank (sound/system/SE_TTriad.scd), loaded on demand: pick up, place,
    // return, an invalid drop, a swap, the coin, a captured card turning over, the turn passing. 45 is the system bank's
    // timer tick; 1000000+n are jingles (GS_Triple_Enc on opening, _Start with START!, _Win on a win).
    private const uint PickSound = 2001, PlaceSound = 2002, ReturnSound = 2003, InvalidSound = 2015, SwapSound = 2006, CoinSound = 2007, FlipSound = 2009,
        TurnSound = 2013, TickSound = 45, OpenJingle = 1000053, StartJingle = 1000054, WinJingle = 1000055;
    private const ushort MatchMusic = 253, CardGamePose = 4079;
    // The card icon's animations (its own clock): a card dropped onto the board, captured sideways or vertically, and
    // dealt into the hands at the start (staggered per slot: blue from 108, red from 114).
    // (A played card simply lands where it's put: the icon's other moves, a spin in from above among them, aren't for it.)
    // Then the hands turn face up, staggered per slot: blue from 120, red (cards an Open rule shows) from 125.
    private const ushort FlipSideways = 102, FlipVertical = 105, DealBlue = 108, DealRed = 114, RevealBlue = 120, RevealRed = 125;
    // The turn timer: the bar under each name (256 px at a full turn) and how long a turn lasts.
    private const uint BlueTimer = 132, RedTimer = 174, BlueLine = 133, RedLine = 175;
    // A turn is TripleTriadDefine row 30 (10 s) plus row 58's grace (5 s), all of it shown on the bar; the tick sounds
    // as 5 seconds remain.
    private const float TimerWidth = 256, TickAt = 5;
    private float TurnSeconds => setup.Duel?.TurnSeconds ?? 15;
    // TripleTriadResult: the three result lines, rewards, match counts, the message line and its check box, the buttons.
    private const uint ResultWindow = 22, ResultLines = 2, WinLine = 3, LoseLine = 4, DrawLine = 5, Rewards = 6, MatchesLeft = 15, MatchesRight = 16,
        MessageLine = 17, MessageCheck = 18, MessageBox = 19, LeftButton = 20, RightButton = 21;
    private const float RewardSpace = 84;

    private readonly UldView view, result;
    private readonly TriadSetup setup;
    private readonly IUldHost host;
    private readonly Random random = new();
    private TriadGame game = null!;
    // The local player's side in the game (blue unless a two-player match's guest), and the other side.
    private TriadPlayer me = TriadPlayer.Blue;
    private TriadPlayer Them => me == TriadPlayer.Blue ? TriadPlayer.Red : TriadPlayer.Blue;
    // A side as shown: the local player's cards are blue, the other's red.
    private TriadPlayer View(TriadPlayer side) => side == me ? TriadPlayer.Blue : TriadPlayer.Red;
    private TriadRemote? Remote => setup.Remote;
    // Moves placed so far in this match, so a two-player move is applied once and in order.
    private int moves;
    // How long the other player has been past their turn's time without a move (they've gone).
    private float overdue;
    private bool rematchAsked;
    private readonly TriadCard?[] blueHand = new TriadCard?[5], redHand = new TriadCard?[5];
    private int selected = -1, hovered = -1;
    private float wait;
    private Phase phase;
    private TriadPlayer shownTurn;
    private readonly Queue<(uint Node, uint Image)> banners = new();
    private bool bannerShowing;
    private enum Phase { Intro, Opening, Reveal, Begin, Announce, Coin, Turn, Opponent, Over }
    // The hands are face down from the deal until they turn over, after the rules are shown.
    private bool concealed;
    // The result window's use: hidden, the match's result (Rematch, Quit), or asking whether to leave (Quit, Keep playing).
    private enum Prompt { None, Result, Leave, Gone }
    private Prompt prompt;
    private bool resultShown;
    private int promptHover = -1;
    // A card being dragged: where the press began, and whether it has moved far enough to be a drag.
    private Vector2 pressedAt;
    private bool pressedOnCard, dragging;
    // Captured cards keep their old colour until their flip turns them over.
    private readonly TriadPlayer?[] flipping = new TriadPlayer?[9];
    private float flipTurn;
    private float turnLeft;

    public TripleTriadMatch(IMiniGameEffects effects, IUldHost host, TriadSetup setup) : base(effects)
    {
        this.setup = setup; this.host = host;
        view = new UldView(host.Layout("TripleTriad"), host);
        result = new UldView(host.Layout("TripleTriadResult"), host);
        Windows.Add((view, Vector2.Zero));
        Windows.Add((result, Vector2.Zero));
        Visible(result.Root, false);
        if (setup.Duel is { Music: > 0 } themed) Effects.Track(themed.Music); else Effects.Music(MatchMusic);
        Effects.Animate(CardGamePose, CardGamePose);
        // An Odyssey duel has its own music and fanfare: Triple Triad's jingles (the start of its theme) stay quiet.
        if (setup.Duel == null) Effects.Sound(OpenJingle);
        Deal();
    }

    public override string Name => "Triple Triad";
    /// <summary>For the preview's stress runs: blue plays itself and every finished match is dealt again.</summary>
    public bool AutoPlay { get; set; }
    /// <summary>With AutoPlay: each finished match is dealt again at once instead of showing the result window.</summary>
    public bool AutoRematch { get; set; } = true;
    /// <summary>Matches finished so far.</summary>
    public int Finished { get; private set; }
    public TriadGame Game => game;
    /// <summary>In an Odyssey duel, how it ended once Done.</summary>
    public TriadOutcome Outcome { get; private set; }
    /// <summary>Raised after each placement: whose it was (the game's side), what it did, and the card placed (the Odyssey's tally).</summary>
    public Action<TriadPlayer, TriadPlacement, TriadCard>? Placed { get; set; }
    /// <summary>The other side's hand slots as last drawn (card, or null when played), for drawing over them.</summary>
    public IEnumerable<(TriadCard? Card, Vector2 Min, Vector2 Max)> TheirHandSlots() =>
        RedHand.Select((id, i) => (redHand[i], view.Node(id)?.ScreenMin ?? default, view.Node(id)?.ScreenMax ?? default)).Where(s => s.Item2 != s.Item3);
    /// <summary>The board's slots as last drawn, with the card on each (null when empty).</summary>
    public IEnumerable<(TriadCard? Card, Vector2 Min, Vector2 Max)> BoardSlots() =>
        BoardCards.Select((id, i) => (game.Board[i], view.Node(id)?.ScreenMin ?? default, view.Node(id)?.ScreenMax ?? default)).Where(s => s.Item2 != s.Item3);
    /// <summary>Where the other side's name band was last drawn (null until drawn).</summary>
    public (Vector2 Min, Vector2 Max)? TheirNameBand() => view.Node(RedNameBand) is { Drawn: true } band ? (band.ScreenMin, band.ScreenMax) : null;
    /// <summary>Your hand slots as last drawn (card, or null when played), for drawing and hovering over them.</summary>
    public IEnumerable<(TriadCard? Card, Vector2 Min, Vector2 Max)> MyHandSlots() =>
        BlueHand.Select((id, i) => (blueHand[i], view.Node(id)?.ScreenMin ?? default, view.Node(id)?.ScreenMax ?? default)).Where(s => s.Item2 != s.Item3);
    /// <summary>The opponent's name, as its name band shows it.</summary>
    public string OpponentName => setup.Opponent.Name;
    /// <summary>Where the whole board (both hands' panel and the grid) was last drawn (null until drawn).</summary>
    public (Vector2 Min, Vector2 Max)? BoardFrame() => view.Root is { Drawn: true } root ? (root.ScreenMin, root.ScreenMax) : null;
    /// <summary>Where your name band was last drawn (null until drawn).</summary>
    public (Vector2 Min, Vector2 Max)? MyNameBand() => view.Node(BlueNameBand) is { Drawn: true } band ? (band.ScreenMin, band.ScreenMax) : null;
    /// <summary>
    /// The rule box's lines and where each was last drawn (top right), for explaining them on hover: a rule (Rolled when
    /// Roulette chose it), or, with more rules than lines, the last line's rules (More).
    /// </summary>
    // Rules the rule box leaves out: an Odyssey duel's Sudden Death, which every duel plays.
    private IEnumerable<TriadRule>? Unlisted => setup.Duel != null ? Haven.Core.Odyssey.OdysseyBalance.Unlisted : null;

    public IEnumerable<(TriadRule? Rule, bool Rolled, IReadOnlyList<TriadRule> More, Vector2 Min, Vector2 Max)> RuleSlots()
    {
        var (lines, more) = TriadGame.RuleLines(game.Rules, setup.Opponent.Rules, RuleLines.Length, Unlisted);
        for (var i = 0; i < RuleLines.Length; i++)
        {
            if (view.Node(RuleLines[i]) is not { Drawn: true, Visible: true } line) continue;
            if (i < lines.Count) yield return (lines[i], !setup.Opponent.Rules.Contains(lines[i]), [], line.ScreenMin, line.ScreenMax);
            else if (i == lines.Count && more.Count > 0) yield return (null, false, more, line.ScreenMin, line.ScreenMax);
        }
    }
    /// <summary>Whether a card of the other side's hand is face up (open rules, Libra).</summary>
    public bool IsOpen(TriadCard card) => game.Open.Contains(card);
    /// <summary>Whether the result or leave prompt is showing (the window over the board).</summary>
    public bool Prompting => prompt != Prompt.None;
    // The opponent's move being thought out by its brain (off the framework thread, on a copy of the game).
    private Task<(int Hand, int Cell)>? thinking;

    public override void Layout(Vector2 screen, float scale)
    {
        Windows[0] = (view, Place(view, screen, scale));
        Windows[1] = (result, Place(result, screen, scale));
        // The result window's art and fonts load while the match plays, so it is complete when it opens.
        if (scale != preloadedAt) { preloadedAt = scale; result.Preload(scale); }
    }
    private float preloadedAt;

    private void Deal()
    {
        var all = setup.Cards;
        if (setup.Duel is { } duel)
        {
            // An Odyssey duel: the decks as the run holds them, and each side's Materia; Sudden Death replays the hands as dealt.
            game = new TriadGame(duel.Blue, duel.Red, duel.First, setup.Opponent.Rules, new Random(duel.Seed), duel.BlueEdge, duel.RedEdge, duel.RouletteSkips) { KeepsOwnCards = true };
            me = TriadPlayer.Blue;
        }
        else if (Remote is { } remote)
        {
            // Two players: both PCs build the same game from the deal (the inviter's deck blue), seeded alike.
            var byId = all.ToDictionary(c => c.Id);
            var deal = remote.Deal;
            game = new TriadGame(deal.HostDeck.Select(id => byId[id]), deal.GuestDeck.Select(id => byId[id]), deal.First, setup.Opponent.Rules, new Random(deal.Seed));
            me = remote.Host ? TriadPlayer.Blue : TriadPlayer.Red;
        }
        else
        {
            // The chosen deck, or (Random rule, or no deck) five of the player's own cards as the deck rules allow.
            TriadCard[] blue = setup.Deck is { Count: 5 } deck && !setup.Opponent.Rules.Contains(TriadRule.Random) ? [.. deck]
                : TriadGame.Deal([.. all.Where(c => setup.Owned(c.Id))], all, random);
            var red = setup.Opponent.Deck?.Invoke(random) is { Length: 5 } npc ? npc : TriadGame.Deal([.. all.Where(c => blue.Any(b => b.Stars == c.Stars))], all, random);
            game = new TriadGame(blue, red, random.Next(2) == 0 ? TriadPlayer.Blue : TriadPlayer.Red, setup.Opponent.Rules, random);
        }
        moves = 0; overdue = 0; rematchAsked = false;
        Hands();
        selected = hovered = -1; dragging = pressedOnCard = false; resultShown = false;
        Array.Clear(flipping);
        Show();
        banners.Clear();
        // The board comes in first (the window's opening: it fades in from 125 %), with the hands still empty; then the
        // cards are dealt and START! shows, as in the game.
        foreach (var id in BlueHand.Concat(RedHand)) Hide(view.Node(id));
        view.Play(101);
        phase = Phase.Intro;
        wait = 0.9f;
    }

    // The hands dealt in, one card after another, then START!.
    // The hands come flying in face down, as in the game; the rules show, then the cards turn over, then START!.
    private void DealIn()
    {
        concealed = true;
        Board();
        for (var i = 0; i < 5; i++)
        {
            view.Play((ushort)(DealBlue + i), Icon(view.Node(BlueHand[i])));
            view.Play((ushort)(DealRed + i), Icon(view.Node(RedHand[i])));
        }
        phase = Phase.Opening;
        wait = 0.9f;
    }

    // The player's cards (and the other side's cards an Open rule shows) turn face up: the back narrows away, then the
    // face widens in its place.
    private void RevealHands()
    {
        concealed = false;
        Board();
        for (var i = 0; i < 5; i++)
        {
            TurnOver(view.Node(BlueHand[i]), blueHand[i] != null, (ushort)(RevealBlue + i));
            TurnOver(view.Node(RedHand[i]), redHand[i] is { } r && Shown(TriadPlayer.Red, r), (ushort)(RevealRed + i));
        }
        void TurnOver(UldElement? slot, bool faceUp, ushort label)
        {
            if (!faceUp || Icon(slot) is not { } icon || icon.Children.FirstOrDefault() is not { } root) return;
            if (root.Find(2) is { } back) back.Visible = true;
            if (root.Find(3) is { } face) face.Scale = new(0, 1);
            view.Play(label, icon, _ => { if (root.Find(2) is { } back) back.Visible = false; });
        }
    }

    private void Hands()
    {
        var mine = game.Hand(me); var theirs = game.Hand(Them);
        for (var i = 0; i < 5; i++) { blueHand[i] = i < mine.Count ? mine[i] : null; redHand[i] = i < theirs.Count ? theirs[i] : null; }
    }

    // The match's resting layout: everything the window shows during play, empty slots and banners hidden.
    private void Show()
    {
        Visible(view.Root, true);
        Hide(view.Node(111));
        if (view.Node(Banners) is { } banners) { banners.Visible = true; foreach (var b in banners.Children) b.Visible = false; }
        for (var i = 0; i < 9; i++) Hide(view.Node(BoardCards[i]));
        ShowName(BlueNameBand, BlueNameLine, setup.PlayerName);
        ShowName(RedNameBand, RedNameLine, setup.Opponent.Name);
        Timers();
        // The rules box lists the match's rules, or hides when there are none.
        // Roulette's picks first; past four rules the last line says how many more (their names on hover in the Odyssey).
        var (lines, more) = TriadGame.RuleLines(game.Rules, setup.Opponent.Rules, RuleLines.Length, Unlisted);
        var names = lines.Select(r => setup.RuleName?.Invoke(r) ?? r.ToString()).ToList();
        if (more.Count > 0) names.Add($"+{more.Count} more");
        if (view.Node(Rules) is { } box) box.Visible = names.Count > 0;
        for (var i = 0; i < RuleLines.Length; i++) if (view.Node(RuleLines[i]) is { } line) { line.Text = i < names.Count ? names[i] : ""; line.Visible = i < names.Count; }
        Hide(view.Node(197));
        Board();
    }

    private static void Visible(UldElement e, bool on) { e.Visible = on; foreach (var c in e.Children) Visible(c, on); }
    private static void Hide(UldElement? e) { if (e != null) e.Visible = false; }
    private static void Icons(UldElement e, uint icon) { if (e.Def.Kind == UldNodeKind.Image) e.Icon = icon; foreach (var c in e.Children) Icons(c, icon); }
    private static UldElement? Face(UldElement? slot) => slot?.Find(3)?.Children.FirstOrDefault()?.Find(3)?.Children.FirstOrDefault();
    // A slot's card icon (the Icon component), whose clock plays the card's drop, flip and deal animations.
    private static UldElement? Icon(UldElement? slot) => slot?.Find(3);

    // A banner over the board (one at a time, in order), hidden again when its animation ends.
    private void Banner(uint node, uint image) { banners.Enqueue((node, image)); if (!bannerShowing) NextBanner(); }
    private void NextBanner()
    {
        if (!banners.TryDequeue(out var next) || view.Node(next.Node) is not { } banner) { bannerShowing = false; return; }
        bannerShowing = true;
        Visible(banner, true); Icons(banner, next.Image);
        view.Play(102, banner, _ => { banner.Visible = false; NextBanner(); });
    }

    // A card slot (the DragDrop component) showing a card face up or down, coloured for its owner, or empty.
    private void Card(UldElement? slot, TriadCard? card, TriadPlayer owner, bool faceUp, bool playable = true, TriadPlayer? side = null, int cell = -1)
    {
        if (slot == null) return;
        slot.Visible = card != null;
        if (card == null) return;
        Visible(slot, true);
        var icon = slot.Find(3)?.Children.FirstOrDefault();
        if (icon?.Find(2) is { } back) back.Visible = !faceUp;
        if (icon?.Find(3) is { } faceComponent) faceComponent.Visible = faceUp;
        slot.Multiply = playable ? Vector3.One : new Vector3(.55f);
        if (slot.Find(2) is { } frame) frame.Alpha = 0;
        if (!faceUp || Face(slot) is not { } root) return;
        if (root.Find(20) is { } art) art.Icon = card.Icon;
        if (root.Find(21) is { } colour) view.Play(owner == TriadPlayer.Blue ? (ushort)101 : (ushort)102, colour);
        // Numbers as they play: with the owner's Materia, what stays on the cell, the card's trait and Ascension; a hand
        // card as it would strike, a board card as it guards.
        var player = side ?? (owner == TriadPlayer.Blue ? me : Them);
        var numbers = new[] { (14u, 0), (15u, 2), (16u, 3), (17u, 1) };
        var deltas = new int[4];
        foreach (var (id, d) in numbers)
        {
            var v = game.Number(card, d, player, cell, cell < 0);
            deltas[d] = v - card.Side(d);
            if (root.Find(id) is { } text) text.Text = v >= 10 ? "A" : v.ToString();
        }
        // The badge shows the change most sides share (Ascension's own badge, reused).
        var modifier = deltas.GroupBy(x => x).OrderByDescending(g => g.Count()).ThenByDescending(g => Math.Abs(g.Key)).First().Key;
        if (root.Find(2) is { } stars) Stars(stars, card.Stars);
        if (root.Find(8) is { } type) { type.Visible = card.Type is >= 1 and <= 4; if (type.Visible) view.Play((ushort)(100 + card.Type), type); }
        // Ascension and Descension show the change on typed cards.
        if (root.Find(10) is { } shown) { shown.Visible = modifier != 0; if (shown.Find(11) is { } mod) mod.Text = modifier > 0 ? $"+{modifier}" : modifier.ToString(); }
    }

    // The card's stars: five star images in a pentagon (3 at the top, 4 and 5 across, 6 and 7 below). The layout's star
    // labels key nothing Haven's renderer plays, so the images for the card's rarity are shown directly: one at the top,
    // two across, a triangle, a square, or all five.
    private static readonly uint[][] StarImages = [[], [3], [4, 5], [3, 6, 7], [4, 5, 6, 7], [3, 4, 5, 6, 7]];
    internal static void Stars(UldElement stars, int count)
    {
        var shown = StarImages[Math.Clamp(count, 0, 5)];
        stars.Visible = true;
        foreach (var star in stars.Children) star.Visible = shown.Contains(star.Def.Id);
    }

    private bool Shown(TriadPlayer owner, TriadCard card) => owner == TriadPlayer.Blue || game.Open.Contains(card);

    private void Score()
    {
        var blue = game.Score(me);
        for (var i = 0; i < ScoreCards.Length; i++)
            if (view.Node(ScoreCards[i])?.Children.FirstOrDefault()?.Find(2) is { } small) small.Part = i < blue ? 2u : 3u;
    }

    private void Board()
    {
        for (var i = 0; i < 9; i++)
        {
            Card(view.Node(BoardCards[i]), game.Board[i], View(flipping[i] ?? game.Owner[i]), true, true, game.Owner[i], i);
            if (view.Node(BoardCells[i]) is { } cell) view.Play(game.Board[i] == null ? (ushort)102 : game.Owner[i] == me ? (ushort)103 : (ushort)104, cell);
        }
        for (var i = 0; i < 5; i++)
        {
            var playable = blueHand[i] is { } b && game.Turn == me && game.Playable(game.Hand(me).IndexOf(b));
            Card(view.Node(BlueHand[i]), blueHand[i], TriadPlayer.Blue, !concealed, playable || game.Turn != me, me);
            Card(view.Node(RedHand[i]), redHand[i], TriadPlayer.Red, !concealed && redHand[i] is { } r && Shown(TriadPlayer.Red, r), true, Them);
        }
        Score();
    }

    // A name in its band under the bar: the one-line text, every other line of the band empty.
    private void ShowName(uint band, uint line, string name)
    {
        if (view.Node(band) is not { } root) return;
        void Clear(UldElement e) { if (e.Def.Kind == UldNodeKind.Text) e.Text = ""; foreach (var c in e.Children) Clear(c); }
        Clear(root);
        if (view.Node(line) is { } text) text.Text = name;
    }

    // The timer bars and clocks: the side to move counts down, the other shows a full bar and no time.
    private void Timers()
    {
        static string Clock(float seconds) => $"{(int)seconds:00}:{(int)(seconds * 100) % 100:00}";
        if (view.Node(BlueClock) is { } blueClock) blueClock.Text = phase == Phase.Turn ? Clock(turnLeft) : "--:--";
        if (view.Node(RedClock) is { } redClock) redClock.Text = phase == Phase.Opponent ? Clock(turnLeft) : "--:--";
        var blue = phase == Phase.Turn ? turnLeft / TurnSeconds : 1; var red = phase == Phase.Opponent ? turnLeft / TurnSeconds : 1;
        if (view.Node(BlueTimer) is { } b) b.Size.X = MathF.Round(TimerWidth * Math.Clamp(blue, 0, 1));
        if (view.Node(RedTimer) is { } r) r.Size.X = MathF.Round(TimerWidth * Math.Clamp(red, 0, 1));
        // The thin line along each bar follows it.
        if (view.Node(BlueLine) is { } bl) bl.Size.X = MathF.Round(TimerWidth * Math.Clamp(blue, 0, 1));
        if (view.Node(RedLine) is { } rl) rl.Size.X = MathF.Round(TimerWidth * Math.Clamp(red, 0, 1));
    }

    private void Turn(TriadPlayer side, bool first = false)
    {
        turnLeft = TurnSeconds; overdue = 0;
        var player = View(side);
        var coin = view.Node(Coin);
        if (first) { view.Play(player == TriadPlayer.Blue ? (ushort)101 : (ushort)103, coin); Effects.Sound(CoinSound); }
        else if (player != shownTurn) { view.Play(player == TriadPlayer.Red ? (ushort)105 : (ushort)107, coin); Effects.Sound(TurnSound); }
        shownTurn = player;
        Banner(TurnBanner, player == TriadPlayer.Blue ? BlueTurnImage : RedTurnImage);
    }

    public override void Update(MiniGameInput input)
    {
        view.Update(input.Seconds);
        result.Update(input.Seconds);
        if (flipTurn > 0 && (flipTurn -= input.Seconds) <= 0) { Array.Clear(flipping); Board(); }
        // Escape asks whether to leave a match in play; in the prompt it means its second choice.
        if (input.Cancel)
        {
            if (prompt == Prompt.Leave) HidePrompt();
            else if (prompt == Prompt.Result) { HidePrompt(); End(); }
            else if (phase != Phase.Over) ShowPrompt(Prompt.Leave);
            return;
        }
        if (prompt != Prompt.None) { PromptInput(input); return; }
        if (phase is Phase.Turn or Phase.Opponent && wait <= 0)
        {
            var before = turnLeft;
            turnLeft = Math.Max(0, turnLeft - input.Seconds);
            if (phase == Phase.Turn && before > TickAt && turnLeft <= TickAt) Effects.Sound(TickSound);
            Timers();
        }
        if (wait > 0) { wait -= input.Seconds; return; }
        switch (phase)
        {
            case Phase.Intro:
                DealIn();
                break;
            case Phase.Opening:
                // The rules in play, announced one by one.
                foreach (var rule in game.Rules) if (RuleImage(rule) is { } image) Banner(RuleBanner, image);
                if (game.Swapped != null) Effects.Sound(SwapSound);
                phase = Phase.Reveal; wait = game.Rules.Count * 1.2f;
                break;
            case Phase.Reveal:
                RevealHands();
                phase = Phase.Begin; wait = 0.8f;
                break;
            case Phase.Begin:
                Banner(StartBanner, StartImage);
                if (setup.Duel == null) Effects.Sound(StartJingle);
                phase = Phase.Announce; wait = 1.6f;
                break;
            case Phase.Announce:
                phase = Phase.Coin; Turn(game.Turn, true); Board(); wait = 1.4f;
                break;
            case Phase.Coin:
                phase = game.Turn == me ? Phase.Turn : Phase.Opponent; wait = phase == Phase.Opponent && Remote == null ? 0.8f : 0;
                break;
            case Phase.Turn:
                PlayerTurn(input);
                break;
            case Phase.Opponent:
                {
                    if (Remote is { } remote) { RemoteTurn(remote, input.Seconds); break; }
                    int hand, cell;
                    if (setup.Duel?.Brain is { } brain)
                    {
                        // The brain thinks on a copy, off the framework thread; the board waits until it has chosen.
                        var seed = random.Next();
                        var copy = game.Copy();
                        thinking ??= Task.Run(() => brain.Choose(copy, new Random(seed)));
                        if (!thinking.IsCompleted) break;
                        (hand, cell) = thinking.IsCompletedSuccessfully ? thinking.Result : game.Choose(random);
                        thinking = null;
                        if (hand < 0 || hand >= game.Hand(Them).Count || cell is < 0 or > 8 || game.Board[cell] != null || !game.Playable(hand)) (hand, cell) = game.Choose(random);
                    }
                    else (hand, cell) = game.Choose(random);
                    if (hand < 0) { phase = Phase.Over; break; }
                    var slot = Array.IndexOf(redHand, game.Hand(Them)[hand]);
                    Place(Them, slot, hand, cell);
                    break;
                }
            case Phase.Over:
                if (setup.Duel != null && Outcome == TriadOutcome.None)
                    Outcome = game.Winner is not { } won ? TriadOutcome.Drawn : won == me ? TriadOutcome.Won : TriadOutcome.Lost;
                if (AutoPlay && setup.Duel != null) { Done = true; break; }
                if (AutoPlay && AutoRematch) { Deal(); break; }
                if (!resultShown) { resultShown = true; ShowPrompt(Prompt.Result); }
                break;
        }
    }

    // The other player's move, as it arrives; one that isn't the next move, or can't be played, is ignored. Long past
    // their time with no move, they've gone.
    private void RemoteTurn(TriadRemote remote, float seconds)
    {
        if (remote.Left) { ShowPrompt(Prompt.Gone); return; }
        while (remote.Moves.TryDequeue(out var move))
        {
            var theirs = game.Hand(Them);
            if (move.Number != moves || move.Hand < 0 || move.Hand >= theirs.Count || move.Cell is < 0 or > 8 || game.Board[move.Cell] != null) continue;
            Place(Them, Array.IndexOf(redHand, theirs[move.Hand]), move.Hand, move.Cell);
            return;
        }
        if (turnLeft <= 0 && (overdue += seconds) > 20) { remote.Left = true; ShowPrompt(Prompt.Gone); }
    }

    private bool leftSent;
    private void End()
    {
        if (Remote is { } remote && !leftSent) { leftSent = true; remote.Leave(); }
        Done = true;
    }

    /// <summary>The home's music and the player's pose come back however the game ends (Quit, leaving the home, unloading).</summary>
    public override void Stop()
    {
        // Leaving the home or unloading mid-match: the other player is told.
        if (Remote is { } remote && !leftSent) { leftSent = true; remote.Leave(); }
        Effects.Music(0);
        Effects.Animate(0, 0);
    }

    // The game's TripleTriadResult window: the result with Rematch and Quit, or the question whether to leave a match.
    private void ShowPrompt(Prompt kind)
    {
        prompt = kind; promptHover = -1;
        dragging = pressedOnCard = false; ReturnDragged();
        Visible(result.Root, true);
        foreach (var id in new[] { Rewards, MatchesLeft, MatchesRight, MessageCheck }) Hide(result.Node(id));
        // No rewards in a home: the message and buttons move up into their place and the window closes up around them.
        foreach (var id in new[] { MessageLine, MessageCheck, MessageBox, LeftButton, RightButton })
            if (result.Node(id) is { } moved) moved.Position = moved.Def.Position - new Vector2(0, RewardSpace);
        result.Root.Size = result.Root.Def.Size - new Vector2(0, RewardSpace);
        if (result.Node(ResultWindow) is { } frame) result.Resize(frame, result.Root.Size);
        // A heading in the result's own lettering every time: the result (You Win!, You Lose..., Draw), or the question
        // in Draw's style; below it a full-size line in the message box, and labels sized for their buttons.
        if (result.Node(ResultLines) is { } lines) lines.Visible = true;
        var shown = kind != Prompt.Result || game.Winner is not { } winner ? DrawLine : winner == me ? WinLine : LoseLine;
        foreach (var id in new[] { WinLine, LoseLine, DrawLine })
            if (result.Node(id) is { } line) { line.Visible = id == shown; line.Text = null; }
        var duel = setup.Duel;
        if (kind != Prompt.Result && result.Node(DrawLine) is { } heading)
            heading.Text = kind == Prompt.Gone ? "Match Over" : duel != null ? "Forfeit the Duel?" : "Leave the Match?";
        if (result.Node(MessageLine) is { } message)
        {
            message.FontSize = 18;
            message.Text = (kind, duel) switch
            {
                (Prompt.Result, null) => host.Text(9201),
                (Prompt.Result, _) => game.Winner is not { } victor ? "Sudden Death couldn't settle it. The duel will be dealt again."
                    : victor == me ? "Victory! On to your reward." : duel.LossNote ?? "Your odyssey ends here.",
                (Prompt.Gone, _) => "Your opponent has left the match.",
                (_, null) => "The match will end here.",
                _ => "Leaving now loses the duel, and ends your odyssey.",
            };
        }
        if (ButtonText(LeftButton) is { } left)
        {
            left.Text = duel != null ? kind == Prompt.Result ? "Continue" : "Forfeit" : host.Text(kind is Prompt.Result ? 9194u : 9195u);
            left.FontSize = 14;
        }
        if (ButtonText(RightButton) is { } right) { right.Text = kind == Prompt.Result ? host.Text(9195) : "Keep playing"; right.FontSize = 14; }
        // With the other player gone, Quit is all there is; a duel's result has only Continue.
        if (result.Node(RightButton) is { } second) second.Visible = kind != Prompt.Gone && !(kind == Prompt.Result && duel != null);
        foreach (var id in new[] { LeftButton, RightButton }) result.Play(1, result.Node(id));
    }

    private UldElement? ButtonText(uint button) => result.Node(button)?.Children.FirstOrDefault()?.Find(2);

    private void HidePrompt() { prompt = Prompt.None; Visible(result.Root, false); }

    private void PromptInput(MiniGameInput input)
    {
        var hover = result.Node(LeftButton) is { } l && l.Contains(input.Mouse) ? 0 : result.Node(RightButton) is { } r && r.Contains(input.Mouse) ? 1 : -1;
        // The window's close button means the second choice (Quit, or Keep playing).
        if (input.Pressed && result.Node(ResultWindow, 2, 7) is { } close && close.Contains(input.Mouse)) { if (prompt == Prompt.Result) { HidePrompt(); End(); } else HidePrompt(); return; }
        if (hover != promptHover)
        {
            // The game's button states: 1 normal, 2 hovered.
            if (promptHover >= 0) result.Play(1, result.Node(promptHover == 0 ? LeftButton : RightButton));
            if (hover >= 0) result.Play(2, result.Node(hover == 0 ? LeftButton : RightButton));
            promptHover = hover;
        }
        if (prompt == Prompt.Result && Remote is { } remote) RemoteResult(remote);
        if (prompt == Prompt.None) return;
        if (!input.Pressed && !input.Confirm) return;
        var choice = input.Confirm && hover < 0 ? 0 : hover;
        if (choice < 0 || prompt == Prompt.Gone && choice != 0) return;
        if (prompt == Prompt.Result && choice == 0 && Remote is { } asking)
        {
            // Two players: a rematch starts once both ask (the inviter deals it).
            if (!rematchAsked && !asking.Left) { rematchAsked = true; asking.AskRematch(); }
            return;
        }
        if (prompt == Prompt.Result && setup.Duel != null) { HidePrompt(); End(); }
        else if (prompt == Prompt.Result) { HidePrompt(); if (choice == 0) Deal(); else End(); }
        else if (choice == 0) { if (setup.Duel != null && prompt == Prompt.Leave) Outcome = TriadOutcome.Forfeit; End(); }
        else HidePrompt();
    }

    // The result window in a two-player match says where the rematch stands, and deals it once both asked.
    private void RemoteResult(TriadRemote remote)
    {
        if (remote.TakeRematch() != null) { HidePrompt(); Deal(); return; }
        if (result.Node(MessageLine) is { } message)
            message.Text = remote.Left ? host.Text(9196) : rematchAsked ? host.Text(9198) : remote.WantsRematch ? host.Text(9197) : host.Text(9201);
    }

    private static uint? RuleImage(TriadRule rule) => rule switch
    {
        TriadRule.AllOpen => 121611, TriadRule.ThreeOpen => 121612, TriadRule.Reverse => 121613, TriadRule.Chaos => 121614, TriadRule.Order => 121615,
        TriadRule.Random => 121616, TriadRule.Swap => 121617, TriadRule.Plus => 121619, TriadRule.Same => 121620, TriadRule.FallenAce => 121621,
        TriadRule.Ascension => 121622, TriadRule.Descension => 121623, TriadRule.SuddenDeath => 121624, _ => null,
    };

    private void PlayerTurn(MiniGameInput input)
    {
        if (AutoPlay || turnLeft <= 0)
        {
            // Time up: the game plays a card for you.
            ReturnDragged(); dragging = pressedOnCard = false;
            var (auto, at) = game.Choose(random);
            Place(me, Array.IndexOf(blueHand, game.Hand(me)[auto]), auto, at);
            selected = -1;
            return;
        }
        hovered = -1;
        for (var i = 0; i < 5; i++)
            if (blueHand[i] != null && !(dragging && i == selected) && view.Node(BlueHand[i]) is { } slot && slot.Contains(input.Mouse)) hovered = i;
        if (input.Pressed && hovered >= 0)
        {
            var index = game.Hand(me).IndexOf(blueHand[hovered]!);
            if (!game.Playable(index)) { Effects.Sound(InvalidSound); pressedOnCard = false; }
            else
            {
                if (selected != hovered) Effects.Sound(PickSound);
                selected = hovered; pressedOnCard = true; pressedAt = input.Mouse;
            }
        }
        // A card held and moved follows the pointer, as in the game.
        if (pressedOnCard && input.Held && selected >= 0 && view.Node(BlueHand[selected]) is { } held)
        {
            if (!dragging && Vector2.Distance(input.Mouse, pressedAt) > 6) dragging = true;
            if (dragging) held.Position = held.Def.Position + (input.Mouse - pressedAt) / Math.Max(.01f, view.DrawScale);
        }
        for (var i = 0; i < 5; i++)
            if (view.Node(BlueHand[i])?.Find(2) is { } frame) frame.Alpha = i == selected ? 1 : i == hovered ? .5f : 0;
        var cell = -1;
        for (var c = 0; c < 9; c++) if (view.Node(BoardCells[c]) is { } target && target.Contains(input.Mouse)) cell = c;
        if (input.Released && pressedOnCard)
        {
            pressedOnCard = false;
            if (dragging)
            {
                // Let go over an empty cell places the card; anywhere else it goes back to the hand.
                dragging = false; ReturnDragged();
                if (cell >= 0 && game.Board[cell] == null) { PlaceSelected(cell); return; }
                Effects.Sound(ReturnSound);
                return;
            }
        }
        // Or pick a card, then click a cell.
        if (!input.Pressed || hovered >= 0 || selected < 0 || cell < 0) return;
        if (game.Board[cell] != null) { Effects.Sound(InvalidSound); return; }
        PlaceSelected(cell);
    }

    private void PlaceSelected(int cell)
    {
        var card = blueHand[selected]!;
        Place(me, selected, game.Hand(me).IndexOf(card), cell);
        selected = -1;
    }

    private void ReturnDragged()
    {
        foreach (var id in BlueHand) if (view.Node(id) is { } slot) slot.Position = slot.Def.Position;
    }

    private void Place(TriadPlayer player, int slot, int hand, int cell)
    {
        // Our move goes to the other player first (numbered, so it's applied once and in order).
        if (player == me && Remote is { } remote) remote.SendMove(moves, hand, cell);
        moves++;
        var before = game.Owner.ToArray();
        var card = game.Hand(player)[hand];
        var placement = game.PlaceCard(hand, cell);
        (player == me ? blueHand : redHand)[slot] = null;
        Placed?.Invoke(player, placement, card);
        // Captured cards turn over (sideways from a neighbour in the row, else up or down) and change colour halfway.
        foreach (var flipped in placement.All) flipping[flipped] = before[flipped];
        flipTurn = placement.All.Any() ? 0.12f + 0.25f : 0;
        Board();
        Effects.Sound(PlaceSound);
        foreach (var flipped in placement.All)
            view.Play(flipped / 3 == cell / 3 ? FlipSideways : FlipVertical, Icon(view.Node(BoardCards[flipped])));
        if (placement.Same.Length > 0) Banner(BurstBanner, SameImage);
        if (placement.Plus.Length > 0) Banner(BurstBanner, PlusImage);
        if (placement.Combo.Length > 0) Banner(BurstBanner, ComboImage);
        if (placement.All.Any()) Effects.Sound(FlipSound);
        wait = placement.All.Any() ? 1.0f : 0.5f;
        if (placement.Same.Length + placement.Plus.Length > 0) wait += 1.2f;
        if (game.Over)
        {
            if (game.RestartSuddenDeath())
            {
                // Sudden Death: each side plays on with the cards they own (in the Odyssey, the hands as dealt).
                Hands(); Banner(RuleBanner, 121624);
                phase = Phase.Announce; wait += 1.5f;
                return;
            }
            phase = Phase.Over; Finished++;
            Banner(ResultBanner, game.Winner is not { } winner ? DrawImage : winner == me ? BlueWinsImage : RedWinsImage);
            if (game.Winner == me && setup.Duel == null) Effects.Sound(WinJingle);
            wait += 1.5f;
            return;
        }
        Turn(game.Turn);
        phase = game.Turn == me ? Phase.Turn : Phase.Opponent;
        if (phase == Phase.Opponent && Remote == null) wait += 0.7f;
    }
}
