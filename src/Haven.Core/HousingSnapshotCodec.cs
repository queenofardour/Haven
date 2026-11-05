using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;

namespace Haven.Core;

/// <summary>Length-prefixed Brotli scene transfer, bounded before allocating/decompressing.</summary>
public static class HousingSnapshotCodec
{
    private static readonly JsonSerializerOptions Compact = new(HouseFormat.Json) { WriteIndented = false };
    public static byte[] Encode(House house)
    {
        _ = HouseFormat.Serialize(house);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(house, Compact);
        var buffer = new byte[BrotliEncoder.GetMaxCompressedLength(bytes.Length) + 4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, bytes.Length);
        if (!BrotliEncoder.TryCompress(bytes, buffer.AsSpan(4), out var written, quality: 4, window: 22)
            || written + 4 > HouseFormat.MaxBytes + 16) throw new InvalidDataException("Compressed scene exceeds its transfer budget.");
        return buffer.AsSpan(0, written + 4).ToArray();
    }

    public static House Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 5 || payload.Length > HouseFormat.MaxBytes + 16) throw new InvalidDataException("Invalid compressed scene size.");
        var length = BinaryPrimitives.ReadInt32LittleEndian(payload);
        if (length is <= 0 or > HouseFormat.MaxBytes) throw new InvalidDataException("Invalid expanded scene size.");
        var bytes = new byte[length];
        using var decoder = new BrotliDecoder();
        var result = decoder.Decompress(payload[4..], bytes, out var consumed, out var written);
        if (result != OperationStatus.Done || consumed != payload.Length - 4 || written != length)
            throw new InvalidDataException("Incomplete, oversized or invalid compressed scene.");
        var house = HouseFormat.Parse(bytes);
        _ = HouseFormat.Serialize(house);
        return house;
    }
}
