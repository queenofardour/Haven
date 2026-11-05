using System.Numerics;
using System.Reflection;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Uld;

namespace Haven.Uld;

// A game window's layout (a ui/uld/*.uld file), read once: its textures, parts, components, timelines and node tree.
// Haven draws minigame windows from these, exactly as the game lays them out (UldView), using the game's own art.

/// <summary>A rectangle of one of the layout's textures, in the texture's normal-resolution pixels.</summary>
public readonly record struct UldPart(uint Texture, float U, float V, float W, float H);

public enum UldNodeKind { Res = 1, Image = 2, Text = 3, NineGrid = 4, Counter = 5, Collision = 8, Component = 1000 }

/// <summary>One node as authored: placement, colour, its timeline and its kind's data.</summary>
public sealed class UldNodeDef
{
    public uint Id, Parent, Next, Previous, Child, Type, Timeline;
    public Vector2 Position, Size, Scale, Origin;
    public float Rotation;
    public bool Visible, Clip;
    public Vector3 Multiply = Vector3.One, Add;
    public float Alpha = 1;
    public UldNodeKind Kind => Type >= 1000 ? UldNodeKind.Component : (UldNodeKind)Type;
    // Image and nine-grid: the part shown; nine-grid offsets (top, bottom, left, right); image flips and wrap.
    public uint PartList, Part;
    public bool FlipH, FlipV;
    public byte Wrap;
    public Vector4 Grid;
    // A nine-grid drawn from nine parts in a row (corners, edges, centre: a window's frame) instead of one part cut up.
    public bool GridCompose;
    // A window instance's header buttons: close, settings, help (bits 1, 2, 4).
    public byte WindowButtons;
    // Text: the Addon sheet row shown at first, colours (ImGui ABGR), font, size, alignment (0-8, as UldAlign) and flags.
    public uint TextId, TextColor = 0xFFFFFFFF, EdgeColor = 0xFF000000;
    public byte Font, FontSize = 12, Align;
    public NodeData.TextFlags TextFlags;
    // Text and edge colours that are rows of the UIColor sheet rather than colours.
    public bool UiTextColor, UiEdgeColor;
    // Counter: the part list's digits start at Part; each digit's width.
    public byte NumberWidth;
}

/// <summary>A keyframed property of a node over an animation's frames.</summary>
public sealed record UldTrack(UldRoot.KeyUsage Usage, UldKey[] Keys);
// Values are doubles so 32-bit keys (UI colour rows such as 0x01000025) stay exact.
public readonly record struct UldKey(uint Frame, double[] Values, byte Interpolation, float Acceleration, float Deceleration);
/// <summary>One animation of a node's timeline: the frames it covers and its tracks.</summary>
public sealed record UldAnimation(uint Start, uint End, UldTrack[] Tracks);
/// <summary>A timeline's label keys: a label starting (command 0), a stop (1) or a jump to label Jump (2, 3).</summary>
public readonly record struct UldLabel(uint Frame, ushort Id, byte Command, byte Jump);
public sealed record UldTimelineDef(uint Id, UldAnimation[] Animations, UldLabel[] Labels);
public sealed record UldComponentDef(uint Id, ComponentData.ComponentType Type, UldNodeDef[] Nodes);

public sealed class UldDocument
{
    public required string Name { get; init; }
    public required Dictionary<uint, (string Path, uint Icon)> Textures { get; init; }
    public required Dictionary<uint, UldPart[]> PartLists { get; init; }
    public required Dictionary<uint, UldComponentDef> Components { get; init; }
    public required Dictionary<uint, UldTimelineDef> Timelines { get; init; }
    public required UldNodeDef[] Widget { get; init; }
    public UldRoot.AlignmentType Alignment { get; init; }
    public Vector2 WidgetPosition { get; init; }

    public static UldDocument Load(string name, UldFile file)
    {
        var textures = file.AssetData.DistinctBy(a => a.Id).ToDictionary(a => a.Id, a => (new string(a.Path).TrimEnd('\0'), a.IconId));
        var parts = file.Parts.DistinctBy(p => p.Id).ToDictionary(p => p.Id, p => p.Parts.Select(x => new UldPart(x.TextureId, x.U, x.V, x.W, x.H)).ToArray());
        var components = file.Components.DistinctBy(c => c.Id).ToDictionary(c => c.Id, c => new UldComponentDef(c.Id, c.Type, [.. c.Nodes.Select(Node)]));
        var timelines = file.Timelines.DistinctBy(t => t.Id).ToDictionary(t => t.Id, Timeline);
        return new UldDocument
        {
            Name = name, Textures = textures, PartLists = parts, Components = components, Timelines = timelines,
            Widget = [.. file.WidgetData.Nodes.Select(Node)], Alignment = file.WidgetData.AlignmentType,
            WidgetPosition = new(file.WidgetData.X, file.WidgetData.Y),
        };
    }

