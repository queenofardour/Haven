using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using FFXIVClientStructs.FFXIV.Component.Shell;
using Haven.Core;
using HavenNative.Sync;
using Lumina.Excel.Sheets;

namespace HavenNative;

/// <summary>Intercept chat before native submission. An error while isolated never falls back to the game.</summary>
internal sealed class PrivateRoomChatService : IDisposable
{
    private unsafe delegate void ProcessChat(UIModule* module, Utf8String* text, nint context, bool history);
    private unsafe delegate void ShellCommand(ShellCommandModule* module, Utf8String* text, UIModule* ui);
    private Hook<ProcessChat>? chatHook;
    private Hook<ShellCommand>? shellHook;
    private readonly IChatGui chat;
    private readonly IPluginLog log;
    private readonly IFramework framework;
    private readonly ICommandManager commands;
    private readonly IObjectTable objects;
    private readonly RelaySyncService relay;
    private readonly Func<bool> isolated;
    private readonly HashSet<string> localCommands = new(StringComparer.OrdinalIgnoreCase)
    { "/sit", "/doze", "/cpose", "/changepose", "/gpose", "/grouppose", "/echo", "/e", "/wait", "/macrolock", "/macroerror" };
    private readonly Dictionary<string, int> chatCommands = new(StringComparer.OrdinalIgnoreCase);
    // Real-world chat commands (tells, free company, linkshells) and their channel.
    private readonly Dictionary<string, WorldChannels> worldCommands = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The real-world channels let through inside a home (Settings → General).</summary>
    public Func<WorldChannels> WorldAllowed { get; set; } = () => WorldChannels.All;
    /// <summary>Runs the game's handling of a real-world chat line, counting zone packets it tried to send (PacketFilterService.WatchWorldChat).</summary>
    public Func<System.Action, int>? WatchWorldChat { get; set; }
    private readonly Dictionary<(XivChatType Kind, string Name, string Text), int> pendingPrints = [];
    private IChatMessage? authorizedPrint;
    private bool disposed;
    public bool Available => chatHook != null && shellHook != null;
    public event System.Action? Submitted;
    public Func<ushort, bool>? TryPlayLockedEmote { get; set; }
    /// <summary>Plays an emote as the emote window does (a typed emote with nothing after it, inside a home).</summary>
    public Func<ushort, bool>? PlayEmote { get; set; }
    /// <summary>While the player possesses an NPC: a typed emote it played, and a /say line it spoke (true: handled).</summary>
    public static Func<ushort, bool>? NpcEmote { get; set; }
    public static Func<string, bool>? NpcSay { get; set; }
    /// <summary>Prints a /say line as spoken by the named NPC (framework thread), past the home's chat-log filter.</summary>
    public static Action<string, string>? SpeakAs { get; private set; }
    // A typed emote handed to the game's own parser, to explain it if the game refuses it.
    private (string Command, long At)? passedEmote;
    /// <summary>Whether room chat senders carry the " [Haven]" marker (Settings → General).</summary>
    public Func<bool> ShowTag { get; set; } = () => true;
    private readonly LocalEmoteCommands emoteCommands = new();
    // Room chat is never lost: each line waits here until the relay echoes it back. Lines typed while the room is
    // reconnecting wait in the outbox and are sent once it is back; a line with no echo returns to the outbox.
    private sealed class Outgoing(RoomChatMessage message, long queuedAt)
    {
        public RoomChatMessage Message { get; } = message;
        public long QueuedAt { get; } = queuedAt;
        public long SentAt;
    }
    private readonly LinkedList<Outgoing> outbox = new(), awaitingEcho = new();
    private const int MaxOutbox = 60;
    private const long EchoTimeoutMs = 8000, GiveUpMs = 30 * 60 * 1000, SendSpacingMs = 550;
    private long nextSendAt;
    private bool sending, toldWaiting;

