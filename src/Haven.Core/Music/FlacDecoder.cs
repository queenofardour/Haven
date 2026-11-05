namespace Haven.Core.Music;

/// <summary>
/// A small FLAC decoder for custom music (no FLAC library with a clear licence decodes audio): STREAMINFO, then frames
/// of constant, verbatim, fixed and LPC subframes with Rice-coded residuals, independent and stereo-decorrelated
/// channels, and wasted bits, as RFC 9639 describes. CRCs are not checked; a damaged file fails or sounds wrong, and
/// nothing else depends on it.
/// </summary>
public sealed class FlacDecoder : IAudioSource
{
    private readonly byte[] data;
    private int bytePos, bitPos;
    private readonly int bitsPerSample;
    private int[][] block = [];
    private int blockSamples, blockRead;

    public int Channels { get; }
    public int SampleRate { get; }
    public long TotalSamples { get; }

    public FlacDecoder(byte[] data)
    {
        this.data = data;
        // An ID3v2 tag sometimes precedes the stream.
        if (data.Length > 10 && data[0] == 'I' && data[1] == 'D' && data[2] == '3')
            bytePos = 10 + (data[6] << 21 | data[7] << 14 | data[8] << 7 | data[9]);
        if (Bits(32) != 0x664C6143) throw new InvalidDataException("Not a FLAC file.");
        var last = false;
        while (!last)
        {
            last = Bits(1) == 1;
            var type = (int)Bits(7);
            var length = (int)Bits(24);
            if (type == 0)
            {
                Bits(16); Bits(16); Bits(24); Bits(24);
                SampleRate = (int)Bits(20);
                Channels = (int)Bits(3) + 1;
                bitsPerSample = (int)Bits(5) + 1;
                TotalSamples = (long)Bits(36);
                bytePos += 16; // MD5
            }
            else bytePos += length;
            if (bytePos > data.Length) throw new InvalidDataException("Truncated FLAC metadata.");
        }
        if (SampleRate == 0 || bitsPerSample == 0) throw new InvalidDataException("The FLAC file has no stream information.");
    }

    public int Read(float[] buffer, int count)
    {
        var written = 0;
        while (written + Channels <= count)
        {
            if (blockRead >= blockSamples && !NextFrame()) break;
            for (var c = 0; c < Channels; c++) buffer[written + c] = block[c][blockRead] * scale;
            written += Channels; blockRead++;
        }
        return written;
    }

    private float scale = 1;

    private bool NextFrame()
    {
        // Frames start byte-aligned with the 14-bit sync code 0b11111111111110.
        AlignToByte();
        while (bytePos + 1 < data.Length && !(data[bytePos] == 0xFF && (data[bytePos + 1] & 0xFE) == 0xF8)) bytePos++;
        if (bytePos + 4 >= data.Length) return false;
        Bits(15); Bits(1); // sync and blocking strategy
        var sizeCode = (int)Bits(4);
        var rateCode = (int)Bits(4);
        var assignment = (int)Bits(4);
        var depthCode = (int)Bits(3);
        Bits(1);
        // The frame or sample number, UTF-8 coded.
        var first = (int)Bits(8);
        var extra = first < 0x80 ? 0 : first < 0xE0 ? 1 : first < 0xF0 ? 2 : first < 0xF8 ? 3 : first < 0xFC ? 4 : first < 0xFE ? 5 : 6;
        for (var i = 0; i < extra; i++) Bits(8);
        var size = sizeCode switch
        {
            1 => 192,
            >= 2 and <= 5 => 576 << (sizeCode - 2),
            6 => (int)Bits(8) + 1,
            7 => (int)Bits(16) + 1,
            >= 8 => 256 << (sizeCode - 8),
            _ => throw new InvalidDataException("Reserved FLAC block size."),
        };
        if (rateCode == 12) Bits(8); else if (rateCode is 13 or 14) Bits(16);
        Bits(8); // header CRC
        var depth = depthCode switch { 0 => bitsPerSample, 1 => 8, 2 => 12, 4 => 16, 5 => 20, 6 => 24, 7 => 32, _ => throw new InvalidDataException("Reserved FLAC sample size.") };
        var channels = assignment < 8 ? assignment + 1 : 2;
        if (channels != Channels) throw new InvalidDataException("FLAC channel count changed mid-stream.");
        if (block.Length != channels || block[0].Length < size) block = [.. Enumerable.Range(0, channels).Select(_ => new int[size])];
        var wide = new long[channels][];
        for (var c = 0; c < channels; c++)
        {
            // The side channel carries one more bit.
            var bits = depth + (assignment == 8 && c == 1 || assignment == 9 && c == 0 || assignment == 10 && c == 1 ? 1 : 0);
            wide[c] = Subframe(size, bits);
        }
        for (var i = 0; i < size; i++)
        {
            long a = wide[0][i], b = channels > 1 ? wide[1][i] : 0;
            switch (assignment)
            {
                case 8: b = a - b; break;                       // left, side
                case 9: a += b; break;                          // side, right
                case 10: a = (a << 1) | (b & 1); var m = a; a = (m + b) >> 1; b = (m - b) >> 1; break; // mid, side
            }
            block[0][i] = (int)a;
            if (channels > 1) block[1][i] = (int)b;
            for (var c = 2; c < channels; c++) block[c][i] = (int)wide[c][i];
        }
        AlignToByte();
        Bits(16); // frame CRC
        scale = 1f / (1L << (depth - 1));
        blockSamples = size; blockRead = 0;
        return true;
    }

