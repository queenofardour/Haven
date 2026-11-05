using System.Numerics;
using Dalamud.Bindings.ImGui;
using Lumina.Data.Parsing.Uld;

namespace Haven.Uld;

/// <summary>What a drawn layout needs from its host: the game's textures, icons, fonts and Addon sheet texts.</summary>
public interface IUldHost
{
    /// <summary>
    /// A part of BGRA texels with colour added (0-1 per channel), clamped: the game's additive node colour, baked.
    /// Pixels is the whole texture, width texels wide; scale is the texture's pixels per layout pixel.
    /// </summary>
    public static byte[] AddColour(byte[] pixels, int width, int height, UldPart part, float scale, Vector3 add, out int w, out int h)
    {
        var x0 = (int)(part.U * scale); var y0 = (int)(part.V * scale);
        w = Math.Max(1, Math.Min((int)(part.W * scale), width - x0)); h = Math.Max(1, Math.Min((int)(part.H * scale), height - y0));
        var result = new byte[w * h * 4];
        int ab = (int)(add.Z * 255), ag = (int)(add.Y * 255), ar = (int)(add.X * 255);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var from = ((y0 + y) * width + x0 + x) * 4; var to = (y * w + x) * 4;
                if (from + 3 >= pixels.Length) continue;
                result[to] = (byte)Math.Clamp(pixels[from] + ab, 0, 255); result[to + 1] = (byte)Math.Clamp(pixels[from + 1] + ag, 0, 255);
                result[to + 2] = (byte)Math.Clamp(pixels[from + 2] + ar, 0, 255); result[to + 3] = pixels[from + 3];
            }
        return result;
    }

    /// <summary>A texture, with how many of its pixels make one layout pixel (2 for the _hr1 copies); null while it loads.</summary>
    (ImTextureID Texture, Vector2 Size, float Scale)? Texture(string path);
    (ImTextureID Texture, Vector2 Size)? Icon(uint icon);
    /// <summary>The game font for a text node (0 Axis, 1 Miedinger medium, 2 Miedinger, 3 Trump Gothic, 4 Jupiter, 5 Jupiter large), at the size it is drawn.</summary>
    ImFontPtr? Font(byte font, float size);
    /// <summary>Haven's interface typeface (Inter Regular, or SemiBold when strong) at a size in pixels, for what Haven draws itself (the Odyssey's screens).</summary>
    ImFontPtr? Interface(bool strong, float size) => null;
    string Text(uint addonRow);
    /// <summary>
    /// A part of a texture with colour added to every texel (the game's additive node colour, which cards use for blue
    /// and red), as its own texture; null while it is made. Add is in 0-1 per channel, rounded by the caller.
    /// </summary>
    (ImTextureID Texture, Vector2 Size)? Added(string path, UldPart part, Vector3 add);
    /// <summary>A game window's layout by name (ui/uld/name.uld).</summary>
    UldDocument Layout(string name);
    /// <summary>A UIColor sheet row as an ImGui colour (the default dark theme's), for a text or its edge.</summary>
    uint UiColor(uint row, bool edge);
}

/// <summary>A node of a drawn layout, as the game's AtkResNode: everything here can be changed while it shows.</summary>
public sealed class UldElement
{
    internal UldElement(UldNodeDef def, UldScope scope, UldElement? parent, object space)
    {
        Def = def; Scope = scope; Parent = parent; Space = space; Reset();
    }
    public UldNodeDef Def { get; }
    public UldElement? Parent { get; }
    public List<UldElement> Children { get; } = [];
    // The timeline clock this node's animations follow: its own when its timeline has labels (the game's AtkTimeline
    // plays those per node), else its nearest such ancestor's, or its component's, or the window's.
    internal UldScope Scope { get; }
    // Node ids are unique within the window, and within each component instance (Space is that instance).
    internal object Space { get; }
    internal bool IsComponent => Def.Kind == UldNodeKind.Component && Children.Count > 0 && Children[0].Space == this;

