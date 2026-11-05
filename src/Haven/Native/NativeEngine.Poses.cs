using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Lumina.Excel.Sheets;

namespace HavenNative;

// Pose balls: the local player is held at a ball and plays its emote, a carrier emote whose
// animation Penumbra serves with a custom one. Position, facing and the emote reach the gathering through the
// engine's own sync; scale does not, so Haven sets each holder's scale on every PC. Framework thread only.
public sealed partial class NativeEngine
{
    /// <summary>
    /// Puts the local player at a place, facing a way, playing a looping (or seated) emote, as a seat does: the emote's
    /// mode, then its intro and loop timelines. Peers see it through the engine's emote sync, as for a seat.
    /// </summary>
    public unsafe void HousingPlayPose(System.Numerics.Vector3 position, float yaw, ushort emoteId)
    {
        if (!packetFilter.IsActive || !zoneLoad.IsZoneLoaded || objectTable.LocalPlayer == null) throw new InvalidOperationException("Enter a private home first.");
        var row = dataManager.GetExcelSheet<Emote>().GetRowOrDefault(emoteId) ?? throw new InvalidOperationException("That emote doesn't exist in this game version.");
        var mode = row.EmoteMode.Value;
        if (mode.ConditionMode == 0) throw new InvalidOperationException("Pose balls play looping emotes only.");
        var character = (Character*)objectTable.LocalPlayer.Address;
        SanitiseLocalPosture("housing pose", evictBaseLane: true);
        character->GameObject.SetPosition(position.X, position.Y, position.Z);
        character->GameObject.SetRotation(yaw);
        character->EmoteController.EmoteId = emoteId;
        character->SetMode((CharacterModes)mode.ConditionMode, (byte)row.EmoteMode.RowId);
        // Seated emotes keep their posture (EmoteMode rows 1-3: on the ground, in a chair, dozing); others stand.
        character->EmoteController.CurrentPoseType = row.EmoteMode.RowId switch
        {
            1 => EmoteController.PoseType.GroundSit, 2 => EmoteController.PoseType.Sit, 3 => EmoteController.PoseType.Doze, _ => EmoteController.PoseType.Idle,
        };
        character->EmoteController.CPoseState = 0;
        var loop = (ushort)row.ActionTimeline[0].RowId;
        var intro = (ushort)row.ActionTimeline[1].RowId;
        character->Timeline.PlayActionTimeline(intro != 0 ? intro : loop, loop);
        stateCapture.RequestFullResend();
    }

    /// <summary>
    /// The local player's body as the game's animation paths name it (Hyur Midlander man "c0101" … Viera woman
    /// "c1801"), from their appearance; null when it can't be read.
    /// </summary>
    public unsafe string? HousingBodyCode
    {
        get
        {
            if (objectTable.LocalPlayer == null) return null;
            var customize = ((Character*)objectTable.LocalPlayer.Address)->DrawData.CustomizeData;
            int? pair = customize.Race switch { 1 => customize.Tribe == 2 ? 3 : 1, 2 => 5, 3 => 11, 4 => 7, 5 => 9, 6 => 13, 7 => 15, 8 => 17, _ => null };
            return pair is int first ? $"c{first + (customize.Sex == 1 ? 1 : 0):00}01" : null;
        }
    }

    /// <summary>Whether the local player is still in an emote (none moved them out of it).</summary>
    public unsafe bool HousingPosePlaying(ushort emoteId)
    {
        if (objectTable.LocalPlayer == null) return false;
        var character = (Character*)objectTable.LocalPlayer.Address;
        return character->EmoteController.EmoteId == emoteId && character->Mode != CharacterModes.Normal;
    }

