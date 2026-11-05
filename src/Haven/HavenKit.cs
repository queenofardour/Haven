using System.Numerics;
using Dalamud.Bindings.ImGui;
using static Haven.HavenTokens;

namespace Haven;

internal enum ButtonKind { Primary, Secondary, Ghost, Danger, Positive }
internal enum BannerKind { Info, Success, Warning, Danger }
// One drag field's frame result: the value changed, the field is held, or a drag or typed edit just ended.
internal readonly record struct FieldDrag(bool Changed, bool Active, bool Committed);

// Refined modern component set. Every control is
// a real ImGui item, so focus, keyboard navigation and hit testing stay native;
// the draw list supplies the look from HavenTokens. A label's "##suffix" is
// kept in the ID and hidden, as in ImGui.
internal static class HavenKit
{
    private static readonly (ImGuiCol, Vector4)[] ThemeColors =
    [
        (ImGuiCol.WindowBg, Window), (ImGuiCol.ChildBg, Vector4.Zero), (ImGuiCol.PopupBg, Overlay),
        (ImGuiCol.Text, Text), (ImGuiCol.TextDisabled, TextTertiary), (ImGuiCol.Border, BorderStrong),
        (ImGuiCol.FrameBg, Inset), (ImGuiCol.FrameBgHovered, Inset), (ImGuiCol.FrameBgActive, Inset),
        (ImGuiCol.Button, Raised), (ImGuiCol.ButtonHovered, Hover), (ImGuiCol.ButtonActive, Divider),
        (ImGuiCol.Header, AccentTint), (ImGuiCol.HeaderHovered, Hover), (ImGuiCol.HeaderActive, AccentTint),
        (ImGuiCol.CheckMark, Accent), (ImGuiCol.SliderGrab, Accent), (ImGuiCol.SliderGrabActive, AccentHover),
        (ImGuiCol.Separator, Divider), (ImGuiCol.ScrollbarBg, Vector4.Zero), (ImGuiCol.ScrollbarGrab, Divider),
        (ImGuiCol.ScrollbarGrabHovered, TextTertiary), (ImGuiCol.ScrollbarGrabActive, TextSecondary),
        (ImGuiCol.TitleBg, Window), (ImGuiCol.TitleBgActive, Window), (ImGuiCol.TitleBgCollapsed, Window),
        (ImGuiCol.Tab, Raised), (ImGuiCol.TabHovered, Hover), (ImGuiCol.TabActive, AccentTint),
        (ImGuiCol.TextSelectedBg, AccentRing), (ImGuiCol.ResizeGrip, Vector4.Zero), (ImGuiCol.ResizeGripHovered, AccentRing),
        (ImGuiCol.ResizeGripActive, Accent),
    ];
    private const int ThemeVars = 13;
    private static readonly Stack<HavenFonts.Scope> ThemeFonts = new();
    private static readonly Stack<bool> PanelBodies = new();