    public Vector2 Position, Size, Scale, Origin;
    /// <summary>Degrees, as the layout files and their timelines store it.</summary>
    public float Rotation;
    public bool Visible;
    /// <summary>Never drawn, whatever Visible says: parts the game's code leaves out (a window's unused header buttons).</summary>
    public bool Omitted;
    public Vector3 Multiply, Add;
    public float Alpha;
    public uint PartList, Part;
    public uint TextColor, EdgeColor;
    /// <summary>UIColor rows for the text and edge colours (texts whose layout says so, and timeline keys); 0 uses the colours.</summary>
    public uint TextColorRow, EdgeColorRow;
    /// <summary>A text node's game font and size (from the layout; code may change them, as the game's addons do).</summary>
    public byte Font, FontSize;
    /// <summary>The text shown; null shows the node's Addon sheet text.</summary>
    public string? Text;
    /// <summary>A counter node's digits.</summary>
    public string Number = "";
    /// <summary>A game icon drawn in place of the node's part (card art, item icons).</summary>
    public uint Icon;
    /// <summary>Where the node was last drawn on screen, for hit testing.</summary>
    public Vector2 ScreenMin, ScreenMax;
    public bool Drawn;

    public void Reset()
    {
        Position = Def.Position; Size = Def.Size; Scale = Def.Scale; Origin = Def.Origin; Rotation = Def.Rotation; Visible = Def.Visible;
        Multiply = Def.Multiply; Add = Def.Add; Alpha = Def.Alpha; PartList = Def.PartList; Part = Def.Part;
        TextColor = Def.TextColor; EdgeColor = Def.EdgeColor; Text = null; Number = ""; Icon = 0; Font = Def.Font; FontSize = Def.FontSize;
        TextColorRow = Def.UiTextColor ? Def.TextColor : 0; EdgeColorRow = Def.UiEdgeColor ? Def.EdgeColor : 0;
    }

    /// <summary>A node below this one by id (inside a component, the component's own ids).</summary>
    public UldElement? Find(uint id)
    {
        var space = IsComponent ? this : Space;
        foreach (var child in Children)
        {
            if (child.Space != space) { if (child.Def.Id == id && IsComponent) return child; continue; }
            if (child.Def.Id == id) return child;
            if (!child.IsComponent && child.Find(id) is { } deeper) return deeper;
        }
        return null;
    }
    /// <summary>Whether the point is over the node as last drawn (and the node and its parents were shown).</summary>
    public bool Contains(Vector2 point) => Drawn && point.X >= ScreenMin.X && point.Y >= ScreenMin.Y && point.X < ScreenMax.X && point.Y < ScreenMax.Y;
}

/// <summary>A timeline clock: the layout's root, or one component, plays its labelled animations on it.</summary>
internal sealed class UldScope(UldTimelineDef? labels)
{
    public UldTimelineDef? Labels = labels;
    public readonly List<UldElement> Members = [];
    public float Frame;
    public bool Playing;
    public ushort Label;
    public event Action<ushort>? Finished;
    public void Finish() => Finished?.Invoke(Label);
}

/// <summary>
/// A game window drawn from its layout with the game's own art: the node tree, components, colours, and the
/// timelines that animate it, played by label as the game's addons play them. Haven drives what is shown (texts,
/// visibility, parts, positions) the way the game's code would, and draws it with ImGui.
/// </summary>
public sealed class UldView
{
    public const float FramesPerSecond = 30;
    private readonly IUldHost host;
    private readonly List<UldScope> scopes = [];
    public UldDocument Document { get; }
    public UldElement Root { get; }
    /// <summary>The drawing scale (layout pixels to screen pixels) last used.</summary>
    public float DrawScale { get; private set; } = 1;

    public UldView(UldDocument document, IUldHost host)
    {
        Document = document; this.host = host;
        var scope = NewScope(document.Widget.FirstOrDefault()?.Timeline ?? 0);
        Root = Build(document.Widget, document.Widget[0], scope, null, this);
    }

    private UldScope NewScope(uint timeline)
    {
        var scope = new UldScope(Document.Timelines.GetValueOrDefault(timeline));
        scopes.Add(scope);
        return scope;
    }

    private UldElement Build(UldNodeDef[] nodes, UldNodeDef def, UldScope scope, UldElement? parent, object space)
    {
        // A node whose timeline has labels plays them on its own clock, which its plain descendants follow.
        if (parent != null && Document.Timelines.TryGetValue(def.Timeline, out var timeline) && timeline.Labels.Length > 0 && scope.Labels != timeline)
            scope = NewScope(def.Timeline);
        var element = new UldElement(def, scope, parent, space);
        scope.Members.Add(element);
        if (def.Kind == UldNodeKind.Component && Document.Components.TryGetValue(def.Type, out var component) && component.Nodes.Length > 0)
        {
            // A component instance: its nodes hang under it, with their own ids, on the component's clock. Its first
            // node is the component's root.
            var root = Build(component.Nodes, component.Nodes[0], NewScope(component.Nodes[0].Timeline), element, element);
            element.Children.Add(root);
            Fit(element, component, root, root.Def.Size, def.Size);
        }
        // Children: the parent's first child, then each next sibling; later ones draw on top.
        var childId = def.Child;
        var guard = 0;
        while (childId != 0 && guard++ < 4096)
        {
            var child = Array.Find(nodes, n => n.Id == childId);
            if (child == null) break;
            element.Children.Add(Build(nodes, child, scope, element, space));
            childId = child.Next;
        }
        return element;
    }