    /// <summary>Holds the local player at a place and facing (each frame while on a ball); writes only when they differ.</summary>
    public unsafe void HousingHoldPose(System.Numerics.Vector3 position, float yaw)
    {
        if (!packetFilter.IsActive || objectTable.LocalPlayer == null) return;
        var obj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)objectTable.LocalPlayer.Address;
        var now = new System.Numerics.Vector3(obj->Position.X, obj->Position.Y, obj->Position.Z);
        if (System.Numerics.Vector3.DistanceSquared(now, position) > 1e-6f) obj->SetPosition(position.X, position.Y, position.Z);
        var turn = MathF.IEEERemainder(obj->Rotation - yaw, MathF.Tau);
        if (MathF.Abs(turn) > 1e-4f) obj->SetRotation(yaw);
    }

    // The movement controller's "may the player turn" check (MoveControllerSubMemberForMine; vnavmesh's
    // IsInputEnabled2). Every player-driven turn asks it first: the right mouse button turning with the camera, the turn
    // keys, facing the target. While it says no the game cancels its own pending turn, as for a locked character.
    // Haven says no while the local player is held on a pose ball; the camera itself still turns.
    private const string PoseCanTurnSignature = "40 53 48 83 EC 20 48 8B D9 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 84 C0 0F 85 ?? ?? ?? ?? 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 84 C0 0F 84 ?? ?? ?? ?? 48 8B CB E8 ?? ?? ?? ?? 84 C0 75 ?? 48 8B 43 20 48 8B 88";
    private delegate byte PoseCanTurnDelegate(nint controller);
    private Dalamud.Hooking.Hook<PoseCanTurnDelegate>? poseCanTurnHook;
    private bool poseCanTurnHookTried;
    private volatile bool poseFacingHeld;

    /// <summary>Stops the game turning the local player (camera, keys, facing a target) while held on a ball.</summary>
    public void HousingHoldFacing(bool hold)
    {
        if (hold) { EnsurePoseCanTurnHook(); EnsureMoveInputHook(); }
        poseFacingHeld = hold;
    }

    // The game's movement input (vnavmesh's RMIWalk; the only call site of this pattern in the game): it sums the keys,
    // the both-mouse-buttons run and autorun into a sideways and a forward amount. While the player is held (a pose
    // ball, an arcade machine, an NPC they possess), Haven zeroes the sums after the game's own reading, so nothing
    // walks the held character: holding the keys back (HoldPoseKeys) left the two mouse buttons running it forward.
    private const string MoveInputSignature = "E8 ?? ?? ?? ?? 80 7B 3E 00 48 8D 3D";
    private unsafe delegate void MoveInputDelegate(nint self, float* sumLeft, float* sumForward, float* sumTurnLeft, byte* haveBackwardOrStrafe, byte* unknown, byte additive);
    private Dalamud.Hooking.Hook<MoveInputDelegate>? moveInputHook;
    private bool moveInputHookTried;

    private unsafe void EnsureMoveInputHook()
    {
        if (moveInputHookTried) return;
        moveInputHookTried = true;
        try
        {
            moveInputHook = hooks.HookFromSignature<MoveInputDelegate>(MoveInputSignature, MoveInputDetour);
            moveInputHook.Enable();
            log.Information("[POSE] movement hold on the movement input at {Address:X}.", moveInputHook.Address);
        }
        catch (Exception ex) { log.Warning(ex, "[POSE] The movement input could not be hooked; both mouse buttons can still walk a held player."); }
    }

    private unsafe void MoveInputDetour(nint self, float* sumLeft, float* sumForward, float* sumTurnLeft, byte* haveBackwardOrStrafe, byte* unknown, byte additive)
    {
        moveInputHook!.Original(self, sumLeft, sumForward, sumTurnLeft, haveBackwardOrStrafe, unknown, additive);
        if (!poseFacingHeld || !packetFilter.IsActive) return;
        if (sumLeft != null) *sumLeft = 0;
        if (sumForward != null) *sumForward = 0;
        if (sumTurnLeft != null) *sumTurnLeft = 0;
        if (haveBackwardOrStrafe != null) *haveBackwardOrStrafe = 0;
    }

    private void EnsurePoseCanTurnHook()
    {
        if (poseCanTurnHookTried) return;
        poseCanTurnHookTried = true;
        try
        {
            poseCanTurnHook = hooks.HookFromSignature<PoseCanTurnDelegate>(PoseCanTurnSignature, PoseCanTurnDetour);
            poseCanTurnHook.Enable();
            log.Information("[POSE] facing hold on the movement turn check at {Address:X}.", poseCanTurnHook.Address);
        }
        catch (Exception ex) { log.Warning(ex, "[POSE] The movement turn check could not be hooked; the camera can still turn a posed player."); }
    }

    private byte PoseCanTurnDetour(nint controller)
    {
        var allowed = poseCanTurnHook!.Original(controller);
        return poseFacingHeld && packetFilter.IsActive ? (byte)0 : allowed;
    }

    /// <summary>Takes the local player out of a pose (back to standing), as leaving a seat does.</summary>
    public void HousingLeavePose() => HousingStandUp();

    /// <summary>
    /// Stands a gathering participant up on this PC (not the local player): out of a looping emote back to normal,
    /// as the return home does for every peer. True if they were in one.
    /// </summary>
    public unsafe bool HousingStandPeer(ulong contentId)
    {
        if (contentId == LocalContentId()) return false;
        var character = PoseCharacter(contentId);
        if (character == null || character->Mode is not (FFXIVClientStructs.FFXIV.Client.Game.Character.CharacterModes.EmoteLoop
            or FFXIVClientStructs.FFXIV.Client.Game.Character.CharacterModes.InPositionLoop)) return false;
        character->EmoteController.EmoteId = 0;
        character->SetMode(FFXIVClientStructs.FFXIV.Client.Game.Character.CharacterModes.Normal, 0);
        character->Timeline.BaseOverride = 0;
        character->GameObject.SetDrawOffset(0, 0, 0);
        character->Timeline.TimelineSequencer.PlayTimeline(3);
        return true;
    }

    // Each character's own scale before a pose changed it, to give back when they leave the ball.
    private readonly Dictionary<ulong, float> poseScales = [];

    /// <summary>
    /// Sets a character's drawn scale (the local player or a gathering participant, by content ID), or gives back
    /// their own with null. Only the drawn model changes: collision, position and the server never see it.
    /// </summary>
    public unsafe void HousingPoseScale(ulong contentId, float? scale)
    {
        var character = PoseCharacter(contentId);
        if (character == null || character->GameObject.DrawObject == null) { if (scale == null) poseScales.Remove(contentId); return; }
        var draw = character->GameObject.DrawObject;
        if (scale is not float value)
        {
            if (poseScales.Remove(contentId, out var own)) { draw->Object.Scale = new(own, own, own); ((FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)draw)->IsTransformChanged = true; }
            return;
        }
        if (!poseScales.ContainsKey(contentId)) poseScales[contentId] = draw->Object.Scale.X is > 0 and < 10 ? draw->Object.Scale.X : 1;
        if (MathF.Abs(draw->Object.Scale.X - value) > 1e-4f) { draw->Object.Scale = new(value, value, value); ((FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)draw)->IsTransformChanged = true; }
    }

    // Characters whose drawn rotation a pose tilted, to put upright again when they leave the ball.
    private readonly HashSet<ulong> poseTilted = [];
    // The tilt to draw each tilted character with, by its draw object: its game object (for the facing) and the tilt
    // and lean in degrees, refreshed every frame (an entry not refreshed lately is dropped, so a draw object reused
    // for something else is never turned).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<nint, (nint GameObject, float Pitch, float Roll, long Frame)> poseTilts = new();
    private long poseTiltFrame;
    // CharacterBase's per-frame copy of its scene transform (Object position, rotation, scale) into its skeleton: virtual
    // function 98 (+0x310), called by CharacterBase.UpdateRender after the game objects' updates. The tilt goes on the
    // copied rotation here, after every writer of Object.Rotation this frame (the game, the engine's sync, SimpleHeels,
    // which rebuilds it each frame for characters it manages), and before physics and the bones.
    private const string PoseSkeletonSignature = "48 83 EC 58 48 8B 81 ?? ?? ?? ?? 48 85 C0 0F 84 ?? ?? ?? ?? F3 0F 10 99 ?? ?? ?? ?? 48 8D 54 24";
    private unsafe delegate void PoseSkeletonDelegate(FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase* self);
    private Dalamud.Hooking.Hook<PoseSkeletonDelegate>? poseTransformHook;
    private bool poseTransformHookTried;

    /// <summary>
    /// Tilts and leans a character's drawn model on its ball (degrees, about its own right and forward axes, pivoting
    /// at the feet), or stands it upright again. Only the drawn skeleton turns: the game keeps the facing, which the
    /// engine's sync carries; the tilt reaches others in the pose entries. Applied each frame to the skeleton after the
    /// game copies the character's transform into it (a hook installed the first time a tilt is needed).
    /// </summary>
    public unsafe void HousingPoseTilt(ulong contentId, float pitch, float roll, bool upright = false)
    {
        var character = PoseCharacter(contentId);
        if (character == null || character->GameObject.DrawObject == null) { if (upright) poseTilted.Remove(contentId); return; }
        var draw = character->GameObject.DrawObject;
        // Upright: the next copy is the game's own.
        if (upright || pitch == 0 && roll == 0) { poseTilts.TryRemove((nint)draw, out _); poseTilted.Remove(contentId); return; }
        poseTilted.Add(contentId);
        EnsurePoseTransformHook();
        poseTilts[(nint)draw] = ((nint)character, pitch, roll, poseTiltFrame);
    }

    /// <summary>Called once a frame: forgets tilts nobody refreshed (a character who left, or a draw object rebuilt).</summary>
    public void HousingPoseTiltFrame()
    {
        poseTiltFrame++;
        foreach (var (draw, tilt) in poseTilts)
            if (poseTiltFrame - tilt.Frame > 2) poseTilts.TryRemove(draw, out _);
    }

    private unsafe void EnsurePoseTransformHook()
    {
        if (poseTransformHookTried) return;
        poseTransformHookTried = true;
        try
        {
            poseTransformHook = hooks.HookFromSignature<PoseSkeletonDelegate>(PoseSkeletonSignature, PoseSkeletonDetour);
            poseTransformHook.Enable();
            log.Information("[POSE] tilt hook on the character skeleton transform at {Address:X}.", poseTransformHook.Address);
        }
        catch (Exception ex) { log.Warning(ex, "[POSE] The character skeleton transform could not be hooked; tilt and lean are unavailable."); }
    }

    private unsafe void PoseSkeletonDetour(FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase* self)
    {
        poseTransformHook!.Original(self);
        try
        {
            if (poseTilts.IsEmpty || !poseTilts.TryGetValue((nint)self, out var tilt)) return;
            var owner = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)tilt.GameObject;
            var skeleton = self->Skeleton;
            if (skeleton == null || owner->DrawObject != (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.DrawObject*)self) return;
            // The copied rotation (yaw, and any pitch or roll another plugin put there), then the tilt in the body's own frame.
            System.Numerics.Quaternion copied = skeleton->Transform.Rotation;
            skeleton->Transform.Rotation = System.Numerics.Quaternion.Normalize(copied * Haven.Core.PoseTilt.Rotation(0, tilt.Pitch, tilt.Roll));
        }
        catch { }
    }

    /// <summary>
    /// The lowest joint of a character's animated body in the world (from its Havok pose, the body skeleton only), or
    /// null while it has none. Joints sit inside the body, so the true surface is a little lower.
    /// </summary>
    /// <summary>
    /// A posed character's lowest joint in the world, and where its body's root is drawn this frame (the two read
    /// together, so the joint's depth below the root holds even while the character is being moved).
    /// </summary>
    public unsafe (System.Numerics.Vector3 Joint, System.Numerics.Vector3 Root)? HousingPoseLowestJoint(ulong contentId)
    {
        var character = PoseCharacter(contentId);
        if (character == null || character->GameObject.DrawObject == null) return null;
        var body = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)character->GameObject.DrawObject;
        var skeleton = body->Skeleton;
        if (skeleton == null || skeleton->PartialSkeletonCount == 0 || skeleton->PartialSkeletons == null) return null;
        var pose = skeleton->PartialSkeletons[0].GetHavokPose(0);
        if (pose == null) return null;
        var model = pose->GetSyncedPoseModelSpace();
        if (model == null || model->Data == null || model->Length is <= 0 or > 1024) return null;
        System.Numerics.Vector3 position = skeleton->Transform.Position, scale = skeleton->Transform.Scale;
        System.Numerics.Quaternion rotation = skeleton->Transform.Rotation;
        System.Numerics.Vector3? lowest = null;
        for (var i = 0; i < model->Length; i++)
        {
            var t = model->Data[i].Translation;
            var world = position + System.Numerics.Vector3.Transform(new System.Numerics.Vector3(t.X, t.Y, t.Z) * scale, rotation);
            if (float.IsFinite(world.Y) && (lowest == null || world.Y < lowest.Value.Y)) lowest = world;
        }
        return lowest is { } joint ? (joint, position) : null;
    }

    /// <summary>The first surface straight below a point (the room's collision, Haven's furniture included), within a distance.</summary>
    public static System.Numerics.Vector3? HousingSurfaceBelow(System.Numerics.Vector3 from, float distance)
    {
        if (!FFXIVClientStructs.FFXIV.Common.Component.BGCollision.BGCollisionModule.RaycastMaterialFilter(from, -System.Numerics.Vector3.UnitY, out var hit, distance)) return null;
        System.Numerics.Vector3 point = hit.Point;
        return float.IsFinite(point.Y) ? point : null;
    }

    /// <summary>The local player's content ID and those of the gathering's participants drawn here, with their positions.</summary>
    public unsafe void HousingPoseCharacters(List<(ulong ContentId, System.Numerics.Vector3 Position)> characters)
    {
        characters.Clear();
        if (!packetFilter.IsActive || !zoneLoad.IsZoneLoaded) return;
        if (objectTable.LocalPlayer is { } local) characters.Add((LocalContentId(), local.Position));
        foreach (var (peer, actor) in stateApply.Peers)
            if (relay.HousingMembers.ContainsKey(peer) && actor.ObjectIndex is { } index && objectTable[(int)index] is Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter player
                && ((Character*)player.Address)->ContentId == actor.ContentId)
                characters.Add((actor.ContentId, player.Position));
    }

    /// <summary>
    /// Redraws the local player and the gathering's participants through Penumbra, so a carrier emote's animation
    /// that changed reloads (a character keeps the animations it has bound until it is redrawn).
    /// </summary>
    public unsafe void HousingRedrawPoseCharacters()
    {
        if (zoneLoad.RedrawThroughPenumbra == null || !packetFilter.IsActive) return;
        var indices = new List<ushort>();
        if (objectTable.LocalPlayer is { } local) indices.Add(local.ObjectIndex);
        foreach (var (peer, actor) in stateApply.Peers)
            if (relay.HousingMembers.ContainsKey(peer) && actor.ObjectIndex is { } index) indices.Add(index);
        // NPCs too: they play carriers on balls (Haven draws them again itself once Penumbra has hidden them).
        foreach (var slot in poseNpcSlots.Values) indices.Add((ushort)HousingNpcObjectIndex(slot));
        foreach (var index in indices)
            try { zoneLoad.RedrawThroughPenumbra(index); }
            catch (Exception ex) { log.Warning(ex, "[Haven] A character could not be redrawn for a new animation."); }
    }

    /// <summary>A gathering participant's character object on this PC (not the local player), or 0.</summary>
    // A message's sender is a player, never one of the home's NPCs (whose pose keys carry PoseNpcs' mark).
    public unsafe nint HousingPeerCharacter(ulong contentId) => contentId == 0 || contentId == LocalContentId() || Haven.Core.PoseNpcs.IsNpc(contentId) ? 0 : (nint)PoseCharacter(contentId);

    // NPCs on pose balls, by their pose key (Haven.Core.PoseNpcs): the client object slot each is drawn in.
    private readonly Dictionary<ulong, uint> poseNpcSlots = [];
    /// <summary>Tells the pose code which object an NPC is drawn in (null: it isn't any more), so its scale and tilt reach it.</summary>
    public void HousingPoseNpc(ulong key, uint? slot)
    {
        if (slot is { } s) poseNpcSlots[key] = s;
        else poseNpcSlots.Remove(key);
    }
    public void HousingPoseNpcsClear() => poseNpcSlots.Clear();
    /// <summary>The object-table index an NPC is drawn at (by its pose key), or null.</summary>
    public int? HousingPoseNpcIndex(ulong key) => poseNpcSlots.TryGetValue(key, out var slot) ? HousingNpcObjectIndex(slot) : null;

    private unsafe Character* PoseCharacter(ulong contentId)
    {
        if (contentId == 0) return null;
        if (poseNpcSlots.TryGetValue(contentId, out var npcSlot)) return NpcIn(npcSlot);
        if (objectTable.LocalPlayer is { } local && LocalContentId() == contentId) return (Character*)local.Address;
        foreach (var (peer, actor) in stateApply.Peers)
            if (actor.ContentId == contentId && relay.HousingMembers.ContainsKey(peer) && actor.ObjectIndex is { } index
                && objectTable[(int)index] is Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter player && ((Character*)player.Address)->ContentId == contentId)
                return (Character*)player.Address;
        return null;
    }
}
