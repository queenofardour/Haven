namespace Haven.Core;

// Restores only an override still owned by this session. A zone transition or
// another music controller may have legitimately replaced it in the meantime.
public sealed class BgmSceneLease
{
    private ushort? original, target;
    public void Engage(ushort current, ushort next)
    {
        if (target == null || current != target) original = current;
        target = next;
    }
    public ushort? Release(ushort? current)
    {
        var restore = current != null && current == target ? original : null;
        original = target = null;
        return restore;
    }
}