    public unsafe PrivateRoomChatService(IGameInteropProvider hooks, IChatGui chat, IPluginLog log, IFramework framework,
        ICommandManager commands, IObjectTable objects, IDataManager data, RelaySyncService relay, Func<bool> isolated)
    {
        this.chat = chat; this.log = log; this.framework = framework; this.commands = commands;
        this.objects = objects; this.relay = relay; this.isolated = isolated;
        SpeakAs = (name, text) => Print(new RoomChatMessage((int)XivChatType.Say, text, name));
        foreach (var row in data.GetExcelSheet<TextCommand>())
        {
            var aliases = new[] { row.Command.ToString(), row.ShortCommand.ToString(), row.Alias.ToString(), row.ShortAlias.ToString() };
            var channel = 0;
            foreach (var alias in aliases) if (RoomChatCommand.Channel(alias) != 0) channel = RoomChatCommand.Channel(alias);
            foreach (var alias in aliases) if (channel != 0 && alias.StartsWith('/')) chatCommands[alias] = channel;
            var world = WorldChat.Command(aliases);
            foreach (var alias in aliases) if (world != WorldChannels.None && alias.StartsWith('/')) worldCommands[alias] = world;
        }
        foreach (var emote in data.GetExcelSheet<Emote>())
        {
            if (emote.TextCommand.RowId == 0) continue;
            var row = emote.TextCommand.Value;
            foreach (var alias in new[] { row.Command.ToString(), row.ShortCommand.ToString(), row.Alias.ToString(), row.ShortAlias.ToString() })
                if (alias.StartsWith('/') && !chatCommands.ContainsKey(alias))
                { localCommands.Add(alias); emoteCommands.Add(alias, checked((ushort)emote.RowId)); }
        }
        try
        {
            chatHook = hooks.HookFromAddress<ProcessChat>((nint)UIModule.MemberFunctionPointers.ProcessChatBoxEntry, OnChat);
            shellHook = hooks.HookFromAddress<ShellCommand>((nint)ShellCommandModule.MemberFunctionPointers.ExecuteCommandInner, OnShell);
            chatHook.Enable(); shellHook.Enable();
        }
        catch (Exception ex)
        {
            chatHook?.Dispose(); shellHook?.Dispose(); chatHook = null; shellHook = null;
            log.Error(ex, "[Haven] Private chat interception unavailable; house entry is disabled.");
        }
        chat.ChatMessage += AuthorizePrint;
        chat.CheckMessageHandled += Filter;
        chat.ChatMessageHandled += PrintHandled;
        chat.ChatMessageUnhandled += PrintDisplayed;
        relay.OnRoomChatDelivery += Receive;
        relay.OnRoomChatReplay += Replayed;
        relay.OnRoomChatRejected += RelayRejected;
        framework.Update += Pump;
    }

    private unsafe void OnChat(UIModule* module, Utf8String* text, nint context, bool history)
    {
        if (!isolated()) { chatHook!.Original(module, text, context, history); return; }
        try
        {
            if (WorldChannelOf(text) is { } world) SendWorld(world, () => chatHook!.Original(module, text, context, history));
            else if (!Handle(text)) chatHook!.Original(module, text, context, history);
        }
        catch (Exception ex) { log.Error(ex, "[Haven] Private chat submission blocked after an error."); chat.PrintError("[Haven] Message blocked; it was not sent to the game world."); }
    }
    private unsafe void OnShell(ShellCommandModule* module, Utf8String* text, UIModule* ui)
    {
        if (!isolated()) { shellHook!.Original(module, text, ui); return; }
        try
        {
            if (WorldChannelOf(text) is { } world) SendWorld(world, () => shellHook!.Original(module, text, ui));
            else if (!Handle(text)) shellHook!.Original(module, text, ui);
        }
        catch (Exception ex) { log.Error(ex, "[Haven] Private macro chat blocked after an error."); }
    }