    private long[] Subframe(int size, int bits)
    {
        Bits(1);
        var type = (int)Bits(6);
        var wasted = 0;
        if (Bits(1) == 1) { wasted = 1; while (Bits(1) == 0) wasted++; }
        bits -= wasted;
        var samples = new long[size];
        if (type == 0) { var v = Signed(bits); Array.Fill(samples, v); }
        else if (type == 1) for (var i = 0; i < size; i++) samples[i] = Signed(bits);
        else if (type is >= 8 and <= 12)
        {
            var order = type - 8;
            for (var i = 0; i < order; i++) samples[i] = Signed(bits);
            Residual(samples, size, order);
            for (var i = order; i < size; i++)
                samples[i] += order switch
                {
                    0 => 0,
                    1 => samples[i - 1],
                    2 => 2 * samples[i - 1] - samples[i - 2],
                    3 => 3 * samples[i - 1] - 3 * samples[i - 2] + samples[i - 3],
                    _ => 4 * samples[i - 1] - 6 * samples[i - 2] + 4 * samples[i - 3] - samples[i - 4],
                };
        }
        else if (type >= 32)
        {
            var order = type - 31;
            for (var i = 0; i < order; i++) samples[i] = Signed(bits);
            var precision = (int)Bits(4) + 1;
            if (precision == 16) throw new InvalidDataException("Invalid FLAC LPC precision.");
            var shift = (int)Signed(5);
            var coefficients = new long[order];
            for (var i = 0; i < order; i++) coefficients[i] = Signed(precision);
            Residual(samples, size, order);
            for (var i = order; i < size; i++)
            {
                long sum = 0;
                for (var j = 0; j < order; j++) sum += coefficients[j] * samples[i - 1 - j];
                samples[i] += sum >> shift;
            }
        }
        else throw new InvalidDataException("Reserved FLAC subframe type.");
        if (wasted > 0) for (var i = 0; i < size; i++) samples[i] <<= wasted;
        return samples;
    }

    // Rice-coded residuals, added into samples from the predictor order on (the prediction is added afterwards).
    private void Residual(long[] samples, int size, int order)
    {
        var method = (int)Bits(2);
        if (method > 1) throw new InvalidDataException("Reserved FLAC residual coding.");
        var paramBits = method == 0 ? 4 : 5;
        var partitionOrder = (int)Bits(4);
        var partitions = 1 << partitionOrder;
        var at = order;
        for (var p = 0; p < partitions; p++)
        {
            var count = (size >> partitionOrder) - (p == 0 ? order : 0);
            var parameter = (int)Bits(paramBits);
            if (parameter == (1 << paramBits) - 1)
            {
                var raw = (int)Bits(5);
                for (var i = 0; i < count; i++) samples[at++] = raw == 0 ? 0 : Signed(raw);
            }
            else for (var i = 0; i < count; i++)
            {
                var quotient = Unary();
                var value = (quotient << parameter) | (long)Bits(parameter);
                samples[at++] = (value >> 1) ^ -(value & 1);
            }
        }
    }

    private ulong Bits(int count)
    {
        ulong value = 0;
        while (count > 0)
        {
            if (bytePos >= data.Length) throw new InvalidDataException("Truncated FLAC stream.");
            var available = 8 - bitPos;
            var take = Math.Min(available, count);
            value = (value << take) | (uint)((data[bytePos] >> (available - take)) & ((1 << take) - 1));
            bitPos += take; count -= take;
            if (bitPos == 8) { bitPos = 0; bytePos++; }
        }
        return value;
    }
    // A unary run: the zeros before the next one bit, read a byte at a time.
    private long Unary()
    {
        long zeros = 0;
        while (true)
        {
            if (bytePos >= data.Length) throw new InvalidDataException("Truncated FLAC stream.");
            var rest = (data[bytePos] << bitPos) & 0xFF;
            if (rest == 0) { zeros += 8 - bitPos; bitPos = 0; bytePos++; continue; }
            var lead = System.Numerics.BitOperations.LeadingZeroCount((uint)rest) - 24;
            zeros += lead; bitPos += lead + 1;
            if (bitPos == 8) { bitPos = 0; bytePos++; }
            return zeros;
        }
    }
    private long Signed(int count) { var v = (long)Bits(count); return count == 0 ? 0 : (v ^ (1L << (count - 1))) - (1L << (count - 1)); }
    private void AlignToByte() { if (bitPos != 0) { bitPos = 0; bytePos++; } }
}