    // The game's component code fits an instance to its node: what spans the component spans the instance; a window's
    // header, its buttons and a button's label follow the right edge; a button or window shows the instance's text.
    private void Fit(UldElement instance, UldComponentDef component, UldElement root, Vector2 from, Vector2 to)
    {
        var delta = to - from;
        if (from.X <= 0 || from.Y <= 0 || to.X <= 0 || to.Y <= 0) return;
        if (delta != Vector2.Zero) Stretch(root);
        void Stretch(UldElement e)
        {
            if (e.Size == from) e.Size = to;
            foreach (var child in e.Children) if (child.Space == instance) Stretch(child);
        }
        switch (component.Type)
        {
            // The standard window frame only (two composed frames, a header with its buttons); Triple Triad's board is a
            // window component laid out its own way.
            case ComponentData.ComponentType.Window when root.Find(9)?.Def.GridCompose == true && root.Find(10)?.Def.GridCompose == true
                && root.Find(2)?.Find(7)?.Def.Kind == UldNodeKind.Component:
                if (root.Find(2) is { } header)
                {
                    header.Size.X += delta.X;
                    foreach (var id in new uint[] { 5, 6, 7 }) if (header.Find(id) is { } button) button.Position.X += delta.X;
                    // Only the buttons the window asks for: close (7), settings (6), help (5).
                    void Keep(uint id, int bit) { if (header.Find(id) is { } button && (instance.Def.WindowButtons & bit) == 0) button.Omitted = true; }
                    Keep(7, 1); Keep(6, 2); Keep(5, 4);
                    if (from == root.Def.Size && instance.Def.TextId != 0 && header.Find(3) is { } title) { title.Text = host.Text(instance.Def.TextId); title.Visible = true; }
                }
                if (root.Find(11) is { } drag) drag.Size.X += delta.X;
                if (root.Find(8) is { } shade) shade.Size.X += delta.X;
                // The selected frame shows while the window has focus; Haven's windows always have it.
                if (root.Find(9) is { } selected) selected.Visible = true;
                break;
            case ComponentData.ComponentType.Button:
                foreach (var child in root.Children)
                {
                    if (child.Def.Kind is UldNodeKind.Text or UldNodeKind.Collision && child.Size != to) child.Size.X += delta.X;
                    if (child.Def.Kind == UldNodeKind.Text && instance.Def.TextId != 0) child.Text = host.Text(instance.Def.TextId);
                }
                break;
        }
    }

    /// <summary>
    /// Asks the host for every texture, icon and font the window can show, at this drawing scale, so a window shown
    /// later (a result window) draws complete the moment it appears instead of filling in as they load.
    /// </summary>
    public void Preload(float scale)
    {
        void Walk(UldElement e)
        {
            switch (e.Def.Kind)
            {
                case UldNodeKind.Image or UldNodeKind.NineGrid or UldNodeKind.Counter:
                    if (e.Icon != 0) host.Icon(e.Icon); else Resolve(e);
                    if (e.Def.GridCompose) for (var i = 1u; i < 9; i++) Resolve(e, e.Part + i);
                    break;
                case UldNodeKind.Text:
                    host.Font(e.Font, e.FontSize * PixelsPerPoint * scale * e.Scale.Y);
                    break;
            }
            foreach (var child in e.Children) Walk(child);
        }
        Walk(Root);
    }

    /// <summary>Resizes a component instance (a window shown smaller than authored), fitting its parts as the game does.</summary>
    public void Resize(UldElement instance, Vector2 size)
    {
        if (!instance.IsComponent || !Document.Components.TryGetValue(instance.Def.Type, out var component)) return;
        var root = instance.Children[0];
        var from = root.Size;
        if (from == size) return;
        instance.Size = size;
        Fit(instance, component, root, from, size);
    }

