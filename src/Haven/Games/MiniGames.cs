using Haven.Uld;

namespace Haven.Games;

/// <summary>What the minigames need from Haven besides layouts and effects.</summary>
public sealed record MiniGameContext(TriadSetup? Triad = null, OdysseySetup? Odyssey = null);

/// <summary>The minigames Haven offers, by name, each built on the game's own layouts.</summary>
public static class MiniGames
{
    public static MiniGame? Create(string name, IMiniGameEffects effects, IUldHost host, MiniGameContext context) => name switch
    {
        "triad" when context.Triad != null => new TripleTriadMatch(effects, host, context.Triad),
        "odyssey" when context.Odyssey != null => new OdysseyGame(effects, host, context.Odyssey),
        _ => null,
    };
}
