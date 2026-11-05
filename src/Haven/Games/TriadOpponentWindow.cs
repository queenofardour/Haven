using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Haven.Core;
using static Haven.HavenTokens;

namespace Haven.Games;

/// <summary>A deck the player built in the game's Decks window: its name and its five cards.</summary>
public sealed record TriadDeck(string Name, IReadOnlyList<TriadCard> Cards);

/// <summary>What the Board's Odyssey tab shows: the saved run's standing, the Crystal Ranks unlocked and the records.</summary>
public sealed record OdysseyTabInfo(bool CanContinue, string ContinueText, int BinderSize, int MateriaCount, int Mgp, int UnlockedRank, Haven.Core.Odyssey.OdysseyRecords Records);

/// <summary>
/// Who to play at the Triple Triad Board, and with which deck: any of the game's Triple Triad NPCs (their own decks and
/// rules), Haven with rules you pick, or another player in the gathering (invited with those rules; they answer on
/// their own PC). Choosing Play starts the match, or sends the invitation. The Odyssey tab continues or starts a
/// Crystal Odyssey run (the Triple Triad roguelite) and shows its records.
/// </summary>
internal sealed class TriadOpponentWindow : Window
{
    private readonly Func<IReadOnlyList<TriadNpc>> npcs;
    private readonly Func<TriadRule, string> ruleName;
    private readonly Func<IReadOnlyList<TriadDeck>> decks;
    private readonly TriadLink? link;
    private ulong player;
    private string search = "";
    private int selected = -1, tab, deck;
    private readonly HashSet<TriadRule> picked = [];
    // The footer's height as last drawn (the deck chooser, its note and Play), so the list above leaves room for it.
    private float footer;
    /// <summary>The opponent and the deck chosen (null: five of the player's own cards).</summary>
    public Action<TriadOpponent, TriadDeck?>? Chosen { get; set; }

    /// <summary>A player in the gathering is invited, with the rules picked and the deck chosen (null: five of the player's own cards).</summary>
    public Action<ulong, string, IReadOnlyList<TriadRule>, TriadDeck?>? Invite { get; set; }

    /// <summary>The Odyssey's standing (null: not available), and starting it (continue, a new run at a Crystal Rank, or the records) or abandoning the run.</summary>
    public Func<OdysseyTabInfo?>? OdysseyInfo { get; set; }
    public Action<OdysseyStart, int>? StartOdyssey { get; set; }
    public Action? AbandonOdyssey { get; set; }
    private int odysseyRank;
    private bool abandonAsked, newRunAsked;

    public TriadOpponentWindow(Func<IReadOnlyList<TriadNpc>> npcs, Func<TriadRule, string> ruleName, Func<IReadOnlyList<TriadDeck>> decks, TriadLink? link = null)
        : base("Triple Triad###Haven.TriadOpponents", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.npcs = npcs; this.ruleName = ruleName; this.decks = decks; this.link = link;
        Size = new Vector2(540, 620); SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new() { MinimumSize = new Vector2(440, 480), MaximumSize = new Vector2(float.MaxValue) };
    }

    public void Open() { selected = -1; IsOpen = true; BringToFront(); }

