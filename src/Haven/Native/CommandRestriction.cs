using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;

namespace HavenNative;

/// <summary>
/// The text-command layer's emote restriction, which Haven's Gold Saucer machines could leave on for the rest of a visit.
/// Research (read-only disassembly, 29 October 2026; addresses for this build only, never used): the emote command
/// (RaptureShellCommandInterface slot 1) refuses with code 10 ("That command is unavailable at this time.", LogMessage
/// 726) while bit 0 of RaptureShellModule.Flags is set and bit 8 isn't. Bit 0 is written by UIModule.ChangeUIMode (mode 7)
/// and by UIModule vfunc 2 (SetFlag(mask, on) on a ushort at UIModule+0xFFA74): whenever the mask touches 0xA4, it sets
/// bit 0 unless 0x04, 0x20 and 0x80 are all set. The minigame agent (173) sets flag 4 when it opens and clears it when a
/// round begins (and clears bit 0 itself right after); in the Gold Saucer the machine's event script settles it when the
/// event ends. Haven plays the machines without that event, and its close of a game that never began cleared flag 4, so
/// bit 0 stayed on until the next zone load. The emote window plays through its agent, which never reads these flags.
/// </summary>
internal static unsafe class CommandRestriction
{
    // UIModule vfunc 2: movzx r9d, word [rcx+disp32] is the flag word.
    private const string SetFlagSignature = "40 53 48 83 EC 20 44 0F B7 89 ?? ?? ?? ?? 48 8B D9 45 84 C0";
    private static delegate* unmanaged<UIModule*, ushort, byte, void> setFlag;
    private static int flagOffset;

    /// <summary>Whether the flag function and its word were found in this game version.</summary>
    public static bool Available => setFlag != null && flagOffset > 0;

    public static void Initialize(ISigScanner scanner, IPluginLog log)
    {
        if (!scanner.TryScanText(SetFlagSignature, out var address)) { log.Warning("[EMOTE-CMD] The UI flag function wasn't found; Gold Saucer games can't put the emote restriction back."); return; }
        var offset = *(int*)(address + 10);
        // The flag word sits deep in UIModule; anything else means the signature matched something new.
        if (offset is < 0x1000 or > 0x200000) { log.Warning($"[EMOTE-CMD] The UI flag word offset 0x{offset:X} is unexpected; left alone."); return; }
        setFlag = (delegate* unmanaged<UIModule*, ushort, byte, void>)address;
        flagOffset = offset;
    }

    /// <summary>UIModule's flag word, or null when unavailable.</summary>
    private static ushort? Flags()
    {
        var ui = UIModule.Instance();
        return ui == null || !Available ? null : *(ushort*)((byte*)ui + flagOffset);
    }

    /// <summary>Whether the shell refuses emote commands now (bit 0 without bit 8).</summary>
    public static bool Restricted
    {
        get { var shell = RaptureShellModule.Instance(); return shell != null && (shell->Flags & 1) != 0 && (shell->Flags & 0x100) == 0; }
    }

    /// <summary>The state a game may change: UIModule flag 4 and the shell's bit 0.</summary>
    public readonly record struct State(bool Flag4, bool Bit0);

    public static State? Capture()
    {
        var shell = RaptureShellModule.Instance();
        return Flags() is { } flags && shell != null ? new State((flags & 4) != 0, (shell->Flags & 1) != 0) : null;
    }

    /// <summary>Puts flag 4 back through the game's own function, then the shell's bit 0 as it was.</summary>
    public static void Restore(State state)
    {
        var ui = UIModule.Instance();
        var shell = RaptureShellModule.Instance();
        if (ui == null || shell == null || Flags() is not { } flags) return;
        if (((flags & 4) != 0) != state.Flag4) setFlag(ui, 4, state.Flag4 ? (byte)1 : (byte)0);
        shell->Flags = state.Bit0 ? shell->Flags | 1 : shell->Flags & ~1u;
    }

    /// <summary>Clears the shell's bit 0 (inside a home nothing else should restrict emotes).</summary>
    public static void Lift()
    {
        var shell = RaptureShellModule.Instance();
        if (shell != null) shell->Flags &= ~1u;
    }

    /// <summary>For the log: the shell flags and UIModule's flag word.</summary>
    public static string Describe()
    {
        var shell = RaptureShellModule.Instance();
        return $"shell flags 0x{(shell == null ? 0 : shell->Flags):X}, UI flags {(Flags() is { } flags ? $"0x{flags:X}" : "unknown")}";
    }
}
