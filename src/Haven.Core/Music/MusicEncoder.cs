using NVorbis;
using OggVorbisEncoder;

namespace Haven.Core.Music;

/// <summary>A custom song, ready to upload and play: Ogg Vorbis, its length, channels and sample rate.</summary>
public sealed record PreparedSong(byte[] Ogg, long Milliseconds, int Channels, int SampleRate);

/// <summary>
/// Prepares a custom song for the orchestrion. Whatever the player chose (MP3, Ogg Vorbis, WAV or FLAC) becomes Ogg
/// Vorbis of at most <see cref="MaxBytes"/> and <see cref="MaxMilliseconds"/>, mono or stereo at 44.1 or 48 kHz, which
/// the game plays inside an SCD (<see cref="ScdMusic"/>). An Ogg Vorbis file that already fits is kept as it is, so it
/// loses nothing; anything else is encoded on the player's PC, at a lower quality only if the first try is too large.
/// </summary>
public static class MusicEncoder
{
    public const long MaxMilliseconds = 10 * 60 * 1000;
    public const int MaxBytes = 8 * 1024 * 1024;
    /// <summary>The largest file a player may choose (a long WAV is big before it is encoded).</summary>
    public const long MaxInputBytes = 300L * 1024 * 1024;
    private static readonly float[] Qualities = [0.25f, 0.05f, -0.1f];

    public static PreparedSong Prepare(byte[] input, CancellationToken token = default, IProgress<float>? progress = null)
    {
        if (input.Length > MaxInputBytes) throw new InvalidDataException("This file is too large. Choose a song of at most 10 minutes.");
        // An Ogg Vorbis file within the limits goes up unchanged.
        if (input.Length <= MaxBytes && Describe(input) is { } existing && existing.Milliseconds <= MaxMilliseconds
            && existing.Channels <= 2 && existing.SampleRate is 44100 or 48000 && SingleStream(input)) return existing with { Ogg = input };
        foreach (var quality in Qualities)
        {
            token.ThrowIfCancellationRequested();
            var song = Encode(AudioSources.Open(input), quality, token, progress);
            if (song.Ogg.Length <= MaxBytes) return song;
        }
        throw new InvalidDataException("This song is too large even at the lowest quality. Choose a shorter one.");
    }

    /// <summary>
    /// Whether a song is one Haven would have made: what an upload is held to, checked again for a song downloaded from
    /// someone else's home before it reaches the game's orchestrion player (which has crashed on streams it didn't expect).
    /// </summary>
    public static bool Playable(PreparedSong song) => song.Ogg.Length <= MaxBytes && song.Milliseconds is > 0 and <= MaxMilliseconds
        && song.Channels is 1 or 2 && song.SampleRate is 44100 or 48000 && SingleStream(song.Ogg);

    /// <summary>
    /// Whether an Ogg file is one stream of whole pages: the first page begins it, every page carries its serial number,
    /// and nothing else follows. The reader above describes only the first stream, so a second one chained after it (with
    /// other channels or another rate than the SCD's header says) would reach the game's player unchecked.
    /// </summary>
    public static bool SingleStream(byte[] ogg)
    {
        uint? serial = null;
        var at = 0;
        while (at < ogg.Length)
        {
            if (at + 27 > ogg.Length || ogg[at] != 'O' || ogg[at + 1] != 'g' || ogg[at + 2] != 'g' || ogg[at + 3] != 'S' || ogg[at + 4] != 0) return false;
            var begins = (ogg[at + 5] & 0x02) != 0;
            var number = BitConverter.ToUInt32(ogg, at + 14);
            if (serial == null ? !begins : begins || number != serial) return false;
            serial = number;
            int segments = ogg[at + 26], body = 0;
            if (at + 27 + segments > ogg.Length) return false;
            for (var i = 0; i < segments; i++) body += ogg[at + 27 + i];
            at += 27 + segments + body;
        }
        return at == ogg.Length && serial != null;
    }

    /// <summary>An Ogg Vorbis file's length, channels and sample rate, or null when it isn't one.</summary>
    public static PreparedSong? Describe(byte[] ogg)
    {
        if (ogg.Length < 4 || ogg[0] != 'O' || ogg[1] != 'g' || ogg[2] != 'g' || ogg[3] != 'S') return null;
        try
        {
            using var reader = new VorbisReader(new MemoryStream(ogg, false), true);
            if (reader.SampleRate <= 0 || reader.Channels <= 0 || reader.TotalSamples <= 0) return null;
            return new(ogg, reader.TotalSamples * 1000 / reader.SampleRate, reader.Channels, reader.SampleRate);
        }
        catch (Exception) { return null; }
    }

