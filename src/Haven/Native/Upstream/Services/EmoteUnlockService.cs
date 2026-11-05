// Derived from Enceladeum/HMapSync at 8140dbb04cfbfa59da9ff0c3f4e787978d368303. AGPL-3.0-or-later. See THIRD_PARTY_NOTICES.md.
using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace HavenNative.Services;

// NB-40 - EMOTES: let NATIVE emote triggers fire LOCKED emotes while in a HMS session, so `/wave`, the emote
// menu, and hotbar macros behave like `/havenengine emote <id>` already does - and like UNLOCKED native emotes already
// do (fire locally + sync to peers). Closes the "unlocked work, locked don't" asymmetry the user flagged.
//
// TWO LAYERS, learned the hard way (b84→b87). Opening the native emote path needs BOTH:
//
//   LAYER 1 - the GUARDS (open the pre-checks so the command reaches its executor at all):
//     1. UIState.IsEmoteUnlocked(id)        - "do you own it"      → refuses "you have not unlocked this emote"
//     2. EmoteManager.CanExecuteEmote(id)   - "can you do it now"  → refuses "<emote> can't be used right now"
//   Opening ONLY these (b84/b85/b86) got the native command PAST the visible refusals - but it then died
//   SILENTLY, because the game's EXECUTOR itself refuses an unowned emote internally, past both guards.
//
//   LAYER 2 - the EXECUTOR REDIRECT (b87): hook the two executors the native path can reach -
//     AgentEmote.ExecuteEmote (the emote-window/agent path) and EmoteManager.ExecuteEmote (the lower path) -
//     and for a GENUINELY-LOCKED emote in-session, DON'T call the game's (refusing) Original; instead invoke
//     HMS's own force-play (forcePlayLocked → PlayResolvedEmote), which drives the ActionTimeline directly - the
//     EXACT path `hms emote <locked>` already uses. The native trigger now rides the proven client-force path,
//     and LocalStateDetector (trigger-agnostic) syncs it to peers. Redirecting at BOTH executors covers the
//     path regardless of whether the text command routes through the agent or straight to the manager; whichever
//     fires first suppresses the other (agent redirect returns before reaching the manager), so no double-play.
//
// SelfCall. HMS sets EmoteUnlockService.SelfCall around its own PlayResolvedEmote; while set ALL four detours
// return Original (the truth) and skip the redirect. So `hms emote <id>` judges on genuine ownership exactly as
// prod does (unchanged, still force-plays locked emotes client-side), and neither the guard spoof nor the
// executor redirect leaks into HMS's own play. The force-play HMS runs from inside the redirect also sets
// SelfCall, so it is not re-entrantly intercepted.
//
// SURGICAL, NOT BLANKET. Gate 2 legitimately refuses UNLOCKED emotes for genuine STATE reasons - e.g. Gridanian
// Gulp (standing-only) refuses while seated, which is correct and must be preserved (the seated-Gulp bug). So
// both the CanExecuteEmote guard AND the executor redirect act ONLY when the emote is GENUINELY locked (its real,
// un-spoofed ownership is false). A genuinely-unlocked emote keeps the engine's real answer and the engine's own
// executor, so native unlocked emotes fire exactly as before and state refusals still hold. The distinguishing
// signal is the real unlock bit, read from row-specific unlock/quest bits (GenuinelyUnlocked).
//
// WHY SAFE. The guard predicates are pure READS; the executor redirect only DIVERTS a locked-in-session play to
// HMS's proven client-force path and suppresses the engine's own (which was going to refuse anyway). Nothing is
// persisted and everything reverts truthfully out of session. The cast never reaches the server: in session the
// packet firewall already drops outbound emote opcodes (the exact reason unlocked native emotes fire+sync
// cleanly today); locked ones now ride the identical proven path, synced by the trigger-agnostic LocalStateDetector.
public sealed unsafe class EmoteUnlockService : IDisposable
{
    private delegate bool IsEmoteUnlockedDelegate(UIState* thisPtr, ushort emoteId);
    private delegate bool CanExecuteEmoteDelegate(EmoteManager* thisPtr, ushort emoteId);
    // Executor delegates - pointers as nint (size-correct; the exact PlayEmoteOption* type is irrelevant to the
    // hook and avoids dragging its namespace in). AgentEmote.ExecuteEmote returns void; EmoteManager.ExecuteEmote
    // returns bool (success).
    private delegate void AgentExecuteEmoteDelegate(nint thisPtr, ushort emoteId, nint opt, bool addToHistory, bool liveUpdate);
    private delegate bool MgrExecuteEmoteDelegate(nint thisPtr, ushort emoteId, nint opt);
    private delegate bool UnlockLinkDelegate(UIState* thisPtr, uint unlockLink, byte minQuestVersion, byte a4);

