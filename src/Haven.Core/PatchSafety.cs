using System;
using System.Collections.Generic;
using System.Globalization;

namespace Haven.Core;

/// <summary>
/// The parts of Haven that read or write game memory at places Haven worked out itself (not through ClientStructs,
/// which Dalamud's team updates for every patch). A patch that moves such a field doesn't fail: the write lands in
/// whatever is there now. So while the running game is newer than the one this Haven was checked on, these are paused
/// unless a runtime check (a canary) shows the field is still where it was, or the player turns them on anyway.
/// Each is listed with how to check it again.
/// </summary>
public enum PatchFeature
{
    /// <summary>Lamp shades dimming when switched off, and the arcade machines' flashes (BgObject +0xCD).</summary>
    FurnishingGlow,
    /// <summary>The free building camera's collision override (camera +0x218/+0x21C).</summary>
    BuildCamera,
    /// <summary>Unowned emotes listed in the emote window (AgentEmote +0x31/+0x34/+0x36).</summary>
    EmoteWindow,
    /// <summary>The Gold Saucer arcade machines (the minigame agent's +0x10/+0x28/+0xA0/+0xEE).</summary>
    Arcade,
    /// <summary>Holding back the game's own /facecamera stare while Haven drives it (Character +0x1930).</summary>
    FaceCamera,
}

public static class PatchGate
{
    public static readonly IReadOnlyDictionary<PatchFeature, string> Names = new Dictionary<PatchFeature, string>
    {
        [PatchFeature.FurnishingGlow] = "lamp glow and machine flashes",
        [PatchFeature.BuildCamera] = "the free building camera",
        [PatchFeature.EmoteWindow] = "unowned emotes in the emote window",
        [PatchFeature.Arcade] = "the Gold Saucer machines",
        [PatchFeature.FaceCamera] = "/facecamera in gatherings",
    };

    /// <summary>
    /// Whether the running game is the one this Haven was checked on. An unknown version on either side counts as
    /// checked: nothing to compare against is no reason to switch features off.
    /// </summary>
    public static bool Checked(string verified, string running)
        => string.IsNullOrWhiteSpace(verified) || string.IsNullOrWhiteSpace(running) || Same(verified, running);

    /// <summary>
    /// Whether a feature may run: on the checked game; else when its canary passed this session; else when the player
    /// turned paused features on for exactly this game version.
    /// </summary>
    public static bool Allows(string verified, string running, string? allowedFor, bool canaryPassed = false)
        => Checked(verified, running) || canaryPassed || allowedFor is { } allowed && Same(allowed, running);

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>"2026.10.16.0000.0000" → its date, or null if it isn't a game version string.</summary>
    public static DateOnly? DateOf(string version)
    {
        var parts = version.Trim().Split('.');
        return parts.Length >= 3 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var y)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var m)
            && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var d)
            && y is > 2000 and < 3000 && m is >= 1 and <= 12 && d is >= 1 and <= 31 ? new DateOnly(y, m, d) : null;
    }
}

/// <summary>Which feature, as the player knows it, a part of Haven's native code belongs to.</summary>
public static class PatchParts
{
    public readonly record struct Part(string Name, bool Critical);

    private static readonly Dictionary<string, Part> byOwner = new(StringComparer.Ordinal)
    {
        ["PacketFilterService"] = new("the packet filter", true),
        ["ZoneLoadService"] = new("loading homes", true),
        ["PrivateRoomChatService"] = new("room chat", true),
        ["EmoteUnlockService"] = new("emotes inside homes", false),
        ["EmoteWindowService"] = new("the emote window", false),
        ["SkillSyncService"] = new("minions inside homes", false),
        ["SocialRequests"] = new("friend and party menus inside homes", false),
        ["SocialListKeeper"] = new("friend and party lists inside homes", false),
        ["NativeArcade"] = new("the Gold Saucer machines", false),
        ["CommandRestriction"] = new("emote commands after a Gold Saucer machine", false),
        ["TriadProbe"] = new("a Triple Triad research probe", false),
        ["TimeFreezeService"] = new("time of day", false),
        ["WeatherCramService"] = new("weather", false),
        ["MapSettingsService"] = new("weather", false),
        ["QuestSpoofService"] = new("quest-dependent objects in homes", false),
        ["NoclipService"] = new("noclip", false),
        ["StateApplyService"] = new("looking at people (look-at sync)", false),
        ["HousingMusicStream"] = new("music", false),
        ["OriginOrchestrion"] = new("pausing a real house's orchestrion", false),
        ["HousingLightingGuard"] = new("room lighting", false),
        ["BuildingMaterials"] = new("wall and floor finishes", false),
    };

