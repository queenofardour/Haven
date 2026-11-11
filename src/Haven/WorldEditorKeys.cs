using System.Runtime.InteropServices;
using Haven.Core;

namespace Haven;

// ImGui can relinquish keyboard ownership over the game viewport. Read just the editor's
// physical buttons, scoped to the foreground game process; this sends no input to the game.
internal static class WorldEditorKeys
{
    private static readonly int[] MovementKeys = [0x25, 0x26, 0x27, 0x28, 0x21, 0x22, 0x45, 0x51, 0x52, 0xBB, 0x6B, 0xBD, 0x6D, 0x46, 0x0D, 0x1B, 0x57, 0x41, 0x53, 0x44];
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    private static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    public static bool Focused
    {
        get { GetWindowThreadProcessId(GetForegroundWindow(), out var process); return process == Environment.ProcessId; }
    }
    public static EditorButtons Read() => new(Down(1), Down(2), Down(0x52), Down(0x1B), Down(0x10));
    public static PlacementKeys ReadPlacement() => new(
        (Down(0x27) ? 1 : 0) - (Down(0x25) ? 1 : 0), (Down(0x28) ? 1 : 0) - (Down(0x26) ? 1 : 0),
        (Down(0x21) ? 1 : 0) - (Down(0x22) ? 1 : 0), (Down(0x45) ? 1 : 0) - (Down(0x51) ? 1 : 0),
        (Down(0xBB) || Down(0x6B) ? 1 : 0) - (Down(0xBD) || Down(0x6D) ? 1 : 0), Down(0x46), Down(0x0D), Down(0x10), Down(0x11));
    public static bool Ctrl => Down(0x11);
    /// <summary>Movement keys for walking a possessed NPC: W/S forward, D/A right, Ctrl to walk rather than run.</summary>
    public static (int Forward, int Strafe, bool Walk, bool Jump) Walking => ((Down(0x57) ? 1 : 0) - (Down(0x53) ? 1 : 0), (Down(0x44) ? 1 : 0) - (Down(0x41) ? 1 : 0), Down(0x11), Down(0x20));
    /// <summary>The free camera's height keys: Space up (1), C down (-1).</summary>
    public static int Rising => (Down(0x20) ? 1 : 0) - (Down(0x43) ? 1 : 0);
    public static bool Alt => Down(0x12);
    public static bool Middle => Down(0x04);
    // Selection shortcuts while decorating: Delete, Ctrl+D, Ctrl+G (Shift unlinks), Escape.
    public static (bool Delete, bool Duplicate, bool Link, bool Unlink, bool Clear) ReadSelectionKeys()
    {
        var ctrl = Down(0x11); var shift = Down(0x10);
        return (Down(0x2E), ctrl && Down(0x44), ctrl && !shift && Down(0x47), ctrl && shift && Down(0x47), Down(0x1B));
    }
    public static bool AnyMovementKeyDown { get { foreach (var key in MovementKeys) if (Down(key)) return true; return false; } }
}