    private readonly Dictionary<ushort, uint> unlockLinks = new();
    // Haven: the one unlock link answered as unlocked while Haven runs the game's executor for an emote the player
    // does not own (0 = none). The executor checks it through IsUnlockLinkUnlockedOrQuestCompleted before playing.
    private uint scopedLink;
    private Hook<UnlockLinkDelegate>? unlockLinkHook;
    public bool IsOwned(ushort emoteId) => GenuinelyUnlocked(emoteId);
    private readonly IPluginLog log;
    private readonly IGameInteropProvider hooks;
    private readonly Func<bool> isSessionActive;
    // HMS force-play for a locked emote id (resolve timelines → PlayResolvedEmote). Returns true when handled.
    private readonly Func<ushort, bool> forcePlayLocked;

    private Hook<IsEmoteUnlockedDelegate>? unlockedHook;
    private Hook<CanExecuteEmoteDelegate>? canExecHook;
    private Hook<AgentExecuteEmoteDelegate>? agentExecHook;
    private Hook<MgrExecuteEmoteDelegate>? mgrExecHook;

    // Set by HMS around its OWN emote play (PlayResolvedEmote). While true, every detour passes through to
    // Original and skips the redirect - the spoof/redirect is only ever meant for the game's native emote path.
    // Framework-thread only; no locking needed.
    public bool SelfCall;
    // Haven: resolve virtual chair/bed markers before the native emote executor.
    // Other emotes, including Change Pose, keep the native path unchanged.
    public Func<ushort, bool>? HousingCanUse { get; set; }
    public Func<ushort, bool>? HousingExecute { get; set; }
    // Haven: aims a native emote at the player's target before it plays inside a home (option = PlayEmoteOption*).
    public Action<nint>? BeforeLocalEmote { get; set; }
    // Haven: the local player performed an emote inside a home (for its log line).
    public Action<ushort>? LocalEmoted { get; set; }
    // Haven: the GameObjectId the local player's emote should aim at inside a home, or null for none.
    public Func<ulong?>? LocalEmoteTarget { get; set; }

    public EmoteUnlockService(IPluginLog log, IGameInteropProvider hooks, IDataManager data, Func<bool> isSessionActive,
        Func<ushort, bool> forcePlayLocked)
    {
        foreach (var row in data.GetExcelSheet<Emote>())
            if (row.RowId is > 0 and <= ushort.MaxValue) unlockLinks[(ushort)row.RowId] = row.UnlockLink;
        this.log = log;
        this.hooks = hooks;
        this.isSessionActive = isSessionActive;
        this.forcePlayLocked = forcePlayLocked;
    }

    // Slash commands can be refused by the native parser before either executor hook runs.
    // Private-room chat invokes this first; owned emotes keep their normal native path.
    /// <summary>
    /// Plays an emote as the emote window does (AgentEmote.ExecuteEmote, through Haven's own handling of it: seats,
    /// poses, emotes not owned). Inside a home, typed emotes go this way too: the game's text-command path once refused
    /// every emote ("That command is unavailable at this time.") for the rest of a visit while the window kept working.
    /// </summary>
    public unsafe bool PlayLikeTheWindow(ushort emoteId)
    {
        if (agentExecHook == null || !isSessionActive() || SelfCall) return false;
        var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentModule.Instance()->GetAgentByInternalId(FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentId.Emote);
        if (agent == null) return false;
        AgentExecDetour((nint)agent, emoteId, 0, true, true);
        return true;
    }

