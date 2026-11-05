// Haven: targeted emotes inside a home.
//
// A targeted emote (/dote's heart, /pet, /hug ...) aims its effect at the character's target. Outside Haven the
// server stamps the local player's target and echoes the emote, but inside a home every outbound packet except
// the heartbeat is filtered, so the local player's own target field can stay empty (the effect lands on yourself)
// and puppets play emotes straight from their timelines with no emote target at all. These helpers point both
// fields at the right object before the emote starts: locally from the TargetSystem's hard target, and on peers
// from the target the sender streams (TargetEntityId).
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace HavenNative.Services;

internal static unsafe class EmoteTargets
{
    /// <summary>The game's "no object" id.</summary>
    private const ulong None = 0xE0000000;

    /// <summary>The local player's hard target, or null when nothing (or only themself) is targeted.</summary>
    public static GameObject* LocalTarget(Character* self)
    {
        var system = TargetSystem.Instance();
        if (system == null) return null;
        var target = system->GetHardTarget();
        return target == null || target == (GameObject*)self ? null : target;
    }

    /// <summary>The entity id streamed to peers as the sender's target: the hard target, 0 for none.</summary>
    public static uint LocalTargetEntity(Character* self, uint fallback)
    {
        if (TargetSystem.Instance() == null) return fallback;
        var target = LocalTarget(self);
        return target == null ? 0 : target->EntityId;
    }

    /// <summary>
    /// Before the local player emotes inside a home: aim the character's target and emote target at the hard
    /// target, and fill an executor option that carries no target.
    /// </summary>
    public static void PrepareLocal(Character* self, nint option = 0)
    {
        if (self == null) return;
        var target = LocalTarget(self);
        ulong id = target == null ? None : (ulong)target->GetGameObjectId();
        self->SetTargetId(id);
        self->EmoteController.Target = id;
        if (option != 0 && target != null)
        {
            var play = (EmoteController.PlayEmoteOption*)option;
            ulong current = play->TargetId;
            if (current == 0 || current == None) play->TargetId = id;
        }
    }

    /// <summary>For the log: the hard target, and what the character and its emote are aimed at.</summary>
    public static string Describe(Character* self, nint option)
    {
        var target = LocalTarget(self);
        var aimed = target == null ? "none" : $"{target->ObjectKind} entity {target->EntityId:X} object {(ulong)target->GetGameObjectId():X} index {target->ObjectIndex}";
        var played = option == 0 ? "" : $" option {(ulong)((EmoteController.PlayEmoteOption*)option)->TargetId:X}";
        return $"hard target {aimed}; character {(ulong)self->TargetId:X}; emote {(ulong)self->EmoteController.Target:X}{played}";
    }

    /// <summary>Before a puppet plays an emote: aim it at the target its sender streamed.</summary>
    public static void PreparePeer(Character* character) => character->EmoteController.Target = character->TargetId;
}
