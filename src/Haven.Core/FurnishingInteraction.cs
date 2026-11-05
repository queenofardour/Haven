namespace Haven.Core;

/// <summary>How a switch's two sides are named: lights and devices turn on, containers open, instruments play.</summary>
public enum SwitchVerb { TurnOn, Open, Play }

/// <summary>What clicking a furnishing switch does, phrased as vanilla's interaction prompt.</summary>
public static class FurnishingInteraction
{
    // Vanilla furnishing interactions need the character close by; the range is measured to the item's centre.
    public const float Range = 4f;

    // closedOn: the side a container's doors rest closed on. The Gaol Partition Door closes on its off side, a Mhachi
    // Coffin on its on side.
    public static string Action(bool food, bool on, bool defaultOn, bool inRange, SwitchVerb verb = SwitchVerb.TurnOn, bool closedOn = false)
    {
        if (!inRange) return "Move closer";
        // Food starts on its served side; taking from it shows the other side until it is replenished.
        if (food) return on == defaultOn ? "Take a serving" : "Replenish";
        return verb switch
        {
            // Instruments and music boxes start silent.
            SwitchVerb.Play => on ? "Stop" : "Play",
            SwitchVerb.Open => on == closedOn ? "Open" : "Close",
            _ => on ? "Turn off" : "Turn on",
        };
    }

    // Orchestrion furnishings open Haven's shared player; only the music's controller can change it.
    public static string Orchestrion(bool canControl, bool inRange) => !inRange ? "Move closer" : canControl ? "Choose music" : "See what's playing";
}
