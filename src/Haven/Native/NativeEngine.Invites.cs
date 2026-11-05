using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace HavenNative;

// Gathering invitations: who on the friend list is near enough to join. Read only.
public sealed partial class NativeEngine
{
    /// <summary>
    /// Friends (the game's friend list, by content ID) who are players in the object table now: near this player in the
    /// real world. Inside a home the real world's players are still there, only hidden. Framework thread.
    /// </summary>
    public unsafe IReadOnlyList<(ulong ContentId, string Name)> HousingNearbyFriends()
    {
        var module = InfoModule.Instance();
        if (module == null || !clientState.IsLoggedIn) return [];
        var list = (InfoProxyCommonList*)module->GetInfoProxyById(InfoProxyId.FriendList);
        if (list == null || list->EntryCount == 0) return [];
        var friends = new Dictionary<ulong, string>();
        for (uint i = 0; i < list->EntryCount && i < 512; i++)
        {
            var entry = list->GetEntry(i);
            if (entry != null && entry->ContentId != 0) friends[entry->ContentId] = entry->NameString;
        }
        var self = LocalContentId();
        var found = new List<(ulong, string)>();
        foreach (var obj in objectTable)
        {
            if (obj is not IPlayerCharacter player) continue;
            var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)player.Address;
            if (character == null) continue;
            var id = character->ContentId;
            if (id == 0 || id == self || !friends.TryGetValue(id, out var name) || found.Any(f => f.Item1 == id)) continue;
            found.Add((id, name.Length > 0 ? name : player.Name.TextValue));
        }
        return found;
    }
}