    // A line for a real-world channel Haven lets through (a tell, free company or linkshell line): its command, or the
    // chat box's current channel for a line without one. Null for everything else.
    private unsafe WorldChannels? WorldChannelOf(Utf8String* text)
    {
        if (text == null) return null;
        var raw = text->ToString().Trim();
        if (raw.Length == 0) return null;
        string command;
        if (raw.StartsWith('/')) (command, _) = RoomChatCommand.Split(raw);
        else
        {
            var shell = RaptureShellModule.Instance();
            var current = shell == null ? "" : ((ShellCommandModule*)shell)->CurrentChannel.ToString().Trim();
            command = current.Split(' ', 2)[0];
        }
        return worldCommands.TryGetValue(command, out var world) ? world : null;
    }

    // The game handles a real-world chat line itself and sends it on its chat connection, which the filter never sees;
    // anything it tries on the zone connection meanwhile stays held back and is counted. A channel turned off in
    // Settings is refused here, so nothing reaches the game.
    private void SendWorld(WorldChannels channel, System.Action send)
    {
        if (!WorldAllowed().HasFlag(channel))
        {
            chat.PrintError("[Haven] " + WorldName(channel) + " are turned off inside a home (Settings → General → Real-world chat in a home).");
            return;
        }
        Submitted?.Invoke();
        if (WatchWorldChat == null) { chat.PrintError("[Haven] Real-world chat is unavailable here."); return; }
        var held = WatchWorldChat(send);
        if (held == 0) log.Information("[Haven] [WORLD-CHAT] {Channel} line handled by the game; nothing needed the filter.", channel);
        else log.Warning("[Haven] [WORLD-CHAT] {Channel} line: the game also tried {Held} zone packet(s), held back; if the line didn't arrive, this is why.", channel, held);
    }
    private static string WorldName(WorldChannels channel) => channel switch
    {
        WorldChannels.Tells => "Tells",
        WorldChannels.FreeCompany => "Free company messages",
        WorldChannels.Party => "Party messages",
        WorldChannels.Linkshells => "Linkshell messages",
        _ => "Cross-world linkshell messages",
    };

