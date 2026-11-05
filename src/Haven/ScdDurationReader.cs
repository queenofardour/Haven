using Haven.Core;
using System.Buffers.Binary;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Scd;

namespace Haven;

internal static class ScdDurationReader
{
    public readonly record struct Track(long Milliseconds, bool Loops);
    public static long Milliseconds(ScdFile file) => Read(file).Milliseconds;
    public static Track Read(ScdFile file)
    {
        if (file.SoundDataCount == 0) throw new InvalidDataException("The orchestrion file contains no sound.");
        if (file.GetSound(0).TrackInfos is not IEnumerable<TrackInfo> tracks) throw new InvalidDataException("Unsupported orchestrion sound arrangement.");
        var indexes = tracks.Select(x => (int)x.AudioDataIndex).Distinct().ToArray();
        if (indexes.Length == 0) throw new InvalidDataException("The orchestrion has no audio track.");
        long duration = 0;
        var loops = false;
        foreach (var index in indexes)
        {
            if (index >= file.AudioDataCount) throw new InvalidDataException("Invalid orchestrion audio reference.");
            var audio = file.GetAudio(index);
            loops |= audio.AudioBasicDesc.LoopEnd > audio.AudioBasicDesc.LoopStart;
            var ms = audio.AudioBasicDesc.Format switch
            {
                AudioFormat.OggVorbis => OggDuration.Milliseconds(audio.AudioData, audio.AudioBasicDesc.Rate),
                (AudioFormat)0x1A => HcaMilliseconds(file.Data, index, audio.AudioBasicDesc.Rate),
                AudioFormat.MsAdpcm => AdpcmMilliseconds(file.Data, index, audio.AudioBasicDesc),
                _ => throw new InvalidDataException($"Unsupported orchestrion audio format {audio.AudioBasicDesc.Format}.")
            };
            duration = Math.Max(duration, ms);
        }
        return new(duration, loops);
    }

    private static long HcaMilliseconds(ReadOnlySpan<byte> scd, int index, uint rate)
    {
        var sub = SubInfo(scd, index);
        if (sub.Length < 24) throw new InvalidDataException("Truncated SCD HCA subinfo.");
        var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(sub[2..]);
        return HcaDuration.Milliseconds(sub.Slice(24, headerSize), rate);
    }

    // Furnishing sound effects are MS-ADPCM. The subinfo is its WAVEFORMATEX: block alignment at +12 and samples per
    // block at +18. Each block opens with 7 header bytes per channel holding two samples; the rest pack two per byte.
    private static long AdpcmMilliseconds(ReadOnlySpan<byte> scd, int index, AudioBasicDesc audio)
    {
        var sub = SubInfo(scd, index);
        if (sub.Length < 20 || audio.Rate == 0 || audio.Channel is 0 or > 8) throw new InvalidDataException("Truncated SCD ADPCM format.");
        var align = BinaryPrimitives.ReadUInt16LittleEndian(sub[12..]);
        var perBlock = BinaryPrimitives.ReadUInt16LittleEndian(sub[18..]);
        if (align < 7 * audio.Channel || perBlock == 0) throw new InvalidDataException("Invalid SCD ADPCM blocks.");
        var rest = audio.Size % align;
        long samples = audio.Size / align * perBlock + (rest >= 7 * audio.Channel ? (rest - 7 * audio.Channel) * 2 / audio.Channel + 2 : 0);
        return samples * 1000 / audio.Rate;
    }

    // Lumina currently skips SCD subinfo. Read this entry's plaintext header from its SCD audio
    // table, past an optional marker chunk; never export or decode the audio itself.
    private static ReadOnlySpan<byte> SubInfo(ReadOnlySpan<byte> scd, int index)
    {
        if (scd.Length < 48 || !scd[..8].SequenceEqual("SEDBSSCF"u8) || scd[12] != 0) throw new InvalidDataException("Unsupported SCD header.");
        var tables = BinaryPrimitives.ReadUInt16LittleEndian(scd[14..]);
        if (tables > scd.Length - 16) throw new InvalidDataException("Truncated SCD tables.");
        var table = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(scd[(tables + 12)..]) + index * 4);
        if (table < 0 || table > scd.Length - 4) throw new InvalidDataException("Invalid SCD audio table.");
        var entry = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(scd[table..]));
        if (entry < 0 || entry > scd.Length - 32) throw new InvalidDataException("Invalid SCD audio offset.");
        var subSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(scd[(entry + 24)..]));
        var sub = scd.Slice(entry + 32, subSize);
        if ((BinaryPrimitives.ReadUInt32LittleEndian(scd[(entry + 28)..]) & 1) != 0)
        {
            if (sub.Length < 8 || !sub[..4].SequenceEqual("MARK"u8)) throw new InvalidDataException("Invalid SCD markers.");
            var markerSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(sub[4..]));
            sub = sub[markerSize..];
        }
        return sub;
    }
}