    public static void PushTheme()
    {
        foreach (var (key, color) in ThemeColors) ImGui.PushStyleColor(key, color);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Px(Space4, Space4));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Px(Space2, Space2));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Px(Space3, (FieldHeight - Size(HavenText.Body)) / 2));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, Px(RadiusWindow));
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, Px(RadiusCard));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, Px(RadiusField));
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, Px(RadiusCard));
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, Px(RadiusField));
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, Px(10));
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, Px(RadiusControl));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 1);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0);
        ThemeFonts.Push(HavenFonts.Use(HavenText.Body));
    }
    public static void PopTheme() { ThemeFonts.Pop().Dispose(); ImGui.PopStyleVar(ThemeVars); ImGui.PopStyleColor(ThemeColors.Length); }

    private static uint U(Vector4 color) => HavenUi.U(color);
    private static string Visible(string label) { var cut = label.IndexOf("##", StringComparison.Ordinal); return cut < 0 ? label : label[..cut]; }
    private static float Avail(float width) => width > 0 ? width : ImGui.GetContentRegionAvail().X + (width < 0 ? width + 1 : 0);
    // An invisible native button: the hit target, focus and navigation item. Also eases this item's hover into
    // `fade` (0 to 1 over about 0.1 s), which controls blend their hover colours with.
    private static float fade;
    private static readonly Dictionary<uint, float> Fades = [];
    private static bool Hit(string label, Vector2 size, out Vector2 min, out bool hovered, out bool held)
    {
        var id = ImGui.GetID("##" + label);
        min = ImGui.GetCursorScreenPos(); var clicked = HavenUi.Surface("##" + label, size);
        hovered = ImGui.IsItemHovered(); held = ImGui.IsItemActive();
        fade = Ease(id, hovered || held);
        return clicked;
    }
    private static float Ease(uint id, bool on)
    {
        var value = Fades.GetValueOrDefault(id); var target = on ? 1f : 0f;
        value += (target - value) * Math.Min(1, ImGui.GetIO().DeltaTime * 14);
        if (Math.Abs(target - value) < .02f) value = target;
        if (value == 0) Fades.Remove(id); else Fades[id] = value;
        return value;
    }
    private static Vector4 Lift(Vector4 rest, Vector4 hover) => Vector4.Lerp(rest, hover, fade);
    private static Vector4 Wash => Hover with { W = fade };
    private static void Center(ImDrawListPtr draw, Vector2 min, Vector2 size, Vector4 color, string text)
        => draw.AddText(min + (size - ImGui.CalcTextSize(text)) / 2, U(color), text);

    // Buttons ------------------------------------------------------------

    public static bool Button(string label, ButtonKind kind = ButtonKind.Secondary, Vector2 size = default, HavenIcon? icon = null)
    {
        using var font = HavenFonts.Use(HavenText.Strong);
        var text = Visible(label); var textWidth = ImGui.CalcTextSize(text).X; var iconWidth = icon == null ? 0 : Px(16 + Space2);
        if (size.X == 0) size.X = textWidth + iconWidth + Px(Space4 * 2); else if (size.X < 0) size.X = Avail(size.X);
        if (size.Y <= 0) size.Y = Px(ControlHeight);
        var clicked = Hit(label, size, out var min, out var hovered, out var held); var draw = ImGui.GetWindowDrawList();
        var (fill, ink, edge) = kind switch
        {
            ButtonKind.Primary => (held ? AccentPressed : Lift(Accent, AccentHover), OnAccent, Vector4.Zero),
            ButtonKind.Positive => (held ? Vector4.Lerp(Success, SuccessFill, .2f) : Lift(Success, Vector4.Lerp(Success, Vector4.One, .15f)), OnSuccess, Vector4.Zero),
            ButtonKind.Danger => (held ? Vector4.Lerp(DangerFill, Danger, .22f) : Lift(DangerFill, Vector4.Lerp(DangerFill, Danger, .12f)), Danger, Vector4.Zero),
            ButtonKind.Ghost => (held ? Divider : Wash, TextOnRaised, Vector4.Zero),
            _ => (held ? Divider : Lift(Raised, Hover), Text, Divider),
        };
        if (fill.W > 0) draw.AddRectFilled(min, min + size, U(fill), Px(RadiusControl));
        if (edge.W > 0) draw.AddRect(min, min + size, U(edge), Px(RadiusControl));
        var x = min.X + (size.X - textWidth - iconWidth) / 2;
        if (icon != null) HavenUi.DrawIcon(draw, icon.Value, new(x + Px(8), min.Y + size.Y / 2), Px(16), ink);
        draw.AddText(new(x + iconWidth, min.Y + (size.Y - ImGui.GetTextLineHeight()) / 2), U(ink), text);
        return clicked;
    }

    public static bool IconButton(string id, HavenIcon icon, string tooltip, bool active = false, float size = ControlHeight)
    {
        var box = new Vector2(Px(size));
        var clicked = Hit(id, box, out var min, out var hovered, out var held); var draw = ImGui.GetWindowDrawList();
        var fill = active ? held ? AccentPressed : Lift(Accent, AccentHover) : held ? Divider : Wash;
        if (fill.W > 0) draw.AddRectFilled(min, min + box, U(fill), Px(RadiusControl));
        HavenUi.DrawIcon(draw, icon, min + box / 2, Px(size * .47f), active ? OnAccent : TextOnRaised);
        Tooltip(tooltip); return clicked;
    }

    // A connected group of choices, such as the in-room toolbar modes.
    public static bool Segmented(string id, IReadOnlyList<string> options, ref int index)
    {
        using var font = HavenFonts.Use(HavenText.Strong);
        var pad = Px(Space1); var height = Px(ToolbarHeight); var widths = options.Select(o => ImGui.CalcTextSize(Visible(o)).X + Px(Space4 * 2)).ToArray();
        var origin = ImGui.GetCursorScreenPos(); var total = new Vector2(widths.Sum() + pad * (options.Count + 1), height + pad * 2);
        var draw = ImGui.GetWindowDrawList(); var changed = false;
        draw.AddRectFilled(origin, origin + total, U(Window), Px(RadiusWindow)); draw.AddRect(origin, origin + total, U(BorderStrong), Px(RadiusWindow));
        var x = origin.X + pad;
        for (var i = 0; i < options.Count; i++)
        {
            ImGui.SetCursorScreenPos(new(x, origin.Y + pad)); var size = new Vector2(widths[i], height);
            if (Hit(id + "." + i, size, out var min, out var hovered, out var held) && index != i) { index = i; changed = true; }
            var selected = index == i;
            var fill = selected ? held ? AccentPressed : Accent : held ? Divider : Wash;
            if (fill.W > 0) draw.AddRectFilled(min, min + size, U(fill), Px(RadiusControl));
            Center(draw, min, size, selected ? OnAccent : TextOnRaised, Visible(options[i])); x += widths[i] + pad;
        }
        ImGui.SetCursorScreenPos(origin); ImGui.Dummy(total); return changed;
    }

    // A rounded filter chip. Selected chips use the solid accent.
    public static bool Pill(string label, bool selected)
    {
        using var font = HavenFonts.Use(HavenText.Small);
        var text = Visible(label); var size = new Vector2(ImGui.CalcTextSize(text).X + Px(Space3 * 2), Px(PillHeight));
        var clicked = Hit(label, size, out var min, out var hovered, out var held); var draw = ImGui.GetWindowDrawList();
        var fill = selected ? held ? AccentPressed : Accent : held ? Divider : Lift(Raised, Hover);
        draw.AddRectFilled(min, min + size, U(fill), size.Y / 2);
        Center(draw, min, size, selected ? OnAccent : TextOnRaised, text); return clicked;
    }

    // Pills that wrap onto further lines; returns true when the choice changes.
    public static bool PillRow(string id, IReadOnlyList<string> options, ref int index, float width = -1)
    {
        var right = ImGui.GetCursorScreenPos().X + Avail(width); var changed = false;
        for (var i = 0; i < options.Count; i++)
        {
            if (i > 0)
            {
                var pill = HavenFonts.Measure(HavenText.Small, Visible(options[i])).X + Px(Space3 * 2);
                ImGui.SameLine(0, Px(6)); if (ImGui.GetCursorScreenPos().X + pill > right) ImGui.NewLine();
            }
            if (Pill(options[i] + "##" + id + "." + i, index == i) && index != i) { index = i; changed = true; }
        }
        return changed;
    }

    // Read-only status chip, such as "Hosting · 2 guests".
    public static void Badge(string text, Vector4 dot)
    {
        using var font = HavenFonts.Use(HavenText.Caption);
        var size = new Vector2(ImGui.CalcTextSize(text).X + Px(Space3 + 9 + 6), Px(24)); var min = ImGui.GetCursorScreenPos(); var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, min + size, U(Inset with { W = .82f }), size.Y / 2);
        draw.AddCircleFilled(min + new Vector2(Px(9 + 3), size.Y / 2), Px(3), U(dot));
        draw.AddText(min + new Vector2(Px(9 + 6 + 6), (size.Y - ImGui.GetTextLineHeight()) / 2), U(Text), text);
        ImGui.Dummy(size);
    }

    // Inputs -------------------------------------------------------------

    private static void FieldLabel(string label) { if (label.Length > 0) { using (HavenFonts.Use(HavenText.Small)) ImGui.TextColored(TextSecondary, label); } }
    private static void PushField() { ImGui.PushStyleColor(ImGuiCol.Border, BorderStrong); ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1); ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, Px(RadiusField)); }
    private static void PopField()
    {
        if (ImGui.IsItemActive()) ImGui.GetWindowDrawList().AddRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), U(Accent), Px(RadiusField));
        ImGui.PopStyleVar(2); ImGui.PopStyleColor();
    }

    public static bool Field(string id, string label, ref string value, string hint = "", float width = -1, int maxLength = 256)
    {
        FieldLabel(label); PushField();
        var changed = TextInput(id, hint, ref value, maxLength, Avail(width)); PopField(); return changed;
    }

    // A field whose result is Enter being pressed in it, for dialogs where Enter takes the primary action.
    public static bool SubmitField(string id, string label, ref string value, string hint = "", float width = -1, int maxLength = 256)
    {
        FieldLabel(label); PushField();
        var submitted = TextInput(id, hint, ref value, maxLength, Avail(width), ImGuiInputTextFlags.EnterReturnsTrue); PopField(); return submitted;
    }

    /// <summary>
    /// A field kept when it is left: Enter, Tab or a click anywhere else returns true once (no Save button needed).
    /// The value follows the typing; the caller compares it with what is stored.
    /// </summary>
    public static bool CommitField(string id, string label, ref string value, string hint = "", float width = -1, int maxLength = 256)
    {
        FieldLabel(label); PushField();
        TextInput(id, hint, ref value, maxLength, Avail(width));
        var left = ImGui.IsItemDeactivated(); PopField(); return left;
    }

    // The standalone preview cannot run native InputTextEx,
    // so it sets this to draw a field's frame and text without editing.
    internal static bool DisplayOnlyText { get; set; }
    private static bool TextInput(string id, string hint, ref string value, int maxLength, float width, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
    {
        if (!DisplayOnlyText) { ImGui.SetNextItemWidth(width); return ImGui.InputTextWithHint(id, hint, ref value, maxLength, flags); }
        var style = ImGui.GetStyle(); var min = ImGui.GetCursorScreenPos(); var size = new Vector2(width, ImGui.GetFrameHeight()); var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, min + size, ImGui.GetColorU32(ImGuiCol.FrameBg), style.FrameRounding);
        if (style.FrameBorderSize > 0) draw.AddRect(min, min + size, ImGui.GetColorU32(ImGuiCol.Border), style.FrameRounding);
        draw.AddText(min + style.FramePadding, U(value.Length > 0 ? Text : TextTertiary), value.Length > 0 ? value : hint);
        ImGui.Dummy(size); return false;
    }

    public static bool Search(string id, ref string value, string hint, float width = -1)
    {
        var size = new Vector2(Avail(width), Px(FieldHeight + 6)); var min = ImGui.GetCursorScreenPos(); var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, min + size, U(Inset), Px(RadiusControl));
        HavenUi.DrawIcon(draw, HavenIcon.Search, min + new Vector2(Px(Space3 + 7), size.Y / 2), Px(15), TextSecondary);
        ImGui.SetCursorScreenPos(min + new Vector2(Px(Space3 + 14 + Space2), 0));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero); ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0, (size.Y - ImGui.GetTextLineHeight()) / 2));
        var changed = TextInput(id, hint, ref value, 160, size.X - Px(Space3 * 2 + 14 + Space2));
        var focused = ImGui.IsItemActive(); ImGui.PopStyleVar(); ImGui.PopStyleColor();
        draw.AddRect(min, min + size, U(focused ? Accent : BorderStrong), Px(RadiusControl));
        ImGui.SetCursorScreenPos(min); ImGui.Dummy(size); return changed;
    }

    public static bool Select(string id, string label, IReadOnlyList<string> options, ref int index, float width = -1)
    {
        FieldLabel(label); PushField(); width = Avail(width); ImGui.SetNextItemWidth(width); var changed = false;
        var open = ImGui.BeginCombo(id, index >= 0 && index < options.Count ? options[index] : "", ImGuiComboFlags.NoArrowButton);
        var (min, max) = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax()); var draw = ImGui.GetWindowDrawList();
        var chevron = new Vector2(max.X - Px(Space3 + 5), (min.Y + max.Y) / 2);
        draw.AddLine(chevron + Px(-5, -2), chevron + Px(0, 3), U(TextSecondary), Px(1.6f)); draw.AddLine(chevron + Px(0, 3), chevron + Px(5, -2), U(TextSecondary), Px(1.6f));
        if (open)
        {
            for (var i = 0; i < options.Count; i++)
                if (ImGui.Selectable(options[i] + "##" + i, i == index) && i != index) { index = i; changed = true; }
            ImGui.EndCombo();
        }
        PopField(); return changed;
    }

    // A drag field with a coloured prefix, for X/Y/Z, rotation and scale.
    public static bool DragField(string id, string prefix, Vector4 prefixColor, ref float value, float speed, string format, float width = -1, float min = 0, float max = 0)
        => Drag(id, prefix, prefixColor, ref value, speed, format, width, min, max).Changed;

    public static FieldDrag Drag(string id, string prefix, Vector4 prefixColor, ref float value, float speed, string format, float width = -1, float min = 0, float max = 0)
    {
        var size = new Vector2(Avail(width), Px(FieldHeight)); var origin = ImGui.GetCursorScreenPos(); var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size, U(Inset), Px(RadiusField));
        float prefixWidth;
        using (HavenFonts.Use(HavenText.Strong))
        {
            prefixWidth = ImGui.CalcTextSize(prefix).X;
            draw.AddText(origin + new Vector2(Px(9), (size.Y - ImGui.GetTextLineHeight()) / 2), U(prefixColor), prefix);
        }
        ImGui.SetCursorScreenPos(origin + new Vector2(prefixWidth + Px(9 + 6), 0));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero); ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero); ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(Px(Space1), (size.Y - ImGui.GetTextLineHeight()) / 2));
        ImGui.SetNextItemWidth(size.X - prefixWidth - Px(9 + 6 + 6));
        var changed = ImGui.DragFloat(id, ref value, speed, min, max, format);
        var active = ImGui.IsItemActive(); var hovered = ImGui.IsItemHovered(); var committed = ImGui.IsItemDeactivatedAfterEdit();
        ImGui.PopStyleVar(); ImGui.PopStyleColor(3);
        draw.AddRect(origin, origin + size, U(active ? Accent : hovered ? Divider : BorderStrong), Px(RadiusField));
        ImGui.SetCursorScreenPos(origin); ImGui.Dummy(size); return new(changed, active, committed);
    }

    // A labelled slider with a thin track; the value prints beside the label.
    public static bool Slider(string id, string label, ref float value, float min, float max, string valueText, float width = -1)
        => Slide(id, label, ref value, min, max, valueText, width).Changed;

    public static FieldDrag Slide(string id, string label, ref float value, float min, float max, string valueText, float width = -1)
    {
        width = Avail(width); var draw = ImGui.GetWindowDrawList();
        using (HavenFonts.Use(HavenText.Small))
        {
            var top = ImGui.GetCursorScreenPos();
            draw.AddText(top, U(TextSecondary), label);
            draw.AddText(top + new Vector2(width - ImGui.CalcTextSize(valueText).X, 0), U(TextOnRaised), valueText);
            ImGui.Dummy(new(width, ImGui.GetTextLineHeight()));
        }
        var height = Px(20); var origin = ImGui.GetCursorScreenPos();
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero); ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero); ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, Vector4.Zero); ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, Vector4.Zero); ImGui.PushStyleColor(ImGuiCol.Text, Vector4.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0, (height - ImGui.GetTextLineHeight()) / 2));
        ImGui.SetNextItemWidth(width);
        var changed = ImGui.SliderFloat(id, ref value, min, max, "", ImGuiSliderFlags.NoInput);
        var active = ImGui.IsItemActive(); var committed = ImGui.IsItemDeactivatedAfterEdit();
        var hovered = ImGui.IsItemHovered() || active; ImGui.PopStyleVar(); ImGui.PopStyleColor(6);
        var knob = Px(7); var t = max > min ? Math.Clamp((value - min) / (max - min), 0, 1) : 0;
        var y = origin.Y + ImGui.GetItemRectSize().Y / 2; var left = origin.X + knob; var right = origin.X + width - knob; var at = left + (right - left) * t;
        draw.AddRectFilled(new(left, y - Px(2)), new(right, y + Px(2)), U(Divider), Px(2));
        draw.AddRectFilled(new(left, y - Px(2)), new(at, y + Px(2)), U(Accent), Px(2));
        if (hovered) draw.AddCircleFilled(new(at, y), knob + Px(4), U(AccentRing), 24);
        draw.AddCircleFilled(new(at, y), knob, U(Text), 24); draw.AddCircle(new(at, y), knob, U(Accent), 24, Px(2));
        return new(changed, active, committed);
    }

    // A labelled on/off switch; the whole row is the hit target.
    public static bool Toggle(string label, ref bool value, float width = -1)
    {
        var size = new Vector2(Avail(width), Px(Math.Max(FieldHeight - 6, Size(HavenText.Body) + 10)));
        var clicked = Hit(label, size, out var min, out _, out _); var draw = ImGui.GetWindowDrawList();
        if (clicked) value = !value;
        var hover = fade;
        // The knob slides and the track fills over the same eased step as hover.
        var on = Ease(ImGui.GetID("##" + label + ".on"), value);
        draw.AddText(min + new Vector2(0, (size.Y - ImGui.GetTextLineHeight()) / 2), U(Text), Visible(label));
        var track = Px(34, 20); var at = new Vector2(min.X + size.X - track.X, min.Y + (size.Y - track.Y) / 2);
        var off = Vector4.Lerp(Divider, TextTertiary, hover); var lit = Vector4.Lerp(Accent, AccentHover, hover);
        draw.AddRectFilled(at, at + track, U(Vector4.Lerp(off, lit, on)), track.Y / 2);
        var knob = new Vector2(at.X + track.Y / 2 + (track.X - track.Y) * on, at.Y + track.Y / 2);
        draw.AddCircleFilled(knob, Px(8), U(Vector4.Lerp(Text, OnAccent, on)), 20);
        return clicked;
    }

    public static bool Swatch(string id, Vector4 color, bool selected, string tooltip)
    {
        var size = new Vector2(Px(22)); var clicked = Hit(id, size, out var min, out var hovered, out _); var draw = ImGui.GetWindowDrawList();
        var center = min + size / 2;
        draw.AddCircleFilled(center, size.X / 2, U(color), 24);
        draw.AddCircle(center, size.X / 2 - Px(selected ? 1 : .5f), U(selected ? Accent : hovered ? TextTertiary : Divider), 24, Px(selected ? 2 : 1));
        Tooltip(tooltip); return clicked;
    }

    // Surfaces -----------------------------------------------------------

    // A selectable card frame. The caller draws its contents from the returned
    // top-left corner after this call.
    public static bool Card(string id, Vector2 size, bool selected, out Vector2 min)
    {
        var clicked = Hit(id, size, out min, out var hovered, out var held); var draw = ImGui.GetWindowDrawList();
        if (selected) draw.AddRect(min - Px(2, 2), min + size + Px(2, 2), U(AccentRing), Px(RadiusCard + 2), ImDrawFlags.None, Px(3));
        draw.AddRectFilled(min, min + size, U(held ? Hover : Lift(HavenTokens.Card, Vector4.Lerp(HavenTokens.Card, Hover, .5f))), Px(RadiusCard));
        draw.AddRect(min, min + size, U(selected ? Accent : Border), Px(RadiusCard));
        return clicked;
    }

    // green: a row marked as the player's own (a custom song in a playlist), tinted green whether selected or not.
    public static bool ListRow(string id, string title, string detail, bool selected, HavenIcon? icon = null, float width = -1, bool green = false)
    {
        var size = new Vector2(Avail(width), Px(detail.Length > 0 ? 58 : RowHeight));
        var clicked = Hit(id, size, out var min, out var hovered, out var held); var draw = ImGui.GetWindowDrawList();
        var fill = green ? SuccessFill with { W = selected || held ? 1 : .75f } : selected ? AccentTint : held ? Divider : Wash;
        if (fill.W > 0) draw.AddRectFilled(min, min + size, U(fill), Px(RadiusControl));
        if (selected || green) draw.AddRect(min, min + size, U(green ? Success with { W = selected ? 1 : .45f } : Accent), Px(RadiusControl));
        var x = min.X + Px(Space3);
        if (icon != null) { HavenUi.DrawIcon(draw, icon.Value, new(x + Px(9), min.Y + size.Y / 2), Px(17), green ? Success : selected ? Accent : TextSecondary); x += Px(18 + Space3); }
        draw.PushClipRect(min, min + size - new Vector2(Px(Space3), 0), true);
        if (detail.Length == 0) draw.AddText(new(x, min.Y + (size.Y - ImGui.GetTextLineHeight()) / 2), U(Text), title);
        else
        {
            draw.AddText(new(x, min.Y + Px(9)), U(Text), title);
            using (HavenFonts.Use(HavenText.Caption)) draw.AddText(new(x, min.Y + Px(32)), U(TextTertiary), detail);
        }
        draw.PopClipRect(); return clicked;
    }

    /// <summary>
    /// Keeps the window just begun in front of other plugins' windows (Chat 2 and the like, which come to the front
    /// whenever they are clicked and then cover Haven's toolbar, panels and placement bar). Only the drawing order
    /// changes: keyboard focus stays where it is, so typing in a chat window goes on working. Skipped while any popup
    /// is open, since a popup must stay above the window that opened it.
    /// </summary>
    public static void KeepOnTop()
    {
        if (ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)) return;
        ImGuiP.BringWindowToDisplayFront(ImGuiP.GetCurrentWindow());
    }

    // A floating in-room panel with a title row and a scrolling body, such as the
    // Furnish drawer or the inspector. Always pair with EndPanel.
    public static bool BeginPanel(string title, string id, Vector2 position, Vector2 size, ref bool open, Vector2 pivot = default, bool closable = true, ImGuiCond placement = ImGuiCond.Always)
    {
        ImGui.SetNextWindowPos(position, placement, pivot); ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Window with { W = .96f });
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, Px(RadiusDrawer)); ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        var visible = ImGui.Begin(title + "###" + id, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.PopStyleVar(2); ImGui.PopStyleColor();
        if (!visible) { PanelBodies.Push(false); return false; }
        KeepOnTop();
        var min = ImGui.GetWindowPos(); var full = ImGui.GetWindowSize(); var shadow = ImGui.GetBackgroundDrawList();
        for (var i = 1; i <= 4; i++) shadow.AddRectFilled(min - Px(i * 2, i * 2 - 6), min + full + Px(i * 2, i * 3 + 4), U(Shadow with { W = .08f }), Px(RadiusDrawer + i * 2));
        var header = Px(56);
        using (HavenFonts.Use(HavenText.Title))
            ImGui.GetWindowDrawList().AddText(min + new Vector2(Px(18), (header - ImGui.GetTextLineHeight()) / 2), U(Text), title);
        if (closable)
        {
            ImGui.SetCursorScreenPos(min + new Vector2(full.X - Px(12 + 32), (header - Px(32)) / 2));
            if (IconButton("##" + id + ".close", HavenIcon.Close, "Close", size: 32)) open = false;
        }
        ImGui.SetCursorScreenPos(min + new Vector2(0, header));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Px(Space4, 0));
        var body = ImGui.BeginChild("##" + id + ".body", new(0, -Px(Space4)), false, ImGuiWindowFlags.AlwaysUseWindowPadding);
        ImGui.PopStyleVar(); PanelBodies.Push(true); return body;
    }
    public static void EndPanel() { if (PanelBodies.Pop()) ImGui.EndChild(); ImGui.End(); }

    /// <summary>
    /// A scrolling body whose content is always as wide as it is with the scrollbar showing, less a gap before the
    /// scrollbar: widths measured inside it (meters, rows) don't jump when the scrollbar comes and goes, and nothing
    /// sits against it. End with ImGui.EndChild().
    /// </summary>
    public static bool BeginScrollBody(string id, Vector2 size)
    {
        var width = size.X > 0 ? size.X : ImGui.GetContentRegionAvail().X + size.X;
        ImGui.SetNextWindowContentSize(new(Math.Max(Px(40), width - ImGui.GetStyle().ScrollbarSize - Px(Space3)), 0));
        return ImGui.BeginChild(id, size, false);
    }

    public static void Banner(BannerKind kind, string title, string message, float width = -1)
    {
        var (fill, ink, icon) = kind switch
        {
            BannerKind.Success => (SuccessFill, Success, HavenIcon.Check), BannerKind.Warning => (WarningFill, Warning, HavenIcon.Alert),
            BannerKind.Danger => (DangerFill, Danger, HavenIcon.Alert), _ => (AccentTint, Accent, HavenIcon.Info),
        };
        width = Avail(width); var textX = Px(Space3 + 18 + Space3); var wrap = width - textX - Px(Space3);
        var min = ImGui.GetCursorScreenPos(); var draw = ImGui.GetWindowDrawList();
        float titleHeight, messageHeight;
        using (HavenFonts.Use(HavenText.Strong)) titleHeight = title.Length > 0 ? ImGui.GetTextLineHeight() + Px(2) : 0;
        using (HavenFonts.Use(HavenText.Small)) messageHeight = message.Length > 0 ? ImGui.CalcTextSize(message, false, wrap).Y : 0;
        var size = new Vector2(width, titleHeight + messageHeight + Px(Space3 * 2));
        draw.AddRectFilled(min, min + size, U(fill), Px(RadiusControl));
        draw.AddRectFilled(min, min + new Vector2(Px(3), size.Y), U(ink), Px(RadiusControl), ImDrawFlags.RoundCornersLeft);
        HavenUi.DrawIcon(draw, icon, min + new Vector2(Px(Space3 + 9), Px(Space3 + 9)), Px(17), ink);
        var y = min.Y + Px(Space3);
        if (title.Length > 0) using (HavenFonts.Use(HavenText.Strong)) draw.AddText(new(min.X + textX, y), U(Text), title);
        if (message.Length > 0) using (HavenFonts.Use(HavenText.Small)) draw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), new(min.X + textX, y + titleHeight), U(TextSecondary), message, wrap);
        ImGui.Dummy(size);
    }

    public static void Tooltip(string text)
    {
        if (text.Length == 0 || !ImGui.IsItemHovered()) return;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Px(10, 7)); ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, Px(RadiusField));
        ImGui.PushStyleColor(ImGuiCol.PopupBg, Overlay); ImGui.PushStyleColor(ImGuiCol.Border, Divider);
        ImGui.BeginTooltip();
        using (HavenFonts.Use(HavenText.Small)) { ImGui.PushTextWrapPos(Px(280)); ImGui.TextUnformatted(text); ImGui.PopTextWrapPos(); }
        ImGui.EndTooltip(); ImGui.PopStyleColor(2); ImGui.PopStyleVar(2);
    }

    // A dashed placeholder card that adds something, such as "New home".
    public static bool GhostCard(string id, Vector2 size, HavenIcon icon, string title, string detail)
    {
        var clicked = Hit(id, size, out var min, out var hovered, out var held); var draw = ImGui.GetWindowDrawList();
        if (hovered || held) draw.AddRectFilled(min, min + size, U(held ? Hover : HavenTokens.Card), Px(RadiusCard));
        var edge = U(hovered ? TextTertiary : Divider); var dash = Px(6); var gap = Px(4);
        void Dashes(Vector2 from, Vector2 to)
        {
            var length = Vector2.Distance(from, to); var step = (to - from) / length;
            for (var at = 0f; at < length; at += dash + gap) draw.AddLine(from + step * at, from + step * Math.Min(length, at + dash), edge, 1);
        }
        // Half-pixel insets keep one-pixel dashes on whole pixels on every side.
        var inset = Px(RadiusCard) * .6f; var a = min + new Vector2(.5f); var b = min + size - new Vector2(.5f);
        Dashes(new(a.X + inset, a.Y), new(b.X - inset, a.Y)); Dashes(new(a.X + inset, b.Y), new(b.X - inset, b.Y));
        Dashes(new(a.X, a.Y + inset), new(a.X, b.Y - inset)); Dashes(new(b.X, a.Y + inset), new(b.X, b.Y - inset));
        float titleHeight, detailHeight;
        using (HavenFonts.Use(HavenText.Strong)) titleHeight = ImGui.GetTextLineHeight();
        using (HavenFonts.Use(HavenText.Small)) detailHeight = detail.Length > 0 ? ImGui.GetTextLineHeight() : 0;
        var y = min.Y + (size.Y - Px(24 + Space2) - titleHeight - detailHeight) / 2;
        HavenUi.DrawIcon(draw, icon, new(min.X + size.X / 2, y + Px(12)), Px(22), TextOnRaised); y += Px(24 + Space2);
        using (HavenFonts.Use(HavenText.Strong)) draw.AddText(new(min.X + (size.X - ImGui.CalcTextSize(title).X) / 2, y), U(Text), title);
        if (detail.Length > 0) using (HavenFonts.Use(HavenText.Small)) draw.AddText(new(min.X + (size.X - ImGui.CalcTextSize(detail).X) / 2, y + titleHeight), U(TextTertiary), detail);
        return clicked;
    }

    // Menus and dialogs ----------------------------------------------------

    // A row inside a popup menu. Disabled rows keep their place but do nothing.
    public static bool MenuItem(string label, bool danger = false, bool enabled = true)
    {
        ImGui.BeginDisabled(!enabled);
        var size = new Vector2(Math.Max(Px(200), ImGui.GetContentRegionAvail().X), Px(34));
        var clicked = Hit(label, size, out var min, out var hovered, out var held); var draw = ImGui.GetWindowDrawList();
        if (hovered || held) draw.AddRectFilled(min, min + size, U(held ? Divider : Hover), Px(RadiusField));
        draw.AddText(new(min.X + Px(10), min.Y + (size.Y - ImGui.GetTextLineHeight()) / 2), U(danger ? Danger : TextOnRaised), Visible(label));
        ImGui.EndDisabled();
        if (clicked) ImGui.CloseCurrentPopup();
        return clicked && enabled;
    }

    public static bool BeginMenu(string id)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Px(6, 6)); ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Px(0, 2));
        var open = ImGui.BeginPopup(id);
        ImGui.PopStyleVar(2); return open;
    }
    public static void MenuRule()
    {
        var min = ImGui.GetCursorScreenPos(); var width = ImGui.GetContentRegionAvail().X;
        ImGui.GetWindowDrawList().AddLine(min + new Vector2(Px(6), Px(4)), min + new Vector2(width - Px(6), Px(4)), U(Divider)); ImGui.Dummy(new(width, Px(9)));
    }

    // A dialog centred on the current window, which darkens behind it. Pair a true result with EndDialog.
    // A plain popup rather than ImGui's modal: ImGui paints a modal's dim at the end of the frame from the global
    // style, after the theme is popped, so it came out as a light grey wash laid over the dialog too. Clicking
    // outside closes the dialog, like Cancel.
    // onScreen centres it on the game window instead, for a dialog opened from a small window such as the inspector.
    // Such a dialog can be dragged by its background, is kept inside the screen as its content grows (a picture
    // loading in), and never grows taller than the screen: anything more scrolls.
    public static bool BeginDialog(string id, string title, float width = 440, bool onScreen = false)
    {
        var host = ImGui.GetWindowPos(); var hostSize = ImGui.GetWindowSize();
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(onScreen ? viewport.Pos + viewport.Size / 2 : host + hostSize / 2, ImGuiCond.Appearing, new Vector2(.5f));
        ImGui.SetNextWindowSize(new(Px(width), 0), ImGuiCond.Always);
        if (onScreen) ImGui.SetNextWindowSizeConstraints(new(Px(width), 0), new(Px(width), Math.Max(Px(200), viewport.Size.Y - Px(32))));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Px(Space5, Space5)); ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Px(Space2, Space3));
        ImGui.PushStyleColor(ImGuiCol.PopupBg, Window); ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, Px(RadiusWindow));
        ImGui.PushStyleColor(ImGuiCol.Border, Divider);
        var open = ImGui.BeginPopup(id, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | (onScreen ? 0 : ImGuiWindowFlags.NoMove));
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(); ImGui.PopStyleColor();
        if (!open) { ImGui.PopStyleVar(2); return false; }
        // Kept on screen: a dialog centred on a panel at the screen's edge (the Furnish drawer) would hang past it.
        {
            var placed = ImGui.GetWindowPos(); var extent = ImGui.GetWindowSize();
            var kept = Vector2.Clamp(placed, viewport.Pos, Vector2.Max(viewport.Pos, viewport.Pos + viewport.Size - extent));
            if (kept != placed) ImGui.SetWindowPos(kept);
        }
        // The shade is drawn from the dialog's own layer, so it covers the host window and all its children, then the
        // dialog's background is laid back over it.
        var draw = ImGui.GetWindowDrawList(); var at = ImGui.GetWindowPos(); var size = ImGui.GetWindowSize();
        draw.PushClipRect(host, host + hostSize, false);
        draw.AddRectFilled(host, host + hostSize, U(new Vector4(0, 0, 0, .5f)), Px(RadiusWindow));
        draw.PopClipRect();
        // The window's own clip rect is inset by its padding; the background redraw needs the full window.
        draw.PushClipRect(at, at + size, false);
        draw.AddRectFilled(at, at + size, U(Window), Px(RadiusWindow));
        draw.AddRect(at, at + size, U(Divider), Px(RadiusWindow));
        draw.PopClipRect();
        using (HavenFonts.Use(HavenText.Title)) ImGui.TextUnformatted(title);
        return true;
    }
    public static void EndDialog() { ImGui.EndPopup(); ImGui.PopStyleVar(2); }

    // Right-aligns the next buttons of the given total width on the current line.
    public static void AlignRight(float width) => ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - width));
    public static float ButtonWidth(string label, HavenIcon? icon = null) => HavenFonts.Measure(HavenText.Strong, Visible(label)).X + (icon == null ? 0 : Px(16 + Space2)) + Px(Space4 * 2);

    // Text ---------------------------------------------------------------

    public static void PageTitle(string title, string subtitle)
    {
        using (HavenFonts.Use(HavenText.Display)) ImGui.TextUnformatted(title);
        if (subtitle.Length > 0) ImGui.TextColored(TextSecondary, subtitle);
    }

    // "Lighting · 148 items": a section name with a quiet count on the right.
    public static void SectionTitle(string title, string detail = "", float width = -1)
    {
        var min = ImGui.GetCursorScreenPos(); width = Avail(width); var draw = ImGui.GetWindowDrawList(); float height;
        using (HavenFonts.Use(HavenText.Strong)) { draw.AddText(min, U(TextOnRaised), title); height = ImGui.GetTextLineHeight(); }
        if (detail.Length > 0)
            using (HavenFonts.Use(HavenText.Caption))
                draw.AddText(min + new Vector2(width - ImGui.CalcTextSize(detail).X, height - ImGui.GetTextLineHeight()), U(TextTertiary), detail);
        ImGui.Dummy(new(width, height));
    }

    public static void Caption(string text, float width = -1)
    {
        using (HavenFonts.Use(HavenText.Small))
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + Avail(width)); ImGui.PushStyleColor(ImGuiCol.Text, TextSecondary);
            ImGui.TextUnformatted(text); ImGui.PopStyleColor(); ImGui.PopTextWrapPos();
        }
    }

    public static void Rule(float width = -1)
    {
        var min = ImGui.GetCursorScreenPos(); width = Avail(width);
        ImGui.Dummy(new(width, Px(Space2))); ImGui.GetWindowDrawList().AddLine(min + new Vector2(0, Px(Space1)), min + new Vector2(width, Px(Space1)), U(Divider));
        ImGui.Dummy(new(width, Px(Space1)));
    }

    // A centred placeholder for an empty list, with an optional primary action.
    public static bool EmptyState(HavenIcon icon, string title, string message, string action = "", float width = -1)
    {
        width = Avail(width); var origin = ImGui.GetCursorScreenPos(); var draw = ImGui.GetWindowDrawList();
        var center = origin + new Vector2(width / 2, Px(28));
        draw.AddCircleFilled(center, Px(28), U(IconWell), 32); HavenUi.DrawIcon(draw, icon, center, Px(24), Accent);
        ImGui.Dummy(new(width, Px(56 + Space3)));
        using (HavenFonts.Use(HavenText.Title)) { ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (width - ImGui.CalcTextSize(title).X) / 2); ImGui.TextUnformatted(title); }
        var wrap = Math.Min(width, Px(400));
        using (HavenFonts.Use(HavenText.Small))
        {
            var block = ImGui.CalcTextSize(message, false, wrap); var at = ImGui.GetCursorScreenPos() + new Vector2((width - block.X) / 2, 0);
            draw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), at, U(TextSecondary), message, wrap); ImGui.Dummy(new(width, block.Y));
        }
        if (action.Length == 0) return false;
        ImGui.Dummy(new(0, Px(Space2)));
        var buttonWidth = HavenFonts.Measure(HavenText.Strong, Visible(action)).X + Px(Space4 * 2);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (width - buttonWidth) / 2);
        return Button(action, ButtonKind.Primary);
    }
}
