using System.Text;

namespace Haven.Core;

/// <summary>
/// How the owner framed a picture: quarter turns clockwise, zoom relative to just filling the frame, and where the
/// picture's centre sits in the frame's view (0 to 1 across and down). Whatever the picture leaves uncovered is black
/// when Dark, otherwise white.
/// </summary>
public sealed record PictureLayout(int Turns = 0, double Zoom = 1, double X = .5, double Y = .5, bool Dark = false)
{
    public const double MinZoom = .05, MaxZoom = 20;
    public bool Valid => Turns is >= 0 and < 4 && double.IsFinite(Zoom) && Zoom is >= MinZoom and <= MaxZoom
        && double.IsFinite(X) && double.IsFinite(Y) && X is >= -2 and <= 3 && Y is >= -2 and <= 3;
}

/// <summary>
/// Composes the 1024 x 512 texture a picture frame shows. A frame draws only a window of its texture: every wall
/// frame's canvas mesh (fun_b0_m0212-0214, 1401) maps U 0.174-0.825 and V 0.128-0.859, a 5:3 window on the wall;
/// the Easel shows a little more around it (U 0.099-0.899, V 0.028-0.929). The owner's framing fills that window
/// exactly, and the picture carries on past it, so the Easel's margin shows more of the same picture.
///
/// A portrait frame is the same frame turned a quarter counter-clockwise about its canvas centre (local Z, which
/// faces the viewer), so its picture is composed turned the other way: the canvas point (u, v) appears at (v, 1 - u)
/// in the portrait view.
/// </summary>
public static class PictureCanvas
{
    public const int Width = ContentLocker.PictureWidth, Height = ContentLocker.PictureHeight;
    public const double Left = .176, Right = .823, Top = .13, Bottom = .857;
    public const double Aspect = 5.0 / 3.0;

    /// <summary>The frame's view in view units: 5:3 across, or 3:5 for a portrait frame.</summary>
    public static (double Width, double Height) View(bool portrait) => portrait ? (1, Aspect) : (Aspect, 1);
    /// <summary>View units per image pixel at zoom 1, where the picture just fills the view.</summary>
    public static double FillScale(int width, int height, bool portrait)
    { var (w, h) = View(portrait); return Math.Max(w / width, h / height); }
    /// <summary>The zoom at which the whole picture just fits inside the view.</summary>
    public static double FitZoom(int width, int height, bool portrait)
    { var (w, h) = View(portrait); return Math.Min(w / width, h / height) / FillScale(width, height, portrait); }

