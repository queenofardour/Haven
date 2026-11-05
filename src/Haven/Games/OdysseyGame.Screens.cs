using System.Numerics;
using Haven.Core;
using Haven.Core.Odyssey;

namespace Haven.Games;

// The Odyssey's screens (design §7), each composed from the director's view of it: the frame's size and heading, and
// the widgets laid out inside it. Sub-choices (a card claim, an item's target, an enhancement's side) are the
// screen's own state here; the run's state is only ever the director's.
public sealed partial class OdysseyGame
{
    private const float Top = 66;
    // Sub-choices within a screen.
    private int selectedUid = -1, choice = -1, pickingItem = -1, eventChoice = -1, eventType, rank;
    private bool shopPack, shopRemoving, shopSelling;

    private void ScreenChanged()
    {
        page = 0; selectedUid = -1; choice = -1; pickingItem = -1; eventChoice = -1; eventType = 0; shopPack = shopRemoving = shopSelling = false;
        if (PreviewPickItem >= 0) { pickingItem = PreviewPickItem; PreviewPickItem = -1; }
        if (PreviewSelling && odyssey.Screen == OdysseyScreen.Shop) { shopSelling = true; PreviewSelling = false; }
        if (mode == Mode.Run && odyssey.Screen == OdysseyScreen.Title) mode = Mode.Title;
        if (mode == Mode.Title) rank = Math.Clamp(rank, 0, odyssey.Records.UnlockedRank);
    }

    // Backs out of a sub-choice; false when there is none.
    private bool Back()
    {
        if (mode != Mode.Run) return false;
        switch (odyssey.Screen)
        {
            case OdysseyScreen.Reward when odyssey.Reward().Claiming: odyssey.CancelClaim(); return true;
            case OdysseyScreen.Deck when pickingItem >= 0: pickingItem = -1; return true;
            case OdysseyScreen.Event when eventChoice >= 0: eventChoice = -1; selectedUid = -1; eventType = 0; return true;
            case OdysseyScreen.Shop when shopPack || shopRemoving || shopSelling: shopPack = shopRemoving = shopSelling = false; return true;
            case OdysseyScreen.Upgrade when selectedUid >= 0: selectedUid = -1; choice = -1; return true;
            case OdysseyScreen.Upgrade or OdysseyScreen.MateriaSwap when odyssey.CanGoBack: odyssey.GoBack(); return true;
        }
        return false;
    }

    private Vector2 SizeFor() => mode switch
    {
        Mode.Title => new(640, 470),
        Mode.Records => new(900, 540),
        _ => odyssey.Screen switch
        {
            OdysseyScreen.StartingBinder => new(1000, 480),
            OdysseyScreen.WorldTitle => new(820, 560),
            OdysseyScreen.Path => new(1000, 510),
            OdysseyScreen.Deck => new(1120, 720),
            OdysseyScreen.Reward => new(1000, 580),
            OdysseyScreen.Upgrade => new(1040, 640),
            OdysseyScreen.MateriaSwap => new(820, 520),
            OdysseyScreen.Event => new(840, EventHeight()),
            OdysseyScreen.Shop => new(1120, 720),
            OdysseyScreen.Victory or OdysseyScreen.GameOver => new(1000, 640),
            _ => new(640, 470),
        },
    };

    // An event's frame fits its text and answers (the answers sat at the bottom of a fixed 600 px frame, under a large
    // gap); choosing a card for an answer shows the binder, at the full height.
    private float EventHeight()
    {
        if (eventChoice >= 0 || odyssey.Screen != OdysseyScreen.Event) return 600;
        var view = odyssey.Event();
        var text = LabelHeight(view.Text, 14, 840 - Margin * 2);
        if (text <= 0) return 600;   // fonts not ready yet: the next layout fits it
        var y = Top + text + 14 + (view.Offered != null ? sprite.Size.Y + 30 : 0);
        return Math.Clamp(y + 24 + (Math.Max(1, view.Choices.Count) - 1) * 36 + 60 + ButtonY, 280, 600);
    }

    private string Heading()
    {
        if (mode == Mode.Title) return "";
        if (mode == Mode.Records) return "Records";
        try
        {
            return odyssey.Screen switch
            {
                OdysseyScreen.StartingBinder => "Your Starting Binder",
                OdysseyScreen.WorldTitle => $"World {odyssey.World.Numeral}",
                OdysseyScreen.Path => odyssey.Path().BossAhead ? "The Boss Awaits" : "Choose Your Opponent",
                OdysseyScreen.Deck => odyssey.Deck().Opponent is { } o ? $"{o.Name}, {o.Title}" : "Your Deck",
                OdysseyScreen.Reward => odyssey.Reward().Heading,
                OdysseyScreen.Upgrade => odyssey.Upgrade().Enhancement.Name,
                OdysseyScreen.MateriaSwap => odyssey.MateriaSwap().LegendaryLimited ? "One Legendary Too Many" : "Materia Slots Full",
                OdysseyScreen.Event => odyssey.Event().Title,
                OdysseyScreen.Shop => "Gold Saucer Card Shop",
                OdysseyScreen.Victory => "Victory!",
                OdysseyScreen.GameOver => odyssey.Run?.Verdict == "Abandoned." ? "Run Abandoned" : "Game Over",
                _ => "",
            };
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return ""; }
    }

    private uint ScreenMusic()
    {
        try
        {
            return odyssey.Screen switch
            {
                OdysseyScreen.StartingBinder => OdysseyContent.TitleMusic,
                OdysseyScreen.WorldTitle or OdysseyScreen.Path or OdysseyScreen.Deck or OdysseyScreen.Event => odyssey.World.MapMusic,
                OdysseyScreen.Reward => odyssey.Reward().Music,
                OdysseyScreen.Upgrade => odyssey.Upgrade().Music,
                OdysseyScreen.MateriaSwap => odyssey.MateriaSwap().Music,
                OdysseyScreen.Shop => OdysseyContent.ShopMusic,
                OdysseyScreen.Victory or OdysseyScreen.GameOver => odyssey.Summary().Music,
                _ => OdysseyContent.TitleMusic,
            };
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return track; }
    }

    private void Compose()
    {
        Status();
        switch (mode)
        {
            case Mode.Title: ComposeTitle(); return;
            case Mode.Records: ComposeRecords(); return;
        }
        switch (odyssey.Screen)
        {
            case OdysseyScreen.StartingBinder: ComposeStartingBinder(); break;
            case OdysseyScreen.WorldTitle: ComposeWorldTitle(); break;
            case OdysseyScreen.Path: ComposePath(); break;
            case OdysseyScreen.Deck: ComposeDeck(); break;
            case OdysseyScreen.Reward: ComposeReward(); break;
            case OdysseyScreen.Upgrade: ComposeUpgrade(); break;
            case OdysseyScreen.MateriaSwap: ComposeMateriaSwap(); break;
            case OdysseyScreen.Event: ComposeEvent(); break;
            case OdysseyScreen.Shop: ComposeShop(); break;
            case OdysseyScreen.Victory or OdysseyScreen.GameOver: ComposeSummary(); break;
            default: mode = Mode.Title; ComposeTitle(); break;
        }
    }

    // ---- Title and records ----

