namespace Haven.Core;

/// <summary>
/// Text from other players or the relay, before it reaches chat, notifications or another plugin: control characters
/// (0x02 starts a game chat formatting code) are removed and the length is capped.
/// </summary>
public static class SafeText
{
    public static string Clean(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var clean = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length <= max) return clean;
        var cut = max;
        if (cut > 0 && char.IsHighSurrogate(clean[cut - 1])) cut--;
        return clean[..cut];
    }
}