    public static Part Of(string owner, string method)
    {
        if (byOwner.TryGetValue(owner, out var part)) return part;
        if (owner == "NativeEngine")
            return method.Contains("BuildCamera", StringComparison.Ordinal) ? new("the free building camera", false)
                : method.Contains("MoveInput", StringComparison.Ordinal) || method.Contains("Pose", StringComparison.Ordinal) ? new("pose balls", false)
                : new("Haven's native engine (" + method + ")", false);
        return new(owner, false);
    }
}

/// <summary>A signature in Dalamud's notation ("48 8B ?? E8"), parsed for matching against game code offline.</summary>
public sealed class SignaturePattern
{
    public byte[] Bytes { get; }
    /// <summary>True where the byte must match; false for a wildcard (?? or ?).</summary>
    public bool[] Mask { get; }
    public string Text { get; }

    private SignaturePattern(string text, byte[] bytes, bool[] mask) { Text = text; Bytes = bytes; Mask = mask; }

    public int Length => Bytes.Length;

    /// <summary>Starts with a call or jump whose target Dalamud follows (ScanText answers the target, not the site).</summary>
    public bool FollowsCall => Mask.Length >= 5 && Mask[0] && Bytes[0] is 0xE8 or 0xE9;

    /// <summary>Parses Dalamud's notation, or null if the text isn't one (fewer than 4 bytes, or anything else in it).</summary>
    public static SignaturePattern? Parse(string text)
    {
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 4) return null;
        var bytes = new byte[tokens.Length];
        var mask = new bool[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token is "??" or "?") continue;
            if (token.Length != 2 || !byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b)) return null;
            bytes[i] = b; mask[i] = true;
        }
        // All wildcards isn't a signature.
        return Array.IndexOf(mask, true) < 0 ? null : new SignaturePattern(text, bytes, mask);
    }

    /// <summary>Every place in code the pattern matches, up to a limit (two is enough to know it isn't unique).</summary>
    public List<int> FindAll(ReadOnlySpan<byte> code, int limit = int.MaxValue)
    {
        var found = new List<int>();
        // Anchor on the longest run of fixed bytes, found with a vectorised search, then check the rest.
        var (anchor, length) = LongestFixedRun();
        var needle = Bytes.AsSpan(anchor, length);
        var from = anchor;
        while (found.Count < limit && from <= code.Length - (Length - anchor))
        {
            var hit = code[from..].IndexOf(needle);
            if (hit < 0) break;
            var start = from + hit - anchor;
            if (start + Length <= code.Length && Matches(code, start)) found.Add(start);
            from += hit + 1;
        }
        return found;
    }

    public bool Matches(ReadOnlySpan<byte> code, int start)
    {
        if (start < 0 || start + Length > code.Length) return false;
        for (var i = 0; i < Length; i++)
            if (Mask[i] && code[start + i] != Bytes[i]) return false;
        return true;
    }

    private (int Start, int Length) LongestFixedRun()
    {
        int bestStart = 0, bestLength = 0;
        for (var i = 0; i < Length;)
        {
            if (!Mask[i]) { i++; continue; }
            var j = i;
            while (j < Length && Mask[j]) j++;
            if (j - i > bestLength) (bestStart, bestLength) = (i, j - i);
            i = j;
        }
        return (bestStart, bestLength);
    }
}