    private void ComposeTitle()
    {
        var title = odyssey.Title();
        var w = frameSize.X;
        Label(0, 70, "The Crystal Odyssey", OdysseyChrome.Miedinger, 30, OdysseyChrome.Gold, w, 1);
        Label(0, 116, "A Triple Triad roguelite", OdysseyChrome.Axis, 15, OdysseyChrome.White, w, 1);
        Label(0, 140, "Fourteen worlds. One deck. No second chances.", OdysseyChrome.Axis, 13, OdysseyChrome.Grey, w, 1);
        var x = (w - 240) / 2; var y = 190f;
        Button(x, y, 240, title.CanContinue ? $"Continue · {title.ContinueText}" : "Continue", () => mode = Mode.Run, title.CanContinue, title.CanContinue, "No run to continue.");
        y += 38;
        Button(x, y, 240, "New run", () =>
        {
            void Start() { odyssey.NewRun(Random.Shared.Next(), rank); mode = Mode.Run; }
            if (title.CanContinue) Ask("Abandon the run?", $"Your run at {title.ContinueText} will be lost, and a new one dealt.", "Abandon it", Start, danger: true);
            else Start();
        }, true, !title.CanContinue);
        y += 38;
        if (title.UnlockedRank > 0)
        {
            rank = Math.Clamp(rank, 0, title.UnlockedRank);
            widgets.Add(new ButtonW(OdRect.At(x, y, 36, OdysseyChrome.ButtonHeightPx), "<", () => rank--, rank > 0, false, PageSound));
            Label(x + 40, y + 6, rank == 0 ? "Crystal Rank · none" : $"Crystal Rank {rank}", OdysseyChrome.Axis, 14, OdysseyChrome.Gold, 160, 1);
            widgets.Add(new ButtonW(OdRect.At(x + 204, y, 36, OdysseyChrome.ButtonHeightPx), ">", () => rank++, rank < title.UnlockedRank, false, PageSound));
            widgets.Add(new BoxW(OdRect.At(x + 40, y, 160, OdysseyChrome.ButtonHeightPx), 0) { TipTitle = "Crystal Rank", Tip = RankText(rank) });
            y += 38;
        }
        Button(x, y, 240, "Records", () => mode = Mode.Records);
        y += 38;
        Button(x, y, 240, "Leave", () => Done = true, sound: ReturnSound);
        var records = title.Records;
        var best = records.BestWorld == 0 ? "no world reached yet" : $"best World {OdysseyContent.WorldAt(records.BestWorld).Numeral}";
        Label(0, frameSize.Y - 44, $"{records.Runs} run{(records.Runs == 1 ? "" : "s")} · {records.Clears} clear{(records.Clears == 1 ? "" : "s")} · {best}", OdysseyChrome.Axis, 12, OdysseyChrome.Grey, w, 1);
    }

    private static string RankText(int rank) => rank switch
    {
        0 => "The Odyssey as designed. Clearing it unlocks Crystal Rank 1.",
        1 => "Opponents carry one more Materia.", 2 => "Rank 1, and only two starting re-deals.", 3 => "Rank 2, and an elite on every path from World II.",
        4 => "Rank 3, and opponents think one level deeper.", 5 => "Rank 4, and bosses play one more rule.", 6 => "Rank 5, and a weaker starting binder.",
        7 => "Rank 6, and shop prices +25 %.", 8 => "Rank 7, and no Phoenix Down in shops.", 9 => "Rank 8, and opponents' cards carry one more enhancement.",
        _ => "Rank 9, and the Endsinger has a second form.",
    };

    private void ComposeRecords()
    {
        var r = odyssey.Records;
        var w = frameSize.X;
        var y = Top;
        Label(Margin, y, $"Runs {r.Runs}   ·   Clears {r.Clears}   ·   Duels won {r.DuelsWon}   ·   Best Crystal Rank cleared {(r.BestRankCleared < 0 ? "none" : r.BestRankCleared.ToString())}",
            OdysseyChrome.Axis, 14, OdysseyChrome.White, w - Margin * 2, 1);
        y += 26;
        var best = r.BestWorld == 0 ? "No world reached yet." : $"Furthest: World {OdysseyContent.WorldAt(r.BestWorld).Numeral}, {(r.BestDuel >= OdysseyBalance.BossDuel ? "the boss" : $"duel {r.BestDuel}")}.";
        Label(Margin, y, best, OdysseyChrome.Axis, 13, OdysseyChrome.Grey, w - Margin * 2, 1);
        y += 30;
        if (r.MvpCard != 0 && setup.Cards.FirstOrDefault(c => c.Id == r.MvpCard) is { } mvp)
        {
            Label(Margin, y, "MVP of all time", OdysseyChrome.Axis, 13, OdysseyChrome.Gold);
            Card(new(Margin, y + 20), new CardView(0, mvp, mvp, odyssey.Power(mvp)), .7f, true, caption: $"{r.MvpCaptures} captures");
        }
        var listX = r.MvpCard != 0 ? Margin + 110 : Margin;
        Label(listX, y, "Recent runs", OdysseyChrome.Axis, 13, OdysseyChrome.Gold);
        y += 22;
        if (r.Recent.Count == 0) Label(listX, y, "None yet. The crystals are waiting.", OdysseyChrome.Axis, 13, OdysseyChrome.Grey);
        foreach (var run in r.Recent.Take(10))
        {
            var world = OdysseyContent.WorldAt(Math.Max(1, run.World));
            var where = run.Won ? "Cleared all fourteen worlds" : $"World {world.Numeral}, {(run.Duel >= OdysseyBalance.BossDuel ? "the boss" : $"duel {run.Duel}")}";
            var line = $"{run.When.ToLocalTime():d MMM}  ·  {where}  ·  {run.DuelsWon} won" + (run.Rank > 0 ? $"  ·  Rank {run.Rank}" : "");
            Label(listX, y, line, OdysseyChrome.Axis, 13, run.Won ? OdysseyChrome.Gold : OdysseyChrome.White);
            var materia = run.Materia.Count == 0 ? "No Materia" : string.Join(", ", run.Materia.Select(k => OdysseyContent.MateriaOf(k)?.Name ?? k));
            widgets.Add(new BoxW(OdRect.At(listX, y - 2, w - listX - Margin, 20), 0) { TipTitle = run.Verdict, Tip = materia, TipColour = OdysseyChrome.White });
            Label(listX + 400, y + 2, materia, OdysseyChrome.Axis, 11, OdysseyChrome.Grey, w - Margin - listX - 400);
            y += 22;
        }
        Button((w - 160) / 2, frameSize.Y - ButtonY, 160, "Back", () => mode = Mode.Title, sound: ReturnSound);
    }

    // ---- The starting binder ----

    private void ComposeStartingBinder()
    {
        var view = odyssey.StartingBinder();
        var w = frameSize.X;
        const float cardScale = .85f; var gap = 14f;
        var cell = sprite.Size * cardScale;
        var rowWidth = view.Cards.Count * cell.X + (view.Cards.Count - 1) * gap;
        var x = (w - rowWidth) / 2; var y = Top + 30;
        foreach (var card in view.Cards)
        {
            Card(new(x, y), card, cardScale, true, caption: $"{card.Power:0.00}");
            x += cell.X + gap;
        }
        y += cell.Y + 40;
        Label(0, y, $"Mean power {view.MeanPower:0.00}" + (view.Rank > 0 ? $"  ·  Crystal Rank {view.Rank}" : ""), OdysseyChrome.Axis, 15, OdysseyChrome.White, w, 1);
        Label(0, y + 26, "Five of these play each duel: at most one 5-star card, and two of 4 stars or more. Every win adds a card to the binder.", OdysseyChrome.Axis, 13, OdysseyChrome.Grey, w, 1);
        Label(0, y + 48, view.ReDealsLeft > 0 ? $"Not happy with the hand? Re-deal up to {view.ReDealsLeft} more time{(view.ReDealsLeft == 1 ? "" : "s")}." : "No re-deals left. Make the best of it.", OdysseyChrome.Axis, 13, OdysseyChrome.Grey, w, 1);
        Button(w / 2 - 170, frameSize.Y - ButtonY, 160, view.ReDealsLeft > 0 ? $"Re-deal ({view.ReDealsLeft} left)" : "Re-deal", () => odyssey.ReDeal(), view.ReDealsLeft > 0, false, "No re-deals left.", FlipSound);
        Button(w / 2 + 10, frameSize.Y - ButtonY, 160, "Set out", () => odyssey.Begin(), true, true);
    }

    // ---- The world's title card ----

