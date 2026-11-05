using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace HavenNative;

// NPCs: characters this PC creates on its own (ClientObjectManager, object-table index = slot + 200),
// dressed from the home's saved look, moved by Haven, with the game's camera following one while the player possesses
// it. Nothing about them is ever sent to the server. Framework thread only.
//
// Objects are kept for the whole visit and reused (NpcPossession's pool): an NPC sent away, removed, renamed or given
// a new look keeps or returns its object rather than deleting it. Deleting an object mid-visit can leave other plugins
// holding its freed address (Penumbra caches the object table between the game's own array updates), and a Penumbra
// redraw then reads it (0.51.1: a crash in Penumbra's redraw, after a run of renames that each deleted and
// remade the NPC). Objects are deleted only when the visit ends (HousingNpcDeleteAll).
public sealed partial class NativeEngine
{
    private const int ClientObjectBase = 200;
    // Haven takes client object slots from the top of the general range down (objects 438, 437…). The low slots belong
    // to GPose and cutscenes (slot 1, object 201, is the GPose player, and Penumbra treats objects 201-242 as GPose
    // actors); the game's own free-slot search stops below slot 239, and slots 240 and up are the screen actors
    // (character screen, examine, fitting room…). CreateBattleCharacter honours an explicit slot (read from the
    // installed game: a slot other than -1 skips the search; 249 and up are refused).
    private const int TopNpcSlot = 238, LowestNpcSlot = 200;
    // Where a parked object waits, far below any home, in case something draws it again (a Penumbra redraw).
    private static readonly System.Numerics.Vector3 ParkedAt = new(0, -5000, 0);

    /// <summary>Makes a new, empty character object for an NPC; its slot, or null when there is no free one.</summary>
    public unsafe uint? HousingNpcCreate()
    {
        var manager = ClientObjectManager.Instance();
        if (objectTable.LocalPlayer == null || manager == null) return null;
        for (var slot = TopNpcSlot; slot >= LowestNpcSlot; slot--)
        {
            if (manager->GetObjectByIndex((ushort)slot) != null) continue;
            var made = manager->CreateBattleCharacter((uint)slot);
            if (made != (uint)slot)
            {
                // Refused (or made elsewhere, which is given back at once, before anything else sees it): try lower.
                if (made != 0xFFFFFFFF) manager->DeleteObjectByIndex((ushort)made, 0);
                continue;
            }
            if (NpcIn(made) == null) { manager->DeleteObjectByIndex((ushort)made, 0); return null; }
            log.Information("[NPC] made an object in slot {Slot} (object {Index}).", made, made + ClientObjectBase);
            return made;
        }
        return null;
    }

    /// <summary>
    /// Sets an undrawn character up as an NPC: on a new object, a human copied from the player first (the template);
    /// then the saved look, its game name (<see cref="Haven.Core.HomeNpcs.GameNames"/>), place and facing. It is a
    /// battle NPC with no game id, no owner and the player's home world, which Glamourer (Penumbra's actor
    /// identification, "player NPC") recognises by name and world. Draw it with <see cref="HousingNpcDrawWhenReady"/>.
    /// </summary>
    public unsafe bool HousingNpcSetup(uint slot, bool fresh, System.Numerics.Vector3 position, float yaw, string gameName, Haven.Core.NpcLook? look, byte level = 0)
    {
        var player = (Character*)(objectTable.LocalPlayer?.Address ?? 0);
        var npc = NpcIn(slot);
        if (player == null || npc == null) return false;
        if (npc->GameObject.DrawObject != null) npc->GameObject.DisableDraw();
        if (fresh)
        {
            npc->CharacterSetup.CopyFromCharacter(player, CharacterSetupContainer.CopyFlags.None);
            // A copy leaves the new character tied to its source (as a cutscene copy is, so Penumbra and Glamourer would
            // treat it as the player); copying it onto itself unties it, so it is its own character (as Brio does).
            npc->CharacterSetup.CopyFromCharacter(npc, CharacterSetupContainer.CopyFlags.None);
        }
        WriteLook(npc, look, player);
        npc->GameObject.ObjectKind = ObjectKind.BattleNpc;
        npc->GameObject.BaseId = 0;
        npc->NameId = 0;
        npc->GameObject.OwnerId = 0xE0000000;
        npc->HomeWorld = player->HomeWorld;
        npc->CurrentWorld = player->CurrentWorld;
        WriteName(npc, gameName);
        npc->Level = level;
        npc->Timeline.BaseOverride = 0;
        npc->GameObject.SetPosition(position.X, position.Y, position.Z);
        npc->GameObject.SetRotation(yaw);
        // Targetable, so a click (Haven's own, see HousingTargetNpc) can make it the game's target.
        npc->GameObject.TargetableStatus |= ObjectTargetableFlags.IsTargetable;
        log.Information("[NPC] set up {Name} in slot {Slot}{Fresh}.", gameName, slot, fresh ? " (new object)" : "");
        return true;
    }

