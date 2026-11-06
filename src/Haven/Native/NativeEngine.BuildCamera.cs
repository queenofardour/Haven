using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace HavenNative;

// The free building camera: while decorating, the world camera can orbit a point
// Haven moves instead of the player. The game's own update still runs (right-drag turns it, the wheel zooms, the
// distance eases), and afterwards its position and look-at are moved together so the look-at lands on that point, the
// way Brio's GPose camera offsets its update. While it is on, the camera's collision answers "nothing in the way" (the
// hook Ktisis uses for its no-collision cameras), so it can pull back through a wall and look into a small room.
// Brio and Ktisis (GPL-3.0) supplied the hook points; the code is Haven's. The world camera only, inside a home only.
public sealed partial class NativeEngine
{
    // The game camera's update (CameraBase vf3), taken from the world camera's own table.
    private unsafe delegate void CameraUpdateDelegate(CameraBase* camera);
    private Hook<CameraUpdateDelegate>? cameraUpdateHook;
    private nint buildCamera;
    // The camera's collision test: the distance the camera may pull back to, written to +0x218 (and +0x21C).
    private const string CameraCollideSignature = "48 8B C4 48 89 58 ?? 48 89 70 ?? 48 89 78 ?? 55 41 56 41 57 48 8D 68 ?? 48 81 EC ?? ?? ?? ?? F3 0F 58 1D";
    private unsafe delegate nint CameraCollideDelegate(nint camera, System.Numerics.Vector3* from, System.Numerics.Vector3* to, float distance, nint unknown, float radius);
    private Hook<CameraCollideDelegate>? cameraCollideHook;
    private bool buildCameraTried;
    private System.Numerics.Vector3? buildCameraFocus;
    private float buildCameraYaw, buildCameraPitch;

    /// <summary>
    /// Points the world camera at a spot of Haven's choosing (null gives it back to the player), turned further by
    /// <paramref name="yaw"/> about the vertical (Matrix4x4.CreateRotationY's sense) and raised by <paramref name="pitch"/>
    /// (radians) beyond the game's own angles. False when the camera couldn't be hooked; it then stays on the player.
    /// </summary>
    public unsafe bool HousingBuildCamera(System.Numerics.Vector3? focus, float yaw, float pitch)
    {
        if (focus != null && !buildCameraTried)
        {
            buildCameraTried = true;
            var manager = CameraManager.Instance();
            var camera = manager == null ? null : manager->Camera;
            if (camera == null) { log.Warning("[CAMERA] no game camera for the free camera."); buildCameraTried = false; return false; }
            try
            {
                buildCamera = (nint)camera;
                var update = ((nint*)*(nint*)camera)[3];
                cameraUpdateHook = hooks.HookFromAddress<CameraUpdateDelegate>(update, CameraUpdateDetour);
                cameraUpdateHook.Enable();
                log.Information("[CAMERA] free camera on the camera update at {Address:X}.", update);
            }
            catch (Exception ex) { log.Warning(ex, "[CAMERA] the camera update could not be hooked; there is no free camera."); cameraUpdateHook = null; }
            try
            {
                cameraCollideHook = hooks.HookFromSignature<CameraCollideDelegate>(CameraCollideSignature, CameraCollideDetour);
                cameraCollideHook.Enable();
            }
            catch (Exception ex) { log.Warning(ex, "[CAMERA] the camera collision could not be hooked; the free camera stops at walls."); }
        }
        buildCameraFocus = cameraUpdateHook == null ? null : focus;
        buildCameraYaw = float.IsFinite(yaw) ? yaw : 0; buildCameraPitch = float.IsFinite(pitch) ? pitch : 0;
        return focus == null || cameraUpdateHook != null;
    }

    /// <summary>Where the world camera looks and stands, and its zoom (distance, and the vertical field of view).</summary>
    public unsafe (System.Numerics.Vector3 LookAt, System.Numerics.Vector3 Position, float Distance, float FieldOfView)? HousingCameraView()
    {
        var manager = CameraManager.Instance();
        var camera = manager == null ? null : manager->Camera;
        if (camera == null) return null;
        var scene = &camera->CameraBase.SceneCamera;
        return (scene->LookAtVector, scene->Object.Position, camera->Distance, camera->FoV);
    }

    /// <summary>Zooms the world camera by a factor of its distance, within the game's own limits; the game eases to it.</summary>
    public unsafe void HousingZoomCamera(float factor)
    {
        var manager = CameraManager.Instance();
        var camera = manager == null ? null : manager->Camera;
        if (camera == null || !float.IsFinite(factor) || factor <= 0) return;
        camera->Distance = Math.Clamp(camera->Distance * factor, camera->MinDistance, camera->MaxDistance);
    }

    private unsafe void CameraUpdateDetour(CameraBase* camera)
    {
        cameraUpdateHook!.Original(camera);
        if ((nint)camera != buildCamera || buildCameraFocus is not { } focus || !packetFilter.IsActive) return;
        var scene = &camera->SceneCamera;
        System.Numerics.Vector3 look = scene->LookAtVector, position = scene->Object.Position;
        if (!float.IsFinite(look.LengthSquared()) || !float.IsFinite(position.LengthSquared())) return;
        var offset = position - look;
        if (buildCameraYaw != 0 || buildCameraPitch != 0) offset = Turned(offset, buildCameraYaw, buildCameraPitch);
        scene->Object.Position = focus + offset;
        scene->LookAtVector = focus;
    }

    // The camera's place relative to what it looks at, turned about the vertical and raised (or lowered) by an angle,
    // kept short of straight down and not far below what it looks at (there is no collision to stop it at the floor).
    private static System.Numerics.Vector3 Turned(System.Numerics.Vector3 offset, float yaw, float pitch)
    {
        var length = offset.Length();
        var level = new System.Numerics.Vector2(offset.X, offset.Z).Length();
        if (length < 1e-4f || level < 1e-4f) return offset;
        var heading = System.Numerics.Vector3.Transform(new System.Numerics.Vector3(offset.X, 0, offset.Z) / level, System.Numerics.Matrix4x4.CreateRotationY(yaw));
        var elevation = Math.Clamp(MathF.Atan2(offset.Y, level) + pitch, -.35f, 1.5f);
        return heading * (MathF.Cos(elevation) * length) + new System.Numerics.Vector3(0, MathF.Sin(elevation) * length, 0);
    }

    private unsafe nint CameraCollideDetour(nint camera, System.Numerics.Vector3* from, System.Numerics.Vector3* to, float distance, nint unknown, float radius)
    {
        // The distance fields (+0x218/+0x21C) aren't in ClientStructs: after a game patch the camera collides as usual
        // until Haven is updated (PatchGuard).
        if (camera != buildCamera || buildCameraFocus == null || !packetFilter.IsActive || !Haven.Native.PatchCheck.PatchGuard.Allows(Haven.Core.PatchFeature.BuildCamera))
            return cameraCollideHook!.Original(camera, from, to, distance, unknown, radius);
        // Nothing in the way: the camera may pull back as far as it is zoomed.
        *(float*)(camera + 0x218) = distance + .001f;
        *(float*)(camera + 0x21C) = distance + .001f;
        return 0;
    }
}
