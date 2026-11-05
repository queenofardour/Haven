using System.Collections.Concurrent;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Haven.Core;
using Haven.Native;

namespace Haven.Games;

/// <summary>
/// Another player at an arcade machine, as this PC shows them: their stance at the machine (the machine's idle as their
/// base idle), each swing or punch (an ActionTimeline played once), and normal again at the end. The machine's own
/// reaction arrives separately (a shared machine timeline). Messages come on the relay's thread; Update applies them.
/// </summary>
internal sealed unsafe class ArcadeWatch(NativeArcade native, Func<ulong, nint> character)
{
    private readonly ConcurrentQueue<GameMessage> inbox = new();
    // Players shown in a machine's stance here, to put back if they leave or the home closes.
    private readonly HashSet<ulong> posed = [];

    public void Receive(GameMessage message) { if (message.Game == "arcade") inbox.Enqueue(message); }

    public void Update(bool inHome)
    {
        if (!inHome) { foreach (var player in posed) Stance(player, 3); posed.Clear(); inbox.Clear(); return; }
        while (inbox.TryDequeue(out var message))
        {
            // Only timelines the installed game has: an unknown ActionTimeline row can crash the game (WireSanity).
            if (message.Values is not [var timeline] || timeline is <= 0 or > ushort.MaxValue
                || HavenNative.Wire.WireSanity.Timeline((ushort)timeline) == 0) continue;
            switch (message.Kind)
            {
                case "pose": if (Stance(message.Sender, (ushort)timeline)) posed.Add(message.Sender); break;
                case "act": if ((Character*)character(message.Sender) is var c && c != null) c->Timeline.PlayActionTimeline((ushort)timeline); break;
                case "end": Stance(message.Sender, 3); posed.Remove(message.Sender); break;
            }
        }
    }

    private bool Stance(ulong player, ushort idle)
    {
        var c = (Character*)character(player);
        if (c == null) return false;
        native.BaseIdle(c, idle);
        return true;
    }
}