    /// <summary>A node of the window by id; ids after the first look inside the component the previous one is.</summary>
    public UldElement? Node(params uint[] path)
    {
        UldElement? at = path.Length > 0 && Root.Def.Id == path[0] ? Root : Root.Find(path[0]);
        for (var i = 1; at != null && i < path.Length; i++) at = (at.Children.FirstOrDefault() is { } root && root.Def.Id == path[i] ? root : null) ?? at.Find(path[i]);
        return at;
    }

    /// <summary>Plays a label on a node's clock (a component instance's own, a node with labels, or the window's).</summary>
    public void Play(ushort label, UldElement? node = null, Action<ushort>? finished = null)
    {
        var scope = Clock(node);
        // A label starts at its key; a key that both names the label and stops is a single-frame pose (a card's stars).
        var start = scope.Labels?.Labels.FirstOrDefault(l => l.Id == label);
        if (start is not { Frame: > 0 } found) return;
        scope.Label = label; scope.Frame = found.Frame; scope.Playing = found.Command != 1;
        if (finished != null) { void Once(ushort l) { scope.Finished -= Once; finished(l); } scope.Finished += Once; }
        Apply(scope);
        if (!scope.Playing) scope.Finish();
    }
    public bool IsPlaying(UldElement? node = null) => Clock(node).Playing;
    private UldScope Clock(UldElement? node) => node == null ? Root.Scope : node.IsComponent ? node.Children[0].Scope : node.Scope;

    /// <summary>Advances every playing timeline.</summary>
    public void Update(float seconds)
    {
        foreach (var scope in scopes)
        {
            if (!scope.Playing || scope.Labels == null) continue;
            var before = scope.Frame;
            scope.Frame += seconds * FramesPerSecond;
            // A label key passed on the way: 1 stops there, 2 and 3 jump to another label's start (loops, returns to idle).
            foreach (var key in scope.Labels.Labels)
            {
                if (key.Frame <= before || key.Frame > scope.Frame || key.Command == 0) continue;
                if (key.Command is 2 or 3 && scope.Labels.Labels.FirstOrDefault(l => l.Id == key.Jump) is { Frame: > 0 } jump)
                    scope.Frame = jump.Frame + (scope.Frame - key.Frame);
                else { scope.Frame = key.Frame; scope.Playing = false; scope.Finish(); }
                break;
            }
            Apply(scope);
        }
    }

    private void Apply(UldScope scope)
    {
        // The playing label's span: from its key to the next key that stops or jumps.
        var labels = scope.Labels?.Labels ?? [];
        var start = labels.FirstOrDefault(l => l.Id == scope.Label && l.Command == 0).Frame;
        var end = labels.Where(l => l.Frame > start && l.Command != 0).Select(l => l.Frame).DefaultIfEmpty(uint.MaxValue).Min();
        foreach (var element in scope.Members)
        {
            if (element.Def.Timeline == 0 || !Document.Timelines.TryGetValue(element.Def.Timeline, out var timeline)) continue;
            UldAnimation? playing = null, upcoming = null;
            foreach (var animation in timeline.Animations)
            {
                if (scope.Frame >= animation.Start && scope.Frame <= animation.End) { playing = animation; break; }
                // A node that fades in later in the label waits invisible in its first pose (Triple Triad's START! strips
                // slide in one after another, each hidden off to the side until its turn; the board fades in after the
                // background). A node that starts visible keeps its own state until then (the banner's flash layer).
                if (animation.Start > scope.Frame && animation.Start >= start && animation.Start <= end && (upcoming == null || animation.Start < upcoming.Start)
                    && FadesIn(animation))
                    upcoming = animation;
            }
            if ((playing ?? upcoming) is { } shown)
                foreach (var track in shown.Tracks) Set(element, track.Usage, Evaluate(track.Keys, scope.Frame));
        }
    }

    private static bool FadesIn(UldAnimation animation) =>
        animation.Tracks.FirstOrDefault(t => t.Usage == UldRoot.KeyUsage.Alpha) is { Keys: [var first, ..] } && first.Values is [0];