    /// <summary>A new plain look: the character is undrawn and given it; HousingNpcDrawWhenReady draws it again.</summary>
    public unsafe void HousingNpcRestyle(uint slot, Haven.Core.NpcLook look)
    {
        var npc = NpcIn(slot);
        if (npc == null) return;
        if (npc->GameObject.DrawObject != null) npc->GameObject.DisableDraw();
        WriteLook(npc, look, (Character*)(objectTable.LocalPlayer?.Address ?? 0));
    }

    /// <summary>Gives a character made here a new game name, in place (the target bar reads it live).</summary>
    public unsafe void HousingNpcRename(uint slot, string gameName)
    {
        var npc = NpcIn(slot);
        if (npc != null) WriteName(npc, gameName);
    }

    /// <summary>The level the character's nameplate and the target bar show (0: none, the nameplate drops it).</summary>
    public unsafe void HousingNpcLevel(uint slot, byte level)
    {
        var npc = NpcIn(slot);
        if (npc != null) npc->Level = level;
    }

    private static unsafe void WriteLook(Character* npc, Haven.Core.NpcLook? look, Character* player)
    {
        if (look is not { Valid: true, Drawable: true }) return;
        look.Customize.AsSpan().CopyTo(npc->DrawData.CustomizeData.Data);
        var gear = npc->DrawData.EquipmentModelIds;
        for (var i = 0; i < Haven.Core.NpcLook.GearSlots && i < gear.Length; i++) gear[i].Value = look.Gear[i];
        // Weapons as saved; a look saved before weapons were kept shows the player's (a reused object would otherwise
        // keep its last NPC's).
        for (var i = 0; i < npc->DrawData.WeaponData.Length && i < Haven.Core.NpcLook.WeaponSlots; i++)
            if (look.Weapons is { } weapons) npc->DrawData.WeaponData[i].ModelId.Value = weapons[i];
            else if (player != null && i < player->DrawData.WeaponData.Length) npc->DrawData.WeaponData[i].ModelId.Value = player->DrawData.WeaponData[i].ModelId.Value;
    }

