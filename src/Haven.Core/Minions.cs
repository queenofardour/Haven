namespace Haven.Core;

/// <summary>What a minion summon does inside a home, where the packet filter drops the request the server would answer.</summary>
public enum MinionSummon
{
    /// <summary>Outside a home: the game sends the request as usual.</summary>
    PassThrough,
    Summon,
    /// <summary>Using the minion that is already out puts it away, as in vanilla.</summary>
    Dismiss,
    /// <summary>No Companion row with this ID.</summary>
    Unknown,
    NotUnlocked,
}

/// <summary>One frame of putting back the minion a character had in the real world, after the return reload.</summary>
public enum MinionRestoreStep
{
    /// <summary>The character shows the minion it had, or none if it had none.</summary>
    Done,
    /// <summary>The owner has no draw object yet, or the last setup is still spawning.</summary>
    Wait,
    /// <summary>Set the companion up (0 puts it away).</summary>
    Setup,
    /// <summary>Out of time; leave the character as it is.</summary>
    GiveUp,
}

/// <summary>
/// Minions are client-side objects: every client spawns one from its owner's companion ID, which the server sends after
/// a summon. Inside a home that request never leaves, so Haven spawns the minion itself, and the detector shares its ID
/// with the gathering. Real-world minions are put back on return, because the exit sweep puts every minion away.
/// </summary>
public static class Minions
{
    /// <summary>A companion setup spawns over a few frames; wait this long before asking again.</summary>
    public const int RetryFrames = 30;
    /// <summary>About ten seconds at 60 fps: the return redraws every player within five.</summary>
    public const int RestoreFrames = 600;

    public static MinionSummon Decide(bool inHome, uint requested, ushort current, bool known, bool unlocked)
    {
        if (!inHome) return MinionSummon.PassThrough;
        if (requested == 0 || requested > ushort.MaxValue || !known) return MinionSummon.Unknown;
        if (requested == current) return MinionSummon.Dismiss;
        return unlocked ? MinionSummon.Summon : MinionSummon.NotUnlocked;
    }

    /// <summary>
    /// <paramref name="frame"/> counts from the start of the restore. Setups are spaced <see cref="RetryFrames"/> apart,
    /// the first as soon as the owner is drawn; <paramref name="lastSetup"/> is the frame of the previous one, or −1.
    /// </summary>
    public static MinionRestoreStep Restore(ushort wanted, ushort current, bool ownerDrawn, int frame, int lastSetup)
    {
        if (current == wanted) return MinionRestoreStep.Done;
        if (frame >= RestoreFrames) return MinionRestoreStep.GiveUp;
        if (!ownerDrawn) return MinionRestoreStep.Wait;
        return lastSetup < 0 || frame - lastSetup >= RetryFrames ? MinionRestoreStep.Setup : MinionRestoreStep.Wait;
    }
}
