namespace Haven.Core;

public static class EmoteOwnership
{
    // Unlike the UI predicate, this deliberately ignores general quest-unlock bypass flags.
    public static bool IsOwned(uint link, ReadOnlySpan<byte> unlocks, ReadOnlySpan<byte> completedQuests)
    {
        if (link == 0) return true;
        var quest = link >= 0x10000;
        var index = quest ? link & 0xFFFF : link;
        var bits = quest ? completedQuests : unlocks;
        var position = index >> 3;
        if (position >= bits.Length) return false;
        // Quest completion is MSB-first; UI unlock links are LSB-first in the installed client.
        var mask = quest ? 0x80 >> (int)(index & 7) : 1 << (int)(index & 7);
        return (bits[(int)position] & mask) != 0;
    }
}