    private unsafe bool Handle(Utf8String* text)
    {
        if (text == null) return true;
        var raw = text->ToString().Trim();
        if (raw.Length == 0) return true;
        Submitted?.Invoke();
        var (command, body) = RoomChatCommand.Split(raw);
        var explicitCommand = raw.StartsWith('/');
        if (!explicitCommand)
        {
            var shell = RaptureShellModule.Instance();
            var current = shell == null ? "" : ((ShellCommandModule*)shell)->CurrentChannel.ToString();
            command = current.Trim().ToLowerInvariant(); body = raw;
        }
        // /random and /dice roll in the room: the relay picks the number, and everyone sees the same roll.
        if (explicitCommand && command is "/random" or "/dice") { Roll(body); return true; }
        var channel = chatCommands.GetValueOrDefault(command, RoomChatCommand.Channel(command));
        if (channel == (int)XivChatType.Say && body.Length > 0 && NpcSay?.Invoke(body) == true) return true;
        if (channel != 0)
        {
            if (body.Length == 0)
            {
                // A channel-only command changes local chat mode, never submits a message.
                return false;
            }
            // A long post goes out as several lines (room chat carries at most 1000 UTF-8 bytes each).
            var parts = RoomChatText.Split(body, RoomChat.MaxTextBytes);
            var messages = parts.Select(part => new RoomChatMessage(channel, part)).ToArray();
            foreach (var message in messages) RoomChat.Encode(message); // Reject control/SeString payloads before queuing.
            // Local feedback must not depend on an asynchronous round trip or a self-frame policy.
            foreach (var message in messages) Print(message with { Name = objects.LocalPlayer?.Name.TextValue ?? "You" });
            if (relay.SoloMode) return true;
            if (outbox.Count + messages.Length > MaxOutbox)
            { Rejected("Too many messages are waiting to be sent. This one was not sent: " + body); return true; }
            var now = Environment.TickCount64;
            foreach (var message in messages) outbox.AddLast(new Outgoing(message, now));
            if ((!relay.IsConnected || !relay.HousingSceneReady) && !toldWaiting)
            { toldWaiting = true; chat.PrintError("[Haven] The room is reconnecting. Your message is kept and will be sent when it is back."); }
            Pump(framework);
            return true;
        }
        if (explicitCommand && body.Length == 0 && emoteCommands.Id(command) is { } npcEmote && NpcEmote?.Invoke(npcEmote) == true) return true;
        // Intercept before native command parsing; quest/unlock checks there can precede the executor hooks.
        if (explicitCommand && emoteCommands.TryPlay(command, TryPlayLockedEmote)) return true;
        // A typed emote alone plays as the emote window plays it; with more after it ("/laugh motion", a target's name)
        // the game's parser reads the rest.
        if (explicitCommand && body.Length == 0 && emoteCommands.Id(command) is { } emote && PlayEmote?.Invoke(emote) == true) return true;
        if (explicitCommand && emoteCommands.Id(command) != null) passedEmote = (command, Environment.TickCount64);
        // Only known local emotes/UI commands and registered plugin commands reach the native parser.
        if (explicitCommand && (localCommands.Contains(command) || commands.Commands.ContainsKey(command))) return false;
        chat.PrintError("[Haven] World chat/actions are blocked here. Use /say, /yell, /shout or /em for room chat; tells, party, free company and linkshells still reach the world.");
        return true;
    }
    private void Roll(string body)
    {
        if (RoomRolls.Limit(body) is not int limit) { chat.PrintError("[Haven] The upper limit for a roll is 2 to 999."); return; }
        if (relay.SoloMode)
        {
            // Alone, there is no one to keep honest: rolled here.
            PrintRoll(RoomRolls.Text(null, RoomRolls.Roll(limit), limit));
            return;
        }
        if (outbox.Count >= MaxOutbox) { Rejected("Too many messages are waiting to be sent. The roll was not sent."); return; }
        outbox.AddLast(new Outgoing(new RoomChatMessage(RoomRolls.Channel, "/random", Roll: limit), Environment.TickCount64));
        if ((!relay.IsConnected || !relay.HousingSceneReady) && !toldWaiting)
        { toldWaiting = true; chat.PrintError("[Haven] The room is reconnecting. Your roll will be made when it is back."); }
        Pump(framework);
    }
    // A roll shows as the game shows its own: a "Random!" line with no sender.
    private void PrintRoll(string text)
    {
        printed.Enqueue((Environment.TickCount64, RoomRolls.Channel, "", text)); while (printed.Count > 200) printed.Dequeue();
        Print(new RoomChatMessage(RoomRolls.Channel, text), named: false);
    }
    // Framework thread: sends the next waiting line, at most one in flight and two a second (the relay's chat
    // allowance), and returns unechoed lines to the outbox.
    private void Pump(IFramework frame)
    {
        if (disposed) return;
        var now = Environment.TickCount64;
        if (!isolated())
        {
            // Left the home with lines unsent: give them back rather than drop them.
            foreach (var item in awaitingEcho) chat.PrintError("[Haven] May not have been sent: " + item.Message.Text);
            foreach (var item in outbox) chat.PrintError("[Haven] Not sent: " + item.Message.Text);
            awaitingEcho.Clear(); outbox.Clear(); toldWaiting = false;
            return;
        }
        for (var node = awaitingEcho.First; node != null;)
        {
            var next = node.Next;
            if (now - node.Value.SentAt > EchoTimeoutMs) { awaitingEcho.Remove(node); outbox.AddFirst(node.Value); }
            node = next;
        }
        while (outbox.First is { } stale && now - stale.Value.QueuedAt > GiveUpMs)
        { outbox.RemoveFirst(); chat.PrintError("[Haven] This message could not be sent in 30 minutes: " + stale.Value.Message.Text); }
        if (outbox.Count == 0 && awaitingEcho.Count == 0) toldWaiting = false;
        if (sending || outbox.First == null || now < nextSendAt || !relay.IsConnected || !relay.HousingSceneReady) return;
        var first = outbox.First.Value;
        outbox.RemoveFirst();
        first.SentAt = now; awaitingEcho.AddLast(first);
        sending = true; nextSendAt = now + SendSpacingMs;
        _ = Send(first);
    }
    private async Task Send(Outgoing item)
    {
        var sent = false;
        try { sent = await relay.TrySendRoomChat(item.Message); }
        catch (Exception ex) { log.Warning(ex, "[Haven] Room chat send failed; it will be sent again."); }
        _ = framework.RunOnFrameworkThread(() =>
        {
            sending = false;
            if (!sent && awaitingEcho.Remove(item)) { outbox.AddFirst(item); nextSendAt = Environment.TickCount64 + 1000; }
        });
    }
    // The relay refused the line in flight (too fast, or the home not ready): it goes back to the front and is tried
    // again shortly, so nothing typed is lost.
    private void RelayRejected(string reason) => _ = framework.RunOnFrameworkThread(() =>
    {
        if (awaitingEcho.First is { } oldest) { awaitingEcho.RemoveFirst(); outbox.AddFirst(oldest.Value); nextSendAt = Environment.TickCount64 + 1500; }
        log.Debug("[Haven] Room chat line refused by the relay ({Reason}); it will be sent again.", SafeText.Clean(reason, 200));
    });
    /// <summary>Words a received emote line for this viewer, or null to keep the sender's text.</summary>
    public Func<RoomChatMessage, string?>? RenderEmote { get; set; }
    // Lines printed recently (channel, sender, text), so a replay after reconnecting shows only what was missed.
    private readonly Queue<(long At, int Channel, string Name, string Text)> printed = new();
    private bool Seen(RoomChatMessage message, long now)
    {
        while (printed.Count > 0 && now - printed.Peek().At > ChatReplay.SeenMs) printed.Dequeue();
        foreach (var line in printed) if (line.Channel == message.Channel && line.Name == message.Name && line.Text == message.Text) return true;
        return false;
    }
    private void Replayed(RoomChatMessage message, ulong from)
    {
        var session = relay.LocalPeerId;
        _ = framework.RunOnFrameworkThread(() =>
        {
            if (disposed || !isolated() || !relay.HousingSceneReady || relay.LocalPeerId != session) return;
            message = Named(message, from);
            if (message.Channel == RoomRolls.Channel)
            {
                if (message.Rolled < 0) return;
                var line = RoomRolls.Text(message.Name, message.Rolled, message.Roll);
                if (!Seen(new RoomChatMessage(RoomRolls.Channel, line), Environment.TickCount64)) PrintRoll(line);
                return;
            }
            if (message.Emote != 0 && RenderEmote?.Invoke(message) is { } worded) message = message with { Text = worded };
            if (Seen(message, Environment.TickCount64)) return;
            Print(message);
        });
    }
    private void Receive(RoomChatMessage message, bool own, ulong from)
    {
        if (own)
        {
            // The relay's echo of our own line: delivered.
            _ = framework.RunOnFrameworkThread(() =>
            {
                // Our own roll comes back made by the relay: shown now, with "You".
                if (message.Channel == RoomRolls.Channel)
                {
                    for (var node = awaitingEcho.First; node != null; node = node.Next)
                        if (node.Value.Message.Channel == RoomRolls.Channel) { awaitingEcho.Remove(node); break; }
                    if (!disposed && isolated() && message.Rolled >= 0) PrintRoll(RoomRolls.Text(null, message.Rolled, message.Roll));
                    return;
                }
                for (var node = awaitingEcho.First; node != null; node = node.Next)
                    if (node.Value.Message.Channel == message.Channel && node.Value.Message.Text == message.Text) { awaitingEcho.Remove(node); return; }
            });
            return;
        }
        var session = relay.LocalPeerId;
        _ = framework.RunOnFrameworkThread(() =>
        {
            if (disposed || !isolated() || !relay.HousingSceneReady || relay.LocalPeerId != session) return;
            message = Named(message, from);
            if (message.Channel == RoomRolls.Channel) { if (message.Rolled >= 0) PrintRoll(RoomRolls.Text(message.Name, message.Rolled, message.Roll)); return; }
            // An emote line is worded for this viewer ("blows you a kiss" when you are its target).
            if (message.Emote != 0 && RenderEmote?.Invoke(message) is { } worded) message = message with { Text = worded };
            Print(message);
        });
    }
    // A member's line goes by the name of their character as the game shows it here, when it is in the home (it always
    // is once they're in: gatherings reuse the real characters), rather than the name the relay gives: a client can't
    // pass its character off as someone else's. Their relay name is kept only while their character isn't here.
    private unsafe RoomChatMessage Named(RoomChatMessage message, ulong from)
    {
        if (from == 0) return message;
        foreach (var obj in objects)
            if (obj is Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter player
                && ((FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)player.Address)->ContentId == from)
                return player.Name.TextValue is { Length: > 0 } name && name != message.Name ? message with { Name = name } : message;
        return message;
    }
    private void AuthorizePrint(IHandleableChatMessage message)
    {
        authorizedPrint = null;
        var key = (message.LogKind, message.OriginalSender.ExtractText(), message.OriginalMessage.ExtractText());
        if (!pendingPrints.TryGetValue(key, out var count)) return;
        if (count == 1) pendingPrints.Remove(key); else pendingPrints[key] = count - 1;
        authorizedPrint = message;
    }
    private void PrintHandled(IChatMessage message)
    {
        if (!ReferenceEquals(message, authorizedPrint)) return;
        if (isolated()) log.Warning("[Haven] A queued room chat line was suppressed by a chat handler (channel {Channel}); no message text logged.", (int)message.LogKind);
        authorizedPrint = null;
    }
    private void PrintDisplayed(IChatMessage message)
    {
        if (!ReferenceEquals(message, authorizedPrint)) return;
        log.Debug("[Haven] Room chat line passed chat handlers (channel {Channel}).", (int)message.LogKind);
        authorizedPrint = null;
    }
    private void Rejected(string reason) => _ = framework.RunOnFrameworkThread(() => chat.PrintError("[Haven] " + reason));