    private static double[] Evaluate(UldKey[] keys, float frame)
    {
        if (keys.Length == 0) return [];
        if (frame <= keys[0].Frame) return keys[0].Values;
        for (var i = 1; i < keys.Length; i++)
        {
            if (frame > keys[i].Frame) continue;
            var a = keys[i - 1]; var b = keys[i];
            // Interpolation 0 and 1 are linear (0 with its easing), 2 eases out (the layouts use it only for short fades and
            // settling moves: Triple Triad's board coming in from 125 %); anything else holds the key.
            if (a.Interpolation > 2 || a.Values.Length != b.Values.Length) return a.Values;
            var t = (frame - a.Frame) / Math.Max(1f, b.Frame - a.Frame);
            if (a.Interpolation == 2) t = 1 - (1 - t) * (1 - t);
            else if (a.Deceleration > 0) t = 1 - MathF.Pow(1 - t, 1 + a.Deceleration);
            else if (a.Acceleration > 0) t = MathF.Pow(t, 1 + a.Acceleration);
            var values = new double[a.Values.Length];
            for (var v = 0; v < values.Length; v++) values[v] = a.Values[v] + (b.Values[v] - a.Values[v]) * t;
            return values;
        }
        return keys[^1].Values;
    }

    private static void Set(UldElement e, UldRoot.KeyUsage usage, double[] d)
    {
        // A single value on a text or edge colour track is an image's part (ushort) or a UI colour row (0x01 high byte).
        if (d.Length == 1 && usage is UldRoot.KeyUsage.TextColor or UldRoot.KeyUsage.EdgeColor)
        {
            var raw = (uint)d[0];
            if (raw >> 24 == 1) { if (usage == UldRoot.KeyUsage.TextColor) e.TextColorRow = raw & 0xFFFFFF; else e.EdgeColorRow = raw & 0xFFFFFF; }
            else if (usage == UldRoot.KeyUsage.TextColor) e.Part = raw;
            return;
        }
        var v = Array.ConvertAll(d, x => (float)x);
        switch (usage)
        {
            case UldRoot.KeyUsage.Position when v.Length >= 2: e.Position = new(v[0], v[1]); break;
            // Timelines turn nodes in radians; nodes store degrees.
            case UldRoot.KeyUsage.Rotation when v.Length >= 1: e.Rotation = v[0] * 180 / MathF.PI; break;
            case UldRoot.KeyUsage.Scale when v.Length >= 2: e.Scale = new(v[0], v[1]); break;
            // Alpha keys are bytes read signed: -1 is 255.
            case UldRoot.KeyUsage.Alpha when v.Length >= 1: e.Alpha = (v[0] < 0 ? v[0] + 256 : v[0]) / 255f; break;
            case UldRoot.KeyUsage.NodeColor when v.Length >= 6: e.Multiply = new(v[0], v[1], v[2]); e.Add = new(v[3], v[4], v[5]); break;
            case UldRoot.KeyUsage.TextColor when v.Length >= 3: e.TextColor = Pack(v, e.TextColor); break;
            case UldRoot.KeyUsage.EdgeColor when v.Length >= 3: e.EdgeColor = Pack(v, e.EdgeColor); break;
            case UldRoot.KeyUsage.Number when v.Length >= 1: e.Part = (uint)v[0]; break;
        }
    }
    private static uint Pack(float[] v, uint old) =>
        (uint)Math.Clamp(v[0], 0, 255) | (uint)Math.Clamp(v[1], 0, 255) << 8 | (uint)Math.Clamp(v[2], 0, 255) << 16 | (v.Length > 3 ? (uint)Math.Clamp(v[3], 0, 255) : old >> 24) << 24;

    /// <summary>Draws the window with its top-left at origin, layout pixels times scale.</summary>
    public void Draw(ImDrawListPtr draw, Vector2 origin, float scale)
    {
        DrawScale = scale;
        Clear(Root);
        Draw(draw, Root, Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(origin), Vector3.One, Vector3.Zero, 1, scale);
    }
    private static void Clear(UldElement e) { e.Drawn = false; foreach (var c in e.Children) Clear(c); }

    private void Draw(ImDrawListPtr draw, UldElement e, Matrix3x2 parent, Vector3 multiply, Vector3 add, float alpha, float scale)
    {
        if (!e.Visible || e.Omitted) return;
        // Position within the parent; scale and rotation turn about the node's origin.
        var local = Matrix3x2.CreateTranslation(-e.Origin) * Matrix3x2.CreateScale(e.Scale) * Matrix3x2.CreateRotation(e.Rotation * MathF.PI / 180)
            * Matrix3x2.CreateTranslation(e.Origin + e.Position);
        var world = local * parent;
        multiply *= e.Multiply; add += e.Add; alpha *= e.Alpha;
        var a = Vector2.Transform(Vector2.Zero, world); var b = Vector2.Transform(e.Size, world);
        e.ScreenMin = Vector2.Min(a, b); e.ScreenMax = Vector2.Max(a, b); e.Drawn = true;
        if (alpha > 0.002f)
            switch (e.Def.Kind)
            {
                case UldNodeKind.Image: DrawImage(draw, e, world, multiply, add, alpha); break;
                case UldNodeKind.NineGrid: DrawNineGrid(draw, e, world, multiply, add, alpha); break;
                case UldNodeKind.Text: DrawText(draw, e, world, multiply, alpha, scale); break;
                case UldNodeKind.Counter: DrawCounter(draw, e, world, multiply, add, alpha); break;
            }
        // A clipping node limits its children to its box (axis-aligned; the layouts clip unrotated slices of banners).
        if (e.Def.Clip) draw.PushClipRect(e.ScreenMin, e.ScreenMax, true);
        foreach (var child in e.Children) Draw(draw, child, world, multiply, add, alpha, scale);
        if (e.Def.Clip) draw.PopClipRect();
    }

