using NLayer;
using NVorbis;

namespace Haven.Core.Music;

/// <summary>Decoded audio, read as interleaved float samples in [-1, 1].</summary>
public interface IAudioSource
{
    int Channels { get; }
    int SampleRate { get; }
    /// <summary>Samples per channel, or -1 when the format does not say.</summary>
    long TotalSamples { get; }
    /// <summary>Fills buffer with up to count interleaved samples (a multiple of Channels); 0 at the end.</summary>
    int Read(float[] buffer, int count);
}

/// <summary>Opens a song the player chose, by its content rather than its file name: MP3, Ogg Vorbis, WAV or FLAC.</summary>
public static class AudioSources
{
    public static IAudioSource Open(byte[] data)
    {
        if (StartsWith(data, "fLaC") || StartsWith(data, "ID3") && FlacAfterId3(data)) return new FlacDecoder(data);
        if (StartsWith(data, "OggS"))
        {
            if (data.AsSpan(0, Math.Min(data.Length, 128)).IndexOf("OpusHead"u8) >= 0)
                throw new InvalidDataException("Opus files aren't supported yet. Use MP3, Ogg Vorbis, WAV or FLAC.");
            return new VorbisSource(data);
        }
        if (StartsWith(data, "RIFF") && data.Length > 12 && data.AsSpan(8, 4).SequenceEqual("WAVE"u8)) return new WavSource(data);
        if (StartsWith(data, "ID3") || data.Length > 2 && data[0] == 0xFF && (data[1] & 0xE0) == 0xE0) return new Mp3Source(data);
        if (data.Length > 8 && data.AsSpan(4, 4).SequenceEqual("ftyp"u8))
            throw new InvalidDataException("AAC and M4A files aren't supported. Use MP3, Ogg Vorbis, WAV or FLAC.");
        throw new InvalidDataException("This isn't a song Haven can read. Use MP3, Ogg Vorbis, WAV or FLAC.");
    }

    private static bool StartsWith(byte[] data, string magic) => data.Length >= magic.Length && data.AsSpan(0, magic.Length).SequenceEqual(System.Text.Encoding.ASCII.GetBytes(magic));
    private static bool FlacAfterId3(byte[] data)
    {
        if (data.Length < 14) return false;
        var skip = 10 + (data[6] << 21 | data[7] << 14 | data[8] << 7 | data[9]);
        return data.Length > skip + 4 && data.AsSpan(skip, 4).SequenceEqual("fLaC"u8);
    }

    private sealed class Mp3Source : IAudioSource
    {
        private readonly MpegFile file;
        public Mp3Source(byte[] data)
        {
            file = new MpegFile(new MemoryStream(data, false));
            if (file.SampleRate <= 0 || file.Channels <= 0) throw new InvalidDataException("This MP3 file has no audio.");
        }
        public int Channels => file.Channels;
        public int SampleRate => file.SampleRate;
        // NLayer's length is an estimate for variable-bit-rate files; the encoder counts as it goes instead.
        public long TotalSamples => -1;
        public int Read(float[] buffer, int count) => file.ReadSamples(buffer, 0, count - count % Channels);
    }

    private sealed class VorbisSource : IAudioSource
    {
        private readonly VorbisReader reader;
        public VorbisSource(byte[] data)
        {
            try { reader = new VorbisReader(new MemoryStream(data, false), true); }
            catch (Exception ex) when (ex is not InvalidDataException) { throw new InvalidDataException("This Ogg file can't be read: " + ex.Message, ex); }
        }
        public int Channels => reader.Channels;
        public int SampleRate => reader.SampleRate;
        public long TotalSamples => reader.TotalSamples;
        public int Read(float[] buffer, int count) => reader.ReadSamples(buffer, 0, count - count % Channels);
    }

    // PCM (8, 16, 24 or 32 bits) or 32-bit float WAV.
    private sealed class WavSource : IAudioSource
    {
        private readonly byte[] data;
        private readonly int bits, start, end;
        private readonly bool isFloat;
        private int at;
        public int Channels { get; }
        public int SampleRate { get; }
        public long TotalSamples => (end - start) / (bits / 8) / Channels;

        public WavSource(byte[] data)
        {
            this.data = data;
            var pos = 12;
            int format = 0;
            while (pos + 8 <= data.Length)
            {
                var id = System.Text.Encoding.ASCII.GetString(data, pos, 4);
                var size = BitConverter.ToInt32(data, pos + 4);
                var body = pos + 8;
                if (size < 0 || body + size > data.Length) size = data.Length - body;
                if (id == "fmt " && size >= 16)
                {
                    format = BitConverter.ToUInt16(data, body);
                    Channels = BitConverter.ToUInt16(data, body + 2);
                    SampleRate = BitConverter.ToInt32(data, body + 4);
                    bits = BitConverter.ToUInt16(data, body + 14);
                    // WAVE_FORMAT_EXTENSIBLE carries the real format in its sub-format GUID.
                    if (format == 0xFFFE && size >= 26) format = BitConverter.ToUInt16(data, body + 24);
                }
                else if (id == "data") { start = body; end = body + size; break; }
                pos = body + size + (size & 1);
            }
            isFloat = format == 3;
            if (format is not (1 or 3) || Channels <= 0 || SampleRate <= 0 || bits is not (8 or 16 or 24 or 32) || isFloat && bits != 32 || end <= start)
                throw new InvalidDataException("This WAV file uses a format Haven can't read. Use PCM or 32-bit float WAV.");
            at = start;
        }

        public int Read(float[] buffer, int count)
        {
            var step = bits / 8; var written = 0;
            count -= count % Channels;
            while (written < count && at + step <= end)
            {
                buffer[written++] = bits switch
                {
                    8 => (data[at] - 128) / 128f,
                    16 => BitConverter.ToInt16(data, at) / 32768f,
                    24 => ((data[at] | data[at + 1] << 8 | (sbyte)data[at + 2] << 16)) / 8388608f,
                    _ => isFloat ? BitConverter.ToSingle(data, at) : BitConverter.ToInt32(data, at) / 2147483648f,
                };
                at += step;
            }
            return written - written % Channels;
        }
    }
}
