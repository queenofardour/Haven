using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Haven.Core;

namespace Haven;

/// <summary>
/// Customize+ for NPCs: an NPC's body profile travels in the home (NpcLook.Body, Customize+'s own profile
/// JSON as its IPC gives it, packed by NpcBodies), and every PC with Customize+ puts it on its copy of the NPC as a
/// temporary profile, the way sync plugins carry a player's. The owner picks one of their Customize+ profiles, or Copy
/// my look takes the one on their character. Every call fails quietly without Customize+: the NPC keeps its plain body.
/// IPC names checked against the installed Customize+ 2.2.1.3. Framework thread only.
/// </summary>
internal sealed class NpcBody(IDalamudPluginInterface plugin, IPluginLog log)
{
    private const int Success = 0;
    private const double ListAge = 5;
    private (DateTime At, IReadOnlyList<(Guid Id, string Name)> List) profiles = (DateTime.MinValue, []);
    private bool warned, listLogged;

    /// <summary>The player's Customize+ profiles by name (asked again every few seconds); empty without Customize+.</summary>
    public IReadOnlyList<(Guid Id, string Name)> Profiles()
    {
        if ((DateTime.UtcNow - profiles.At).TotalSeconds < ListAge) return profiles.List;
        var list = new List<(Guid, string)>();
        try
        {
            // A list of tuples whose shape has grown between versions: read only the id and name, by position. Dalamud
            // may hand it over as the tuples themselves or, converted for a subscriber of another type, as JSON (each
            // tuple an object of Item1, Item2…); both are read through Newtonsoft's view of it.
            var rows = plugin.GetIpcSubscriber<object>("CustomizePlus.Profile.GetList").InvokeFunc();
            if (rows != null && Newtonsoft.Json.Linq.JToken.FromObject(rows) is Newtonsoft.Json.Linq.JArray array)
                foreach (var row in array)
                    if (row is Newtonsoft.Json.Linq.JObject item && Guid.TryParse(item["Item1"]?.ToString(), out var id) && item["Item2"]?.ToString() is { Length: > 0 } name)
                        list.Add((id, name));
            if (list.Count == 0 && !listLogged)
            {
                listLogged = true;
                var shape = rows == null ? "nothing" : $"{rows.GetType().Name}: {Newtonsoft.Json.JsonConvert.SerializeObject(rows)}";
                log.Information("[NPC-BODY] Customize+ listed no profiles Haven could read ({Shape}).", shape.Length > 400 ? shape[..400] : shape);
            }
        }
        catch (Exception ex) { Unavailable(ex); }
        profiles = (DateTime.UtcNow, list.OrderBy(x => x.Item2, StringComparer.CurrentCultureIgnoreCase).ToList());
        return profiles.List;
    }

    /// <summary>One of the player's profiles as an NPC keeps it, or null.</summary>
    public string? Read(Guid profile)
    {
        try
        {
            var (code, json) = plugin.GetIpcSubscriber<Guid, (int, string?)>("CustomizePlus.Profile.GetByUniqueId").InvokeFunc(profile);
            if (code != Success || json == null) { log.Warning("[NPC-BODY] Customize+ didn't give the profile (code {Code}).", code); return null; }
            return NpcBodies.Pack(json);
        }
        catch (Exception ex) { Unavailable(ex); return null; }
    }

    /// <summary>The profile on the player's own character now, as an NPC keeps it; null with none (or without Customize+).</summary>
    public string? ReadPlayer()
    {
        try
        {
            var (code, id) = plugin.GetIpcSubscriber<ushort, (int, Guid?)>("CustomizePlus.Profile.GetActiveProfileIdOnCharacter").InvokeFunc(0);
            return code == Success && id is { } active ? Read(active) : null;
        }
        catch (Exception ex) { Unavailable(ex); return null; }
    }

    /// <summary>Shapes the NPC at this object index with a body, or takes any shaping away (null).</summary>
    public void Apply(int index, string name, string? body)
    {
        if (body == null) { Clear(index); return; }
        if (NpcBodies.Unpack(body) is not { } json) { log.Warning("[NPC-BODY] {Name}'s Customize+ profile can't be read.", name); return; }
        try
        {
            var set = plugin.GetIpcSubscriber<ushort, string, (int, Guid?)>("CustomizePlus.Profile.SetTemporaryProfileOnCharacter");
            var (code, _) = set.InvokeFunc((ushort)index, json);
            // Customize+ may know Haven's NPCs only as the player their name and world make, as Penumbra does.
            if (code != Success) (code, _) = HavenNative.NativeEngine.HousingNpcAsPlayer(index, () => set.InvokeFunc((ushort)index, json));
            if (code == Success) log.Debug("[NPC-BODY] {Name} wears its Customize+ profile.", name);
            else log.Warning("[NPC-BODY] Customize+ wouldn't shape {Name} (code {Code}).", name, code);
        }
        catch (Exception ex) { Unavailable(ex); }
    }

    /// <summary>Takes the NPC's temporary profile away (it is put away, or its body was removed).</summary>
    public void Clear(int index)
    {
        try
        {
            var delete = plugin.GetIpcSubscriber<ushort, int>("CustomizePlus.Profile.DeleteTemporaryProfileOnCharacter");
            if (delete.InvokeFunc((ushort)index) != Success) HavenNative.NativeEngine.HousingNpcAsPlayer(index, () => delete.InvokeFunc((ushort)index));
        }
        catch (Exception ex) { Unavailable(ex); }
    }

    private void Unavailable(Exception ex)
    {
        if (warned) return;
        warned = true;
        log.Information("[NPC-BODY] Customize+ isn't available ({Message}); NPCs keep their plain bodies.", ex.Message);
    }
}