    private static unsafe void WriteName(Character* npc, string name)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(name);
        var field = npc->GameObject.Name;
        field.Clear();
        bytes.AsSpan(0, Math.Min(bytes.Length, field.Length - 1)).CopyTo(field);
    }

    /// <summary>Draws the character once its model is ready; true when it is drawn.</summary>
    public unsafe bool HousingNpcDrawWhenReady(uint slot)
    {
        var npc = NpcIn(slot);
        if (npc == null) return false;
        if (npc->GameObject.DrawObject != null) return true;
        // A redraw in progress (below): drawn again once Penumbra shows the character.
        if (((uint)npc->GameObject.RenderFlags & Hidden) != 0 || !npc->GameObject.IsReadyToDraw()) return false;
        npc->GameObject.EnableDraw();
        return true;
    }

    private const uint Hidden = 0x02;   // RenderFlags: hidden (Penumbra's redraw sets it for a frame)

    /// <summary>
    /// Penumbra redraws a character (for Glamourer's face, race or body changes, or any plugin's redraw) by hiding it for
    /// a frame. The game rebuilds a world character's model then, but not one a plugin made (Penumbra undraws and draws
    /// those itself only in GPose's slots), so the change never showed. When an NPC is hidden that way it is undrawn
    /// here; <see cref="HousingNpcDrawWhenReady"/> draws it again once shown, and Glamourer dresses the new model.
    /// </summary>
    public unsafe bool HousingNpcRedrawAsked(uint slot)
    {
        var npc = NpcIn(slot);
        if (npc == null || npc->GameObject.DrawObject == null || ((uint)npc->GameObject.RenderFlags & Hidden) == 0) return false;
        npc->GameObject.DisableDraw();
        return true;
    }

    /// <summary>Shows a character a redraw left hidden (Penumbra unloaded mid-redraw, say).</summary>
    public unsafe void HousingNpcShow(uint slot)
    {
        var npc = NpcIn(slot);
        if (npc != null) npc->GameObject.RenderFlags &= ~(VisibilityFlags)Hidden;
    }

    /// <summary>
    /// Puts a character away for reuse: undrawn, untargeted, not followed by the camera, untargetable and far below the
    /// home. Call each frame while parked: a Penumbra redraw draws it again, and this undraws it.
    /// </summary>
    public unsafe void HousingNpcPark(uint slot)
    {
        var npc = NpcIn(slot);
        if (npc == null) return;
        Untarget((GameObject*)npc);
        if (cameraNpc == slot) cameraNpc = null;
        // Its tilt on a ball goes with it now, not two frames later: the object may be deleted next, and the tilt hook
        // would read it through a draw object reused meanwhile.
        foreach (var (draw, tilt) in poseTilts) if (tilt.GameObject == (nint)npc) poseTilts.TryRemove(draw, out _);
        // No name: a parked object is no one to Glamourer or Penumbra (its next NPC writes its own).
        if (npc->GameObject.Name[0] != 0) npc->GameObject.Name.Clear();
        if (npc->GameObject.DrawObject != null) npc->GameObject.DisableDraw();
        if (npc->Timeline.BaseOverride != 0) npc->Timeline.BaseOverride = 0;
        npc->GameObject.TargetableStatus &= ~ObjectTargetableFlags.IsTargetable;
        if (npc->GameObject.Position.Y > ParkedAt.Y + 1) npc->GameObject.SetPosition(ParkedAt.X, ParkedAt.Y, ParkedAt.Z);
    }

    // Every target the game keeps (target, soft, focus, mouse-over, previous…) let go of a character before it is
    // parked or deleted, so none points at it afterwards.
    private static unsafe void Untarget(GameObject* npc)
    {
        var targets = TargetSystem.Instance();
        if (targets == null) return;
        if (targets->Target == npc) targets->Target = null;
        if (targets->SoftTarget == npc) targets->SoftTarget = null;
        if (targets->GPoseTarget == npc) targets->GPoseTarget = null;
        if (targets->MouseOverTarget == npc) targets->MouseOverTarget = null;
        if (targets->MouseOverNameplateTarget == npc) targets->MouseOverNameplateTarget = null;
        if (targets->FocusTarget == npc) targets->FocusTarget = null;
        if (targets->PreviousTarget == npc) targets->PreviousTarget = null;
    }

    /// <summary>
    /// How many of these slots still hold a character with the name Haven gave it (read only): logged when Haven drops
    /// its NPC handles on a zone change, to learn whether the game really removes client-made characters then.
    /// </summary>
    public unsafe int HousingNpcSurvivors(IEnumerable<(uint Slot, string GameName)> npcs)
    {
        var count = 0;
        foreach (var (slot, name) in npcs)
        {
            var npc = NpcIn(slot);
            if (npc != null && npc->GameObject.NameString == name) count++;
        }
        return count;
    }

    /// <summary>Deletes every character made here: only when the visit ends (see above).</summary>
    public unsafe void HousingNpcDeleteAll(IEnumerable<uint> slots)
    {
        var manager = ClientObjectManager.Instance();
        if (manager == null) return;
        foreach (var slot in slots)
        {
            if (NpcIn(slot) == null) continue;
            HousingNpcPark(slot);
            manager->DeleteObjectByIndex((ushort)slot, 0);
            log.Information("[NPC] deleted slot {Slot}.", slot);
        }
        cameraNpc = null;
    }

    /// <summary>The local player's look as drawn (customize and gear), to save on an NPC.</summary>
    public unsafe Haven.Core.NpcLook? HousingPlayerLook() => LookOf((Character*)(objectTable.LocalPlayer?.Address ?? 0));

    /// <summary>A character made here's look as its game data holds it (customize, gear, weapons).</summary>
    public unsafe Haven.Core.NpcLook? HousingNpcLook(uint slot) => LookOf(NpcIn(slot));

    private static unsafe Haven.Core.NpcLook? LookOf(Character* player)
    {
        if (player == null) return null;
        var customize = player->DrawData.CustomizeData.Data.ToArray();
        var gear = player->DrawData.EquipmentModelIds;
        var values = new ulong[Haven.Core.NpcLook.GearSlots];
        for (var i = 0; i < values.Length && i < gear.Length; i++) values[i] = gear[i].Value & ((1UL << 40) - 1);
        var weapons = new ulong[Haven.Core.NpcLook.WeaponSlots];
        for (var i = 0; i < weapons.Length && i < player->DrawData.WeaponData.Length; i++) weapons[i] = player->DrawData.WeaponData[i].ModelId.Value;
        return customize.Length == Haven.Core.NpcLook.CustomizeBytes ? new Haven.Core.NpcLook { Customize = customize, Gear = values, Weapons = weapons } : null;
    }

    /// <summary>Where a character is and which way it faces.</summary>
    public unsafe (System.Numerics.Vector3 Position, float Yaw)? HousingNpcPlace(uint slot)
    {
        var npc = NpcIn(slot);
        if (npc == null) return null;
        return (npc->GameObject.Position, npc->GameObject.Rotation);
    }

    /// <summary>
    /// Moves a character and sets its legs: 0 idle, otherwise a clip held as its base animation (a character the game
    /// makes has no movement of its own, so its legs only move when told). The clip is played only when
    /// <paramref name="start"/> (the caller's own record says it changed) and held every frame after: the game lets go
    /// of a short clip (the jump's take-off) when it ends, and reading that back as "changed" replayed it again and
    /// again, while playing it once without holding it snapped the character back to standing.
    /// </summary>
    public unsafe void HousingMoveNpc(uint slot, System.Numerics.Vector3 position, float yaw, ushort hold, ushort play)
    {
        var npc = NpcIn(slot);
        if (npc == null) return;
        npc->GameObject.SetPosition(position.X, position.Y, position.Z);
        npc->GameObject.SetRotation(yaw);
        // The base is written first, so a clip started now plays over it and the base follows when it ends.
        if (npc->Timeline.BaseOverride != hold) npc->Timeline.BaseOverride = hold;
        if (play != 0) npc->Timeline.TimelineSequencer.PlayTimeline(play);
    }

    /// <summary>The character's base override, its first three slots' timelines and its drawn height, for the jump trace.</summary>
    public unsafe string HousingNpcTimelines(uint slot)
    {
        var npc = NpcIn(slot);
        if (npc == null) return "gone";
        var sequencer = &npc->Timeline.TimelineSequencer;
        var drawn = npc->GameObject.DrawObject == null ? float.NaN : npc->GameObject.DrawObject->Object.Position.Y;
        return $"base {npc->Timeline.BaseOverride}, slots {sequencer->GetSlotTimeline(0)}/{sequencer->GetSlotTimeline(1)}/{sequencer->GetSlotTimeline(2)}, y {npc->GameObject.Position.Y:0.00} drawn {drawn:0.00}";
    }

    /// <summary>Whether the game itself shows a nameplate for a character made here (then Haven draws none).</summary>
    public unsafe bool HousingNpcHasNameplate(uint slot)
    {
        var npc = NpcIn(slot);
        var module = FFXIVClientStructs.FFXIV.Client.UI.UIModule.Instance();
        var ui = module == null ? null : module->GetUI3DModule();
        if (npc == null || ui == null) return false;
        foreach (var info in ui->NamePlateObjectInfoPointers)
            if (info.Value != null && info.Value->GameObject == (GameObject*)npc) return true;
        return false;
    }

    /// <summary>Plays a one-shot animation over the character's base (a landing, an emote).</summary>
    public unsafe void HousingNpcPlay(uint slot, ushort timeline)
    {
        var npc = NpcIn(slot);
        if (npc != null && timeline != 0) npc->Timeline.TimelineSequencer.PlayTimeline(timeline);
    }

    /// <summary>The object-table index of a character made here (Penumbra, Glamourer and the typing bubble key on it).</summary>
    public static int HousingNpcObjectIndex(uint slot) => (int)slot + ClientObjectBase;

    /// <summary>The character's height as drawn (its model's), or null when it isn't drawn.</summary>
    public unsafe float? HousingNpcHeight(uint slot)
    {
        var npc = NpcIn(slot);
        if (npc == null || npc->GameObject.DrawObject == null) return null;
        var height = npc->GameObject.GetHeight();
        return float.IsFinite(height) && height is > 0 and < 10 ? height : null;
    }

    /// <summary>Makes a character made here the game's target (the target bar shows its name; emotes aim at it).</summary>
    public unsafe void HousingTargetNpc(uint slot)
    {
        var npc = NpcIn(slot);
        var targets = TargetSystem.Instance();
        if (npc != null && targets != null && packetFilter.IsActive) targets->Target = (GameObject*)npc;
    }

    /// <summary>Clears the game's target if it is this character; true when it was.</summary>
    public unsafe bool HousingNpcUntarget(uint slot)
    {
        var npc = NpcIn(slot);
        var targets = TargetSystem.Instance();
        if (npc == null || targets == null || targets->Target != (GameObject*)npc) return false;
        targets->Target = null;
        return true;
    }

    /// <summary>Whether a character made here is the game's current target.</summary>
    public unsafe bool HousingNpcTargeted(uint slot)
    {
        var npc = NpcIn(slot);
        var targets = TargetSystem.Instance();
        return npc != null && targets != null && targets->Target == (GameObject*)npc;
    }

    /// <summary>The camera's look direction along the floor, from the camera towards what it follows.</summary>
    public unsafe System.Numerics.Vector3? HousingCameraForward()
    {
        var manager = CameraManager.Instance();
        var camera = manager == null ? null : manager->GetActiveCamera();
        if (camera == null) return null;
        var scene = &camera->CameraBase.SceneCamera;
        var look = (System.Numerics.Vector3)scene->LookAtVector - (System.Numerics.Vector3)scene->Object.Position;
        look.Y = 0;
        return look.LengthSquared() < 1e-6f ? null : System.Numerics.Vector3.Normalize(look);
    }

    // The camera follows the possessed character: the game camera asks its target (vf18) every frame.
    private delegate nint CameraTargetDelegate(nint camera);
    private Hook<CameraTargetDelegate>? cameraTargetHook;
    private nint cameraTargetCamera;
    private uint? cameraNpc;

    /// <summary>Points the game camera at a character made here, or back at the player (null).</summary>
    public unsafe void HousingCameraOnNpc(uint? slot)
    {
        if (slot != null && cameraTargetHook == null)
        {
            var manager = CameraManager.Instance();
            var camera = manager == null ? null : manager->Camera;
            if (camera == null) { log.Warning("[NPC] no game camera to follow the NPC."); return; }
            try
            {
                cameraTargetCamera = (nint)camera;
                var function = ((nint*)*(nint*)camera)[18];
                cameraTargetHook = hooks.HookFromAddress<CameraTargetDelegate>(function, CameraTargetDetour);
                cameraTargetHook.Enable();
                log.Information("[NPC] camera target hooked at {Address:X}.", function);
            }
            catch (Exception ex) { log.Warning(ex, "[NPC] the camera target could not be hooked; the camera stays on you."); return; }
        }
        cameraNpc = slot;
    }

    private unsafe nint CameraTargetDetour(nint camera)
    {
        if (camera == cameraTargetCamera && cameraNpc is { } slot && packetFilter.IsActive && NpcIn(slot) is var npc && npc != null && npc->GameObject.DrawObject != null)
            return (nint)npc;
        return cameraTargetHook!.Original(camera);
    }

    private static unsafe Character* NpcIn(uint slot)
    {
        var manager = ClientObjectManager.Instance();
        if (manager == null) return null;
        var npc = (GameObject*)manager->GetObjectByIndex((ushort)slot);
        return npc != null && npc->IsCharacter() ? (Character*)npc : null;
    }
}
