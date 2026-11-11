using System.Numerics;
using Dalamud.Bindings.ImGui;
using Haven.Core;

namespace Haven;

internal sealed partial class FurnishingEditor
{
    // The free building camera: while decorating, the toolbar's camera button lets
    // the camera leave the player. It orbits a point Haven moves (NativeEngine.HousingBuildCamera) and the character
    // holds still. WASD pans along the floor (Shift faster), a middle-drag pans in the screen's plane, the wheel zooms
    // and a right-drag turns it, as the game's own camera does. The game has the mouse except while a piece is being
    // placed, so then Haven zooms and turns it itself (a right-click still sets it down; WorldEditorInput). A piece moved by
    // the keys or the inspector's fields takes the camera with it; one following the pointer doesn't, since the camera
    // moving under the pointer would move the piece again, and it would run off across the room.
    private bool freeCamera;
    private Vector3 cameraFocus;
    // Turns Haven adds to the game's own while placing (radians): around the vertical, and up or down.
    private float cameraYaw, cameraPitch;
    private Vector3? cameraFollowed;
    private bool cameraPanning;
    private float rightDrag;
    private const float PanSpeed = .9f, DragTurn = .006f, WheelZoom = .88f;

    private void ToggleFreeCamera()
    {
        if (freeCamera) { StopFreeCamera(); return; }
        if (engine.HousingCameraView() is not { } view) return;
        cameraFocus = view.LookAt; cameraYaw = cameraPitch = 0; cameraFollowed = null;
        freeCamera = engine.HousingBuildCamera(cameraFocus, 0, 0);
        if (!freeCamera) housing.Run(() => throw new InvalidOperationException("The free camera isn't available in this game version."));
    }

    private void StopFreeCamera()
    {
        if (freeCamera) engine.HousingBuildCamera(null, 0, 0);
        freeCamera = false; cameraPanning = false; cameraFollowed = null; housing.CameraHolding = false;
    }

    /// <summary>Each frame while decorating: pans, zooms and turns the free camera, and follows a piece moved by keys.</summary>
    private void UpdateFreeCamera(House? house, bool focused, bool underUi, bool placing, bool typing)
    {
        if (freeCamera && (!editing || house == null || !housing.IsReady || housing.NpcHolding)) StopFreeCamera();
        housing.CameraHolding = freeCamera;
        if (!freeCamera || house == null || engine.HousingCameraView() is not { } view) return;
        var io = ImGui.GetIO();
        var dt = Math.Clamp(io.DeltaTime, 0, .1f);
        var forward = view.LookAt - view.Position;
        var flat = Vector3.Normalize(new Vector3(forward.X, 0, forward.Z) is var f && f.LengthSquared() > 1e-6f ? f : Vector3.UnitZ);
        var right = ScreenRight(view.LookAt, flat);
        var keys = focused && !typing && !WorldEditorKeys.Ctrl && !ImGui.IsAnyItemActive() && !GameTextInput();
        // WASD along the floor, faster the further out the camera is.
        if (keys && WorldEditorKeys.Walking is var walk && (walk.Forward != 0 || walk.Strafe != 0))
        {
            var speed = Math.Clamp(view.Distance * PanSpeed, 1.5f, 12) * (WorldEditorKeys.Read().Shift ? 3 : 1);
            var move = flat * walk.Forward + right * walk.Strafe;
            cameraFocus += Vector3.Normalize(move) * speed * dt;
        }
        // Space rises and C sinks, at the same speed.
        if (keys && WorldEditorKeys.Rising is var rise && rise != 0)
        {
            var speed = Math.Clamp(view.Distance * PanSpeed, 1.5f, 12) * (WorldEditorKeys.Read().Shift ? 3 : 1);
            cameraFocus += Vector3.UnitY * rise * speed * dt;
        }
        // A middle-drag moves the view with the pointer, in the screen's plane.
        var middle = focused && WorldEditorKeys.Middle;
        if (!middle) cameraPanning = false;
        else if (!cameraPanning && !underUi && !OverGameWindow()) cameraPanning = true;
        if (cameraPanning && io.MouseDelta != Vector2.Zero && forward.LengthSquared() > 1e-6f)
        {
            var up = Vector3.Normalize(Vector3.Cross(right, Vector3.Normalize(forward)));
            if (up.Y < 0) up = -up;
            var perPixel = 2 * view.Distance * MathF.Tan(Math.Clamp(view.FieldOfView, .2f, 2f) / 2) / Math.Max(ImGui.GetMainViewport().Size.Y, 1);
            cameraFocus += (-right * io.MouseDelta.X + up * io.MouseDelta.Y) * perPixel;
        }
        if (placing && focused && !underUi)
        {
            // Haven has the mouse while placing, so the game neither zooms nor turns: the wheel and a right-drag do it here.
            if (io.MouseWheel != 0) engine.HousingZoomCamera(MathF.Pow(WheelZoom, io.MouseWheel));
            if (WorldEditorKeys.Read().Right && io.MouseDelta != Vector2.Zero)
            {
                rightDrag += io.MouseDelta.Length();
                if (rightDrag > 4) input.RightDragged();
                cameraYaw += TurnRight(view.LookAt, flat) * io.MouseDelta.X * DragTurn;
                cameraPitch = Math.Clamp(cameraPitch + io.MouseDelta.Y * DragTurn, -1.4f, 1.4f);
            }
        }
        if (!WorldEditorKeys.Read().Right) rightDrag = 0;
        // A piece moved without the pointer (keys, the inspector's fields) takes the camera along.
        if (placing && housing.Placement != null)
        {
            var at = Vector3.Transform(housing.PreviewPosition, FurnitureCatalogue.HouseMatrix(house));
            if (cameraFollowed is { } before && !followCursor && axisDrag == null) cameraFocus += at - before;
            cameraFollowed = at;
        }
        else cameraFollowed = null;
        if (!float.IsFinite(cameraFocus.LengthSquared())) cameraFocus = view.LookAt;
        engine.HousingBuildCamera(cameraFocus, cameraYaw, cameraPitch);
    }

    // The floor direction that runs to the right of the screen, found from the game's own projection.
    private Vector3 ScreenRight(Vector3 at, Vector3 flat)
    {
        var right = Vector3.Normalize(Vector3.Cross(flat, Vector3.UnitY));
        return gameGui.WorldToScreen(at, out var middle) && gameGui.WorldToScreen(at + right, out var side) && side.X < middle.X ? -right : right;
    }

    // Which way a turn about the vertical (Matrix4x4.CreateRotationY) turns the view towards the screen's right: 1 or -1.
    private float TurnRight(Vector3 at, Vector3 flat)
    {
        var turned = Vector3.Transform(flat, Matrix4x4.CreateRotationY(.1f));
        return gameGui.WorldToScreen(at + flat, out var ahead) && gameGui.WorldToScreen(at + turned, out var after) && after.X < ahead.X ? -1 : 1;
    }
}