    public bool TryPlayLockedCommand(ushort emoteId)
    {
        if (!isSessionActive() || SelfCall || GenuinelyUnlocked(emoteId)) return false;
        try { BeforeLocalEmote?.Invoke(0); if (TryNativeUnowned(emoteId, 0)) return true; }
        catch (Exception ex) { log.Error("[HavenNative] [EMOTE-REDIR] native play: " + ex.Message); }
        return forcePlayLocked(emoteId);
    }

    // Haven: plays an emote the player does not own through the game's own executor, as an owned one plays, so its
    // targeted effects (Dote's heart) reach the target. Played from timelines they land on the player. The executor
    // refuses it at one check, the emote's unlock link, which is answered as unlocked for this call only; the server
    // request it sends is dropped by the packet filter. If the executor reports failure, or reports success without
    // starting any animation (as EmoteController.PlayEmote did in 0.32.3), the caller falls back to timelines.
    private bool TryNativeUnowned(ushort emoteId, nint opt)
    {
        if (unlockLinkHook == null || mgrExecHook == null || !unlockLinks.TryGetValue(emoteId, out var link) || link == 0) return false;
        var manager = EmoteManager.Instance();
        var self = FFXIVClientStructs.FFXIV.Client.Game.Control.Control.Instance()->LocalPlayer;
        if (manager == null || self == null) return false;
        var before = stackalloc ushort[14];
        for (var i = 0; i < 14; i++) before[i] = (ushort)self->Character.Timeline.TimelineSequencer.TimelineIds[i];
        var aimed = stackalloc ulong[4];
        if (opt == 0 && LocalEmoteTarget?.Invoke() is ulong target) { aimed[1] = target; opt = (nint)aimed; }
        bool played;
        // Not SelfCall: the executor asks IsEmoteUnlocked and CanExecuteEmote too, which must keep answering for the
        // session. Calling Original directly cannot re-enter the detours.
        scopedLink = link;
        try { played = mgrExecHook.Original((nint)manager, emoteId, opt); }
        finally { scopedLink = 0; }
        var started = false;
        for (var i = 0; i < 14 && !started; i++) started = before[i] != (ushort)self->Character.Timeline.TimelineSequencer.TimelineIds[i];
        log.Information("[EMOTE-TARGET] unowned {Emote} through the game: played {Played}, animation {Started}", emoteId, played, started);
        if (!played || !started) return false;
        LocalEmoted?.Invoke(emoteId);
        return true;
    }

    private bool UnlockLinkDetour(UIState* thisPtr, uint unlockLink, byte minQuestVersion, byte a4)
    {
        if (scopedLink != 0 && unlockLink == scopedLink) return true;
        return unlockLinkHook!.Original(thisPtr, unlockLink, minQuestVersion, a4);
    }