    private static uint Tint(Vector3 multiply, Vector3 add, float alpha, uint baseColor = 0xFFFFFFFF)
    {
        // The game adds colour after multiplying; drawn here as a brighter tint (ImGui has no additive tint).
        var r = (baseColor & 255) / 255f * multiply.X + add.X; var g = (baseColor >> 8 & 255) / 255f * multiply.Y + add.Y;
        var b = (baseColor >> 16 & 255) / 255f * multiply.Z + add.Z; var a = (baseColor >> 24 & 255) / 255f * alpha;
        return (uint)(Math.Clamp(r, 0, 1) * 255) | (uint)(Math.Clamp(g, 0, 1) * 255) << 8 | (uint)(Math.Clamp(b, 0, 1) * 255) << 16 | (uint)(Math.Clamp(a, 0, 1) * 255) << 24;
    }

    private (ImTextureID Texture, Vector2 Size, float Scale, UldPart Part)? Resolve(UldElement e, uint? partIndex = null)
    {
        if (!Document.PartLists.TryGetValue(e.PartList, out var list)) return null;
        var index = (int)(partIndex ?? e.Part);
        if (index < 0 || index >= list.Length) return null;
        var part = list[index];
        if (!Document.Textures.TryGetValue(part.Texture, out var source)) return null;
        if (source.Icon != 0) return host.Icon(source.Icon) is { } icon ? (icon.Texture, icon.Size, icon.Size.X / Math.Max(1, part.W), part) : null;
        return host.Texture(source.Path) is { } texture ? (texture.Texture, texture.Size, texture.Scale, part) : null;
    }

    private static void Quad(ImDrawListPtr draw, ImTextureID texture, Matrix3x2 world, Vector2 min, Vector2 max, Vector2 uvMin, Vector2 uvMax, uint color)
    {
        draw.AddImageQuad(texture, Vector2.Transform(min, world), Vector2.Transform(new Vector2(max.X, min.Y), world), Vector2.Transform(max, world),
            Vector2.Transform(new Vector2(min.X, max.Y), world), uvMin, new Vector2(uvMax.X, uvMin.Y), uvMax, new Vector2(uvMin.X, uvMax.Y), color);
    }

    private void DrawImage(ImDrawListPtr draw, UldElement e, Matrix3x2 world, Vector3 multiply, Vector3 add, float alpha)
    {
        var color = Tint(multiply, add, alpha);
        if (e.Icon != 0)
        {
            if (host.Icon(e.Icon) is { } icon) Quad(draw, icon.Texture, world, Vector2.Zero, e.Size, Vector2.Zero, Vector2.One, color);
            return;
        }
        if (Resolve(e) is not { } r) return;
        var uvMin = new Vector2(r.Part.U, r.Part.V) * r.Scale / r.Size; var uvMax = new Vector2(r.Part.U + r.Part.W, r.Part.V + r.Part.H) * r.Scale / r.Size;
        // Added colour: draw a copy of the part with the colour added to its texels, exactly as the game does.
        var texture = r.Texture;
        if (Added(e, r.Part, add) is { } added) { texture = added; uvMin = Vector2.Zero; uvMax = Vector2.One; color = Tint(multiply, Vector3.Zero, alpha); }
        if (e.Def.FlipH) (uvMin.X, uvMax.X) = (uvMax.X, uvMin.X);
        if (e.Def.FlipV) (uvMin.Y, uvMax.Y) = (uvMax.Y, uvMin.Y);
        // Wrap 2 tiles the part across the node, 3 tiles it mirroring every other copy (a corner made into a frame);
        // otherwise it is stretched to the node's size.
        if (e.Def.Wrap is 2 or 3 && r.Part.W > 0 && r.Part.H > 0)
        {
            var row = 0;
            for (var y = 0f; y < e.Size.Y - 0.01f; y += r.Part.H, row++)
            {
                var column = 0;
                for (var x = 0f; x < e.Size.X - 0.01f; x += r.Part.W, column++)
                {
                    var w = Math.Min(r.Part.W, e.Size.X - x); var h = Math.Min(r.Part.H, e.Size.Y - y);
                    var a = uvMin; var b = uvMin + (uvMax - uvMin) * new Vector2(w / r.Part.W, h / r.Part.H);
                    if (e.Def.Wrap == 3 && column % 2 == 1) { a.X = uvMax.X; b.X = uvMax.X - (uvMax.X - uvMin.X) * (w / r.Part.W); }
                    if (e.Def.Wrap == 3 && row % 2 == 1) { a.Y = uvMax.Y; b.Y = uvMax.Y - (uvMax.Y - uvMin.Y) * (h / r.Part.H); }
                    Quad(draw, texture, world, new(x, y), new(x + w, y + h), a, b, color);
                }
            }
            return;
        }
        Quad(draw, texture, world, Vector2.Zero, e.Size, uvMin, uvMax, color);
    }