    public override void PreDraw()
    {
        HavenKit.PushTheme();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Px(Space5, Space4)); ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, Px(RadiusWindow));
    }
    public override void PostDraw() { ImGui.PopStyleVar(2); HavenKit.PopTheme(); }

    private static readonly TriadRule[] Choosable =
        [TriadRule.AllOpen, TriadRule.ThreeOpen, TriadRule.Same, TriadRule.Plus, TriadRule.Reverse, TriadRule.FallenAce, TriadRule.Ascension,
         TriadRule.Descension, TriadRule.Order, TriadRule.Chaos, TriadRule.Swap, TriadRule.SuddenDeath, TriadRule.Roulette];

    public override void Draw()
    {
        HavenKit.KeepOnTop();
        var start = ImGui.GetCursorScreenPos();
        using (HavenFonts.Use(HavenText.Title)) ImGui.GetWindowDrawList().AddText(start + Px(0, 6), HavenUi.U(Text), "Triple Triad");
        ImGui.SetCursorScreenPos(new(start.X + ImGui.GetContentRegionAvail().X - Px(ControlHeight), start.Y));
        if (HavenKit.IconButton("##triad-close", HavenIcon.Close, "Close")) IsOpen = false;
        ImGui.SetCursorScreenPos(start + new Vector2(0, Px(ControlHeight + Space2)));
        HavenKit.Caption("Choose an opponent and a deck. Nothing is won or lost: matches are played in your home only.");
        ImGui.Dummy(Px(0, Space1));
        if (HavenKit.Segmented("##triad-tab", ["Game opponents", "Haven", "Players here", "Odyssey"], ref tab)) { selected = tab == 1 ? -2 : -1; player = 0; abandonAsked = newRunAsked = false; }
        ImGui.Dummy(Px(0, Space1));
        if (tab == 3) { DrawOdyssey(); return; }

        // The deck chooser and Play stay at the bottom; the list or the rules fill what's left.
        var body = Math.Max(Px(120), ImGui.GetContentRegionAvail().Y - (footer > 0 ? footer : Px(ControlHeight * 3 + Space4 * 2)) - ImGui.GetStyle().ItemSpacing.Y);
        var list = npcs();
        if (tab == 0)
        {
            HavenKit.Search("##npc-search", ref search, "Search opponents and places");
            if (ImGui.BeginChild("##npcs", new Vector2(0, body - Px(ControlHeight + Space2))))
            {
                if (list.Count == 0) HavenKit.Caption("Reading the game's Triple Triad players…");
                for (var i = 0; i < list.Count; i++)
                {
                    var npc = list[i];
                    if (search.Length > 0 && !npc.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) && !npc.Place.Contains(search, StringComparison.CurrentCultureIgnoreCase)) continue;
                    var rules = npc.Rules.Count == 0 ? "No rules" : string.Join(" · ", npc.Rules.Select(ruleName));
                    if (HavenKit.ListRow("npc" + npc.Row, npc.Name, (npc.Place.Length > 0 ? npc.Place + " · " : "") + rules, selected == i)) selected = i;
                    if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) Start(list);
                }
            }
            ImGui.EndChild();
        }
        else if (tab == 2)
        {
            if (ImGui.BeginChild("##players", new Vector2(0, body)))
            {
                var players = link?.Players() ?? [];
                HavenKit.Caption(link == null ? "Playing other players needs a gathering."
                    : players.Count == 0 ? "Nobody else is in this gathering yet. Host a gathering, or join a friend's, to play each other."
                    : "Invite someone here to a match. They choose their deck and answer on their own screen. "
                      + (picked.Count == 0 ? "No rules (pick them on the Haven tab)." : "Rules: " + string.Join(" · ", picked.Select(ruleName)) + "."));
                ImGui.Dummy(Px(0, Space1));
                foreach (var (id, name) in players)
                    if (HavenKit.ListRow("player" + id, name, "In this gathering", player == id)) player = id;
                if (players.All(p => p.Id != player)) player = 0;
                selected = player != 0 ? -3 : -1;
                if (link?.AskingStatus is { } status) { ImGui.Dummy(Px(0, Space2)); HavenKit.Caption(status); }
            }
            ImGui.EndChild();
        }
        else if (ImGui.BeginChild("##haven-rules", new Vector2(0, body)))
        {
            selected = -2;
            HavenKit.Caption(picked.Count == 0 ? "Haven plays with no rules, or up to four you pick." : "Rules: " + string.Join(" · ", picked.Select(ruleName)));
            ImGui.Dummy(Px(0, Space1));
            var column = (ImGui.GetContentRegionAvail().X - Px(Space3)) / 2;
            for (var i = 0; i < Choosable.Length; i++)
            {
                var rule = Choosable[i];
                var on = picked.Contains(rule);
                var allowed = on || rule == TriadRule.Roulette || picked.All(p => TriadGame.Compatible(p, rule));
                ImGui.BeginDisabled(!allowed || !on && picked.Count >= 4);
                if (HavenKit.Toggle(ruleName(rule) + "##rule" + (int)rule, ref on, column)) { if (on) picked.Add(rule); else picked.Remove(rule); }
                ImGui.EndDisabled();
                if ((i & 1) == 0 && i + 1 < Choosable.Length) ImGui.SameLine(column + Px(Space3));
            }
            ImGui.EndChild();
        }
        else ImGui.EndChild();

        var footerTop = ImGui.GetCursorPosY();
        HavenKit.Rule();
        var available = decks();
        var names = available.Select(d => d.Name).Append("Five of your own cards").ToList();
        deck = Math.Clamp(deck, 0, names.Count - 1);
        HavenKit.Select("##triad-deck", "Deck", names, ref deck);
        if (available.Count == 0) HavenKit.Caption("No decks found. Build one in the game's Triple Triad Decks window, or play with five of your own cards.");
        if (tab == 2 && link?.Waiting == true)
        {
            if (HavenKit.Button("Cancel invitation")) link.CancelInvite();
        }
        else
        {
            ImGui.BeginDisabled(selected == -1);
            if (HavenKit.Button(tab == 2 ? "Invite" : "Play", ButtonKind.Primary)) Start(list);
            ImGui.EndDisabled();
        }
        footer = ImGui.GetCursorPosY() - footerTop;
    }

    // The Odyssey tab: a pitch, the saved run with Continue and Abandon, a new run (at a Crystal Rank once unlocked), and the records.
    private void DrawOdyssey()
    {
        if (!ImGui.BeginChild("##odyssey", new Vector2(0, 0))) { ImGui.EndChild(); return; }
        HavenKit.Caption("The Crystal Odyssey is a Triple Triad roguelite: cross the fourteen numbered Final Fantasy worlds with one binder of cards. "
            + "Beaten opponents give up a card; Materia, Enhancements and Items grow your deck. One lost duel ends the run. Fourteen worlds. One deck. No second chances.");
        ImGui.Dummy(Px(0, Space2));
        var info = OdysseyInfo?.Invoke();
        if (info == null) { HavenKit.Caption("The Odyssey isn't available right now."); ImGui.EndChild(); return; }
        HavenKit.SectionTitle("Your run", info.CanContinue ? info.ContinueText : "None in progress");
        if (info.CanContinue)
        {
            HavenKit.Caption($"{info.BinderSize} cards in the binder · {info.MateriaCount} Materia · {info.Mgp} gil");
            ImGui.Dummy(Px(0, Space1));
            if (abandonAsked)
            {
                HavenKit.Caption("Abandon this run? It can't be recovered.");
                if (HavenKit.Button("Abandon the run", ButtonKind.Danger)) { abandonAsked = false; AbandonOdyssey?.Invoke(); }
                ImGui.SameLine(0, Px(Space2));
                if (HavenKit.Button("Keep it")) abandonAsked = false;
            }
            else
            {
                if (HavenKit.Button("Continue", ButtonKind.Primary)) StartOdyssey?.Invoke(OdysseyStart.Continue, 0);
                ImGui.SameLine(0, Px(Space2));
                if (HavenKit.Button("Abandon…")) abandonAsked = true;
            }
            ImGui.Dummy(Px(0, Space3));
        }
        HavenKit.SectionTitle("New run", info.UnlockedRank > 0 ? $"Crystal Ranks up to {info.UnlockedRank} unlocked" : "");
        if (info.UnlockedRank > 0)
        {
            var ranks = Enumerable.Range(0, info.UnlockedRank + 1).Select(r => r == 0 ? "No Crystal Rank" : $"Crystal Rank {r}").ToList();
            odysseyRank = Math.Clamp(odysseyRank, 0, info.UnlockedRank);
            HavenKit.Select("##odyssey-rank", "Crystal Rank", ranks, ref odysseyRank, Px(260));
        }
        else { HavenKit.Caption("Clear the Odyssey to unlock Crystal Ranks, each a harder run."); odysseyRank = 0; }
        if (newRunAsked)
        {
            HavenKit.Caption("Start a new run? The run in progress is lost.");
            if (HavenKit.Button("Start anew", ButtonKind.Danger)) { newRunAsked = false; StartOdyssey?.Invoke(OdysseyStart.NewRun, odysseyRank); }
            ImGui.SameLine(0, Px(Space2));
            if (HavenKit.Button("Keep the run")) newRunAsked = false;
        }
        else if (HavenKit.Button("New run", info.CanContinue ? ButtonKind.Secondary : ButtonKind.Primary))
        {
            if (info.CanContinue) newRunAsked = true; else StartOdyssey?.Invoke(OdysseyStart.NewRun, odysseyRank);
        }
        ImGui.Dummy(Px(0, Space3));
        var r = info.Records;
        var best = r.BestWorld == 0 ? "no world reached yet" : $"best World {Haven.Core.Odyssey.OdysseyContent.WorldAt(r.BestWorld).Numeral}";
        HavenKit.SectionTitle("Records");
        HavenKit.Caption($"{r.Runs} run{(r.Runs == 1 ? "" : "s")} · {r.Clears} clear{(r.Clears == 1 ? "" : "s")} · {best} · {r.DuelsWon} duels won");
        if (HavenKit.Button("Open the records")) StartOdyssey?.Invoke(OdysseyStart.Records, 0);
        ImGui.EndChild();
    }

    private void Start(IReadOnlyList<TriadNpc> list)
    {
        var available = decks();
        var chosenDeck = deck < available.Count ? available[deck] : null;
        if (selected == -3 && link?.Players().FirstOrDefault(p => p.Id == player) is { Id: not 0 } invited)
        {
            Invite?.Invoke(invited.Id, invited.Name, [.. picked], chosenDeck);
            return;
        }
        var opponent = selected == -2 ? new TriadOpponent("Haven", [.. picked]) : selected >= 0 && selected < list.Count ? list[selected].Opponent : null;
        if (opponent == null) return;
        IsOpen = false;
        Chosen?.Invoke(opponent, deck < available.Count ? available[deck] : null);
    }
}
