using System.Numerics;

namespace Haven.Core;

public enum TypingState : byte { Idle, Active, Paused }

public static class TypingWire
{
    public const long PauseAfterMs = 1500;
    public const long HeartbeatMs = 2000;
    public const long ExpireAfterMs = 6000;
    public static TypingState Decode(ReadOnlySpan<byte> payload) => payload.Length == 1 && payload[0] <= (byte)TypingState.Paused
        ? (TypingState)payload[0] : throw new InvalidDataException("Invalid typing state.");
    public static ulong Fingerprint(ReadOnlySpan<byte> draft)
    {
        ulong hash = 14695981039346656037;
        foreach (var value in draft) hash = unchecked((hash ^ value) * 1099511628211);
        return hash;
    }
}

// Draft bytes exist only in the caller's temporary span. No text or fingerprint is sent.
public sealed class TypingActivity
{
    private ulong fingerprint;
    private bool hasDraft, submitted;
    private long changedAt;
    public void Submitted() => submitted = true;
    public TypingState Observe(long now, bool focused, ReadOnlySpan<byte> draft)
        => ObserveDraft(now, focused, !draft.IsEmpty && !TypingDraft.IsCommand(draft), TypingWire.Fingerprint(draft), false);
    // Chat 2 exposes presence and length, not the draft itself. Character-input pulses
    // handle edits that replace text without changing its length; no key or text is retained.
    public TypingState ObserveMetadata(long now, bool focused, bool hasText, int length, bool edited)
        => ObserveDraft(now, focused, hasText && length is > 0 and <= 8192, (ulong)Math.Max(0, length), edited && focused);
    private TypingState ObserveDraft(long now, bool focused, bool nonempty, ulong next, bool edited)
    {
        if (!nonempty) { Reset(); return TypingState.Idle; }
        if (!hasDraft || next != fingerprint || edited) { fingerprint = next; hasDraft = true; changedAt = now; submitted = false; }
        if (submitted) return TypingState.Idle;
        return focused && now - changedAt < TypingWire.PauseAfterMs ? TypingState.Active : TypingState.Paused;
    }
    public void Reset() { hasDraft = submitted = false; fingerprint = 0; changedAt = 0; }
}

/// <summary>
/// A draft that starts with "/" is most likely a command (a plugin's, or the game's), so it shows no typing bubble,
/// unless it is a room chat command followed by a space ("/em ", "/s ", "/y "...): then the player is writing a post.
/// </summary>
public static class TypingDraft
{
    private static readonly string[] ChatPrefixes = ["/em", "/emote", "/s", "/say", "/sh", "/shout", "/y", "/yell"];
    public static bool IsCommand(ReadOnlySpan<byte> draft)
    {
        var start = 0;
        while (start < draft.Length && draft[start] is (byte)' ' or (byte)'\t') start++;
        if (start >= draft.Length || draft[start] != (byte)'/') return false;
        var end = start;
        while (end < draft.Length && draft[end] is not ((byte)' ' or (byte)'\t')) end++;
        if (end >= draft.Length) return true; // Still typing the command word itself.
        var word = draft[start..end];
        foreach (var prefix in ChatPrefixes)
            if (word.Length == prefix.Length && System.Text.Ascii.EqualsIgnoreCase(word, System.Text.Encoding.ASCII.GetBytes(prefix))) return false;
        return true;
    }
}

// Pure geometry shared with the native draw path, so viewport clipping can be verified headlessly.
public readonly record struct TypingBubbleLayout(Vector2 Min, Vector2 Max, Vector2 TailLeft, Vector2 TailRight, Vector2 TailTip)
{
    public Vector2 Dot(int index) => new(Min.X + (Max.X - Min.X) * (index + 1) / 4, (Min.Y + Max.Y) / 2);
    public static TypingBubbleLayout? At(Vector2 anchor, Vector2 viewport, Vector2 size, float scale)
    {
        if (!float.IsFinite(anchor.X) || !float.IsFinite(anchor.Y) || !float.IsFinite(scale) || scale <= 0) return null;
        var half = new Vector2(25, 13) * scale;
        var center = anchor - new Vector2(0, 22) * scale;
        var min = center - half; var max = center + half;
        if (min.X < viewport.X || max.X > viewport.X + size.X || min.Y < viewport.Y || anchor.Y > viewport.Y + size.Y) return null;
        return new(min, max, new(center.X - 4 * scale, max.Y - scale), new(center.X + 4 * scale, max.Y - scale), anchor);
    }
}