    private ImTextureID? Added(UldElement e, UldPart part, Vector3 add)
    {
        if (add.LengthSquared() < 1e-6f || !Document.Textures.TryGetValue(part.Texture, out var source) || source.Icon != 0) return null;
        // Rounded to 1/64 so animated colours reuse a handful of copies.
        var rounded = new Vector3(MathF.Round(add.X * 64), MathF.Round(add.Y * 64), MathF.Round(add.Z * 64)) / 64;
        return host.Added(source.Path, part, rounded)?.Texture;
    }

    private void DrawNineGrid(ImDrawListPtr draw, UldElement e, Matrix3x2 world, Vector3 multiply, Vector3 add, float alpha)
    {
        if (e.Def.GridCompose) { DrawComposedGrid(draw, e, world, Tint(multiply, add, alpha)); return; }
        if (Resolve(e) is not { } r) return;
        var color = Tint(multiply, add, alpha);
        var (top, bottom, left, right) = (e.Def.Grid.X, e.Def.Grid.Y, e.Def.Grid.Z, e.Def.Grid.W);
        float[] xs = [0, left, Math.Max(left, e.Size.X - right), e.Size.X]; float[] ys = [0, top, Math.Max(top, e.Size.Y - bottom), e.Size.Y];
        float[] us = [r.Part.U, r.Part.U + left, r.Part.U + r.Part.W - right, r.Part.U + r.Part.W]; float[] vs = [r.Part.V, r.Part.V + top, r.Part.V + r.Part.H - bottom, r.Part.V + r.Part.H];
        for (var row = 0; row < 3; row++)
            for (var col = 0; col < 3; col++)
            {
                if (xs[col + 1] <= xs[col] || ys[row + 1] <= ys[row]) continue;
                Quad(draw, r.Texture, world, new(xs[col], ys[row]), new(xs[col + 1], ys[row + 1]),
                    new Vector2(us[col], vs[row]) * r.Scale / r.Size, new Vector2(us[col + 1], vs[row + 1]) * r.Scale / r.Size, color);
            }
    }

    // Nine parts in reading order (corners, edges and centre each their own texture): the corners keep their size, the
    // edges and centre stretch between them.
    private void DrawComposedGrid(ImDrawListPtr draw, UldElement e, Matrix3x2 world, uint color)
    {
        var parts = new (ImTextureID Texture, UldPart Part, float Scale, Vector2 Size)?[9];
        for (var i = 0; i < 9; i++) parts[i] = Resolve(e, e.Part + (uint)i) is { } r ? (r.Texture, r.Part, r.Scale, r.Size) : null;
        if (parts[0] is not { } corner || parts[8] is not { } far) return;
        var (top, bottom, left, right) = (e.Def.Grid.X, e.Def.Grid.Y, e.Def.Grid.Z, e.Def.Grid.W);
        if (left <= 0) left = corner.Part.W; if (top <= 0) top = corner.Part.H; if (right <= 0) right = far.Part.W; if (bottom <= 0) bottom = far.Part.H;
        left = Math.Min(left, e.Size.X / 2); right = Math.Min(right, e.Size.X / 2); top = Math.Min(top, e.Size.Y / 2); bottom = Math.Min(bottom, e.Size.Y / 2);
        float[] xs = [0, left, e.Size.X - right, e.Size.X]; float[] ys = [0, top, e.Size.Y - bottom, e.Size.Y];
        for (var row = 0; row < 3; row++)
            for (var col = 0; col < 3; col++)
            {
                if (parts[row * 3 + col] is not { } p || xs[col + 1] <= xs[col] || ys[row + 1] <= ys[row]) continue;
                Quad(draw, p.Texture, world, new(xs[col], ys[row]), new(xs[col + 1], ys[row + 1]),
                    new Vector2(p.Part.U, p.Part.V) * p.Scale / p.Size, new Vector2(p.Part.U + p.Part.W, p.Part.V + p.Part.H) * p.Scale / p.Size, color);
            }
    }

