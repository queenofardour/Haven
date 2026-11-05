namespace Haven.Core;

public readonly record struct EditorButtons(bool Left, bool Right, bool Rotate, bool Escape, bool Shift);
/// <param name="Keep">While placing: set the piece down where it is (a right-click), even without a surface under the pointer.</param>
public readonly record struct EditorCommands(bool Place, bool Cancel, float Rotation, bool Select, bool Context, bool Keep = false);

// Edge detection is independent of raycasting. A missing surface must never trap a preview.
public sealed class WorldEditorInput
{
    private EditorButtons previous;
    private bool primed;
    // A right press while placing with the free camera: a drag turns the camera, so only a release without one keeps the piece.
    private bool rightHeld, rightDragged;
    public bool LeftPressed { get; private set; }
    public bool RightPressed { get; private set; }

    /// <summary>The held right button turned the camera: letting it go doesn't set the piece down.</summary>
    public void RightDragged() { if (rightHeld) rightDragged = true; }

    /// <param name="rightTurnsCamera">The free camera is on: while placing, right-drag turns it and a right-click (a
    /// release without a drag) sets the piece down, rather than the press doing so at once.</param>
    public EditorCommands Update(EditorButtons buttons, bool focused, bool overUi, bool textInput, bool placing, bool ready, bool hasSurface, bool editing = true, bool rightTurnsCamera = false)
    {
        LeftPressed = RightPressed = false;
        if (!focused) { primed = false; rightHeld = false; previous = buttons; return default; }
        var left = primed && buttons.Left && !previous.Left;
        var right = primed && buttons.Right && !previous.Right;
        var rightReleased = primed && !buttons.Right && previous.Right;
        var rotate = primed && buttons.Rotate && !previous.Rotate;
        var escape = primed && buttons.Escape && !previous.Escape;
        LeftPressed = left; RightPressed = right;
        previous = buttons; primed = true;
        if (!placing || !rightTurnsCamera) rightHeld = false;
        if (placing)
        {
            // Only Escape throws a placement away; a right-click keeps it where it is (it used to cancel, and a move
            // made and not yet clicked down was easily lost).
            if (escape) return new(false, true, 0, false, false);
            var keep = false;
            if (!rightTurnsCamera) keep = right && !overUi;
            else
            {
                if (right && !overUi) { rightHeld = true; rightDragged = false; }
                if (rightReleased && rightHeld) { rightHeld = false; keep = !rightDragged; }
            }
            if (keep) return new(false, false, 0, false, false, Keep: true);
            return new(ready && hasSurface && left && !overUi,
                false, rotate && !textInput && ready ? (buttons.Shift ? 1 : 15) : 0, false, false);
        }
        return ready && editing && !overUi ? new(false, false, 0, left, right) : default;
    }
}
