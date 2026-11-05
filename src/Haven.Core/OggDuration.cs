using System.Buffers.Binary;

namespace Haven.Core;

public static class OggDuration
{
    // Read sample granules from complete Ogg pages, rather than estimating from
    // compressed bytes/bitrate or asking the user for a playback timeout.
    public static long Milliseconds(ReadOnlySpan<byte> data, uint sampleRate)
    {
        if (sampleRate is < 8000 or > 384000) throw new InvalidDataException("Invalid audio sample rate.");
        Dictionary<uint, ulong> streams = [];
        var offset = 0;
        while (offset < data.Length)
        {
            var page = data[offset..];
            if (page.Length < 27 || !page[..4].SequenceEqual("OggS"u8) || page[4] != 0) throw new InvalidDataException("Invalid Ogg page.");
            var segments = page[26];
            if (page.Length < 27 + segments) throw new InvalidDataException("Truncated Ogg segment table.");
            var length = 27 + segments;
            foreach (var size in page.Slice(27, segments)) length += size;
            if (length > page.Length) throw new InvalidDataException("Truncated Ogg audio page.");
            var granule = BinaryPrimitives.ReadUInt64LittleEndian(page[6..]);
            var serial = BinaryPrimitives.ReadUInt32LittleEndian(page[14..]);
            if (granule != ulong.MaxValue) streams[serial] = Math.Max(streams.GetValueOrDefault(serial), granule);
            offset += length;
        }
        var samples = streams.Values.Sum(x => (double)x);
        var milliseconds = Math.Ceiling(samples * 1000 / sampleRate);
        if (!double.IsFinite(milliseconds) || milliseconds is <= 0 or > 86_400_000) throw new InvalidDataException("Invalid or unavailable audio duration.");
        return (long)milliseconds;
    }
}
