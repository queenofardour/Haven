namespace Haven.Core;

public sealed class LocalEmoteCommands
{
    private readonly Dictionary<string, ushort> commands = new(StringComparer.OrdinalIgnoreCase);
    public void Add(string alias, ushort id)
    {
        if (id != 0 && alias.StartsWith('/')) commands.TryAdd(alias, id);
    }
    /// <summary>The emote a command names, or null.</summary>
    public ushort? Id(string command) => commands.TryGetValue(command, out var id) ? id : null;
    public bool TryPlay(string command, Func<ushort, bool>? playLocked) =>
        playLocked != null && commands.TryGetValue(command, out var id) && playLocked(id);
}
