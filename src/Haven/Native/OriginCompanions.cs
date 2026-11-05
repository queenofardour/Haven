using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Haven.Core;

namespace HavenNative;

// The exit sweep puts away every minion Haven set up, on the player and on peers, and the server never resends a
// minion it thinks is still out. So the minions players had in the real world are noted on entry and set up again
// after the return. SetupCompanion is what the client runs when the server announces a minion: it spawns the local
// object and sends nothing.
internal sealed unsafe class OriginCompanions
{
    private readonly IObjectTable objects;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly Dictionary<ulong, ushort> origin = [];
    private readonly List<(ulong ContentId, ushort Wanted, int LastSetup)> pending = [];
    private int frame;

    public OriginCompanions(IObjectTable objects, IFramework framework, IPluginLog log)
    {
        this.objects = objects;
        this.framework = framework;
        this.log = log;
    }

    /// <summary>Notes every player's minion while still in the real world, before the forward load.</summary>
    public void Capture()
    {
        Stop();
        origin.Clear();
        foreach (var obj in objects)
            if (obj is IPlayerCharacter && ((Character*)obj.Address)->ContentId is var id and not 0)
                origin[id] = Current((Character*)obj.Address);
        var mine = objects.LocalPlayer is { } me ? Current((Character*)me.Address) : (ushort)0;
        log.Information($"[HavenNative] [ORIGIN] noted the minions of {origin.Count} player(s); yours: {(mine == 0 ? "none" : mine)}.");
    }

    /// <summary>
    /// After the return: puts back the real-world minion of the player and of each session peer (by object index).
    /// Other players were never touched, and the client's cache of them is the best there is.
    /// </summary>
    public void Restore(IEnumerable<ushort> peerIndices)
    {
        Stop();
        var ids = new HashSet<ulong>();
        if (objects.LocalPlayer is { } me) ids.Add(((Character*)me.Address)->ContentId);
        foreach (var idx in peerIndices)
            if (objects[idx] is IPlayerCharacter peer) ids.Add(((Character*)peer.Address)->ContentId);
        foreach (var id in ids)
            if (origin.TryGetValue(id, out var wanted)) pending.Add((id, wanted, -1));
        origin.Clear();
        if (pending.Count == 0) return;
        frame = 0;
        framework.Update += Poll;
    }

    public void Stop()
    {
        framework.Update -= Poll;
        pending.Clear();
    }

    private void Poll(IFramework fw)
    {
        for (var i = pending.Count - 1; i >= 0; i--)
        {
            var (id, wanted, lastSetup) = pending[i];
            var owner = Find(id);
            if (owner == null) { pending.RemoveAt(i); continue; }
            var current = Current(owner);
            switch (Minions.Restore(wanted, current, ((GameObject*)owner)->DrawObject != null, frame, lastSetup))
            {
                case MinionRestoreStep.Done:
                    if (lastSetup >= 0) log.Information($"[HavenNative] [RETURN] put back minion {(wanted == 0 ? "none" : wanted)} on [{((GameObject*)owner)->ObjectIndex}].");
                    pending.RemoveAt(i);
                    break;
                case MinionRestoreStep.Setup:
                    owner->CompanionData.SetupCompanion((short)wanted, 0);
                    pending[i] = (id, wanted, frame);
                    break;
                case MinionRestoreStep.GiveUp:
                    log.Warning($"[HavenNative] [RETURN] minion {wanted} did not come back on [{((GameObject*)owner)->ObjectIndex}] (shows {current}).");
                    pending.RemoveAt(i);
                    break;
            }
        }
        frame++;
        if (pending.Count == 0) framework.Update -= Poll;
    }

    private Character* Find(ulong contentId)
    {
        foreach (var obj in objects)
            if (obj is IPlayerCharacter && ((Character*)obj.Address)->ContentId == contentId) return (Character*)obj.Address;
        return null;
    }

    // The live minion is the spawned object's BaseId (its Companion row); CompanionData's own ID field reads 0.
    private static ushort Current(Character* character) => character->CompanionData.CompanionObject is var companion && companion != null
        ? (ushort)companion->BaseId : (ushort)0;
}
