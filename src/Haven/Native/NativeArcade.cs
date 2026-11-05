using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Haven.Native;

/// <summary>
/// The game's own Gold Saucer arcade windows, opened and driven locally. Every arcade machine runs through one UI agent
/// (AgentGoldSaucerMiniGame, 173): it opens the machine's window (PunchingMachine, Hummer, BasketBall, MogCatcher, the
/// gathering pair), moves the needle or cursor, plays the UI sounds and shows the result balloons. In the Gold Saucer an
/// event handler stands between the agent and the server; here Haven gives the agent its own event interface instead,
/// so the agent's messages (start, hit, swing, double down, close) come to Haven, which answers them as the server and
/// the handler would. Nothing is sent to the server. Read-only research: arcade-timing and arcade-search, client build
/// 2026.10.16; every function is found by a signature that was unique in that build, and the arcade is offered only
/// when all of them resolve.
/// </summary>
internal sealed unsafe class NativeArcade : IDisposable
{
    public const uint AgentId = 173;
    // Agent functions (AgentGoldSaucerMiniGame).
    private const string OpenSignature = "40 53 56 48 81 EC F8 00 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 E0 00 00 00";
    private const string CloseSignature = "48 8B 01 48 C7 81 D8 00 00 00 00 00 00 00 48 FF 60 28";
    private const string StateSignature = "48 89 74 24 18 57 48 81 EC D0 00 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 C0 00 00 00 48 8B 01 8B F2";
    private const string BeginSignature = "40 53 48 83 EC 20 48 8B D9 45 33 C0 48 8B 49 10";
    private const string BalloonSignature = "48 8B C4 48 89 58 10 48 89 68 18 48 89 70 20 57 41 56 41 57 48 81 EC E0 00 00 00";
    private const string RoundSignature = "48 89 5C 24 10 48 89 6C 24 18 48 89 74 24 20 57 41 56 41 57 48 81 EC 50 01 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84";
    private const string ProgressSignature = "4C 8B DC 56 48 81 EC 30 01 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 20 01 00 00 83 79 20 00 48 8B F1 48 8B 02";
    private const string MessageSignature = "4C 8D 0D ?? ?? ?? ?? 4C 8B D1 4D 8B C1 33 C0 90 41 39 10 74 0C FF C0 49 83 C0 08 83 F8 08 72 F0";
    private const string DoubleDownSignature = "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 48 83 EC 20 41 8B E9 41 8B F0 45 33 C9 8B DA 41 B8 6D 24 00 00";
    // The character's base idle animation (the machine's idle pose for the whole game) and its refresh.
    private const string BaseIdleSignature = "66 85 D2 B8 03 00 00 00 66 0F 45 C2 66 89 81 ?? ?? ?? ?? C3";
    // Client::UI::PlaySoundEffect(id, out, out, flags): the game's UI sounds by id (bank × 1000 + index).
    private const string SoundSignature = "40 53 48 81 EC 90 01 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 50 01 00 00 F6 05 ?? ?? ?? ?? 04 8B D9";
    private const string RefreshBaseSignature = "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 48 89 7C 24 20 41 56 48 83 EC 20 48 8B 41 08 48 8B F9";

    private readonly delegate* unmanaged<AgentInterface*, OpenArgs*, void> open;
    private readonly delegate* unmanaged<AgentInterface*, void> close, begin;
    private readonly delegate* unmanaged<AgentInterface*, int, void> state;
    private readonly delegate* unmanaged<AgentInterface*, Vector3*, byte, uint, void> balloon;
    private readonly delegate* unmanaged<AgentInterface*, RoundArgs*, void> round;
    private readonly delegate* unmanaged<AgentInterface*, int*, void> progress;
    private readonly delegate* unmanaged<AgentInterface*, uint, void> message;
    private readonly delegate* unmanaged<AgentInterface*, int, int, int, void> doubleDown;
    private readonly delegate* unmanaged<TimelineContainer*, ushort, void> baseIdle;
    private readonly delegate* unmanaged<TimelineContainer*, void> refreshBase;
    private readonly delegate* unmanaged<uint, nint, nint, byte, void> sound;
    private readonly IPluginLog log;
    private readonly IAddonLifecycle lifecycle;

    /// <summary>Whether every function was found, so the arcade can be offered.</summary>
    public bool Available { get; }

    // The event interface the agent talks to: a vtable of three slots, the first receiving its messages.
    private nint owner, vtable, name;
    // The emote restriction before the window opened (see CommandRestriction), put back when it closes.
    private HavenNative.CommandRestriction.State? restriction;
    private static NativeArcade? current;
    private readonly Queue<(int Code, int First, int Second)> messages = new();