    /// <summary>
    /// An emote's log line: printed here as the game prints its own (no sender), and sent to the room as an emote line
    /// that follows the sender's name. Nothing is sent when the room is unavailable; the line is cosmetic.
    /// </summary>
    public void EmoteLine(string local, string? broadcast, ushort emote = 0, string target = "")
    {
        if (disposed || !isolated()) return;
        Print(new RoomChatMessage(29, local), named: false);
        if (broadcast == null || relay.SoloMode || !relay.IsConnected || !relay.HousingSceneReady) return;
        var message = new RoomChatMessage(28, broadcast, Emote: emote, Target: SafeText.Clean(target, 80));
        try { RoomChat.Encode(message); } catch (InvalidDataException) { return; }
        _ = SendQuietly(message);
    }
    private async Task SendQuietly(RoomChatMessage message)
    {
        try { await relay.TrySendRoomChat(message); }
        catch (Exception ex) { log.Debug(ex, "[Haven] Emote line send skipped."); }
    }
    private void Print(RoomChatMessage message, bool named = true)
    {
        if (named) { printed.Enqueue((Environment.TickCount64, message.Channel, message.Name, message.Text)); while (printed.Count > 200) printed.Dequeue(); }
        var name = !named ? "" : ShowTag() ? message.Name + " [Haven]" : message.Name;
        var key = ((XivChatType)message.Channel, name, message.Text);
        pendingPrints[key] = pendingPrints.GetValueOrDefault(key) + 1;
        chat.Print(new XivChatEntry { Type = key.Item1, Name = new SeString(new Dalamud.Game.Text.SeStringHandling.Payloads.TextPayload(name)),
            Message = new SeString(new Dalamud.Game.Text.SeStringHandling.Payloads.TextPayload(message.Text)) });
    }
    // The game refused a typed emote inside a home: what the player's character and the game's conditions were, so the
    // cause can be found (once every emote command was refused for the rest of a visit while the window still worked).
    private unsafe void LogRefusal(string command, string text)
    {
        try
        {
            var conditions = FFXIVClientStructs.FFXIV.Client.Game.Conditions.Instance();
            var set = new List<int>();
            if (conditions != null) for (var i = 1; i < 104; i++) if (((bool*)conditions)[i]) set.Add(i);
            var player = objects.LocalPlayer is { } local ? (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)local.Address : null;
            var restricted = CommandRestriction.Restricted;
            log.Warning("[EMOTE-CMD] the game refused {Command}: \"{Text}\"; mode {Mode}/{Param}, emote {Emote}, base override {Base}, conditions [{Conditions}], {Flags}",
                command, text, player == null ? -1 : (int)player->Mode, player == null ? -1 : player->ModeParam, player == null ? -1 : player->EmoteController.EmoteId,
                player == null ? -1 : player->Timeline.BaseOverride, string.Join(",", set), CommandRestriction.Describe());
            // A restriction left on (a Gold Saucer machine's flags) refuses every typed emote: inside a home nothing should.
            if (restricted && isolated()) { CommandRestriction.Lift(); log.Warning("[EMOTE-CMD] lifted the text commands' emote restriction."); }
        }
        catch (Exception ex) { log.Warning(ex, "[EMOTE-CMD] the game refused {Command}: \"{Text}\"", command, text); }
    }