    private void ComposeWorldTitle()
    {
        var view = odyssey.WorldTitle();
        var world = view.World; var boss = view.Boss;
        var w = frameSize.X;
        Label(0, Top + 6, world.Title, OdysseyChrome.Miedinger, 30, OdysseyChrome.Gold, w, 1);
        Label(0, Top + 48, world.Subtitle, OdysseyChrome.Axis, 17, OdysseyChrome.White, w, 1);
        var flavourHeight = LabelHeight(world.Flavour, 13, 700);
        Label((w - 700) / 2, Top + 78, world.Flavour, OdysseyChrome.Axis, 13, OdysseyChrome.Grey, 700, 1);
        var y = Top + 86 + flavourHeight + 12;
        var panel = OdRect.At(Margin, y, w - Margin * 2, 214);
        Box(panel);
        var x = panel.X + 18;
        Label(x, y + 12, "AT THE END OF THE WORLD", OdysseyChrome.Axis, 12, OdysseyChrome.Gold);
        Label(x, y + 32, boss.Name, OdysseyChrome.Miedinger, 20, OdysseyChrome.White);
        Label(x, y + 60, boss.Title, OdysseyChrome.Axis, 13, OdysseyChrome.Grey);
        if (boss.Plan.Line.Length > 0) Label(x, y + 82, "\"" + boss.Plan.Line + "\"", OdysseyChrome.Axis, 13, OdysseyChrome.White, 500);
        Label(x, y + 128, "Rules", OdysseyChrome.Axis, 12, OdysseyChrome.Grey);
        Pills(x + 60, y + 124, 460, OdysseyBalance.Listed(boss.Rules).Select(r => RulePill(r)));
        Label(x, y + 160, "Materia", OdysseyChrome.Axis, 12, OdysseyChrome.Grey);
        if (view.BossMateria.Count == 0) Label(x + 60, y + 160, "None", OdysseyChrome.Axis, 12, OdysseyChrome.Grey);
        else Pills(x + 60, y + 156, 460, view.BossMateria.Select(MateriaPill));
        Card(new(panel.Max.X - 18 - sprite.Size.X, y + 28), boss.Signature, 1, false, caption: "Signature card");
        // Five duels, then the boss.
        y = panel.Max.Y + 18;
        var pipX = w / 2 - 70;
        for (var i = 0; i < OdysseyBalance.DuelsPerWorld; i++) widgets.Add(new CircleW(new(pipX + i * 24, y + 8), 6, 0xFF2A2A2A, OdysseyChrome.DimGold));
        widgets.Add(new CircleW(new(pipX + 5 * 24 + 6, y + 8), 10, 0xFF3A2020, OdysseyChrome.Red));
        Label(pipX + 5 * 24 + 24, y, "Five duels, then the boss", OdysseyChrome.Axis, 12, OdysseyChrome.Grey);
        Button((w - 180) / 2, frameSize.Y - ButtonY, 180, "Set out", () => odyssey.EnterPath(), true, true);
    }

    // ---- The path ----

    private void ComposePath()
    {
        var view = odyssey.Path();
        var w = frameSize.X;
        var n = view.Choices.Count;
        var panelWidth = n >= 3 ? 300 : 330; var gap = n >= 3 ? 24 : 40;
        var total = n * panelWidth + (n - 1) * gap;
        var x = (w - total) / 2; var y = Top + 4;
        for (var i = 0; i < n; i++)
        {
            var index = i;
            var o = view.Choices[i];
            var panel = OdRect.At(x, y, panelWidth, 380);
            Box(panel, 1, false, () => Challenge(index, o));
            var (kind, kindColour) = o.Kind switch
            {
                OpponentKind.Boss => ("BOSS", OdysseyChrome.Red), OpponentKind.Elite => ("ELITE", OdysseyChrome.Gold),
                OpponentKind.Secret => ("SECRET DUEL", OdysseyChrome.Purple), _ => ("DUELIST", OdysseyChrome.Grey),
            };
            var kindWidth = chrome.Measure(OdysseyChrome.Axis, 11, kind).X + 14;
            widgets.Add(new PillW(OdRect.At(panel.Centre.X - kindWidth / 2, y + 14, kindWidth, 17), kind, kindColour, null, false, 11));
            Label(x + 8, y + 40, o.Name, OdysseyChrome.Miedinger, 17, OdysseyChrome.White, panelWidth - 16, 1);
            Label(x + 8, y + 66, o.Title, OdysseyChrome.Axis, 12, OdysseyChrome.Grey, panelWidth - 16, 1);
            var cardScale = .8f;
            if (o.Scanned is { Count: > 0 } hand)
            {
                // Libra: their whole hand in a row, as small as it needs to be to fit the panel.
                const float spacing = 4;
                cardScale = Math.Min(.8f, (panelWidth - 28 - (hand.Count - 1) * spacing) / (hand.Count * sprite.Size.X));
                var rowWidth = hand.Count * sprite.Size.X * cardScale + (hand.Count - 1) * spacing;
                for (var k = 0; k < hand.Count; k++)
                    Card(new(panel.Centre.X - rowWidth / 2 + k * (sprite.Size.X * cardScale + spacing), y + 90), hand[k], cardScale, false);
                Label(x + 8, y + 90 + sprite.Size.Y * cardScale + 3, "Their hand · scanned by Libra", OdysseyChrome.Axis, 12, OdysseyChrome.Yellow, panelWidth - 16, 1);
            }
            else Card(new(panel.Centre.X - sprite.Size.X * cardScale / 2, y + 90), o.Signature, cardScale, false, caption: o.Kind == OpponentKind.Regular ? "Their best card" : "Signature card");
            var cy = y + 90 + sprite.Size.Y * cardScale + 24;
            Label(x + 14, cy + 3, "Rules", OdysseyChrome.Axis, 11, OdysseyChrome.Grey);
            cy += Pills(x + 56, cy, panelWidth - 70, OdysseyBalance.Listed(o.Rules).Select(r => RulePill(r)), 11) + 8;
            Label(x + 14, cy + 3, "Materia", OdysseyChrome.Axis, 11, OdysseyChrome.Grey);
            if (o.Materia.Count == 0) { Label(x + 56, cy + 3, "None", OdysseyChrome.Axis, 11, OdysseyChrome.Grey); cy += 25; }
            else cy += Pills(x + 56, cy, panelWidth - 70, o.Materia.Select(MateriaPill), 11) + 8;
            Label(x + 14, cy + 2, o.RewardNote, OdysseyChrome.Axis, 11, OdysseyChrome.White, panelWidth - 28);
            Label(x + 8, y + 306, $"Skill {o.Plan.AiLevel} of 12  ·  Deck power {o.Plan.Power:0.00}", OdysseyChrome.Axis, 11, OdysseyChrome.Grey, panelWidth - 16, 1);
            Button(panel.Centre.X - 90, y + 332, 180, o.Kind == OpponentKind.Boss ? "Face the boss" : "Challenge", () => Challenge(index, o), true, n == 1);
            x += panelWidth + gap;
        }
    }

    private void Challenge(int index, OpponentView o) => odyssey.ChoosePath(index);

    // ---- The deck ----