    private void DrawCounter(ImDrawListPtr draw, UldElement e, Matrix3x2 world, Vector3 multiply, Vector3 add, float alpha)
    {
        if (e.Number.Length == 0) return;
        var color = Tint(multiply, add, alpha);
        var width = e.Def.NumberWidth > 0 ? e.Def.NumberWidth : e.Size.Y * 0.6f;
        // Digits are right-aligned in the node (the game's counters show scores this way).
        var x = e.Size.X - e.Number.Length * width;
        foreach (var ch in e.Number)
        {
            if (char.IsDigit(ch) && Resolve(e, e.Part + (uint)(ch - '0')) is { } r)
                Quad(draw, r.Texture, world, new(x, 0), new(x + width, e.Size.Y),
                    new Vector2(r.Part.U, r.Part.V) * r.Scale / r.Size, new Vector2(r.Part.U + r.Part.W, r.Part.V + r.Part.H) * r.Scale / r.Size, color);
            x += width;
        }
    }

    /// <summary>
    /// A text node's font size is in points, as the game's fonts are named (MiedingerMid 12 is 12 pt, 16 px). Until
    /// 0.52.3 Haven drew it as pixels, so every game-UI text came out at three quarters of its size (a card's numbers
    /// visibly smaller than the game's) and its fonts were built below their native size, which softened them.
    /// </summary>
    public const float PixelsPerPoint = 4f / 3;

    private void DrawText(ImDrawListPtr draw, UldElement e, Matrix3x2 world, Vector3 multiply, float alpha, float scale)
    {
        var text = e.Text ?? (e.Def.TextId != 0 ? host.Text(e.Def.TextId) : "");
        var size = e.FontSize * PixelsPerPoint * scale * e.Scale.Y;
        if (text.Length == 0 || host.Font(e.Font, size) is not { } font) return;
        var min = Vector2.Transform(Vector2.Zero, world); var box = e.Size * scale * e.Scale;
        var wrap = (e.Def.TextFlags & (NodeData.TextFlags.WordWrap | NodeData.TextFlags.Multiline)) != 0 ? box.X : 0f;
        // Measured in the font at its own size (times the global font scale, which CalcTextSize applies), then scaled to the node's.
        var factor = size / Math.Max(1f, font.FontSize * ImGui.GetIO().FontGlobalScale);
        ImGui.PushFont(font); var measured = ImGui.CalcTextSize(text, false, wrap > 0 ? wrap / factor : -1f) * factor; ImGui.PopFont();
        // Alignment 0-8: rows top/middle/bottom, columns left/centre/right.
        var column = e.Def.Align % 3; var row = e.Def.Align / 3;
        var at = min + new Vector2(column == 0 ? 0 : column == 1 ? (box.X - measured.X) / 2 : box.X - measured.X, row == 0 ? 0 : row == 1 ? (box.Y - measured.Y) / 2 : box.Y - measured.Y);
        at = new(MathF.Round(at.X), MathF.Round(at.Y));
        if ((e.Def.TextFlags & NodeData.TextFlags.Edge) != 0)
        {
            var edge = Tint(Vector3.One, Vector3.Zero, alpha, e.EdgeColorRow != 0 ? host.UiColor(e.EdgeColorRow, true) : e.EdgeColor);
            var step = Math.Max(1f, scale);
            for (var dy = -1; dy <= 1; dy++) for (var dx = -1; dx <= 1; dx++)
                if (dx != 0 || dy != 0) draw.AddText(font, size, at + new Vector2(dx, dy) * step, edge, text, wrap);
        }
        draw.AddText(font, size, at, Tint(multiply, Vector3.Zero, alpha, e.TextColorRow != 0 ? host.UiColor(e.TextColorRow, false) : e.TextColor), text, wrap);
    }
}