    [StructLayout(LayoutKind.Explicit, Size = 0x20)]
    private struct OpenArgs
    {
        [FieldOffset(0x00)] public nint Owner;
        [FieldOffset(0x08)] public int GameType;
        [FieldOffset(0x0C)] public int TimeoutSeconds;
        [FieldOffset(0x10)] public ushort Fee;
        [FieldOffset(0x18)] public nint Name;
    }

    /// <summary>A gathering game's round as the agent shows it: swings, gauge, round number, cursor speed, time.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 0x18)]
    public struct RoundArgs
    {
        [FieldOffset(0x00)] public int Attempts;
        [FieldOffset(0x04)] public int Gauge;
        [FieldOffset(0x08)] public int Round;
        [FieldOffset(0x0C)] public int GaugeMax;
        [FieldOffset(0x10)] public ushort Speed;
        [FieldOffset(0x14)] public int Seconds;
    }

    public NativeArcade(ISigScanner scanner, IAddonLifecycle lifecycle, IPluginLog log)
    {
        this.log = log;
        this.lifecycle = lifecycle;
        lifecycle.RegisterListener(AddonEvent.PreSetup, OnPromptSetup);
        lifecycle.RegisterListener(AddonEvent.PreSetup, OnPayoutValues);
        lifecycle.RegisterListener(AddonEvent.PreRefresh, OnPayoutValues);
        // UI sounds stand alone: Triple Triad uses them even if an arcade function isn't found.
        if (scanner.TryScanText(SoundSignature, out var soundAddress)) sound = (delegate* unmanaged<uint, nint, nint, byte, void>)soundAddress;
        HavenNative.CommandRestriction.Initialize(scanner, log);
        try
        {
            open = (delegate* unmanaged<AgentInterface*, OpenArgs*, void>)scanner.ScanText(OpenSignature);
            close = (delegate* unmanaged<AgentInterface*, void>)scanner.ScanText(CloseSignature);
            state = (delegate* unmanaged<AgentInterface*, int, void>)scanner.ScanText(StateSignature);
            begin = (delegate* unmanaged<AgentInterface*, void>)scanner.ScanText(BeginSignature);
            balloon = (delegate* unmanaged<AgentInterface*, Vector3*, byte, uint, void>)scanner.ScanText(BalloonSignature);
            round = (delegate* unmanaged<AgentInterface*, RoundArgs*, void>)scanner.ScanText(RoundSignature);
            progress = (delegate* unmanaged<AgentInterface*, int*, void>)scanner.ScanText(ProgressSignature);
            message = (delegate* unmanaged<AgentInterface*, uint, void>)scanner.ScanText(MessageSignature);
            doubleDown = (delegate* unmanaged<AgentInterface*, int, int, int, void>)scanner.ScanText(DoubleDownSignature);
            baseIdle = (delegate* unmanaged<TimelineContainer*, ushort, void>)scanner.ScanText(BaseIdleSignature);
            refreshBase = (delegate* unmanaged<TimelineContainer*, void>)scanner.ScanText(RefreshBaseSignature);
            Available = true;
            log.Information("[ARCADE] The Gold Saucer arcade functions were found.");
        }
        catch (Exception exception)
        {
            log.Warning(exception, "[ARCADE] The Gold Saucer arcade functions weren't found in this game version; the arcade machines stay unplayable.");
        }
    }

    private static AgentInterface* Agent
    {
        get { var module = AgentModule.Instance(); return module == null ? null : module->GetAgentByInternalId((AgentId)AgentId); }
    }

    /// <summary>Whether a window of the agent is open (Haven's or the game's own).</summary>
    public bool Busy { get { var agent = Agent; return agent != null && agent->IsAgentActive(); } }
    public bool Open => owner != 0;

