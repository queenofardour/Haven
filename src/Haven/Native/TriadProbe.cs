using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;

namespace Haven.Native;

/// <summary>
/// A read-only record of a real Triple Triad match (/haven triadprobe, then play an NPC outside Haven): every command the
/// game queues for its match window (AgentTripleTriad.PushCommand) and every event the window sends back through the
/// game's relay, in the Dalamud log as [TRIAD-PROBE]. It changes nothing; it exists so Haven can later drive the game's
/// own match window, whose start sequence and flip command were read offline but not yet seen in a match.
/// </summary>
internal sealed unsafe class TriadProbe(IGameInteropProvider hooks, IPluginLog log) : IDisposable
{
    private const string PushSignature = "48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 20 48 8D 99 70 01 00 00";
    private const string RelaySignature = "40 55 53 41 54 41 55 48 8B EC 48 83 EC 78 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 45 D8 4C 8B E1 4C 89 45 A8 48 8B 49 10";
    private delegate nint PushDelegate(nint agent, byte* command);
    private delegate nint RelayDelegate(nint self, nint result, byte* values, uint count, ulong kind);
    private Hook<PushDelegate>? push;
    private Hook<RelayDelegate>? relay;

    public bool Recording => push?.IsEnabled == true;

    /// <summary>Starts or stops recording; returns whether it now records.</summary>
    public bool Toggle()
    {
        if (Recording) { push!.Disable(); relay?.Disable(); log.Information("[TRIAD-PROBE] stopped."); return false; }
        try
        {
            push ??= hooks.HookFromSignature<PushDelegate>(PushSignature, Push);
            relay ??= hooks.HookFromSignature<RelayDelegate>(RelaySignature, Relay);
            push.Enable(); relay.Enable();
            log.Information("[TRIAD-PROBE] recording: play a Triple Triad match against an NPC.");
            return true;
        }
        catch (Exception e) { log.Warning(e, "[TRIAD-PROBE] The match functions weren't found in this game version."); return false; }
    }

    private nint Push(nint agent, byte* command)
    {
        try { if (command != null) log.Information($"[TRIAD-PROBE] command {Convert.ToHexString(new ReadOnlySpan<byte>(command, 16))} (kind {command[0]}, side {command[1]}, {*(uint*)(command + 4)}, {*(uint*)(command + 8)}, {*(uint*)(command + 12)})"); }
        catch { }
        return push!.Original(agent, command);
    }

    // An AtkValue is 16 bytes: its type at +0, its value at +8.
    private nint Relay(nint self, nint result, byte* values, uint count, ulong kind)
    {
        try
        {
            var shown = new List<string>();
            for (var i = 0; i < Math.Min(count, 12u) && values != null; i++)
                shown.Add($"{values[i * 16]}:{*(int*)(values + i * 16 + 8)}");
            log.Information($"[TRIAD-PROBE] event kind {kind} [{string.Join(", ", shown)}]");
        }
        catch { }
        return relay!.Original(self, result, values, count, kind);
    }

    public void Dispose() { push?.Dispose(); relay?.Dispose(); }
}
