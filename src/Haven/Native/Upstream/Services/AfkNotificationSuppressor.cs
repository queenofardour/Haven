// Derived from Enceladeum/HMapSync at 8140dbb04cfbfa59da9ff0c3f4e787978d368303. AGPL-3.0-or-later. See THIRD_PARTY_NOTICES.md.
using System;
using Dalamud.Game.Text;
using Dalamud.Game.Chat;
using Dalamud.Plugin.Services;

namespace HavenNative.Services;

/// <summary>
/// S250: suppresses the duty AFK / "you will be expelled" system notification, but ONLY while an
/// HMS session is active. This exists because HMS's raw zone loads spin up an InstanceContent
/// director (for the wall-down convenience) whose Lua still runs the idle-watch and prints the
/// inactivity warning (the client's "inactive for 10 minutes" nag - see ExpelWarningFragments
/// below for the actual matched text) - even though the actual expel never fires (that needs full
/// duty machinery HMS doesn't run). The nag is harmless but annoying during long explore sessions.
///
/// DESIGN (deliberately surgical - see handover §9):
///   * We do NOT touch the director Lua. It bundles many behaviours (objective tracking, sequence
///     ticks, the idle watch); killing it to silence one harmless message is a bad trade.
///   * We suppress only the CHAT LINE, via IChatGui.CheckMessageHandled → PreventOriginal().
///
/// HARD CONSTRAINTS (both enforced below):
///   * Only suppress while relay.IsConnected (in an HMS session).
///   * NEVER suppress outside a session - a real player legitimately AFK in a real dungeon must
///     still get their system nudge. HMS must not eat anyone's real expel warning.
///
/// The match is intentionally tight: system-channel only, plus a content check on the message
/// text, so we don't swallow unrelated system lines.
///
/// Haven 0.38.1: also the game's own away-status lines (LogMessage 97 "Updating online status to Away from
/// Keyboard." and 98 "Updating online status. No longer away from keyboard."). Inside a home the packet filter holds
/// back the status change, so the server never confirms it and the client repeats the line about every half minute.
/// Letting that packet through would weaken the filter that keeps the real character safe; the game's next attempt
/// after the visit goes through by itself. So the lines are hidden during a visit, with one note saying why. They are
/// matched exactly, in the client's own language, from the game's LogMessage sheet.
/// </summary>
public sealed class AfkNotificationSuppressor : IDisposable
{
    private readonly IChatGui chat;
    private readonly IPluginLog log;
    private readonly Func<bool> isSessionActive;

    private bool subscribed;
    // The game's away-status lines in the client's language (LogMessage 97 and 98), and whether this visit's note was shown.
    private readonly string[] awayStatusLines;
    private readonly Action<string>? note;
    private bool noted;

    // Distinctive fragments of the duty inactivity / expel warning. Matched case-insensitively;
    // ANY of these present (in a system-channel line) identifies the message. Kept as fragments
    // (not the whole sentence) so minor server wording differences still match, while staying
    // specific enough not to catch unrelated lines.
    private static readonly string[] ExpelWarningFragments =
    {
        "expelled from the duty",
        "inactive for 10 minutes",
        "since your last activity",
    };

    public AfkNotificationSuppressor(IChatGui chat, IPluginLog log, Func<bool> isSessionActive, string[]? awayStatusLines = null, Action<string>? note = null)
    {
        this.chat = chat;
        this.log = log;
        this.isSessionActive = isSessionActive;
        this.awayStatusLines = awayStatusLines ?? [];
        this.note = note;
    }

    public void Initialize()
    {
        if (subscribed) return;
        chat.CheckMessageHandled += OnCheckMessageHandled;
        subscribed = true;
        log.Information("[HavenNative] AFK-notification suppressor armed (active only during HMS sessions).");
    }

    private void OnCheckMessageHandled(IHandleableChatMessage message)
    {
        try
        {
            // CONSTRAINT 1: only inside an HMS session. Outside a session we do nothing - real
            // AFK warnings for real dungeons pass through untouched.
            if (!isSessionActive())
            {
                noted = false;
                return;
            }

            // System-channel only. The inactivity warning is a system message; restricting to the
            // system channels avoids ever touching player/other chat.
            var kind = message.LogKind;
            if (kind != XivChatType.SystemMessage && kind != XivChatType.SystemError)
                return;

            var text = message.Message.TextValue;
            if (string.IsNullOrEmpty(text))
                return;

            // The away-status line the game repeats while the filter holds its status change back.
            foreach (var line in awayStatusLines)
            {
                if (line.Length > 0 && text.Trim().Equals(line, StringComparison.OrdinalIgnoreCase))
                {
                    message.PreventOriginal();
                    if (!noted)
                    {
                        noted = true;
                        note?.Invoke("[Haven] Your away-from-keyboard status can't change while you're in a home. It updates by itself once you leave.");
                    }
                    return;
                }
            }

            foreach (var frag in ExpelWarningFragments)
            {
                if (text.IndexOf(frag, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    message.PreventOriginal();
                    log.Debug("[HavenNative] Suppressed duty AFK warning (in-session): " + text);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            // Never let a suppressor error break chat - just log and let the message through.
            log.Debug("[HavenNative] AFK suppressor error (message allowed through): " + ex.Message);
        }
    }

    public void Dispose()
    {
        if (!subscribed) return;
        try { chat.CheckMessageHandled -= OnCheckMessageHandled; }
        catch (Exception ex) { log.Debug("[HavenNative] AFK suppressor unsubscribe failed: " + ex.Message); }
        subscribed = false;
    }
}