    /// <summary>The pixels turned clockwise by quarter turns.</summary>
    public static byte[] Turn(byte[] rgba, int width, int height, int turns, out int turnedWidth, out int turnedHeight)
    {
        Check(rgba, width, height);
        turns &= 3;
        (turnedWidth, turnedHeight) = turns % 2 == 0 ? (width, height) : (height, width);
        if (turns == 0) return rgba;
        var output = new byte[rgba.Length];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var (nx, ny) = turns switch { 1 => (height - 1 - y, x), 2 => (width - 1 - x, height - 1 - y), _ => (y, width - 1 - x) };
                Buffer.BlockCopy(rgba, (y * width + x) * 4, output, (ny * turnedWidth + nx) * 4, 4);
            }
        return output;
    }

    /// <summary>The frame's texture for a picture framed by layout, flattened onto its background.</summary>
    public static byte[] Compose(byte[] rgba, int width, int height, PictureLayout layout, bool portrait)
    {
        if (!layout.Valid) throw new InvalidDataException("Invalid picture framing.");
        var pixels = Turn(rgba, width, height, layout.Turns, out var iw, out var ih);
        var (vw, vh) = View(portrait);
        var scale = FillScale(iw, ih, portrait) * layout.Zoom;
        // View units one texture pixel spans; when the picture is much larger than that, average it down first so
        // sampling never skips detail.
        var (across, down) = portrait ? (vh, vw) : (vw, vh);
        var perTexel = Math.Max(across / ((Right - Left) * Width), down / ((Bottom - Top) * Height));
        var factor = (int)Math.Floor(perTexel / scale);
        if (factor >= 2) { pixels = Reduce(pixels, iw, ih, factor, out iw, out ih); scale *= factor; }
        byte background = layout.Dark ? (byte)0 : (byte)255;
        var flat = Flatten(pixels, background);
        var output = new byte[Width * Height * 4];
        for (var ty = 0; ty < Height; ty++)
            for (var tx = 0; tx < Width; tx++)
            {
                double cu = ((tx + .5) / Width - Left) / (Right - Left), cv = ((ty + .5) / Height - Top) / (Bottom - Top);
                var (px, py) = portrait ? (cv, 1 - cu) : (cu, cv);
                double ix = (px - layout.X) * vw / scale + iw / 2.0 - .5, iy = (py - layout.Y) * vh / scale + ih / 2.0 - .5;
                Sample(flat, iw, ih, ix, iy, background, output.AsSpan((ty * Width + tx) * 4, 4));
            }
        return output;
    }

    /// <summary>
    /// The framing that shows a composed texture, used as the source, the way it hangs now: for framing again a
    /// picture whose original this PC no longer has (0.27.0 kept none). The texture's window is 1.78:1 against the
    /// view's 5:3, so it matches across and shows a little more above and below.
    /// </summary>
    public static PictureLayout AsPlaced(bool portrait)
    {
        var (w, h) = View(portrait);
        // View units per texel along the texture's U, which runs across a landscape view and up a portrait one.
        var scale = (portrait ? h : w) / ((Right - Left) * Width);
        double cu = (.5 - Left) / (Right - Left), cv = (.5 - Top) / (Bottom - Top);
        var (x, y) = portrait ? (cv, 1 - cu) : (cu, cv);
        // A portrait texture shows in its frame turned a quarter counter-clockwise: three quarter turns clockwise.
        var turns = portrait ? 3 : 0;
        var (tw, th) = portrait ? (Height, Width) : (Width, Height);
        return new(turns, scale / FillScale(tw, th, portrait), x, y);
    }

    /// <summary>
    /// The key of one frame's picture: its original framed one way, for one orientation. Every PC builds the frame's
    /// texture from the original and names it by this, so the same framing is built once and two framings never mix.
    /// </summary>
    // Frames ask for their key every frame (every PC, every framed picture): a SHA-256 each time adds up.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, PictureLayout?, bool), string> frameKeys = new();
    public static string FrameKey(string original, PictureLayout? layout, bool portrait)
    {
        if (frameKeys.TryGetValue((original, layout, portrait), out var known)) return known;
        if (frameKeys.Count > 4096) frameKeys.Clear();
        return frameKeys[(original, layout, portrait)] = ComputeFrameKey(original, layout, portrait);
    }
    private static string ComputeFrameKey(string original, PictureLayout? layout, bool portrait)
    {
        var l = layout ?? new();
        var text = string.Join('|', original, l.Turns, l.Zoom.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            l.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture), l.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture), l.Dark, portrait);
        return ContentLocker.Fingerprint(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>What a frame shows of a composed texture, upright, at the given size: for the picker.</summary>
    public static byte[] Visible(byte[] texture, bool portrait, int width, int height)
    {
        Check(texture, Width, Height);
        var output = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                double px = (x + .5) / width, py = (y + .5) / height;
                var (cu, cv) = portrait ? (1 - py, px) : (px, py);
                Sample(texture, Width, Height, (Left + cu * (Right - Left)) * Width - .5, (Top + cv * (Bottom - Top)) * Height - .5, 255,
                    output.AsSpan((y * width + x) * 4, 4));
            }
        return output;
    }

    // A portrait picture says so in a JPEG comment right after the start marker, so every PC offers it only to
    // portrait frames. The comment is part of the file, so its fingerprint covers it.
    private static readonly byte[] PortraitComment = Encoding.ASCII.GetBytes("haven:portrait");
    public static byte[] MarkPortrait(byte[] jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) throw new InvalidDataException("Not a JPEG picture.");
        var length = PortraitComment.Length + 2;
        return [0xFF, 0xD8, 0xFF, 0xFE, (byte)(length >> 8), (byte)length, .. PortraitComment, .. jpeg.AsSpan(2)];
    }
    public static bool IsPortrait(ReadOnlySpan<byte> jpeg) => jpeg.Length >= 6 + PortraitComment.Length && jpeg[2] == 0xFF && jpeg[3] == 0xFE
        && jpeg.Slice(6, PortraitComment.Length).SequenceEqual(PortraitComment);

    private static void Check(byte[] rgba, int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height * 4 != rgba.Length) throw new InvalidDataException("Invalid picture pixels.");
    }

    /// <summary>Averages factor x factor blocks.</summary>
    public static byte[] Reduce(byte[] rgba, int width, int height, int factor, out int reducedWidth, out int reducedHeight)
    {
        reducedWidth = Math.Max(1, width / factor); reducedHeight = Math.Max(1, height / factor);
        var output = new byte[reducedWidth * reducedHeight * 4];
        for (var y = 0; y < reducedHeight; y++)
            for (var x = 0; x < reducedWidth; x++)
                for (var c = 0; c < 4; c++)
                {
                    int sum = 0, count = 0;
                    for (var dy = 0; dy < factor && y * factor + dy < height; dy++)
                        for (var dx = 0; dx < factor && x * factor + dx < width; dx++) { sum += rgba[((y * factor + dy) * width + x * factor + dx) * 4 + c]; count++; }
                    output[(y * reducedWidth + x) * 4 + c] = (byte)((sum + count / 2) / count);
                }
        return output;
    }

    // Transparent parts take the background colour.
    private static byte[] Flatten(byte[] rgba, byte background)
    {
        var output = new byte[rgba.Length];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            int a = rgba[i + 3];
            for (var c = 0; c < 3; c++) output[i + c] = (byte)((rgba[i + c] * a + background * (255 - a) + 127) / 255);
            output[i + 3] = 255;
        }
        return output;
    }

    // Bilinear, with the background beyond the picture's edge.
    private static void Sample(byte[] rgba, int width, int height, double x, double y, byte background, Span<byte> output)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        for (var c = 0; c < 3; c++)
        {
            double Texel(int tx, int ty) => tx < 0 || ty < 0 || tx >= width || ty >= height ? background : rgba[(ty * width + tx) * 4 + c];
            var value = (Texel(x0, y0) * (1 - fx) + Texel(x0 + 1, y0) * fx) * (1 - fy) + (Texel(x0, y0 + 1) * (1 - fx) + Texel(x0 + 1, y0 + 1) * fx) * fy;
            output[c] = (byte)Math.Clamp(Math.Round(value), 0, 255);
        }
        output[3] = 255;
    }
}
