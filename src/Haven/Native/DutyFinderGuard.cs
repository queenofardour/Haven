using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace HavenNative;

// Inside Haven the firewall suppresses every outbound packet, so a duty registration never reaches the server
// while the client still shows a queue (and a pop could not be answered). Close the queueing windows instead.
internal sealed unsafe class DutyFinderGuard : IDisposable
{
    // Duty Finder (incl. roulettes and PvP), Raid Finder, Duty Support and the older Trust window.
    private static readonly string[] Windows = ["ContentsFinder", "RaidFinder", "DawnStory", "Dawn"];
    private readonly IAddonLifecycle lifecycle;
    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly IToastGui toast;
    private readonly Func<bool> inside;

    public DutyFinderGuard(IAddonLifecycle lifecycle, IFramework framework, IGameGui gameGui, IToastGui toast, Func<bool> inside)
    {
        this.lifecycle = lifecycle; this.framework = framework; this.gameGui = gameGui; this.toast = toast; this.inside = inside;
        lifecycle.RegisterListener(AddonEvent.PostSetup, Windows, OnOpened);
    }

    private void OnOpened(AddonEvent type, AddonArgs args)
    {
        if (!inside()) return;
        var name = args.AddonName;
        toast.ShowError("Duties cannot be joined from inside a Haven home. Leave the home first.");
        // Close on the next frame, as the window's own close button would, rather than mid-setup.
        framework.RunOnTick(() =>
        {
            var addon = gameGui.GetAddonByName(name);
            if (addon.Address != nint.Zero) ((AtkUnitBase*)addon.Address)->Close(true);
        });
    }

    public void Dispose() => lifecycle.UnregisterListener(AddonEvent.PostSetup, Windows, OnOpened);
}
