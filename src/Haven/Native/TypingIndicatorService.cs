using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using Haven.Core;
using HavenNative.Services;
using HavenNative.Sync;

namespace HavenNative;

internal sealed class TypingIndicatorService : IDisposable
{
    private readonly IFramework framework;
    private readonly IGameGui gui;
    private readonly IObjectTable objects;
    private readonly IPluginLog log;
    private readonly RelaySyncService relay;
    private readonly StateApplyService actors;
    private readonly PrivateRoomChatService chat;
    private readonly Func<bool> inHome;
    private readonly TypingActivity activity = new(), chatTwoActivity = new();
    // Match the published tuple contract using ushort for ChatTwo.Code.ChatType.
    // Dalamud converts the enum across plugin boundaries; there is no Chat 2 dependency.
    private readonly ICallGateSubscriber<(bool Visible, bool Focused, bool HasText, bool IsTyping, int Length, ushort Channel)> chatTwoQuery;
    private bool chatTwoFocused;
    private int chatTwoEdited;
    private long chatTwoRetry;
    private bool chatTwoFailureLogged;
    private readonly ConcurrentDictionary<string, (TypingState State, long Until)> remote = new();
    private readonly IDalamudPluginInterface pluginInterface;
    private record Marker(ushort Index, nint Address, uint EntityId, TypingState State, bool Npc = false);

    /// <summary>
    /// Set by the plugin: the object index of the NPC a character (by content id) is speaking as, or null. A bubble then
    /// shows over the NPC instead of the character, on every PC.
    /// </summary>
    public static Func<ulong, int?>? SpeakerFor { get; set; }
    private Marker[] markers = [];
    private TypingState local, sent;
    private long nextPoll, nextSend, heartbeat;
    private int submitted;
    private bool inputFailureLogged;
    private long nextInputRetry;
    /// <summary>Settings → General: whether others see when you type, and whether you see others' bubbles.</summary>
    public Func<bool> ShareOwn { get; set; } = () => true;
    public Func<bool> ShowOthers { get; set; } = () => true;

    public TypingIndicatorService(IDalamudPluginInterface pluginInterface, IFramework framework, IGameGui gui, IObjectTable objects, IPluginLog log,
        RelaySyncService relay, StateApplyService actors, PrivateRoomChatService chat, Func<bool> inHome)
    {
        this.pluginInterface = pluginInterface;
        chatTwoQuery = pluginInterface.GetIpcSubscriber<(bool, bool, bool, bool, int, ushort)>("ChatTwo.GetChatInputState");
        pluginInterface.UiBuilder.Draw += Draw;
        this.framework = framework; this.gui = gui; this.objects = objects; this.log = log;
        this.relay = relay; this.actors = actors; this.chat = chat; this.inHome = inHome;
        framework.Update += Update;
        chat.Submitted += Submitted;
        relay.OnTyping += Received;
        relay.OnPeerLeft += Left;
        relay.OnDisconnected += Disconnected;
        relay.OnRoomJoined += Joined;
    }
    private void Submitted() => Interlocked.Exchange(ref submitted, 1);
    private void Left(string peer) => remote.TryRemove(peer, out _);
    private void Disconnected() => remote.Clear();
    private void Joined(RoomJoinedData _) => remote.Clear();
    private void Received(string peer, TypingState state)
    {
        if (state == TypingState.Idle) remote.TryRemove(peer, out _);
        else remote[peer] = (state, Environment.TickCount64 + TypingWire.ExpireAfterMs);
    }
    private async Task Publish(TypingState state)
    {
        try { await relay.SendTyping(state); }
        catch (Exception ex) { log.Debug(ex, "[Haven] Typing presence send skipped."); }
    }
    private unsafe TypingState ReadInput(long now)
    {
        if (now < nextInputRetry) return TypingState.Idle;
        try
        {
            var addon = (AddonChatLog*)gui.GetAddonByName("ChatLog").Address;
            return ChatTypingInput.Observe(addon, activity, now);
        }
        catch (Exception ex)
        {
            nextInputRetry = now + 1000;
            if (!inputFailureLogged) log.Warning(ex, "[Haven] Typing input unavailable; retrying after the chat UI reloads.");
            inputFailureLogged = true;
            return TypingState.Idle;
        }
    }
    private TypingState ReadChatTwo(long now)
    {
        var edited = Interlocked.Exchange(ref chatTwoEdited, 0) != 0;
        chatTwoFocused = false;
        if (now < chatTwoRetry) return TypingState.Idle;
        if (!chatTwoQuery.HasFunction) { chatTwoActivity.Reset(); return TypingState.Idle; }
        try
        {
            var state = chatTwoQuery.InvokeFunc();
            chatTwoFocused = state.Visible && state.Focused;
            return chatTwoActivity.ObserveMetadata(now, chatTwoFocused, state.HasText, state.Length, edited);
        }
        catch (Exception ex)
        {
            chatTwoActivity.Reset(); chatTwoRetry = now + 1000;
            if (!chatTwoFailureLogged) log.Warning(ex, "[Haven] Chat 2 typing state unavailable; retrying without changing chat.");
            chatTwoFailureLogged = true; return TypingState.Idle;
        }
    }
    private unsafe void Update(IFramework frame)
    {
        var now = Environment.TickCount64;
        if (now < nextPoll) return;
        nextPoll = now + 100;
        if (Interlocked.Exchange(ref submitted, 0) != 0) { activity.Submitted(); chatTwoActivity.Submitted(); }
        if (!inHome()) { activity.Reset(); chatTwoActivity.Reset(); chatTwoFocused = false; local = TypingState.Idle; remote.Clear(); }
        else
        {
            var native = ReadInput(now); var replacement = ReadChatTwo(now);
            local = native == TypingState.Active || replacement == TypingState.Active ? TypingState.Active
                : native == TypingState.Paused || replacement == TypingState.Paused ? TypingState.Paused : TypingState.Idle;
            if (!ShareOwn()) local = TypingState.Idle;
        }

        if (!relay.IsConnected || !relay.HousingSceneReady) { sent = TypingState.Idle; remote.Clear(); }
        else if ((local != sent || local != TypingState.Idle && now >= heartbeat) && (local == TypingState.Idle || now >= nextSend))
        {
            // At most two a second: the relay's typing allowance (8 burst, 2/s) silently drops more, and a dropped
            // Idle would leave the bubble up until it expires.
            sent = local; heartbeat = now + TypingWire.HeartbeatMs; nextSend = now + 500;
            _ = Publish(local);
        }
        var next = new List<Marker>();
        if (inHome())
        {
            var player = objects.LocalPlayer;
            if (player != null && local != TypingState.Idle) next.Add(Over(player, local));
            foreach (var entry in ShowOthers() ? remote : [])
            {
                if (now >= entry.Value.Until || relay.PeerConnections.TryGetValue(entry.Key, out var online) && !online)
                { remote.TryRemove(entry.Key, out _); continue; }
                if (actors.Peers.TryGetValue(entry.Key, out var actor) && actor.ObjectIndex is {} index && objects[(int)index] is {} obj)
                    next.Add(Over(obj, entry.Value.State));
            }
        }
        Volatile.Write(ref markers, next.ToArray());
    }