    public void Init()
    {
        // Sigs from ClientStructs' [MemberFunction]s (E8-leading call-site sigs; Dalamud's ScanText follows the
        // rel32 to the function). HookFromSignature only - never a manual resolve.
        try
        {
            unlockedHook = hooks.HookFromSignature<IsEmoteUnlockedDelegate>(
                "E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? ?? ?? 41 B8", UnlockedDetour);
            unlockedHook.Enable();
            log.Information("[HavenNative] [EMOTE-UNLOCK] IsEmoteUnlocked hook installed.");
        }
        catch (Exception ex)
        {
            log.Error("[HavenNative] [EMOTE-UNLOCK] IsEmoteUnlocked hook failed: " + ex.Message);
        }

        try
        {
            unlockLinkHook = hooks.HookFromSignature<UnlockLinkDelegate>("E8 ?? ?? ?? ?? 44 22 F0", UnlockLinkDetour);
            unlockLinkHook.Enable();
            log.Information("[HavenNative] [EMOTE-UNLOCK] IsUnlockLinkUnlockedOrQuestCompleted hook installed.");
        }
        catch (Exception ex)
        {
            unlockLinkHook = null;
            log.Error("[HavenNative] [EMOTE-UNLOCK] IsUnlockLinkUnlockedOrQuestCompleted hook failed: " + ex.Message);
        }

        try
        {
            canExecHook = hooks.HookFromSignature<CanExecuteEmoteDelegate>(
                "E8 ?? ?? ?? ?? 84 C0 0F 84 ?? ?? ?? ?? 48 85 F6 74 05", CanExecDetour);
            canExecHook.Enable();
            log.Information("[HavenNative] [EMOTE-UNLOCK] CanExecuteEmote hook installed.");
        }
        catch (Exception ex)
        {
            log.Error("[HavenNative] [EMOTE-UNLOCK] CanExecuteEmote hook failed: " + ex.Message);
        }

        try
        {
            agentExecHook = hooks.HookFromSignature<AgentExecuteEmoteDelegate>(
                "E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? BA ?? ?? ?? ?? 48 8B CE E8 ?? ?? ?? ?? 48 8B F8", AgentExecDetour);
            agentExecHook.Enable();
            log.Information("[HavenNative] [EMOTE-UNLOCK] AgentEmote.ExecuteEmote hook installed.");
        }
        catch (Exception ex)
        {
            log.Error("[HavenNative] [EMOTE-UNLOCK] AgentEmote.ExecuteEmote hook failed: " + ex.Message);
        }

        try
        {
            mgrExecHook = hooks.HookFromSignature<MgrExecuteEmoteDelegate>(
                "E8 ?? ?? ?? ?? 84 C0 0F 85 ?? ?? ?? ?? 0F B6 1D", MgrExecDetour);
            mgrExecHook.Enable();
            log.Information("[HavenNative] [EMOTE-UNLOCK] EmoteManager.ExecuteEmote hook installed.");
        }
        catch (Exception ex)
        {
            log.Error("[HavenNative] [EMOTE-UNLOCK] EmoteManager.ExecuteEmote hook failed: " + ex.Message);
        }
    }

    // GATE 1. In session, report every emote as owned so native triggers get past the ownership refusal. Out of
    // session - or while HMS is running its OWN emote play (SelfCall) - the truth.
    private bool UnlockedDetour(UIState* thisPtr, ushort emoteId)
    {
        try { if (isSessionActive() && !SelfCall) return true; }
        catch { }
        return unlockedHook!.Original(thisPtr, emoteId);
    }

    // GATE 2. In session, open the "can't be used right now" refusal ONLY for genuinely-locked emotes - a
    // genuinely-unlocked emote keeps the engine's real answer, so real state refusals (standing-only emote while
    // seated) still hold. Inert while HMS runs its own play (SelfCall).
    private bool CanExecDetour(EmoteManager* thisPtr, ushort emoteId)
    {
        try
        {
            if (isSessionActive() && !SelfCall && (HousingCanUse?.Invoke(emoteId) == true || !GenuinelyUnlocked(emoteId))) return true;
        }
        catch { }
        return canExecHook!.Original(thisPtr, emoteId);
    }

    // EXECUTOR REDIRECT (agent path). Past the two guards, the game's own executor still refuses an unowned
    // emote internally (silent no-op). For a genuinely-locked emote in-session, divert to HMS's force-play and
    // DON'T run Original - so the native trigger fires the emote through HMS's proven client-force path. Inert
    // for genuinely-unlocked emotes (they use the engine's own executor) and while HMS plays its own (SelfCall).
    private void AgentExecDetour(nint thisPtr, ushort emoteId, nint opt, bool addToHistory, bool liveUpdate)
    {
        try
        {
            if (isSessionActive() && !SelfCall) BeforeLocalEmote?.Invoke(opt);
            if (isSessionActive() && !SelfCall && HousingExecute?.Invoke(emoteId) == true) { LocalEmoted?.Invoke(emoteId); return; }
            if (isSessionActive() && !SelfCall && !GenuinelyUnlocked(emoteId))
            {
                if (TryNativeUnowned(emoteId, opt)) return;
                log.Debug("[HavenNative] [EMOTE-REDIR] agent.ExecuteEmote id=" + emoteId + " locked in-session → force-play");
                if (forcePlayLocked != null && forcePlayLocked(emoteId))
                    return;   // handled by HMS; suppress the engine's refusing executor
            }
        }
        catch (Exception ex) { log.Error("[HavenNative] [EMOTE-REDIR] agent detour: " + ex.Message); }
        agentExecHook!.Original(thisPtr, emoteId, opt, addToHistory, liveUpdate);
    }