    private static UldNodeDef Node(UldRoot.NodeData n)
    {
        var node = new UldNodeDef
        {
            Id = n.NodeId, Parent = (uint)n.ParentId, Next = (uint)n.NextSiblingId, Previous = (uint)n.PrevSiblingId, Child = (uint)n.ChildNodeId,
            Type = (uint)n.NodeType, Timeline = n.TimelineId,
            Position = new(n.X, n.Y), Size = new(n.W, n.H), Scale = new(n.ScaleX, n.ScaleY), Origin = new(n.OriginX, n.OriginY),
            Rotation = n.Rotation, Visible = n.Visible, Clip = n.Clip || n.NodeType == 1 && n.ClipCount > 0,
            Multiply = new Vector3(n.MultiplyRed, n.MultiplyGreen, n.MultiplyBlue) / 100f, Add = new Vector3(n.AddRed, n.AddGreen, n.AddBlue) / 255f,
            Alpha = n.Alpha / 255f,
        };
        switch (n.Node)
        {
            case NodeData.ImageNode image:
                node.PartList = image.PartListId; node.Part = image.PartId; node.FlipH = image.FlipH; node.FlipV = image.FlipV; node.Wrap = image.Wrap;
                break;
            case NodeData.NineGridNode grid:
                node.PartList = grid.PartListId; node.Part = grid.PartId; node.Grid = new(grid.TopOffset, grid.BottomOffset, grid.LeftOffset, grid.RightOffset);
                node.GridCompose = grid.GridPartsType == NodeData.GridPartsType.Compose;
                break;
            case NodeData.TextNode text:
                node.TextId = text.TextId; node.TextColor = text.Color; node.EdgeColor = text.EdgeColor; node.Font = (byte)text.Font;
                node.FontSize = text.FontSize; node.Align = (byte)(text.Alignment & 0xF); node.TextFlags = text.TextFlags;
                node.UiTextColor = (text.TextFlags2 & NodeData.TextFlags2.IsUIColor) != 0; node.UiEdgeColor = (text.TextFlags2 & NodeData.TextFlags2.IsUIEdgeColor) != 0;
                break;
            case NodeData.CounterNode counter:
                node.PartList = counter.PartListId; node.Part = counter.PartId; node.NumberWidth = counter.NumberWidth;
                break;
            case NodeData.ButtonComponentNode button:
                node.TextId = button.TextId;
                break;
            case NodeData.WindowComponentNode window:
                node.TextId = window.TitleTextId;
                node.WindowButtons = (byte)((window.CloseButton ? 1 : 0) | (window.ConfigButton ? 2 : 0) | (window.HelpButton ? 4 : 0));
                break;
        }
        return node;
    }

    // A file timeline holds both animations and label sets; label sets are the frame data whose keys are labels.
    private static UldTimelineDef Timeline(UldRoot.Timeline t)
    {
        var animations = new List<UldAnimation>(); var labels = new List<UldLabel>();
        foreach (var frames in t.FrameData)
        {
            var tracks = new List<UldTrack>();
            foreach (var group in frames.KeyGroups)
            {
                if (group.Type == UldRoot.KeyGroupType.Label)
                {
                    foreach (var key in group.Frames.OfType<Keyframes.LabelKeyframe>()) labels.Add(new(key.Keyframe.Time, key.LabelId, key.LabelCommand, key.JumpId));
                    continue;
                }
                tracks.Add(new(group.Usage, [.. group.Frames.Select(Key).OfType<UldKey>()]));
            }
            if (tracks.Count > 0) animations.Add(new(frames.StartFrame, frames.EndFrame, [.. tracks]));
        }
        return new(t.Id, [.. animations], [.. labels.OrderBy(l => l.Frame)]);
    }

    // Keyframes come in many value types (floats, bytes, shorts, bools, colours); each becomes its values as floats.
    private static UldKey? Key(IKeyframe key)
    {
        var type = key.GetType();
        if (type.GetField("Keyframe")?.GetValue(key) is not Keyframes.BaseKeyframeData head) return null;
        if (key is Keyframes.ColorKeyframe color)
            return new(head.Time, [color.MultiplyRed / 100f, color.MultiplyGreen / 100f, color.MultiplyBlue / 100f, color.AddRed / 255f, color.AddGreen / 255f, color.AddBlue / 255f],
                head.Interpolation, head.Acceleration, head.Deceleration);
        var value = type.GetField("Value", BindingFlags.Public | BindingFlags.Instance)?.GetValue(key);
        double[] values = value switch
        {
            Array array => [.. array.Cast<object>().Select(Number)],
            null => [],
            _ => [Number(value)],
        };
        return new(head.Time, values, head.Interpolation, head.Acceleration, head.Deceleration);
    }
    private static double Number(object value) => value switch { bool b => b ? 1 : 0, _ => Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture) };
}
