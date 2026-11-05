using System.Text.RegularExpressions;

namespace Haven.Core;

/// <summary>Real-world chat channels a player may use from inside a home (Settings → General).</summary>
[Flags]
public enum WorldChannels
{
    None = 0,
    Tells = 1,
    FreeCompany = 2,
    Linkshells = 4,
    CrossLinkshells = 8,
    Party = 16,
    All = Tells | FreeCompany | Linkshells | CrossLinkshells | Party,
}

/// <summary>
/// Which of the game's chat commands and chat log channels are real-world chat Haven lets through: tells (and
/// replies), free company, the eight linkshells, the eight cross-world linkshells and party (cross-world too). Say,
/// shout, yell and emotes stay room chat; the Novice Network, alliance and everything else stay blocked.
/// </summary>
public static partial class WorldChat
{
    /// <summary>The channel a text command belongs to, from any of its names (command, short command, aliases).</summary>
    public static WorldChannels Command(IEnumerable<string> names)
    {
        foreach (var raw in names)
        {
            var name = raw.Trim().ToLowerInvariant();
            if (name is "/tell" or "/t" or "/reply" or "/r") return WorldChannels.Tells;
            if (name is "/freecompany" or "/fc") return WorldChannels.FreeCompany;
            if (name is "/party" or "/p") return WorldChannels.Party;
            if (CrossLinkshell().IsMatch(name)) return WorldChannels.CrossLinkshells;
            if (Linkshell().IsMatch(name)) return WorldChannels.Linkshells;
        }
        return WorldChannels.None;
    }

    /// <summary>The channel of a chat log line (Dalamud's XivChatType values), or None.</summary>
    public static WorldChannels Log(int kind) => kind switch
    {
        12 or 13 => WorldChannels.Tells,           // TellOutgoing, TellIncoming
        24 => WorldChannels.FreeCompany,
        14 or 32 => WorldChannels.Party,           // Party, CrossParty
        >= 16 and <= 23 => WorldChannels.Linkshells, // Ls1-Ls8
        37 or (>= 101 and <= 107) => WorldChannels.CrossLinkshells,
        _ => WorldChannels.None,
    };

    [GeneratedRegex("^/(linkshell|l)[1-8]$")]
    private static partial Regex Linkshell();
    [GeneratedRegex("^/(cwlinkshell|cwl)[1-8]?$")]
    private static partial Regex CrossLinkshell();
}
