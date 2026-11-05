using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Object = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;

namespace HavenNative;

// Diagnostic for undyed dyeable furniture: in a real house, prints what the game itself wrote into each matching
// housing model's dye buffer, so Haven can match it. Read-only: walks the scene tree and reads fields, never writes,
// calls nothing on the objects, and stops after a bounded number of nodes.
internal static unsafe class DyeProbe
{
    // Glow: print each model's per-instance emissive instead (see Emissive), to compare a vanilla lamp on and off.
    public static List<string> Read(string filter, int limit = 30, bool glow = false)
    {
        List<string> lines = [];
        var world = World.Instance();
        if (world == null) { lines.Add("No scene is loaded."); return lines; }
        var visited = new HashSet<nint>();
        var pending = new Stack<nint>();
        if (world->ChildObject != null) pending.Push((nint)world->ChildObject);
        var seen = 0;
        while (pending.Count > 0 && visited.Count < 50_000 && lines.Count < limit)
        {
            // Siblings form a ring; walk it once and queue each child ring.
            var first = (Object*)pending.Pop();
            for (var node = first; node != null && visited.Add((nint)node); node = node->NextSiblingObject)
            {
                if (node->ChildObject != null) pending.Push((nint)node->ChildObject);
                if (node->GetObjectType() != ObjectType.BgObject) continue;
                var bg = (BgObject*)node;
                if (bg->ModelResourceHandle == null) continue;
                var path = bg->ModelResourceHandle->FileName.ToString();
                if (!path.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                seen++;
                if (lines.Count >= limit) break;
                if (glow) { lines.Add($"{Path.GetFileName(path)}: {Emissive(bg)}"); continue; }
                var buffer = bg->StainBuffer;
                var stain = buffer == null ? "no dye buffer"
                    : $"srgb {buffer->SrgbByteColor.R},{buffer->SrgbByteColor.G},{buffer->SrgbByteColor.B},{buffer->SrgbByteColor.A} " +
                      $"linear {buffer->LinearFloatColor.X:0.###},{buffer->LinearFloatColor.Y:0.###},{buffer->LinearFloatColor.Z:0.###},{buffer->LinearFloatColor.W:0.###} " +
                      $"flags 0x{*((byte*)buffer + 0x30):X2}";
                lines.Add($"{Path.GetFileName(path)}: {stain}; color {bg->Color.R},{bg->Color.G},{bg->Color.B},{bg->Color.A} intensity {bg->ColorIntensity}");
            }
        }
        lines.Add($"{seen} matching model(s) found.");
        return lines;
    }

    // The glow C110 fades: a byte at BgObject +0xCD, 255 the authored glow and 0 none (read-only research: the
    // command's update, RVA 0x1921500, writes it through RVA 0x73F6F0 as round(level × 255)).
    // The glow byte (C110) and the colour override the C113 setter and the type-6 colour controller write: +0xCE
    // intensity, +0xD0..+0xD2 colour, +0xD3 blend (whether a blend of 0 shows any colour is what this is for).
    private static string Emissive(BgObject* bg)
    {
        var b = (byte*)bg;
        return $"glow {b[0xCD]} of 255, colour intensity {b[0xCE]}, rgb {b[0xD0]},{b[0xD1]},{b[0xD2]}, blend {b[0xD3]}";
    }
}
