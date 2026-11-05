using System.Buffers.Binary;

namespace Haven.Core;

public readonly record struct SwitchState(Guid Furnishing, bool On);

/// <summary>
/// Furnishing switch sides shared in a gathering: u16 count, then per entry a 16-byte furnishing ID and one byte
/// (0 off, 1 on). One click sends one entry; a late joiner receives the room's whole table. Strictly validated.
/// </summary>
public static class SwitchWire
{
    public const int Protocol = 1;
    public const int MaxEntries = HouseFormat.MaxObjects;
    private const int Entry = 17;

    public static byte[] Encode(IReadOnlyCollection<SwitchState> states)
    {
        if (states.Count is 0 or > MaxEntries) throw new InvalidDataException("Invalid switch state count.");
        var bytes = new byte[2 + states.Count * Entry];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)states.Count);
        var at = 2;
        foreach (var state in states)
        {
            if (state.Furnishing == Guid.Empty) throw new InvalidDataException("A switch needs a furnishing.");
            state.Furnishing.TryWriteBytes(bytes.AsSpan(at, 16)); bytes[at + 16] = (byte)(state.On ? 1 : 0); at += Entry;
        }
        return bytes;
    }

    public static SwitchState[] Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2) throw new InvalidDataException("Truncated switch states.");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        if (count is 0 or > MaxEntries || payload.Length != 2 + count * Entry) throw new InvalidDataException("Invalid switch state length.");
        var states = new SwitchState[count]; HashSet<Guid> seen = [];
        for (var i = 0; i < count; i++)
        {
            var entry = payload.Slice(2 + i * Entry, Entry);
            var id = new Guid(entry[..16]);
            if (id == Guid.Empty || entry[16] > 1 || !seen.Add(id)) throw new InvalidDataException("Invalid switch state entry.");
            states[i] = new(id, entry[16] == 1);
        }
        return states;
    }
}
