using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Haven.Native;

/// <summary>
/// Keeps the social lists usable inside a home. Entering a home makes the game refresh its friend list (and the free
/// company and linkshell member lists when their windows open): a server request Haven's filter drops while telling
/// the game it went out. The game then empties the list to wait for an answer that never comes, so the rows on screen
/// are left with nothing behind them and right-clicking one opens no menu. While inside, those refreshes are held back
/// and the lists keep what they had on entry. Haven's return isn't a real zone change, so nothing outside asks again:
/// once the filter is open after a visit, the friend list (and any list held back) is requested afresh. (Estate
/// Teleportation asks to leave the home first: SocialRequests.)
/// </summary>
internal sealed unsafe class SocialListKeeper : IDisposable
{
    private delegate bool RequestData(InfoProxyInterface* proxy);
    private readonly IPluginLog log;
    private readonly IAddonLifecycle lifecycle;
    private readonly Func<bool> inside;
    /// <summary>
    /// Lets a list's refresh through inside a home (SocialRequests.PassListRefresh: its request out, its pages in), given
    /// the RequestData function and the call; null result when it can't, and the list is kept instead.
    /// </summary>
    public Func<nint, int, Func<bool>, bool?>? PassRefresh { get; set; }
    // Lists refreshed for real inside a home, with the place of the request opcode in their RequestData.
    private static readonly Dictionary<InfoProxyId, int> Live = new() { [InfoProxyId.FriendList] = 0x6E, [InfoProxyId.PartyMember] = 0x4B };
    private readonly IFramework framework;
    // Lists whose refresh was held back during the visit; asked for once the filter is open again.
    private readonly HashSet<nint> held = [];
    private bool wasInside;
    private long refreshAt;
    private readonly List<Hook<RequestData>> hooks = [];
    private readonly Dictionary<nint, (Hook<RequestData> Hook, InfoProxyId Id)> byProxy = [];
    private readonly Dictionary<nint, nint> requestFunctions = [];

    public SocialListKeeper(IGameInteropProvider interop, IAddonLifecycle lifecycle, IFramework framework, IPluginLog log, Func<bool> inside)
    {
        this.log = log; this.lifecycle = lifecycle; this.framework = framework; this.inside = inside;
        var module = InfoModule.Instance();
        if (module == null) { log.Warning("[Haven] [SOCIAL] The info module isn't there; social lists aren't kept inside homes."); return; }
        var seen = new Dictionary<nint, Hook<RequestData>>();
        // Each hooked function calls its own original: one detour per address.
        foreach (var id in new[] { InfoProxyId.FriendList, InfoProxyId.PartyMember, InfoProxyId.FreeCompanyMember, InfoProxyId.LinkshellMember, InfoProxyId.CrossWorldLinkshellMember })
        {
            var proxy = module->GetInfoProxyById(id);
            if (proxy == null) continue;
            // RequestData is the proxy's own virtual function 3; each list class has its own.
            var address = ((nint*)*(nint*)proxy)[3];
            if (!seen.TryGetValue(address, out var hook))
            {
                try
                {
                    Hook<RequestData>? own = null;
                    own = interop.HookFromAddress<RequestData>(address, proxy => Detour(proxy, own!));
                    hook = own;
                    hook.Enable();
                    seen[address] = hook; hooks.Add(hook);
                }
                catch (Exception ex) { log.Warning(ex, "[Haven] [SOCIAL] Couldn't keep the {List} inside homes.", id); continue; }
            }
            byProxy[(nint)proxy] = (hook, id);
            requestFunctions[(nint)proxy] = address;
        }
        framework.Update += OnFrame;
    }

    // On entering a home the lists' sizes are noted; two seconds after leaving (the filter open again), the friend
    // list and every list held back are requested afresh, so they refill from the server.
    private void OnFrame(IFramework _)
    {
        try
        {
            var now = inside();
            if (now != wasInside)
            {
                wasInside = now;
                log.Information("[Haven] [SOCIAL] {Where} a home: {Lists}.", now ? "Entering" : "Leaving", Sizes());
                if (now) held.Clear();
                else refreshAt = Environment.TickCount64 + 2000;
            }
            if (now || refreshAt == 0 || Environment.TickCount64 < refreshAt) return;
            refreshAt = 0;
            foreach (var (proxy, kept) in byProxy)
            {
                if (kept.Id != InfoProxyId.FriendList && !held.Contains(proxy)) continue;
                var sent = kept.Hook.Original((InfoProxyInterface*)proxy);
                log.Information("[Haven] [SOCIAL] Asked for the {List} again after the visit ({Result}).", kept.Id, sent ? "sent" : "not sent");
            }
            held.Clear();
        }
        catch (Exception ex) { log.Error(ex, "[Haven] [SOCIAL] The social lists couldn't be refreshed."); }
    }

    // Each kept list's entry count (InfoProxyInterface +0x10).
    private string Sizes() => string.Join(", ", byProxy.Select(p => $"{p.Value.Id} {*(uint*)((byte*)p.Key + 0x10)}"));

    private bool Detour(InfoProxyInterface* proxy, Hook<RequestData> hook)
    {
        if (!inside() || !byProxy.TryGetValue((nint)proxy, out var kept)) return hook.Original(proxy);
        // The friend list refreshes for real inside a home (its request out, its pages in): live statuses, and a
        // removal redraws properly. Other lists, or when that can't be resolved, keep what they had on entry.
        if (Live.TryGetValue(kept.Id, out var opcodeAt) && PassRefresh?.Invoke(requestFunctions[(nint)proxy], opcodeAt, () => hook.Original(proxy)) is { } sent)
        {
            log.Information("[Haven] [SOCIAL] Let the {List} refresh through ({Result}).", kept.Id, sent ? "sent" : "not sent");
            return sent;
        }
        held.Add((nint)proxy);
        log.Information("[Haven] [SOCIAL] Kept the {List} as it was on entry, {Count} entries (its refresh would be dropped and empty it).", kept.Id, *(uint*)((byte*)proxy + 0x10));
        return false;
    }

    public void Dispose()
    {
        framework.Update -= OnFrame;
        foreach (var hook in hooks) hook.Dispose();
        hooks.Clear(); byProxy.Clear();
    }
}
