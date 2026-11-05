namespace Haven.Core;

/// <summary>
/// Batched delivery. A client that joins with Batching receives, instead of one WebSocket message per frame, a Batch
/// frame (<see cref="HousingWire.Batch"/>) every <see cref="WindowMs"/> or so while traffic flows: whole downleg frames,
/// each prefixed with its length (u32 little-endian). Only the newest movement frame per sender is kept. Each message
/// through Cloudflare costs; a room of N players sending movement at 10 Hz becomes about 20 messages a second per
/// player instead of 10·N. Recovery checkpoints and large frames always travel alone (the edge reads checkpoints).
/// </summary>
public static class FrameBatch
{
    public const int WindowMs = 50, MaxBytes = 256 * 1024, StandaloneBytes = 64 * 1024;

    public static byte[] Pack(IReadOnlyList<byte[]> frames)
    {
        var payload = new byte[frames.Sum(f => 4 + f.Length)];
        var at = 0;
        foreach (var frame in frames)
        {
            BitConverter.TryWriteBytes(payload.AsSpan(at, 4), (uint)frame.Length);
            frame.CopyTo(payload, at + 4);
            at += 4 + frame.Length;
        }
        return payload;
    }

    public static List<byte[]> Unpack(byte[] payload)
    {
        var frames = new List<byte[]>();
        for (var at = 0; at < payload.Length;)
        {
            if (payload.Length - at < 4) throw new InvalidDataException("Truncated batch.");
            var length = BitConverter.ToUInt32(payload, at);
            if (length == 0 || length > payload.Length - at - 4) throw new InvalidDataException("Truncated batch frame.");
            frames.Add(payload.AsSpan(at + 4, (int)length).ToArray());
            at += 4 + (int)length;
            if (frames.Count > 4096) throw new InvalidDataException("Batch too long.");
        }
        return frames;
    }

    /// <summary>A downleg frame's kind and sender, read from its header ([magic][kind][flags][senderLen:2][sender]).</summary>
    public static (byte Kind, string Sender) Header(byte[] frame)
    {
        if (frame.Length < 5) return (0, "");
        var senderLength = frame[3] | frame[4] << 8;
        return (frame[1], frame.Length < 5 + senderLength ? "" : System.Text.Encoding.UTF8.GetString(frame, 5, senderLength));
    }

    /// <summary>
    /// Splits frames gathered in one window into messages: runs of small frames packed together (only the newest
    /// movement frame per sender kept), and checkpoints or large frames on their own, in order.
    /// </summary>
    public static List<byte[]> Messages(IReadOnlyList<byte[]> frames, byte movementKind, byte checkpointKind, Func<byte[], byte[]> wrap)
    {
        var newestMovement = new Dictionary<string, int>();
        for (var i = 0; i < frames.Count; i++)
            if (Header(frames[i]) is { Kind: var kind, Sender: var sender } && kind == movementKind) newestMovement[sender] = i;
        var messages = new List<byte[]>();
        var run = new List<byte[]>();
        var runBytes = 0;
        void Flush()
        {
            if (run.Count == 1) messages.Add(run[0]);
            else if (run.Count > 1) messages.Add(wrap(Pack(run)));
            run.Clear(); runBytes = 0;
        }
        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            var (kind, sender) = Header(frame);
            if (kind == movementKind && newestMovement.GetValueOrDefault(sender, i) != i) continue;
            if (kind == checkpointKind || frame.Length > StandaloneBytes) { Flush(); messages.Add(frame); continue; }
            if (runBytes + 4 + frame.Length > MaxBytes) Flush();
            run.Add(frame); runBytes += 4 + frame.Length;
        }
        Flush();
        return messages;
    }
}
