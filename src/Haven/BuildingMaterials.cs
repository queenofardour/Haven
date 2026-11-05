using System.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace Haven;

// The housing preview's own per-background-object material loader. It owns the
// asynchronous resource references and releases them in the BgObject destructor.
// Never replace a shared ModelResourceHandle's material table.
internal sealed unsafe class BuildingMaterials
{
    internal const string Signature = "48 89 5C 24 ?? 57 48 83 EC ?? F6 41 30 02 48 8B FA 48 8B D9";
    private readonly delegate* unmanaged<BgObjectStainBuffer*, byte*, void> load;
    public bool Available => load != null;
    public BuildingMaterials(ISigScanner scanner)
    {
        if (scanner.TryScanText(Signature, out var address)) load = (delegate* unmanaged<BgObjectStainBuffer*, byte*, void>)address;
    }
    public bool Apply(BgObject* ownedObject, string material)
    {
        if (!Available) throw new InvalidOperationException("Building surface finishes are unavailable in this game build.");
        var buffer = ownedObject->StainBuffer;
        if (buffer == null) return false;
        // Same material-change capability bit tested by the native loader itself.
        if ((*((byte*)buffer + 0x30) & 2) == 0) throw new InvalidOperationException("This building model has no native replaceable material buffer.");
        var bytes = Encoding.UTF8.GetBytes(material + '\0');
        fixed (byte* path = bytes) load(buffer, path);
        ownedObject->UpdateMaterials();
        return true;
    }
}