    public static PreparedSong Encode(IAudioSource source, float quality, CancellationToken token = default, IProgress<float>? progress = null)
    {
        var channels = Math.Min(2, source.Channels);
        var rate = source.SampleRate is 44100 or 48000 ? source.SampleRate : 48000;
        if (source.TotalSamples > 0 && source.TotalSamples * 1000 / source.SampleRate > MaxMilliseconds)
            throw new InvalidDataException("This song is longer than 10 minutes. Choose a shorter one.");
        var limit = (long)rate * MaxMilliseconds / 1000;
        var info = VorbisInfo.InitVariableBitRate(channels, rate, quality);
        var ogg = new OggStream(new Random().Next());
        ogg.PacketIn(HeaderPacketBuilder.BuildInfoPacket(info));
        ogg.PacketIn(HeaderPacketBuilder.BuildCommentsPacket(new Comments()));
        ogg.PacketIn(HeaderPacketBuilder.BuildBooksPacket(info));
        using var output = new MemoryStream();
        void Flush(bool force) { while (ogg.PageOut(out var page, force)) { output.Write(page.Header); output.Write(page.Body); } }
        Flush(true);
        var state = ProcessingState.Create(info);
        var resampler = new Resampler(source.SampleRate, rate, channels);
        var input = new float[4096 * source.Channels];
        // One array per channel: the encoder writes every array it is given.
        var planes = Enumerable.Range(0, channels).Select(_ => new float[8192]).ToArray();
        long written = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var read = source.Read(input, input.Length - input.Length % source.Channels);
            if (read <= 0) break;
            var frames = read / source.Channels;
            // Surround folds down to stereo: even channels to the left, odd to the right, scaled to keep the level.
            var mixed = new float[frames * channels];
            var weight = source.Channels > channels ? channels / (float)source.Channels : 1f;
            for (var i = 0; i < frames; i++)
                for (var c = 0; c < source.Channels; c++)
                    mixed[i * channels + (channels == 1 ? 0 : c % 2)] += input[i * source.Channels + c] * weight;
            var samples = resampler.Process(mixed);
            var count = samples.Length / channels;
            if (count == 0) continue;
            if (planes[0].Length < count) planes = [.. Enumerable.Range(0, channels).Select(_ => new float[count])];
            for (var i = 0; i < count; i++) for (var c = 0; c < channels; c++) planes[c][i] = Math.Clamp(samples[i * channels + c], -1f, 1f);
            // The encoder takes at most a few thousand samples per call.
            for (var offset = 0; offset < count; offset += 1024)
            {
                state.WriteData(planes, Math.Min(1024, count - offset), offset);
                while (!ogg.Finished && state.PacketOut(out var packet)) { ogg.PacketIn(packet); Flush(false); }
            }
            written += count;
            if (written > limit) throw new InvalidDataException("This song is longer than 10 minutes. Choose a shorter one.");
            if (source.TotalSamples > 0) progress?.Report(Math.Min(1f, written / (float)((double)source.TotalSamples * rate / source.SampleRate)));
        }
        if (written == 0) throw new InvalidDataException("This song has no audio.");
        state.WriteEndOfStream();
        while (!ogg.Finished && state.PacketOut(out var last)) { ogg.PacketIn(last); Flush(false); }
        Flush(true);
        return new(output.ToArray(), written * 1000 / rate, channels, rate);
    }

    // Linear resampling for rates the game doesn't use (22.05, 32, 96 kHz …); 44.1 and 48 kHz pass through.
    private sealed class Resampler(int from, int to, int channels)
    {
        private double position;
        private float[] previous = new float[channels];
        private bool started;
        public float[] Process(float[] input)
        {
            if (from == to) return input;
            var frames = input.Length / channels;
            var step = (double)from / to;
            var output = new List<float>((int)(frames / step) * channels + channels);
            float Sample(int frame, int c) => frame < 0 ? previous[c] : input[frame * channels + c];
            if (!started) { started = true; for (var c = 0; c < channels; c++) previous[c] = frames > 0 ? input[c] : 0; }
            for (; position < frames - 1; position += step)
            {
                var i = (int)Math.Floor(position); var t = (float)(position - i);
                for (var c = 0; c < channels; c++) output.Add(Sample(i, c) * (1 - t) + Sample(i + 1, c) * t);
            }
            position -= frames;
            if (frames > 0) for (var c = 0; c < channels; c++) previous[c] = input[(frames - 1) * channels + c];
            return [.. output];
        }
    }
}
