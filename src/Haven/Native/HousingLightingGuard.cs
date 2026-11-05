using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.Graphics.Environment;

namespace HavenNative;

// The native IndoorTerritory brightness writer targets LayoutWorld.ActiveLayout,
// and also writes LayoutWorld's saved indoor data. A client-only house has no
// server lighting authority. Keep those native writes isolated while it is live;
// leave the runtime's private preview in charge instead of modifying saved data.
internal sealed unsafe class HousingLightingGuard : IDisposable
{
    // Installed client: int SetLayoutLightLevel(float), XMM0 argument, EAX 0/2 result.
    // Verified read-only against the same binary as the API 15 fixture writer.
    internal const string Signature = "40 53 48 83 EC 30 48 8B 1D ?? ?? ?? ?? 0F 29 74 24 ?? 0F 28 F0 48 85 DB";
    private delegate int SetLayoutLightLevel(float level);
    private readonly Hook<SetLayoutLightLevel>? hook;
    private readonly Func<uint, bool> ready;
    private readonly IPluginLog log;
    private nint boundLayout;
    private uint territory;
    private float target = 1;
    private float? reportedRequest;
    private bool failed;
    public bool Available => hook != null && !failed;
    public long InterceptedWrites { get; private set; }

    public HousingLightingGuard(ISigScanner scanner, IGameInteropProvider interop, IPluginLog log, Func<uint, bool> ready)
    {
        this.log = log; this.ready = ready;
        try { hook = interop.HookFromAddress<SetLayoutLightLevel>(scanner.ScanText(Signature), Detour); }
        catch (Exception ex) { log.Warning(ex, "[Haven] Native room-lighting isolation is unavailable; GPose transition replay remains active."); }
    }

    public void Bind(nint layout, uint territoryId, float level)
    {
        if (layout == 0 || !float.IsFinite(level) || level is < 0 or > 1) { Clear(); return; }
        if (boundLayout != layout || target != level) reportedRequest = null;
        boundLayout = layout; territory = territoryId; target = level;
        if (hook != null && !failed && !hook.IsEnabled)
        {
            try { hook.Enable(); }
            catch (Exception ex)
            {
                failed = true; Clear();
                log.Warning(ex, "[Haven] Room-lighting isolation could not be enabled; transition replay remains active.");
            }
        }
    }

    public void Clear()
    {
        // Release ownership before disabling: a detour already in flight must
        // fall through to Original during a transition or plugin cleanup.
        boundLayout = 0; territory = 0; reportedRequest = null;
        if (hook?.IsEnabled == true) hook.Disable();
    }

    public void ReportTransition(bool gposing)
    {
        reportedRequest = null;
        var environment = EnvManager.Instance();
        var weather = environment == null ? -1 : environment->ActiveWeather;
        // Read-only lighting sample within the known EnvState extent. The RGB
        // fields at +0x20/+0x38 are documented by Brio/Ktisis; no environment
        // block is restored or held, so photo exposure/lights keep their controls.
        var sample = environment == null ? null : (float*)((byte*)&environment->EnvState + 0x20);
        var lighting = sample == null ? "unavailable" : $"sun({sample[0]:0.###},{sample[1]:0.###},{sample[2]:0.###}) ambient({sample[6]:0.###},{sample[7]:0.###},{sample[8]:0.###})";
        log.Information($"[Haven lighting] GPose {(gposing ? "entered" : "left")}: territory {territory}, room level {target:0.###}, weather {weather}, {lighting}, native writes isolated {InterceptedWrites}, guard available {Available}.");
    }

    private int Detour(float requested)
    {
        var world = LayoutWorld.Instance();
        var layout = world == null ? null : world->ActiveLayout;
        if (boundLayout == 0 || (nint)layout != boundLayout || !ready(territory)) return hook!.Original(requested);
        try
        {
            InterceptedWrites++;
            if (Math.Abs(requested - target) > .0001f && reportedRequest == null)
            {
                reportedRequest = requested;
                log.Information($"[Haven] Kept private room lighting at {target:0.###}; native housing requested {requested:0.###} (territory {territory}).");
            }
            // The runtime submits its desired level once and on mode/settings
            // changes. Do not allocate/replay a fixture buffer for every native
            // transition tick, or call Original (which also touches saved data).
            return 0;
        }
        catch (Exception ex)
        {
            // Do not mutate an origin territory or keep retrying a broken hook.
            failed = true; Clear();
            log.Warning(ex, "[Haven] Room-lighting isolation stopped after a guard failure.");
            return hook!.Original(requested);
        }
    }

    internal static int Submit(LayoutManager* layout, float level)
    {
        var world = LayoutWorld.Instance();
        if (layout == null || world == null || world->ActiveLayout != layout || layout->HousingType == 0) return 2;
        // 0xFFFF leaves all fixture slots untouched. The native fixture helper
        // allocates this transient preview buffer when necessary.
        var result = LayoutWorld.SetInteriorFixture(0, 0, -1, ushort.MaxValue);
        if (result != 0) return result;
        if (world->ActiveLayout != layout) return 2;
        if (layout->IndoorAreaData == null) return 2;
        layout->IndoorAreaData->LightLevel = level;
        layout->HousingLayoutDataUpdatePending = true;
        return 0;
    }

    internal static bool PrepareFixtureCommand(LayoutManager* layout)
    {
        var world = LayoutWorld.Instance();
        if (layout == null || world == null || world->ActiveLayout != layout || layout->HousingType != 2) return false;
        // These are transient preview commands, not the shell's resident
        // fixtures. Leaving previous IDs here replays them on every update,
        // instead of isolating each floor/part's new request.
        // Preserve windows, doors, other exterior data and the global light level.
        if (layout->IndoorAreaData != null) InteriorFixtureCommands.Clear(layout->IndoorAreaData);
        return true;
    }

    public void Dispose() { Clear(); hook?.Dispose(); }
}
