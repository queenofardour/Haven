namespace Haven.Core;

/// <summary>Real-world activity that a visit would freeze in place on the server.</summary>
public readonly record struct EntryState(bool Duty, bool DutyQueue, bool Pvp, bool Combat, bool Cutscene, bool Transition, bool Dead);

/// <summary>
/// While inside Haven the client sends the server nothing but its heartbeat, so the real character stands frozen.
/// Entering from a duty, PvP, combat, a scripted event or a duty queue would leave a party member, quest or match
/// stalled (and a queue pop unanswerable), so entry is refused with the reason.
/// </summary>
public static class EntryGuard
{
    public static string? Reason(EntryState state) => state switch
    {
        // Shown as the game's one-line red error message, so each reason is a single short sentence.
        { Transition: true } => "Wait until you have finished changing areas before entering a home.",
        { Duty: true } => "Leave your duty before entering a home.",
        { Pvp: true } => "Haven homes cannot be entered from PvP areas.",
        { DutyQueue: true } => "Leave the duty queue before entering a home.",
        { Combat: true } => "Leave combat before entering a home.",
        { Cutscene: true } => "Finish the current cutscene or event before entering a home.",
        { Dead: true } => "Return to life before entering a home.",
        _ => null,
    };
}
