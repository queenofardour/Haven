using System.Buffers.Binary;

namespace Haven.Core;

public static class HcaDuration
{
    // HCA's fmt chunk describes complete sample blocks, encoder delay and tail
    // padding. No audio decoding/decryption is necessary to read its duration.
    public static long Milliseconds(ReadOnlySpan<byte> header, uint expectedRate)
    {
        if (header.Length < 24 || !header[..4].SequenceEqual("HCA\0"u8) || !header.Slice(8, 4).SequenceEqual("fmt\0"u8))
            throw new InvalidDataException("Invalid HCA header.");
        var size = BinaryPrimitives.ReadUInt16BigEndian(header[6..]);
        if (size < 24 || size > header.Length) throw new InvalidDataException("Truncated HCA header.");
        var rate = BinaryPrimitives.ReadUInt32BigEndian(header[12..]) & 0xFFFFFF;
        var blocks = BinaryPrimitives.ReadUInt32BigEndian(header[16..]);
        var delay = BinaryPrimitives.ReadUInt16BigEndian(header[20..]);
        var padding = BinaryPrimitives.ReadUInt16BigEndian(header[22..]);
        var samples = blocks * 1024L - delay - padding;
        if (rate != expectedRate || rate is < 8000 or > 384000 || samples <= 0) throw new InvalidDataException("Invalid HCA sample information.");
        var ms = (samples * 1000 + rate - 1) / rate;
        if (ms > 86_400_000) throw new InvalidDataException("Invalid HCA duration.");
        return ms;
    }
}
