using System.Numerics;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Haven.Core;
using Haven.Native;

namespace Haven.Games;

/// <summary>
/// One game at a Gold Saucer arcade furnishing, in the game's own window (NativeArcade), with Haven answering the agent
/// as the Gold Saucer's event handler and server would: the player stands at the machine in its idle pose; a hit plays
/// the player's swing and the machine's reaction, shows the result balloon after the machine's delay, and ends the game
/// (or, for Monster Toss, starts the next throw while time remains). Out on a Limb and The Finer Miner run their
/// "Gauge Your Skill!" and "Find the Sweet Spot!" rounds with Double Down (GatheringArcade). Nothing reaches the server.
/// </summary>
internal sealed unsafe class ArcadeSession
{
    private readonly NativeArcade native;
    private readonly IObjectTable objects;
    private readonly IChatGui chat;
    private readonly IDataManager data;
    private readonly IPluginLog log;
    private readonly Random random = new();
    /// <summary>Plays one of the machine's own timelines by its index (0 is its resting one); set by the controller.</summary>
    public Action<Guid, byte>? MachineTimeline { get; set; }
    /// <summary>The player's stance at the machine, each swing and the end, for the gathering to see: (kind, machine, timeline).</summary>
    public Action<string, Guid, ushort>? Share { get; set; }
    /// <summary>Holds the player in place while true (movement keys and turning), as the Gold Saucer does.</summary>
    public Action<bool>? Hold { get; set; }

    private ArcadeMachine? machine;
    private Guid item;
    private Matrix4x4 frame;
    private bool begun, waiting, timeIsUp, ending;
    private long deadline, windowGoneAt;
    // The seconds a gathering round had left when its clock paused (Double Down asked).
    private int pausedSeconds;
    private int shots;
    private GatheringArcade? gathering;
    private readonly List<(long At, Action Run)> timers = [];

    public ArcadeSession(NativeArcade native, IObjectTable objects, IChatGui chat, IDataManager data, IPluginLog log)
    {
        this.native = native; this.objects = objects; this.chat = chat; this.data = data; this.log = log;
    }

    /// <summary>Personal bests (this PC), and the character they belong to.</summary>
    public ArcadeBests? Bests { get; set; }
    public Func<string?>? Character { get; set; }
    private string machineName = "";
    private bool scored;

    /// <summary>Monster Toss's objective while it runs (the Gold Saucer shows it in the duty list): shots made, seconds left.</summary>
    public (string Name, int Shots, int Seconds)? Objective => machine is { Rounds: true } m && begun && !ending
        ? (machineName, shots, deadline > 0 ? (int)Math.Clamp((deadline - Now + 999) / 1000, 0, 3600) : 0) : null;

    public bool Available => native.Available;
    public bool Active => machine != null;
    public Guid Item => item;

