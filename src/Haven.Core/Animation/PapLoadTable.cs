using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Haven.Core.Animation;

/// <summary>
/// The game's animation load table (chara/xls/animation/papLoadTable.plt): for each human body (skeleton) and each
/// animation file, whose copy of the file that body loads. A Miqo'te woman playing Red Ranger Pose B loads the Hyur
/// man's copy, because only three bodies ship one; for a ground-sitting pose she loads her own. A mod's file is built
/// on one skeleton, so serving it where the game will read another body's copy draws it on the wrong bones (the
/// character T-poses). Layout, read from the installed table (and checked against NoireLib's reader's description):
/// a 0x10 header (file count u16 at 0, entry count u16 at 2, folder and name string bases u32 at 8 and 12); 12 bytes a
/// file (folder and name offsets u32 at 4 and 8); then 168 bytes a body entry (body code u32 at 0, variant u16 at 4,
/// first override u16 at 6, then twenty 64-bit words with one bit for every four files); then 8-byte override records
/// (body code u32, variant u16, folder offset u16). A clear bit means the body reads its own copy; a set bit points at
/// four records, one per file, naming the body whose copy it reads.
/// </summary>
public sealed class PapLoadTable
{
    public const string GamePath = "chara/xls/animation/papLoadTable.plt";
    private const int Header = 0x10, FileRecord = 12, EntryRecord = 168, BitsAt = 8, Words = (EntryRecord - BitsAt) / 8, Override = 8;
    private readonly byte[] table;
    private readonly uint folderBase;
    private readonly int overrideBase;
    private readonly string[] folders;
    private readonly Dictionary<string, int> files;
    private readonly Dictionary<uint, int> bodies;

    private PapLoadTable(byte[] table, uint folderBase, int overrideBase, string[] folders, Dictionary<string, int> files, Dictionary<uint, int> bodies)
    { this.table = table; this.folderBase = folderBase; this.overrideBase = overrideBase; this.folders = folders; this.files = files; this.bodies = bodies; }

    /// <summary>Reads the table; null when it isn't laid out as expected.</summary>
    public static PapLoadTable? Parse(byte[] table)
    {
        if (table.Length < Header) return null;
        var fileCount = BinaryPrimitives.ReadUInt16LittleEndian(table);
        var entryCount = BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(2));
        var folderBase = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(8));
        var nameBase = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(12));
        var entryBase = Header + fileCount * FileRecord;
        var overrideBase = entryBase + entryCount * EntryRecord;
        if (fileCount == 0 || entryCount == 0 || overrideBase > table.Length || folderBase >= table.Length || nameBase >= table.Length) return null;
        var folders = new string[fileCount];
        var files = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < fileCount; i++)
        {
            var at = Header + i * FileRecord;
            if (Text(table, folderBase, BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(at + 4))) is not { } folder
                || Text(table, nameBase, BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(at + 8))) is not { } name) return null;
            folders[i] = folder;
            files.TryAdd((folder + "/" + name).ToLowerInvariant(), i);
        }
        var bodies = new Dictionary<uint, int>();
        for (var i = 0; i < entryCount; i++)
        {
            var at = entryBase + i * EntryRecord;
            var code = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(at));
            if (code >> 16 == 0 && BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(at + 4)) == 1) bodies.TryAdd(code, at);
        }
        return new(table, folderBase, overrideBase, folders, files, bodies);
    }

    /// <summary>
    /// Whose copy of an animation (a timeline key under bt_common, as "emote/j_pose01_loop") a body (as "c0801") loads: a body code,
    /// or null when the table doesn't know the body or the file.
    /// </summary>
    public string? Reads(string race, string key)
    {
        if (race.Length != 5 || race[0] != 'c' || !uint.TryParse(race.AsSpan(1), out var code) || !bodies.TryGetValue(code, out var entry)) return null;
        // Files are listed by their folder (bt_common for every emote) and name, as the paths under a body's a0001 folder.
        if (!files.TryGetValue(("bt_common/" + key).ToLowerInvariant(), out var file)) return null;
        var word = file >> 8; var bit = (file & 0xFF) >> 2;
        if (word >= Words) return null;
        var bits = BinaryPrimitives.ReadUInt64LittleEndian(table.AsSpan(entry + BitsAt + word * 8));
        if ((bits >> bit & 1) == 0) return race;
        var before = BitOperations.PopCount(bits & ((1UL << bit) - 1));
        for (var w = 0; w < word; w++) before += BitOperations.PopCount(BinaryPrimitives.ReadUInt64LittleEndian(table.AsSpan(entry + BitsAt + w * 8)));
        var first = BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(entry + 6));
        var record = (long)overrideBase + Override * ((file & 3) + 4L * (before + first));
        if (record < overrideBase || record + Override > table.Length) return null;
        var target = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan((int)record));
        if (target >> 16 != 0 || BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan((int)record + 4)) != 1) return null;
        var folder = Text(table, folderBase, BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan((int)record + 6)));
        return folder == folders[file] ? $"c{target:0000}" : null;
    }

    private static string? Text(byte[] table, uint start, uint offset)
    {
        var at = (long)start + offset;
        if (at >= table.Length) return null;
        var end = Array.IndexOf(table, (byte)0, (int)at);
        return end < 0 ? null : Encoding.ASCII.GetString(table, (int)at, end - (int)at);
    }
}