    /// <summary>
    /// Opens a machine's window. The agent plays the window's intro, then asks "Play a round of …?" (the fee is 0, so
    /// nothing is charged); Yes arrives as message 20 or 21.
    /// </summary>
    public void Start(int gameType, int timeoutSeconds, string machineName)
    {
        if (!Available) throw new InvalidOperationException("The arcade isn't available in this game version.");
        if (Open) throw new InvalidOperationException("An arcade game is already open.");
        var agent = Agent;
        if (agent == null || agent->IsAgentActive()) throw new InvalidOperationException("Another Gold Saucer game is open.");
        vtable = Marshal.AllocHGlobal(sizeof(nint) * 3);
        ((nint*)vtable)[0] = (nint)(delegate* unmanaged<nint, AtkValue*, AtkValue*, uint, ulong, AtkValue*>)&Receive;
        ((nint*)vtable)[1] = (nint)(delegate* unmanaged<nint, void>)&Nothing;
        ((nint*)vtable)[2] = (nint)(delegate* unmanaged<nint, void>)&Nothing;
        owner = Marshal.AllocHGlobal(0x40);
        new Span<byte>((void*)owner, 0x40).Clear();
        *(nint*)owner = vtable;
        // The agent keeps the name pointer for its prompts, so it lives until the window closes.
        var bytes = System.Text.Encoding.UTF8.GetBytes(machineName + "\0");
        name = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, name, bytes.Length);
        current = this;
        messages.Clear();
        var args = new OpenArgs { Owner = owner, GameType = gameType, TimeoutSeconds = timeoutSeconds, Fee = 0, Name = name };
        restriction = HavenNative.CommandRestriction.Capture();
        try { open(agent, &args); }
        catch { Release(); throw; }
        log.Information($"[ARCADE] Opened game type {gameType} ({machineName}).");
    }

    /// <summary>The agent's messages since the last call: [code, value, value].</summary>
    public bool Next(out (int Code, int First, int Second) next) => messages.TryDequeue(out next);

    // The agent while Haven's window is open; null otherwise.
    private AgentInterface* Live { get { var agent = Agent; return Open ? agent : null; } }
    public void Begin() { var agent = Live; if (agent != null) begin(agent); }
    /// <summary>Window state: 3 next round, 4 busy, 5 show the result, 6 fire a result now.</summary>
    public void State(int value) { var agent = Live; if (agent != null) state(agent, value); }
    public void Balloon(Vector3 position, byte machineType, uint icon) { var agent = Live; if (agent != null) balloon(agent, &position, machineType, icon); }
    public void Round(RoundArgs args) { var agent = Live; if (agent != null) round(agent, &args); }
    public void Progress(int remaining, int gauge) { var agent = Live; if (agent == null) return; var values = stackalloc int[2] { remaining, gauge }; progress(agent, values); }
    public void Message(uint logMessage) { var agent = Live; if (agent != null) message(agent, logMessage); }
    public void DoubleDown(int remaining, int payout, int potential) { var agent = Live; if (agent != null) doubleDown(agent, remaining, payout, potential); }

    // The gathering games' clock, as the agent's update writes it while the Gold Saucer's game manager runs (it doesn't
    // in a home): the remaining seconds into NumberArray 104 [2] (the round's dial) and their "m:ss" text (Addon 33's
    // format) into agent+0x28 (the dial's text and Double Down's "Time Remaining").
    private int shownSeconds = -1;
    public void Time(int seconds)
    {
        var agent = Live;
        if (agent == null || seconds == shownSeconds) return;
        shownSeconds = seconds;
        var stage = AtkStage.Instance();
        if (stage != null && stage->GetNumberArrayData((NumberArrayType)104) is var numbers && numbers != null && numbers->Size > 2) numbers->SetValue(2, seconds);
        ((FFXIVClientStructs.FFXIV.Client.System.String.Utf8String*)((byte*)agent + 0x28))->SetString($"{seconds / 60}:{seconds % 60:00}");
    }

    /// <summary>A UI sound effect by the game's id.</summary>
    public void Sound(uint id) { if (sound != null && id != 0) sound(id, 0, 0, 0); }
    public bool CanPlaySounds => sound != null;

    /// <summary>The player's base idle animation (a machine's idle pose); 3 is the normal idle.</summary>
    public void BaseIdle(Character* character, ushort timeline)
    {
        if (!Available || character == null) return;
        baseIdle(&character->Timeline, timeline);
        refreshBase(&character->Timeline);
    }

    /// <summary>Closes the window and lets go of the event interface (the agent forgets it first).</summary>
    public void Close(bool begun)
    {
        if (!Open) return;
        var agent = Agent;
        if (agent != null)
        {
            close(agent);
            // The agent keeps the name pointer (+0xA0) after closing and reads it when a window finishes opening: clear it
            // before the buffer goes (a null name skips the prompt; opening always writes it again).
            *(nint*)((byte*)agent + 0xA0) = 0;
        }
        // Opening sets UI flag 4 (the nameplates) and a round's start clears it, and each change can switch on the text
        // commands' emote restriction, which the Gold Saucer's event script would settle afterwards: both go back to how
        // they were before the window opened. Without this, typed emotes were refused for the rest of the visit.
        if (restriction is { } before)
        {
            HavenNative.CommandRestriction.Restore(before);
            log.Information($"[ARCADE] Put back the UI flags as they were ({HavenNative.CommandRestriction.Describe()}; the game had begun: {begun}).");
        }
        else if (agent != null && !begun && *(void**)((byte*)agent + 0x10) is var ui && ui != null)
        {
            // Unknown game version: at least undo opening's flag 4, and the restriction that change switches on.
            ((delegate* unmanaged<void*, int, byte, void>)(*(nint**)ui)[2])(ui, 4, 0);
            HavenNative.CommandRestriction.Lift();
        }
        restriction = null;
        Release();
        shownSeconds = -1;
        log.Information("[ARCADE] Closed.");
    }

    private void Release()
    {
        current = null;
        if (owner != 0) Marshal.FreeHGlobal(owner);
        if (vtable != 0) Marshal.FreeHGlobal(vtable);
        if (name != 0) Marshal.FreeHGlobal(name);
        owner = vtable = name = 0;
        messages.Clear();
    }

    // The agent calls its owner's first slot with [code, value, value]; the returned value must be left empty, because
    // the agent destroys it. Messages are queued and answered on the next framework update.
    [UnmanagedCallersOnly]
    private static AtkValue* Receive(nint self, AtkValue* result, AtkValue* values, uint count, ulong kind)
    {
        if (result != null) new Span<byte>(result, sizeof(AtkValue)).Clear();
        if (current is { } arcade && self == arcade.owner && values != null && count > 0)
            arcade.messages.Enqueue((values[0].Int, count > 1 ? values[1].Int : 0, count > 2 ? values[2].Int : 0));
        return result;
    }
    [UnmanagedCallersOnly] private static void Nothing(nint self) { }

    /// <summary>The entry prompt's two lines (the game and the player's personal best), shown in place of the fee.</summary>
    public (string Question, string Best)? Prompt { get; set; }

    // The entry prompt (10 values: value 2 the question, value 3 the choice count, value 9 the ticket line) asks "Play a
    // round of X for 0 MGP? You currently possess N." and offers a Gold Saucer ticket as a third choice when the player
    // holds any (the agent counts item 10085 and raises its choice count, agent +0xEE, to 3). Haven's games are free: the
    // question names the game and the player's best instead, and only Yes and No are offered.
    private void OnPromptSetup(AddonEvent type, AddonArgs args)
    {
        if (!Open || args is not AddonSetupArgs { AtkValueCount: 10 } setup) return;
        var agent = Agent;
        var values = (AtkValue*)setup.AtkValues;
        if (agent == null || values == null || *((byte*)agent + 0xEE) is not (2 or 3) || values[3].Type != AtkValueType.Int) return;
        if (values[3].Int == 3 && values[9].Type != 0) { values[3].Int = 2; log.Information($"[ARCADE] Left the ticket choice off the {args.AddonName} prompt."); }
        if (Prompt is { } prompt && IsText(values[2]))
        {
            var text = new Dalamud.Game.Text.SeStringHandling.SeStringBuilder().AddText(prompt.Question)
                .Add(Dalamud.Game.Text.SeStringHandling.Payloads.NewLinePayload.Payload).AddText(prompt.Best).Build().Encode();
            SetText(ref values[2], text);
        }
    }

    // Double Down speaks of payouts in MGP: while Haven's game is open, the windows it opens say points instead.
    private void OnPayoutValues(AddonEvent type, AddonArgs args)
    {
        if (!Open) return;
        AtkValue* values = null; uint count = 0;
        if (args is AddonSetupArgs setup) { values = (AtkValue*)setup.AtkValues; count = setup.AtkValueCount; }
        else if (args is AddonRefreshArgs refresh) { values = (AtkValue*)refresh.AtkValues; count = refresh.AtkValueCount; }
        if (values == null || count > 256) return;
        ReadOnlySpan<byte> mgp = "MGP"u8, points = "points"u8;
        for (var i = 0; i < count; i++)
        {
            if (!IsText(values[i]) || values[i].String.Value == null) continue;
            var bytes = System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpanFromNullTerminated(values[i].String.Value);
            if (bytes.IndexOf(mgp) < 0) continue;
            var changed = new List<byte>(bytes.Length + 16);
            for (var at = 0; at < bytes.Length;)
                if (bytes[at..].StartsWith(mgp)) { changed.AddRange(points); at += mgp.Length; }
                else changed.Add(bytes[at++]);
            SetText(ref values[i], [.. changed]);
        }
    }

    private static bool IsText(AtkValue value) => value.Type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.String8;

    // A game-owned copy (freed with the value, as the game frees its own), null-terminated.
    private static void SetText(ref AtkValue value, byte[] text)
    {
        var terminated = new byte[text.Length + 1];
        text.CopyTo(terminated, 0);
        fixed (byte* pointer = terminated) value.SetManagedString(pointer);
    }

    public void Dispose()
    {
        lifecycle.UnregisterListener(AddonEvent.PreSetup, OnPromptSetup);
        lifecycle.UnregisterListener(AddonEvent.PreSetup, OnPayoutValues);
        lifecycle.UnregisterListener(AddonEvent.PreRefresh, OnPayoutValues);
        Close(false);
    }
}
