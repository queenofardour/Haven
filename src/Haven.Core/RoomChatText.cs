using System.Text;

namespace Haven.Core;

public static class RoomChatText
{
    /// <summary>
    /// Splits a post into lines of at most maxBytes UTF-8 bytes, at the last space before the limit where there is one
    /// (otherwise between characters, never inside one). Short posts come back whole.
    /// </summary>
    public static string[] Split(string text, int maxBytes)
    {
        text = text.Trim();
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes) return [text];
        var parts = new List<string>();
        while (text.Length > 0)
        {
            if (Encoding.UTF8.GetByteCount(text) <= maxBytes) { parts.Add(text); break; }
            // The longest prefix within the limit, ending on a whole character.
            int bytes = 0, end = 0;
            while (end < text.Length)
            {
                var width = char.IsHighSurrogate(text[end]) && end + 1 < text.Length ? 2 : 1;
                var size = Encoding.UTF8.GetByteCount(text.AsSpan(end, width));
                if (bytes + size > maxBytes) break;
                bytes += size; end += width;
            }
            var cut = text.LastIndexOf(' ', Math.Max(0, end - 1), end);
            if (cut <= end / 2) cut = end; // No useful space: split between characters.
            parts.Add(text[..cut].TrimEnd());
            text = text[cut..].TrimStart();
        }
        return [.. parts.Where(p => p.Length > 0)];
    }
}