    /// <summary>Starts a game at a machine (its world transform), standing the player where the Gold Saucer would.</summary>
    public void Start(ArcadeMachine machine, Guid item, Matrix4x4 world)
    {
        if (Active) throw new InvalidOperationException("You're already playing.");
        if (!native.Available) throw new InvalidOperationException("The arcade isn't available in this game version.");
        if (native.Busy) throw new InvalidOperationException("Another Gold Saucer game is open.");
        var player = objects.LocalPlayer ?? throw new InvalidOperationException("You aren't logged in.");
        this.machine = machine; this.item = item; frame = world;
        begun = waiting = timeIsUp = ending = scored = false; shots = 0; timers.Clear(); windowGoneAt = 0;
        gathering = machine.Gathering ? new GatheringArcade(machine.Type == 4, random) : null;
        var name = data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRowOrDefault(machine.NameText)?.Text.ExtractText() ?? machine.Key;
        machineName = name;
        // The prompt names the game and the character's best, in place of the fee and their MGP.
        var best = Character?.Invoke() is { } who ? Bests?.Best(who, machine.Key) : null;
        native.Prompt = ($"Play a round of {name}?", best is { } b ? $"Your personal best: {ArcadeBests.Describe(machine.Key, b)}" : "No personal best yet.");
        // The player at the machine's offset, facing it, in its idle pose (as the handler's StartGame does).
        var origin = Vector3.Transform(Vector3.Zero, world);
        var right = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, world));
        var up = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, world));
        var forward = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, world));
        var stand = origin + right * machine.PlayerOffset.X + up * machine.PlayerOffset.Y + forward * machine.PlayerOffset.Z;
        var character = (Character*)player.Address;
        character->GameObject.SetPosition(stand.X, stand.Y, stand.Z);
        // Facing the machine as the Gold Saucer stands you: its yaw turned half round.
        character->GameObject.SetRotation(MathF.Atan2(-forward.X, -forward.Z));
        native.BaseIdle(character, machine.Idle);
        Share?.Invoke("pose", item, machine.Idle);
        Hold?.Invoke(true);
        try { native.Start(machine.Type, machine.Timeout, name); }
        catch { Finish(); throw; }
    }

    private static long Now => Environment.TickCount64;
    private void After(float seconds, Action run) => timers.Add((Now + (long)(seconds * 1000), run));
    private Vector3 BalloonAt => Vector3.Transform(machine!.BalloonOffset, frame) is var p ? p : default;

    /// <summary>Answers the agent's messages and runs the game's timers; call every framework update.</summary>
    public void Update(bool inHome)
    {
        if (machine == null) return;
        if (!inHome || !native.Open) { Finish(); return; }
        // A watchdog: the game's window gone for two seconds without a closing message (the aim window hands over to the
        // swing window within that) lets the player go.
        if (native.Busy) windowGoneAt = 0;
        else if (windowGoneAt == 0) windowGoneAt = Now;
        else if (Now - windowGoneAt > 2000) { log.Warning("[ARCADE] The game's window closed without a message; ending the game."); Finish(); return; }
        while (native.Next(out var message)) if (!ending) Answer(message.Code, message.First, message.Second);
        for (var i = 0; i < timers.Count && machine != null; i++)
            if (timers[i].At <= Now) { var run = timers[i].Run; timers.RemoveAt(i--); run(); }
        if (machine != null && begun && deadline > 0 && Now >= deadline) TimeUp();
        // The gathering games show their round's clock (the dial beside the swings, Double Down's time remaining).
        if (machine is { Gathering: true } && begun && !ending)
            native.Time(deadline > 0 ? (int)Math.Clamp((deadline - Now + 999) / 1000, 0, 3600) : timeIsUp ? 0 : pausedSeconds);
    }

    private void Answer(int code, int first, int second)
    {
        var m = machine!;
        log.Debug($"[ARCADE] agent message {code} [{first}, {second}]");
        switch (code)
        {
            case 20 or 21:   // "Play a round?" Yes
                if (begun) break;
                begun = true; deadline = Now + m.Timeout * 1000L;
                native.Begin();
                break;
            case 16:         // a hit (timing machines: the zone 0-3; Moogle's Paw: 0 miss, 1 large, 3 small)
                Result(Math.Clamp(first, 0, 3));
                break;
            case 17:         // Gauge Your Skill: the zone sets the difficulty, and the first round opens
                gathering!.Choose(first);
                // Each round has its own time limit, as the server's round start gives one.
                deadline = Now + m.Timeout * 1000L; timeIsUp = false;
                native.Round(RoundArgs(0));
                break;
            case 18:         // Find the Sweet Spot: a swing at 0-100
                Swing(first);
                break;
            case 23:         // Double Down? (value 0 claims)
                if (first == 0 && gathering is { RoundWon: true } claimed) Scored(claimed.Payout);
                if (first != 0 && gathering is { CanDoubleDown: true })
                {
                    gathering.Next();
                    deadline = Now + m.Timeout * 1000L; timeIsUp = false;
                    native.Round(RoundArgs(gathering.Round));
                    native.State(3);
                }
                else Finish();
                break;
            case 19 or 22:   // declined, quit or closed
                Finish();
                break;
        }
    }

    private NativeArcade.RoundArgs RoundArgs(int round) => new()
    {
        Attempts = gathering!.Attempts, Gauge = gathering.Gauge, Round = round, GaugeMax = GatheringArcade.GaugeMax,
        Speed = gathering.Speed, Seconds = (int)Math.Max(1, (deadline - Now) / 1000),
    };

    // A timing machine's or the claw's result: the player's action and the machine's reaction now, the balloon after
    // the machine's delay, then the end (or Monster Toss's next throw).
    private void Result(int level)
    {
        var m = machine!;
        waiting = true;
        Animate(m.Actions[level]);
        MachineTimeline?.Invoke(item, m.MachineTimelines[level]);
        native.State(5);
        if (m.Rounds && level > 0) shots++;
        if (!m.Rounds) Scored(ArcadeBests.Score(m.Key, level, 0, 0));
        After(m.BalloonDelay, () => native.Balloon(BalloonAt, (byte)m.Type, m.Images[level]));
        After(m.EndDelay, () =>
        {
            waiting = false;
            if (m.Rounds && !timeIsUp) { MachineTimeline?.Invoke(item, 0); native.State(3); }
            else if (m.Rounds) EndWithTimeUp();
            else Finish();
        });
    }

    private void Swing(int position)
    {
        var m = machine!; var game = gathering!;
        waiting = true;
        var level = game.Swing(position);
        native.State(4);
        Animate(m.Actions[level]);
        MachineTimeline?.Invoke(item, m.MachineTimelines[level]);
        After(m.BalloonDelay, () =>
        {
            native.Progress(game.SwingsLeft, game.Gauge);
            native.Message(game.LevelMessage(level));
            Log(game.LevelMessage(level));
            native.Balloon(BalloonAt, (byte)m.Type, m.Images[level]);
        });
        After(m.EndDelay, () =>
        {
            waiting = false;
            MachineTimeline?.Invoke(item, 0);
            // The time limit pauses while Double Down is asked.
            if (game.RoundWon && game.CanDoubleDown && !timeIsUp) { pausedSeconds = (int)Math.Clamp((deadline - Now + 999) / 1000, 0, 3600); native.Time(pausedSeconds); deadline = 0; native.DoubleDown(GatheringArcade.MaxRounds - game.Round - 1, game.Payout, game.NextPayout); }
            else if (game.RoundWon || game.SwingsLeft <= 0) { Scored(game.RoundWon ? game.Payout : 0); Finish(); }
            else if (timeIsUp) EndWithTimeUp();
            else native.State(3);
        });
    }

    // The time limit: a swing or throw in flight finishes first (its end sees the time is up); otherwise "TIME'S UP".
    private void TimeUp()
    {
        deadline = 0; timeIsUp = true;
        if (!waiting) EndWithTimeUp();
    }

    // "TIME'S UP" over the machine, then the window closes once the balloon has shown (closing would remove it).
    private void EndWithTimeUp()
    {
        if (ending) return;
        ending = true;
        native.State(4);
        native.Balloon(BalloonAt, (byte)machine!.Type, ArcadeMachine.TimeUpImage);
        After(2.5f, Finish);
    }

    private void Animate(ushort timeline)
    {
        if (timeline == 0 || objects.LocalPlayer is not { } player) return;
        ((Character*)player.Address)->Timeline.PlayActionTimeline(timeline);
        Share?.Invoke("act", item, timeline);
    }

    private void Log(uint row)
    {
        var text = data.GetExcelSheet<Lumina.Excel.Sheets.LogMessage>().GetRowOrDefault(row)?.Text.ExtractText();
        if (!string.IsNullOrWhiteSpace(text)) chat.Print(new Dalamud.Game.Text.XivChatEntry { Type = Dalamud.Game.Text.XivChatType.SystemMessage, Message = text });
    }

    // A game's score against the character's personal best, told in chat (once a game; a lost Double Down scores 0).
    private void Scored(int points)
    {
        if (scored || machine is not { } m) return;
        scored = true;
        if (points <= 0 || Character?.Invoke() is not { } who || Bests == null) return;
        var previous = Bests.Best(who, m.Key);
        var text = ArcadeBests.Describe(m.Key, points);
        chat.Print(Bests.Record(who, m.Key, points)
            ? $"[Haven] {machineName}: {text}, a new personal best!"
            : $"[Haven] {machineName}: {text}. Your personal best: {ArcadeBests.Describe(m.Key, previous ?? points)}.");
    }

    /// <summary>Ends the game: the window closes, the player is let go in their normal idle, the machine rests.</summary>
    public void Finish()
    {
        if (machine == null) return;
        // Monster Toss scores its baskets when the time is up.
        if (machine.Rounds && begun) Scored(ArcadeBests.Score(machine.Key, 0, shots, 0));
        machine = null; timers.Clear(); deadline = 0;
        try { native.Close(begun); } catch (Exception e) { log.Warning(e, "[ARCADE] Closing the window failed."); }
        if (objects.LocalPlayer is { } player) native.BaseIdle((Character*)player.Address, 3);
        Share?.Invoke("end", item, 3);
        MachineTimeline?.Invoke(item, 0);
        Hold?.Invoke(false);
    }
}