    // The bubble's character: the one typing, or the NPC they are speaking as.
    private unsafe Marker Over(Dalamud.Game.ClientState.Objects.Types.IGameObject typist, TypingState state)
    {
        var contentId = ((FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)typist.Address)->ContentId;
        if (SpeakerFor?.Invoke(contentId) is { } index && objects[index] is { } npc)
            return new((ushort)index, npc.Address, npc.EntityId, state, Npc: true);
        return new(typist.ObjectIndex, typist.Address, typist.EntityId, state);
    }

    private unsafe void Draw()
    {
        if (Haven.CinematicMode.Hiding) return;
        if (!inHome()) return;
        var player = objects.LocalPlayer;
        if (player == null) return;
        // Only an editing pulse is retained. This covers same-length replacement,
        // paste and deletion in Chat 2 without accessing or recording its text.
        if (chatTwoFocused && (ImGui.GetIO().InputQueueCharacters.Size > 0
            || ImGui.IsKeyPressed(ImGuiKey.Backspace) || ImGui.IsKeyPressed(ImGuiKey.Delete)
            || ImGui.GetIO().KeyCtrl && (ImGui.IsKeyPressed(ImGuiKey.V) || ImGui.IsKeyPressed(ImGuiKey.X)
                || ImGui.IsKeyPressed(ImGuiKey.Z) || ImGui.IsKeyPressed(ImGuiKey.Y))))
            Interlocked.Exchange(ref chatTwoEdited, 1);
        var viewport = ImGui.GetMainViewport();
        var scale = Math.Clamp(ImGui.GetIO().FontGlobalScale, 0.75f, 2f);
        foreach (var marker in Volatile.Read(ref markers))
        {
            var obj = objects[(int)marker.Index];
            if (obj == null || obj.Address != marker.Address || obj.EntityId != marker.EntityId || (byte)obj.ObjectKind != 1 && !marker.Npc || Vector3.DistanceSquared(player.Position, obj.Position) > 35 * 35) continue;
            var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address;
            FFXIVClientStructs.FFXIV.Common.Math.Vector3 overhead = default;
            if (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject.MemberFunctionPointers.GetNamePlateWorldPosition != null)
                native->GetNamePlateWorldPosition(&overhead);
            var anchor = new Vector3(overhead.X, overhead.Y, overhead.Z);
            if (!float.IsFinite(anchor.LengthSquared()) || anchor.Y <= obj.Position.Y || Vector3.DistanceSquared(anchor, obj.Position) > 10000)
            {
                var height = native->GetHeight(); // Model-aware virtual getter, not the raw Height field.
                if (!float.IsFinite(height) || height <= 0 || height > 100) continue;
                anchor = obj.Position + new Vector3(native->DrawOffset.X, native->DrawOffset.Y + height, native->DrawOffset.Z);
            }
            anchor.Y += 0.15f;
            if (!gui.WorldToScreen(anchor, out var screen) || !float.IsFinite(screen.X) || !float.IsFinite(screen.Y)) continue;
            // Keep the bubble clear of the native overhead labels at any camera distance.
            screen.Y -= 18 * scale;
            Haven.TypingIndicatorUi.Draw(screen, marker.State, ImGui.GetTime(), viewport.Pos, viewport.Size, scale);
        }
    }
    public void Dispose()
    {
        framework.Update -= Update; chat.Submitted -= Submitted;
        relay.OnTyping -= Received; relay.OnPeerLeft -= Left; relay.OnDisconnected -= Disconnected; relay.OnRoomJoined -= Joined;
        pluginInterface.UiBuilder.Draw -= Draw;
        _ = Publish(TypingState.Idle); remote.Clear(); Volatile.Write(ref markers, []);
    }
}