    // EXECUTOR REDIRECT (manager path). Same as the agent redirect, for the lower-level executor the text
    // command may reach directly. Returns true (success) after force-playing so any caller sees the emote as
    // executed. If the agent redirect already fired for this trigger it returned before reaching here, so this
    // only runs when the manager is the actual entry - no double-play.
    private bool MgrExecDetour(nint thisPtr, ushort emoteId, nint opt)
    {
        try
        {
            if (isSessionActive() && !SelfCall) BeforeLocalEmote?.Invoke(opt);
            if (isSessionActive() && !SelfCall && HousingExecute?.Invoke(emoteId) == true) { LocalEmoted?.Invoke(emoteId); return true; }
            if (isSessionActive() && !SelfCall && !GenuinelyUnlocked(emoteId))
            {
                if (TryNativeUnowned(emoteId, opt)) return true;
                log.Debug("[HavenNative] [EMOTE-REDIR] mgr.ExecuteEmote id=" + emoteId + " locked in-session → force-play");
                if (forcePlayLocked != null && forcePlayLocked(emoteId))
                    return true;   // handled by HMS; suppress the engine's refusing executor
            }
        }
        catch (Exception ex) { log.Error("[HavenNative] [EMOTE-REDIR] mgr detour: " + ex.Message); }
        // The executor plays the emote locally from its option, and an emote command passes none: it then aims at
        // "no object" and overwrites the target Haven set. Outside a home the server's echo puts the target right;
        // inside, nothing does. So a missing option is replaced by one carrying the target. The executor copies
        // only TargetId (+0x8), two flag bytes (+0x10, +0x11) and an optional object (+0x18) from it.
        var aimed = stackalloc ulong[4];
        try
        {
            if (opt == 0 && isSessionActive() && !SelfCall && LocalEmoteTarget?.Invoke() is ulong target)
            { aimed[1] = target; opt = (nint)aimed; }
        }
        catch (Exception ex) { log.Error("[HavenNative] [EMOTE-REDIR] emote target: " + ex.Message); }
        var played = mgrExecHook!.Original(thisPtr, emoteId, opt);
        try { if (played && isSessionActive() && !SelfCall) LocalEmoted?.Invoke(emoteId); }
        catch (Exception ex) { log.Error("[HavenNative] [EMOTE-REDIR] emote line: " + ex.Message); }
        return played;
    }

    // Read the actual row-specific bit, not a hooked UI predicate or its general quest-bypass flag.
    // Only reads: no quest state, unlock flags or saved data are modified.
    private bool GenuinelyUnlocked(ushort emoteId)
    {
        if (!unlockLinks.TryGetValue(emoteId, out var link)) return false;
        return NativeEmoteOwnership.IsOwned(UIState.Instance(), link >= 0x10000 ? QuestManager.Instance() : null, link);
    }

    public void Dispose()
    {
        try { unlockedHook?.Disable(); unlockedHook?.Dispose(); } catch { }
        try { unlockLinkHook?.Disable(); unlockLinkHook?.Dispose(); } catch { }
        try { canExecHook?.Disable(); canExecHook?.Dispose(); } catch { }
        try { agentExecHook?.Disable(); agentExecHook?.Dispose(); } catch { }
        try { mgrExecHook?.Disable(); mgrExecHook?.Dispose(); } catch { }
    }
}