    private void Filter(IHandleableChatMessage message)
    {
        // Follow the event identity through later plugin formatting, rather than re-matching altered text.
        if (ReferenceEquals(message, authorizedPrint))
        { if (!isolated()) message.PreventOriginal(); return; }
        var key = (message.LogKind, message.OriginalSender.ExtractText(), message.OriginalMessage.ExtractText());
        if (pendingPrints.TryGetValue(key, out var count))
        {
            if (count == 1) pendingPrints.Remove(key); else pendingPrints[key] = count - 1;
            if (!isolated()) message.PreventOriginal();
            return;
        }
        if (!isolated()) return;
        var kind = message.LogKind;
        if (passedEmote is { } passed && Environment.TickCount64 - passed.At < 2000 && kind == XivChatType.ErrorMessage) { passedEmote = null; LogRefusal(passed.Command, message.OriginalMessage.ExtractText()); }
        // Real world player channels are hidden, except those let through (tells, free company, linkshells, as
        // chosen in Settings); system/errors and local UI feedback remain visible.
        var world = WorldChat.Log((int)kind);
        if (world != WorldChannels.None && WorldAllowed().HasFlag(world)) return;
        if (kind is >= XivChatType.Say and <= XivChatType.CrossLinkShell1
            || kind is >= XivChatType.CrossLinkShell2 and <= XivChatType.CrossLinkShell8)
            message.PreventOriginal();
    }
    public void Dispose()
    {
        disposed = true; SpeakAs = null;
        chatHook?.Dispose(); shellHook?.Dispose();
        chat.ChatMessage -= AuthorizePrint; chat.CheckMessageHandled -= Filter;
        chat.ChatMessageHandled -= PrintHandled; chat.ChatMessageUnhandled -= PrintDisplayed;
        relay.OnRoomChatDelivery -= Receive; relay.OnRoomChatReplay -= Replayed; relay.OnRoomChatRejected -= RelayRejected; framework.Update -= Pump;
        pendingPrints.Clear();
    }
}