    private void ComposeDeck()
    {
        var view = odyssey.Deck();
        var w = frameSize.X;
        var items = view.Items;
        var picking = pickingItem >= 0 && pickingItem < items.Count ? items[pickingItem] : null;
        // The opponent's rules (an Echo Screen picks one of them) and Materia, under the heading.
        Label(Margin, 60, "Rules", OdysseyChrome.Axis, 12, OdysseyChrome.Grey);
        Pills(Margin + 56, 56, 700, OdysseyBalance.Listed(view.RulesInPlay).Select(r =>
        {
            Action? click = picking is { Item.Kind: ItemKind.EchoScreen } && r != TriadRule.SuddenDeath ? () => UseItem(pickingItem, rule: r) : null;
            return RulePill(r, click);
        }));
        // Sudden Death is left out of every rule list (every duel plays it), so it's said once here.
        Label(760, 60, "Every duel: a draw is played again (Sudden Death).", OdysseyChrome.Axis, 12, OdysseyChrome.Grey, w - Margin - 760);
        Label(Margin, 86, "Their Materia", OdysseyChrome.Axis, 12, OdysseyChrome.Grey);
        var cancelledBy = view.Buffs.FirstOrDefault(b => b.StartsWith("Remedy") || b.StartsWith("Ribbon"))?.Split(':')[0];
        if (view.TheirMateria.Count == 0) Label(Margin + 92, 86, cancelledBy != null ? $"Cancelled by your {cancelledBy}" : "None", OdysseyChrome.Axis, 12, OdysseyChrome.Grey);
        else Pills(Margin + 92, 82, 700, view.TheirMateria.Select(MateriaPill));
        // Left: the deck's five slots, then the binder.
        var y = 118f;
        Label(Margin, y, $"Your deck · {view.Deck.Count} of {OdysseyBalance.DeckSize}", OdysseyChrome.Axis, 14, OdysseyChrome.Gold);
        const float slotScale = .8f; var slot = sprite.Size * slotScale; var slotGap = 12f;
        y += 22;
        for (var i = 0; i < OdysseyBalance.DeckSize; i++)
        {
            var at = new Vector2(Margin + i * (slot.X + slotGap), y);
            if (i < view.Deck.Count)
            {
                var card = view.Deck[i];
                var uid = card.Uid;
                var potion = view.Buffs.Any(b => b == $"Potion on {card.Card.Name}");
                Action click = picking is { Item.NeedsCard: true } ? () => UseItem(pickingItem, uid) : () => odyssey.ToggleDeckCard(uid);
                Card(at, card, slotScale, true, click, false, false, potion ? "Potion" : null, OdysseyChrome.Green);
            }
            else widgets.Add(new RectW(new(at, slot), 0x50000000, OdysseyChrome.DimGold, 6));
        }
        var hintX = Margin + 5 * (slot.X + slotGap) + 8;
        var hintWidth = 760 - 16 - hintX;
        if (picking is { Item.AddsRule: true })
        {
            // Queen of Cards' Favour: the rules it could add, beside the deck; rules bind both sides.
            var text = $"Add a rule with the {picking.Item.Name}. It binds both of you.";
            Label(hintX, y, text, OdysseyChrome.Axis, 13, OdysseyChrome.Gold, hintWidth);
            var addable = odyssey.AddableRules();
            if (addable.Count == 0) Label(hintX, y + LabelHeight(text, 13, hintWidth) + 8, "No rule of its pool fits with theirs.", OdysseyChrome.Axis, 12, OdysseyChrome.Grey, hintWidth);
            else Pills(hintX, y + LabelHeight(text, 13, hintWidth) + 8, hintWidth, addable.Select(r => (setup.RuleName(r), OdysseyChrome.Green, (string?)setup.RuleName(r), (string?)OdysseyChrome.RuleText(r), (Action?)(() => UseItem(pickingItem, rule: r)), false)));
        }
        else if (picking != null)
            Label(hintX, y + 30, picking.Item.NeedsCard ? $"Choose a deck card for the {picking.Item.Name}." : $"Choose one of their rules to cancel with the {picking.Item.Name}.", OdysseyChrome.Axis, 13, OdysseyChrome.Gold, hintWidth);
        else Label(hintX, y + 30, "Click a binder card to add it, a deck card to take it out.", OdysseyChrome.Axis, 12, OdysseyChrome.Grey, 200);
        y += slot.Y + 22;
        Label(Margin, y, "Binder", OdysseyChrome.Axis, 14, OdysseyChrome.Gold);
        y += 22;
        BinderGrid(Margin, y, 700, 4, .72f, view.Binder, v => v.Allowed || v.InDeck ? () => odyssey.ToggleDeckCard(v.Uid) : () => Fail(v.Reason ?? "That card can't join the deck."),
            v => v.InDeck, v => !v.Allowed && !v.InDeck);
        // Right: their hand, your Materia, your Items, this duel's buffs.
        var rx = 760f; var rw = w - Margin - rx;
        y = 118;
        Label(rx, y, "Their hand", OdysseyChrome.Axis, 14, OdysseyChrome.Gold);
        y += 22;
        if (view.OpponentCardsShown.Count == 0) Label(rx, y + 2, view.RulesInPlay.Contains(TriadRule.ThreeOpen) ? "Hidden until the deal: Three Open shows three." : "Hidden. An Ether or Libra Materia would show it.", OdysseyChrome.Axis, 12, OdysseyChrome.Grey, rw);
        else
        {
            const float theirScale = .6f; var their = sprite.Size * theirScale;
            for (var i = 0; i < view.OpponentCardsShown.Count; i++) Card(new(rx + i * (their.X + 8), y), view.OpponentCardsShown[i], theirScale, false);
        }
        y += 92;
        Label(rx, y, "Your Materia", OdysseyChrome.Axis, 14, OdysseyChrome.Gold);
        y += 22;
        if (view.MyMateria.Count == 0) Label(rx, y + 2, "None yet.", OdysseyChrome.Axis, 12, OdysseyChrome.Grey);
        foreach (var materia in view.MyMateria)
        {
            MateriaIcon(rx, y, 22, materia);
            Label(rx + 30, y + 3, materia.Name, OdysseyChrome.Axis, 13, OdysseyChrome.MateriaTint(materia.Colour));
            Label(rx + 150, y + 4, materia.Description, OdysseyChrome.Axis, 11, OdysseyChrome.Grey, rw - 150);
            // A description that wraps pushes the next row down.
            y += Math.Max(26, LabelHeight(materia.Description, 11, rw - 150) + 10);
        }
        y = Math.Max(y + 10, 392);
        Label(rx, y, "Items", OdysseyChrome.Axis, 14, OdysseyChrome.Gold);
        y += 22;
        if (items.Count == 0) Label(rx, y + 2, "None.", OdysseyChrome.Axis, 12, OdysseyChrome.Grey);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i]; var index = i;
            ItemIcon(rx, y, 22, item.Item.Name, item.Item.Description, item.Item.Icon);
            Label(rx + 30, y + 4, item.Item.Name, OdysseyChrome.Axis, 13, OdysseyChrome.White);
            var needsTarget = item.Item.NeedsCard || item.Item.NeedsRule;
            var active = pickingItem == index;
            Button(rx + rw - 72, y - 2, 72, active ? "Cancel" : item.Item.Held ? "Held" : "Use", () =>
            {
                if (active) { pickingItem = -1; return; }
                if (needsTarget) { pickingItem = index; return; }
                UseItem(index);
            }, item.CanUse || active, false, item.Reason);
            y += 30;
        }
        y = Math.Max(y + 10, 510);
        Label(rx, y, "This duel", OdysseyChrome.Axis, 14, OdysseyChrome.Gold);
        y += 22;
        if (view.Buffs.Count == 0 && !view.PhoenixHeld) Label(rx, y + 2, "No items in play.", OdysseyChrome.Axis, 12, OdysseyChrome.Grey);
        foreach (var buff in view.Buffs) { Label(rx, y + 2, buff, OdysseyChrome.Axis, 12, OdysseyChrome.Green, rw); y += 18; }
        if (view.PhoenixHeld) { Label(rx, y + 2, "A Phoenix Down is held: a loss deals the duel again.", OdysseyChrome.Axis, 12, OdysseyChrome.Yellow, rw); y += 18; }
        // Duel!
        if (view.WhyNot != null) Label(Margin, frameSize.Y - ButtonY + 7, view.WhyNot, OdysseyChrome.Axis, 13, OdysseyChrome.Grey);
        Button((w - 180) / 2, frameSize.Y - ButtonY, 180, "Duel!", () => odyssey.StartDuel(), view.CanStart, true, view.WhyNot);
    }

    private void UseItem(int index, int cardUid = -1, TriadRule rule = TriadRule.SuddenDeath)
    {
        var item = odyssey.Deck().Items[index];
        if (odyssey.ItemRefusal(index, cardUid, rule) is { } why) { Fail(why); return; }
        if (item.Item.Kind == ItemKind.SmokeBomb)
        {
            Ask("Use the Smoke Bomb?", "This duel is skipped: no reward and no gil. The bomb is used up.", "Use it", () => { pickingItem = -1; odyssey.UseItem(index); });
            return;
        }
        pickingItem = -1;
        odyssey.UseItem(index, cardUid, rule);
        Effects.Sound(FlipSound);
    }

    // ---- Rewards ----

    private void ComposeReward()
    {
        var view = odyssey.Reward();
        var w = frameSize.X;
        if (view.Claiming)
        {
            Label(0, Top, $"Claim one of {odyssey.Run?.Opponent?.Name ?? "their"} cards", OdysseyChrome.Axis, 15, OdysseyChrome.White, w, 1);
            var n = view.Claimable.Count; var gap = 20f;
            var x = (w - (n * sprite.Size.X + (n - 1) * gap)) / 2;
            for (var i = 0; i < n; i++)
            {
                var index = i; var card = view.Claimable[i];
                Card(new(x, Top + 50), card, 1, false, () => { odyssey.ClaimCard(index); Effects.Sound(FlipSound); }, caption: $"Power {card.Power:0.00}");
                x += sprite.Size.X + gap;
            }
            Label(0, Top + 50 + sprite.Size.Y + 36, "The card joins your binder with everything it carries.", OdysseyChrome.Axis, 13, OdysseyChrome.Grey, w, 1);
            Button((w - 160) / 2, frameSize.Y - ButtonY, 160, "Back", () => odyssey.CancelClaim(), sound: ReturnSound);
            return;
        }
        var count = view.Choices.Count;
        var gapX = 16f;
        var panelWidth = Math.Min(230, (w - Margin * 2 - (count - 1) * gapX) / count);
        var panelHeight = 352f;
        var left = (w - (count * panelWidth + (count - 1) * gapX)) / 2;
        var y = Top + 4;
        Label(0, y - 2, view.Starting ? "Choose one Enhancement to set out with." : view.Boss && view.Stage == 1 ? "Choose one Materia." : "Choose one reward.", OdysseyChrome.Axis, 13, OdysseyChrome.Grey, w, 1);
        y += 20;
        for (var i = 0; i < count; i++)
        {
            var c = view.Choices[i]; var index = i;
            var panel = OdRect.At(left + i * (panelWidth + gapX), y, panelWidth, panelHeight);
            Box(panel, c.Enabled ? 1 : .6f, false, c.Enabled ? () => odyssey.PickReward(index) : null);
            var cx = panel.Centre.X;
            if (c.Kind == RewardKind.ClaimCard)
            {
                var signature = view.Claimable.FirstOrDefault();
                if (signature != null) widgets.Add(new CardW(new(new(cx - sprite.Size.X * .5f / 2, y + 16), sprite.Size * .5f), signature.Card, .5f, false, false));
            }
            else widgets.Add(new IconW(OdRect.At(cx - 28, y + 18, 56, 56), setup.IconOf(c.Icon), c.Kind is RewardKind.Materia or RewardKind.MateriaSlot or RewardKind.MasterMateria ? OdysseyChrome.MateriaTint(OdysseyContent.MateriaOf(c.Name.ToLowerInvariant().Replace(' ', '-'))?.Colour ?? MateriaColour.Purple) : 0xFFFFFFFF));
            Label(panel.X + 8, y + 86, c.Name, OdysseyChrome.Axis, 15, c.Enabled ? OdysseyChrome.White : OdysseyChrome.Dark, panelWidth - 16, 1);
            var kind = c.Kind switch { RewardKind.ClaimCard => "Card", RewardKind.Enhancement => "Enhancement", RewardKind.Materia or RewardKind.MasterMateria => "Materia", RewardKind.MateriaSlot => "Materia slot", _ => "Item" };
            Label(panel.X + 8, y + 130, c.Kind == RewardKind.ClaimCard ? kind : $"{c.Rarity} {kind}", OdysseyChrome.Axis, 12, c.Kind == RewardKind.ClaimCard ? OdysseyChrome.Grey : OdysseyChrome.RarityColour(c.Rarity), panelWidth - 16, 1);
            Label(panel.X + 12, y + 154, c.Description, OdysseyChrome.Axis, 12, c.Enabled ? OdysseyChrome.Grey : OdysseyChrome.Dark, panelWidth - 24);
            if (!c.Enabled && c.Reason != null) Label(panel.X + 12, y + 262, c.Reason, OdysseyChrome.Axis, 11, OdysseyChrome.Red, panelWidth - 24, 1);
            Button(cx - 70, y + panelHeight - 44, 140, c.Kind == RewardKind.ClaimCard ? "Choose a card" : "Take", () => odyssey.PickReward(index), c.Enabled, false, c.Reason);
        }
        if (view.RerollPrice is { } price)
            Button(Margin, frameSize.Y - ButtonY, 190, price == 0 ? "Reroll · free (Card Sharp)" : $"Reroll · {price} gil", () => { odyssey.Reroll(); if (price > 0) Effects.Sound(CoinSound); }, view.CanReroll, false,
                price > view.Mgp ? "Not enough gil." : null, PageSound);
        Button(w - Margin - 140, frameSize.Y - ButtonY, 140, "Skip", () => odyssey.SkipReward(), sound: ReturnSound);
    }

    // ---- Upgrade: an enhancement's card and side ----

    private static readonly string[] SideNames = ["Top", "Right", "Bottom", "Left"], CornerNames = ["Top-right", "Bottom-right", "Bottom-left", "Top-left"],
        TypeNames = ["Primal", "Scion", "Society", "Garlean"];

    private void ComposeUpgrade()
    {
        var view = odyssey.Upgrade();
        var e = view.Enhancement;
        var w = frameSize.X;
        ItemIcon(Margin, Top - 4, 40, e.Name, e.Description, e.Icon);
        // A long description wraps to a second line: the caption goes under it, and the binder under both.
        var described = LabelHeight(e.Description, 14, 540);
        Label(Margin + 52, Top - 2, e.Description, OdysseyChrome.Axis, 14, OdysseyChrome.White, 540);
        Label(Margin + 52, Top + described + 2, $"{e.Rarity} Enhancement · choose the card it goes on. Once applied it can't be undone.", OdysseyChrome.Axis, 12, OdysseyChrome.RarityColour(e.Rarity), 540);
        if (view.Back is { } back) Button(Margin, frameSize.Y - ButtonY, 220, back, () => odyssey.GoBack(), sound: ReturnSound);
        var gridY = Top + Math.Max(54, described + 36);
        Label(Margin, gridY, "Binder", OdysseyChrome.Axis, 14, OdysseyChrome.Gold);
        BinderGrid(Margin, gridY + 22, 600, 5, .72f, view.Binder, v => () => { selectedUid = v.Uid; if (!e.NeedsChoice) choice = 0; }, v => v.Uid == selectedUid,
            v => v.Enhancements >= OdysseyBalance.MaxEnhancements);
        var rx = 660f;
        Label(rx, gridY, "Preview", OdysseyChrome.Axis, 14, OdysseyChrome.Gold);
        if (selectedUid < 0 || view.Binder.All(v => v.Uid != selectedUid))
        {
            Label(rx, gridY + 26, "Pick a card from your binder to see it before and after.", OdysseyChrome.Axis, 13, OdysseyChrome.Grey, w - Margin - rx);
            return;
        }
        var selected = view.Binder.First(v => v.Uid == selectedUid);
        var needs = e.NeedsChoice;
        var picked = needs ? choice : 0;
        var preview = odyssey.UpgradePreview(selectedUid, Math.Max(0, needs && e.Kind == EnhancementKind.SetType ? picked : picked));
        const float cardScale = .9f; var cell = sprite.Size * cardScale;
        var cy = gridY + 26;
        var before = new CardView(selected.Uid, preview.Before, selected.Base, odyssey.Power(preview.Before));
        var after = new CardView(selected.Uid, preview.After, selected.Base, odyssey.Power(preview.After));
        Card(new(rx, cy), before, cardScale, true, caption: "Before");
        Label(rx + cell.X + 8, cy + cell.Y / 2 - 16, "▶", OdysseyChrome.Axis, 22, OdysseyChrome.Gold, 40, 1);
        Card(new(rx + cell.X + 56, cy), after, cardScale, true, caption: picked >= 0 || !needs ? $"After · power {after.Power:0.00}" : "After");
        cy += cell.Y + 40;
        if (needs)
        {
            var names = e.ChoosesSide ? SideNames : e.ChoosesCorner ? CornerNames : TypeNames;
            Label(rx, cy, e.Kind switch { EnhancementKind.Shift => "Which side gains 2?", EnhancementKind.SwapCorner => "Which corner swaps?", _ when e.ChoosesSide => "Which side?", _ when e.ChoosesCorner => "Which corner?", _ => "Which type?" },
                OdysseyChrome.Axis, 13, OdysseyChrome.White);
            cy += 22;
            for (var i = 0; i < 4; i++)
            {
                var value = e.Kind == EnhancementKind.SetType ? i + 1 : i;
                Button(rx + i % 2 * 128, cy + i / 2 * 36, 120, names[i], () => choice = value, true, choice == value, sound: PickSound);
            }
            cy += 80;
        }
        var ready = preview.Refusal == null && (!needs || choice >= 0 && (e.Kind != EnhancementKind.SetType || choice >= 1));
        if (preview.Refusal != null && (!needs || choice >= 0)) Label(rx, cy, preview.Refusal, OdysseyChrome.Axis, 13, OdysseyChrome.Red, w - Margin - rx);
        else if (preview.ReplacesPerk) Label(rx, cy, $"This replaces its {OdysseyChrome.PerkName(preview.Before.Perk)} perk.", OdysseyChrome.Axis, 13, OdysseyChrome.Yellow, w - Margin - rx);
        else if (needs && choice < 0) Label(rx, cy, "Choose above.", OdysseyChrome.Axis, 13, OdysseyChrome.Grey);
        cy += 30;
        var uid = selectedUid; var chosen = picked;
        Button(rx, cy, 180, "Apply", () => Ask($"Apply {e.Name}?", $"{preview.Before.Name} becomes {Sides(preview.After)}{(preview.After.Perk != TriadPerk.None ? $" with {OdysseyChrome.PerkName(preview.After.Perk)}" : "")}. Enhancements can't be undone.",
            "Apply", () => { odyssey.ApplyUpgrade(uid, chosen); Effects.Sound(FlipSound); }), ready, true, preview.Refusal ?? "Choose first.");
    }
    private static string Sides(TriadCard c) => $"{N(c.Top)} {N(c.Right)} {N(c.Bottom)} {N(c.Left)}";
    private static string N(int side) => side >= 10 ? "A" : side.ToString();

    // ---- Materia: the slots are full ----

    private void ComposeMateriaSwap()
    {
        var view = odyssey.MateriaSwap();
        var w = frameSize.X;
        var panel = OdRect.At(Margin, Top - 4, w - Margin * 2, 84);
        Box(panel);
        MateriaIcon(panel.X + 16, panel.Y + 18, 48, view.New);
        Label(panel.X + 80, panel.Y + 12, view.New.Name, OdysseyChrome.Miedinger, 17, OdysseyChrome.MateriaTint(view.New.Colour));
        Label(panel.X + 80, panel.Y + 36, $"{view.New.Rarity} · new", OdysseyChrome.Axis, 12, OdysseyChrome.RarityColour(view.New.Rarity));
        Label(panel.X + 80, panel.Y + 54, view.New.Description, OdysseyChrome.Axis, 13, OdysseyChrome.White, panel.Width - 100);
        var y = panel.Max.Y + 14;
        Label(Margin, y, view.LegendaryLimited
            ? $"You can slot only {(view.LegendaryLimit == 1 ? "one Legendary Materia" : $"{view.LegendaryLimit} Legendary Materia")} at a time. Replace {(view.LegendaryLimit == 1 ? "it" : "one of them")}, or let the new one go."
            : $"Your {view.Slots.Count} slots are full. Replace one, or let the new Materia go.", OdysseyChrome.Axis, 13, OdysseyChrome.Grey, w - Margin * 2);
        y += 26;
        for (var i = 0; i < view.Slots.Count; i++)
        {
            var slot = view.Slots[i]; var index = i;
            MateriaIcon(Margin + 8, y + 4, 28, slot);
            Label(Margin + 48, y + 2, slot.Name, OdysseyChrome.Axis, 14, OdysseyChrome.MateriaTint(slot.Colour));
            Label(Margin + 48, y + 20, slot.Description, OdysseyChrome.Axis, 11, OdysseyChrome.Grey, w - Margin * 2 - 200);
            var refusal = i < view.Refusals.Count ? view.Refusals[i] : null;
            Button(w - Margin - 130, y + 4, 130, "Replace", () => Ask($"Replace {slot.Name}?", $"{slot.Name} is lost; {view.New.Name} takes its slot.", "Replace", () => odyssey.KeepMateria(index)),
                refusal == null, false, refusal);
            y += 42;
        }
        if (view.Back is { } back) Button(Margin, frameSize.Y - ButtonY, 220, back, () => odyssey.GoBack(), sound: ReturnSound);
        Button(view.Back != null ? w - Margin - 240 : (w - 240) / 2, frameSize.Y - ButtonY, 240, $"Let {view.New.Name} go", () => Ask($"Discard {view.New.Name}?", "The new Materia is lost.", "Discard", () => odyssey.DiscardNewMateria(), danger: true), sound: ReturnSound);
    }

    // ---- Events ----

    private void ComposeEvent()
    {
        var view = odyssey.Event();
        var w = frameSize.X;
        var textWidth = w - Margin * 2;
        var y = Top;
        var textHeight = LabelHeight(view.Text, 14, textWidth);
        Label(Margin, y, view.Text, OdysseyChrome.Axis, 14, OdysseyChrome.White, textWidth);
        y += textHeight + 14;
        if (view.Offered is { } offered)
        {
            Card(new((w - sprite.Size.X) / 2, y), offered, 1, true, caption: $"Power {offered.Power:0.00}");
            y += sprite.Size.Y + 30;
        }
        var choices = view.Choices;
        if (eventChoice >= 0 && eventChoice < choices.Count && choices[eventChoice].NeedsCard && !view.Resolved)
        {
            // A choice that wants a card (and a type): the binder, then Confirm.
            var pick = choices[eventChoice];
            Label(Margin, y, pick.NeedsType ? "Choose a card, and the type it takes." : "Choose a card.", OdysseyChrome.Axis, 13, OdysseyChrome.Gold);
            y += 22;
            var index = eventChoice;
            y += BinderGrid(Margin, y, textWidth, 2, .66f, view.Binder, v => () => { if (odyssey.EventCardRefusal(index, v.Uid) is { } why) Fail(why); else selectedUid = v.Uid; },
                v => v.Uid == selectedUid, v => odyssey.EventCardRefusal(index, v.Uid) != null) + 10;
            if (pick.NeedsType)
            {
                for (var i = 0; i < 4; i++) { var t = i + 1; Button(Margin + i * 128, y, 120, TypeNames[i], () => eventType = t, true, eventType == t, sound: PickSound); }
                y += 36;
            }
            var ready = selectedUid >= 0 && (!pick.NeedsType || eventType >= 1);
            var uid = selectedUid; var type = eventType;
            Button(w / 2 - 170, frameSize.Y - ButtonY, 160, "Confirm", () => odyssey.ChooseEvent(index, uid, type), ready, true, "Choose first.");
            Button(w / 2 + 10, frameSize.Y - ButtonY, 160, "Back", () => { eventChoice = -1; selectedUid = -1; eventType = 0; }, sound: ReturnSound);
            return;
        }
        var buttonWidth = 420f; var x = (w - buttonWidth) / 2;
        y = Math.Max(y, frameSize.Y - ButtonY - (choices.Count - 1) * 36 - 60);
        for (var i = 0; i < choices.Count; i++)
        {
            var c = choices[i]; var index = i;
            Button(x, y, buttonWidth, c.Text, () => { if (c.NeedsCard) { eventChoice = index; selectedUid = -1; eventType = 0; page = 0; } else odyssey.ChooseEvent(index); }, c.Enabled, false, c.Reason);
            y += 36;
        }
    }

    // ---- The Gold Saucer Card Shop ----

    private void ComposeShop()
    {
        var view = odyssey.Shop();
        var w = frameSize.X;
        if (shopPack)
        {
            Label(0, Top, $"Choose a card from the pack · {view.PackPrice} gil", OdysseyChrome.Axis, 15, OdysseyChrome.White, w, 1);
            var n = view.Pack.Count; var gap = 24f;
            var px = (w - (n * sprite.Size.X + (n - 1) * gap)) / 2;
            for (var i = 0; i < n; i++)
            {
                var index = i; var card = view.Pack[i];
                Card(new(px, Top + 50), card, 1, true, () => Ask($"Take {card.Card.Name}?", $"{view.PackPrice} gil for the card.", "Buy", () => { odyssey.BuyPackCard(index); Effects.Sound(CoinSound); shopPack = false; }),
                    caption: $"Power {card.Power:0.00}");
                px += sprite.Size.X + gap;
            }
            Button((w - 160) / 2, frameSize.Y - ButtonY, 160, "Back", () => shopPack = false, sound: ReturnSound);
            return;
        }
        if (shopRemoving)
        {
            Label(0, Top, $"Remove a card from the binder · {view.RemovalPrice} gil (the price rises each time)", OdysseyChrome.Axis, 15, OdysseyChrome.White, w, 1);
            BinderGrid(Margin, Top + 30, w - Margin * 2, 4, .72f, view.Binder,
                v => v.Allowed ? () => Ask($"Remove {v.Card.Name}?", $"{view.RemovalPrice} gil, and the card is gone from the binder for good.", "Remove", () => { odyssey.RemoveCard(v.Uid); Effects.Sound(CoinSound); }, danger: true)
                    : () => Fail(v.Reason ?? "That card can't be removed."), null, v => !v.Allowed);
            Button((w - 160) / 2, frameSize.Y - ButtonY, 160, "Back", () => shopRemoving = false, sound: ReturnSound);
            return;
        }
        if (shopSelling)
        {
            Label(0, Top, "Sell a card to the shop · one a visit, priced by its stars and Enhancements", OdysseyChrome.Axis, 15, OdysseyChrome.White, w, 1);
            var prices = view.ForSale.ToDictionary(s => s.Card.Uid, s => s.Price);
            BinderGrid(Margin, Top + 30, w - Margin * 2, 4, .72f, [.. view.ForSale.Select(s => s.Card)],
                v => v.Allowed ? () => Ask($"Sell {v.Card.Name}?", $"The shop pays {prices[v.Uid]} gil, and the card is gone from the binder for good.", "Sell",
                        () => { odyssey.SellCard(v.Uid); Effects.Sound(CoinSound); shopSelling = false; }, danger: true)
                    : () => Fail(v.Reason ?? "That card can't be sold."), null, v => !v.Allowed, v => $"{prices[v.Uid]} gil");
            Button((w - 160) / 2, frameSize.Y - ButtonY, 160, "Back", () => shopSelling = false, sound: ReturnSound);
            return;
        }
        var columns = 4; var gapX = 16f; var panelWidth = (w - Margin * 2 - (columns - 1) * gapX) / columns; var panelHeight = 232f;
        for (var i = 0; i < view.Wares.Count; i++)
        {
            var ware = view.Wares[i]; var index = i;
            var panel = OdRect.At(Margin + i % columns * (panelWidth + gapX), Top - 6 + i / columns * (panelHeight + 14), panelWidth, panelHeight);
            Box(panel, ware.Sold ? .5f : 1);
            var textColour = ware.Sold ? OdysseyChrome.Dark : OdysseyChrome.White;
            if (ware.Kind == WareKind.Pack)
            {
                const float packScale = .46f; var small = sprite.Size * packScale;
                for (var j = 0; j < view.Pack.Count; j++)
                    widgets.Add(new CardW(new(new(panel.X + 14 + j * (small.X + 6), panel.Y + 50), small), view.Pack[j].Card, packScale, true, !ware.Sold, view.Pack[j].Delta, ware.Sold) { TipTitle = CardTitle(view.Pack[j]), Tip = CardTip(view.Pack[j]) });
            }
            else
            {
                var tint = ware.Kind == WareKind.Materia && OdysseyContent.MateriaOf(ware.Name.ToLowerInvariant().Replace(' ', '-')) is { } m ? OdysseyChrome.MateriaTint(m.Colour) : 0xFFFFFFFFu;
                widgets.Add(new IconW(OdRect.At(panel.X + 14, panel.Y + 14, 44, 44), setup.IconOf(ware.Icon), ware.Sold ? 0xFF606060 : tint));
            }
            Label(panel.X + 68, panel.Y + 14, ware.Name, OdysseyChrome.Axis, 14, textColour, panelWidth - 82);
            var kind = ware.Kind switch { WareKind.Enhancement => "Enhancement", WareKind.Materia => "Materia", WareKind.Item => "Item", _ => "Card pack" };
            if (ware.Kind == WareKind.Pack) Label(panel.X + 14, panel.Y + 112, "Three cards of the next world's tier. Open it to see them; one is yours.", OdysseyChrome.Axis, 12, ware.Sold ? OdysseyChrome.Dark : OdysseyChrome.Grey, panelWidth - 28);
            else
            {
                Label(panel.X + 68, panel.Y + 52, $"{ware.Rarity} {kind}", OdysseyChrome.Axis, 11, ware.Sold ? OdysseyChrome.Dark : OdysseyChrome.RarityColour(ware.Rarity), panelWidth - 82);
                Label(panel.X + 14, panel.Y + 76, ware.Description, OdysseyChrome.Axis, 12, ware.Sold ? OdysseyChrome.Dark : OdysseyChrome.Grey, panelWidth - 28);
            }
            Label(panel.X + 14, panel.Y + panelHeight - 66, ware.Sold ? "Sold" : $"{ware.Price} gil", OdysseyChrome.Axis, 14, ware.Sold ? OdysseyChrome.Dark : view.Mgp >= ware.Price ? OdysseyChrome.Gold : OdysseyChrome.Red);
            var label = ware.Kind == WareKind.Pack ? "Open the pack" : "Buy";
            Button(panel.X + 14, panel.Y + panelHeight - 42, panelWidth - 28, ware.Sold ? "Sold" : label, () =>
            {
                if (ware.Kind == WareKind.Pack) { shopPack = true; page = 0; return; }
                Ask($"Buy {ware.Name}?", $"{ware.Price} gil. {ware.Description}"
                    + (ware.Kind == WareKind.Materia && ware.Rarity == Rarity.Legendary && view.LegendariesFull ? $"\n\nYou can slot only {view.LegendaryLimit} Legendary Materia: you'll choose whether it replaces yours, or let it go (the gil is spent either way)."
                        : ware.Kind == WareKind.Materia && view.SlotsFull ? "\n\nYour Materia slots are full: you'll choose one of yours to replace with it, or let it go (the gil is spent either way)." : ""),
                    "Buy", () => { odyssey.Buy(index); Effects.Sound(CoinSound); });
            }, ware.CanBuy, false, ware.Reason, PlaceSound);
        }
        // What you carry, and the shop's services.
        var y = Top - 6 + 2 * (panelHeight + 14) + 4;
        Label(Margin, y, "Your Materia", OdysseyChrome.Axis, 13, OdysseyChrome.Gold);
        var x = Margin + 100;
        if (view.MyMateria.Count == 0) Label(x, y, "None yet", OdysseyChrome.Axis, 12, OdysseyChrome.Grey);
        foreach (var materia in view.MyMateria) { MateriaIcon(x, y - 4, 26, materia); x += 32; }
        x = Math.Max(x + 30, 420);
        Label(x, y, "Items", OdysseyChrome.Axis, 13, OdysseyChrome.Gold);
        x += 60;
        if (view.Items.Count == 0) Label(x, y, "None", OdysseyChrome.Axis, 12, OdysseyChrome.Grey);
        // A held Item sells back for half its price (click it), freeing its slot.
        for (var i = 0; i < view.Items.Count; i++)
        {
            var item = view.Items[i]; var index = i; var price = i < view.SellPrices.Count ? view.SellPrices[i] : 0;
            ItemIcon(x, y - 4, 26, item.Name, $"{item.Description}\n\nClick to sell it for {price} gil.", item.Icon,
                click: () => Ask($"Sell {item.Name}?", $"The shop pays {price} gil, half its price, and the slot is free for another Item.", "Sell",
                    () => { odyssey.SellItem(index); Effects.Sound(CoinSound); }));
            x += 32;
        }
        if (view.Items.Count > 0) Label(x + 6, y, "click one to sell it for half its price", OdysseyChrome.Axis, 11, OdysseyChrome.Grey);
        Label(w - Margin - 400, y, $"{view.Mgp} gil", OdysseyChrome.Axis, 14, OdysseyChrome.Gold, 400, 2);
        var by = frameSize.Y - ButtonY;
        Button(Margin, by, 220, $"Remove a card · {view.RemovalPrice} gil", () => { shopRemoving = true; page = 0; }, view.Mgp >= view.RemovalPrice, false, "Not enough gil.");
        Button(Margin + 236, by, 170, view.CardSold ? "Card sold" : "Sell a card", () => { shopSelling = true; page = 0; }, !view.CardSold, false, "The shop buys one card a visit.");
        Button(Margin + 422, by, 170, $"Reroll · {view.RerollPrice} gil", () => Ask("Reroll the shop?", $"{view.RerollPrice} gil for new wares and a new pack.", "Reroll", () => { odyssey.RerollShop(); Effects.Sound(CoinSound); }),
            view.Mgp >= view.RerollPrice, false, "Not enough gil.", PageSound);
        Button(w - Margin - 200, by, 200, "Leave the shop", () => Ask("Leave the shop?", $"On to World {OdysseyContent.WorldAt(Math.Min(OdysseyBalance.Worlds, view.World.Number + 1)).Numeral}. The shop doesn't reopen.", "Leave", () => odyssey.LeaveShop()), true, true);
    }

    // ---- Victory and game over ----

    private void ComposeSummary()
    {
        var view = odyssey.Summary();
        var w = frameSize.X;
        var y = Top - 2;
        var where = view.Won ? "You crossed all fourteen worlds." : $"Fallen in World {view.World.Numeral}, {(view.Duel >= OdysseyBalance.BossDuel ? "against the boss" : $"duel {view.Duel}")}.";
        Label(0, y, where, OdysseyChrome.Axis, 16, OdysseyChrome.White, w, 1);
        Label(0, y + 26, $"{view.DuelsWon} of {view.DuelsPlayed} duels won  ·  {view.MgpEarned} gil earned" + (view.Rank > 0 ? $"  ·  Crystal Rank {view.Rank}" : ""), OdysseyChrome.Axis, 13, OdysseyChrome.Grey, w, 1);
        Label(Margin, y + 54, "\"" + view.Verdict + "\"", OdysseyChrome.Axis, 15, OdysseyChrome.Gold, w - Margin * 2, 1);
        widgets.Add(new BoxW(OdRect.At(Margin, y + 50, w - Margin * 2, 24), 0) { TipTitle = "The Moogle's Verdict", Tip = "A note on how fair the last duel was: the deck power gap, the margin, and whether a rule of chance was in play." });
        y += 96;
        var x = Margin + 16;
        Label(x, y, "MVP", OdysseyChrome.Axis, 13, OdysseyChrome.Gold);
        if (view.Mvp is { } mvp) Card(new(x, y + 20), mvp, .9f, true, caption: $"{view.MvpCaptures} capture{(view.MvpCaptures == 1 ? "" : "s")}");
        else Label(x, y + 22, "No captures", OdysseyChrome.Axis, 12, OdysseyChrome.Grey);
        x += 130;
        Label(x, y, "Best placement", OdysseyChrome.Axis, 13, OdysseyChrome.Gold);
        if (view.BestPlacementCard is { } best) Card(new(x, y + 20), best, .9f, true, caption: $"{view.BestPlacement} cards at once", tip: $"Power {best.Power:0.00}\n{view.BestPlacementWhere}");
        else Label(x, y + 22, "None", OdysseyChrome.Axis, 12, OdysseyChrome.Grey);
        x += 150;
        var rightWidth = w - Margin - x;
        Label(x, y, "Materia", OdysseyChrome.Axis, 13, OdysseyChrome.Gold);
        var my = y + 20;
        if (view.Materia.Count == 0) Label(x, my, "None", OdysseyChrome.Axis, 12, OdysseyChrome.Grey);
        else my += Pills(x, my, rightWidth, view.Materia.Select(MateriaPill), 11);
        my += 14;
        Label(x, my, $"Cards claimed · {view.Claimed.Count}", OdysseyChrome.Axis, 13, OdysseyChrome.Gold);
        my += 20;
        const float claimScale = .6f; var small = sprite.Size * claimScale; var perRow = (int)((rightWidth + 6) / (small.X + 6));
        var shown = Math.Min(view.Claimed.Count, perRow * 2);
        for (var i = 0; i < shown; i++) Card(new(x + i % perRow * (small.X + 6), my + i / perRow * (small.Y + 6)), view.Claimed[i], claimScale);
        var rows = Math.Min(2, (shown + perRow - 1) / perRow);
        my += rows * (small.Y + 6);
        if (view.Claimed.Count > shown) { Label(x, my, $"and {view.Claimed.Count - shown} more", OdysseyChrome.Axis, 11, OdysseyChrome.Grey); my += 16; }
        // The road: a pip per duel, by world, under whichever column reached further down.
        y = Math.Max(y + 160, my + 10);
        Label(Margin + 16, y, "The road", OdysseyChrome.Axis, 13, OdysseyChrome.Gold);
        var worlds = view.Duels.GroupBy(d => d.World).OrderBy(g => g.Key).ToList();
        var px = Margin + 16f; var py = y + 24f;
        foreach (var group in worlds)
        {
            var numeral = OdysseyContent.WorldAt(group.Key).Numeral;
            var width = 30 + group.Count() * 13;
            if (px + width > w - Margin) { px = Margin + 16; py += 22; }
            Label(px, py - 1, numeral, OdysseyChrome.Axis, 11, OdysseyChrome.Grey, 26, 2);
            var dx = px + 30;
            foreach (var duel in group)
            {
                var colour = duel.Outcome == "Won" ? OdysseyChrome.Green : duel.Revived != null ? OdysseyChrome.Yellow : OdysseyChrome.Red;
                widgets.Add(new CircleW(new(dx + 5, py + 7), duel.Kind == OpponentKind.Boss ? 6 : 4, colour, OdysseyChrome.Edge));
                widgets.Add(new BoxW(OdRect.At(dx - 1, py, 13, 14), 0) { TipTitle = $"{duel.Opponent} · {duel.Outcome}{(duel.Revived != null ? $" ({duel.Revived})" : "")}", Tip = $"Margin {duel.Margin:+0;-0;0} · {duel.Mgp} gil · {string.Join(", ", OdysseyBalance.Listed(duel.Rules).Select(setup.RuleName))}", TipColour = colour });
                dx += 13;
            }
            px += width + 10;
        }
        if (view.RankUnlocked) Label(0, frameSize.Y - ButtonY - 34, $"Crystal Rank {view.Rank + 1} unlocked!", OdysseyChrome.Axis, 15, OdysseyChrome.Gold, w, 1);
        Button((w - 180) / 2, frameSize.Y - ButtonY, 180, "Continue", () => { odyssey.CloseSummary(); mode = Mode.Title; }, true, true);
    }
}
