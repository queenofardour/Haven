using System.Text;
using Haven.Core;
using Haven.Core.Music;

internal static class MusicTests
{
    public static async Task Run(Func<string, Action, Task> check)
    {
        await check("FLAC decodes verbatim, fixed, LPC and constant subframes, independent and decorrelated stereo", () =>
        {
            var left = Enumerable.Range(0, 64).Select(i => (int)(Math.Sin(i / 5.0) * 9000)).ToArray();
            var right = Enumerable.Range(0, 64).Select(i => (int)(Math.Cos(i / 7.0) * 7000)).ToArray();
            var flac = Flac.Stream(44100, 2, 16, 3 * 64, [
                Flac.Frame(0, 64, 1, w => { Flac.Verbatim(w, left, 16); Flac.Verbatim(w, right, 16); }),
                Flac.Frame(1, 64, 8, w => { Flac.Fixed(w, left, 16, 2); Flac.Verbatim(w, [.. left.Zip(right, (l, r) => l - r)], 17); }),
                Flac.Frame(2, 64, 10, w => { Flac.Lpc(w, left, 16); Flac.Constant(w, 0, 17); }),
            ]);
            var decoder = new FlacDecoder(flac);
            Assert(decoder.Channels == 2 && decoder.SampleRate == 44100 && decoder.TotalSamples == 192);
            var samples = new float[2 * 192 + 2];
            Assert(decoder.Read(samples, samples.Length) == 2 * 192);
            for (var i = 0; i < 64; i++)
            {
                Assert(Near(samples[2 * i], left[i]) && Near(samples[2 * i + 1], right[i]));             // independent
                Assert(Near(samples[128 + 2 * i], left[i]) && Near(samples[128 + 2 * i + 1], right[i])); // left/side
                Assert(Near(samples[256 + 2 * i], left[i]) && Near(samples[256 + 2 * i + 1], left[i]));  // mid/side, equal sides
            }
            static bool Near(float value, int sample) => Math.Abs(value - sample / 32768f) < 1e-6;
        });

        await check("Songs become Ogg Vorbis of the right length; an Ogg Vorbis file that fits passes unchanged", () =>
        {
            var song = MusicEncoder.Prepare(Wav(44100, 2, 16, 2.0));
            Assert(song.Channels == 2 && song.SampleRate == 44100 && Math.Abs(song.Milliseconds - 2000) <= 30);
            Assert(MusicEncoder.Describe(song.Ogg) is { Channels: 2, SampleRate: 44100 } described && Math.Abs(described.Milliseconds - 2000) <= 30);
            Assert(MusicEncoder.Prepare(song.Ogg).Ogg == song.Ogg);
            // A second stream chained after the first is refused for the game (the reader describes only the first).
            var chained = song.Ogg.Concat(MusicEncoder.Prepare(Wav(22050, 1, 8, 0.5)).Ogg).ToArray();
            Assert(MusicEncoder.SingleStream(song.Ogg) && !MusicEncoder.SingleStream(chained) && !MusicEncoder.SingleStream(song.Ogg[..^1])
                && MusicEncoder.Describe(chained) is { } first && !MusicEncoder.Playable(first));
            // Other rates are resampled to 48 kHz; mono stays mono; 8-bit WAV reads.
            var mono = MusicEncoder.Prepare(Wav(22050, 1, 8, 1.0));
            Assert(mono.Channels == 1 && mono.SampleRate == 48000 && Math.Abs(mono.Milliseconds - 1000) <= 30);
            // Longer than ten minutes is refused before encoding; non-audio is refused by content.
            RejectData(() => MusicEncoder.Prepare(Wav(8000, 1, 8, 601)));
            RejectData(() => MusicEncoder.Prepare(Encoding.ASCII.GetBytes("not a song at all, just text")));
            RejectData(() => AudioSources.Open([0, 0, 0, 0x20, .. "ftypM4A "u8.ToArray()]));
        });

        await check("A song is wrapped in an SCD after the template's other sections, as the game's Vorbis music is", () =>
        {
            var template = new byte[0x90];
            "SEDBSSCF"u8.CopyTo(template);
            BitConverter.TryWriteBytes(template.AsSpan(0x10), 0x84);
            BitConverter.TryWriteBytes(template.AsSpan(0x34), (short)1);
            BitConverter.TryWriteBytes(template.AsSpan(0x3C), 0x50);
            BitConverter.TryWriteBytes(template.AsSpan(0x50), 0x60);
            BitConverter.TryWriteBytes(template.AsSpan(0x60), 4);           // data length
            BitConverter.TryWriteBytes(template.AsSpan(0x6C), 6);           // Vorbis
            BitConverter.TryWriteBytes(template.AsSpan(0x7C), 0x01);        // a marker flag, dropped
            Assert(ScdMusic.IsTemplate(template));
            var song = MusicEncoder.Prepare(Wav(48000, 2, 16, 0.5));
            var scd = ScdMusic.Wrap(template, song.Ogg, song.Channels, song.SampleRate);
            Assert(scd.AsSpan(0, 0x60).SequenceEqual(template.AsSpan(0, 0x60).ToArray().Select((b, i) => i is >= 0x10 and < 0x14 ? scd[i] : b).ToArray()));
            Assert(BitConverter.ToInt32(scd, 0x60) == song.Ogg.Length && BitConverter.ToInt32(scd, 0x64) == 2 && BitConverter.ToInt32(scd, 0x68) == 48000);
            Assert(BitConverter.ToInt32(scd, 0x6C) == 6 && BitConverter.ToInt32(scd, 0x7C) == 0);
            // A loop over the whole song in bytes of its audio, from the first page after the Vorbis headers to the end,
            // as orchestrion tracks have: a stream that reaches its end stalls the game's orchestrion player.
            var loopStart = BitConverter.ToInt32(scd, 0x70);
            Assert(BitConverter.ToInt32(scd, 0x74) == song.Ogg.Length && loopStart == ScdMusic.FirstAudioPage(song.Ogg) && loopStart > 0 && loopStart < song.Ogg.Length);
            Assert(song.Ogg.AsSpan(loopStart, 4).SequenceEqual("OggS"u8) && BitConverter.ToInt64(song.Ogg, loopStart + 6) > 0);
            var seek = BitConverter.ToInt32(scd, 0x80 + 0x10) / 4;
            Assert(seek >= 1 && BitConverter.ToInt32(scd, 0x78) == 0x20 + seek * 4);
            var audio = 0x80 + 0x20 + seek * 4;
            Assert(scd.AsSpan(audio, song.Ogg.Length).SequenceEqual(song.Ogg) && BitConverter.ToInt32(scd, 0x10) == audio + song.Ogg.Length && scd.Length % 16 == 0);
            Assert(ScdMusic.IsTemplate(scd));
            Assert(!ScdMusic.IsTemplate(template[..0x40]));
        });

        await check("The Ogg inside a game SCD reads back, plain, with its header XORed (version 2) or all of it scrambled (3)", () =>
        {
            var song = MusicEncoder.Prepare(Wav(44100, 2, 16, 0.5));
            var header = ScdMusic.FirstAudioPage(song.Ogg);
            const int data = 0x60 + 0x20 + 0x20 + 8;
            // A game file's layout: the Vorbis headers after the seek table, apart from the audio pages.
            byte[] Game(byte version, byte xor)
            {
                var stream = song.Ogg.ToArray();
                var audio = song.Ogg.Length - header;
                if (version == 2) for (var i = 0; i < header; i++) stream[i] ^= xor;
                if (version == 3) ScdOgg.Xor3(stream, audio);
                var scd = new byte[data + stream.Length];
                "SEDBSSCF"u8.CopyTo(scd);
                BitConverter.TryWriteBytes(scd.AsSpan(0x0E), (short)0x30);
                BitConverter.TryWriteBytes(scd.AsSpan(0x34), (short)1);
                BitConverter.TryWriteBytes(scd.AsSpan(0x3C), 0x50);
                BitConverter.TryWriteBytes(scd.AsSpan(0x50), 0x60);
                BitConverter.TryWriteBytes(scd.AsSpan(0x60), audio);
                BitConverter.TryWriteBytes(scd.AsSpan(0x64), 2);
                BitConverter.TryWriteBytes(scd.AsSpan(0x68), 44100);
                BitConverter.TryWriteBytes(scd.AsSpan(0x6C), 6);
                BitConverter.TryWriteBytes(scd.AsSpan(0x78), 0x20 + 8 + header);
                scd[0x80] = version; scd[0x81] = 0x20; scd[0x82] = xor;
                BitConverter.TryWriteBytes(scd.AsSpan(0x90), 8);
                BitConverter.TryWriteBytes(scd.AsSpan(0x94), header);
                stream.CopyTo(scd, data);
                return scd;
            }
            foreach (var (version, xor) in new (byte, byte)[] { (2, 0), (2, 0x88), (3, 0xEA) })
                Assert(ScdOgg.Extract(Game(version, xor)).AsSpan().SequenceEqual(song.Ogg));
            Assert(!Game(3, 0).AsSpan(data, 4).SequenceEqual("OggS"u8));
            // Haven's own SCD (version 0, the headers left in the audio) reads back as it was made.
            var template = new byte[0x90];
            "SEDBSSCF"u8.CopyTo(template);
            BitConverter.TryWriteBytes(template.AsSpan(0x0E), (short)0x30);
            BitConverter.TryWriteBytes(template.AsSpan(0x10), 0x84);
            BitConverter.TryWriteBytes(template.AsSpan(0x34), (short)1);
            BitConverter.TryWriteBytes(template.AsSpan(0x3C), 0x50);
            BitConverter.TryWriteBytes(template.AsSpan(0x50), 0x60);
            BitConverter.TryWriteBytes(template.AsSpan(0x60), 4);
            BitConverter.TryWriteBytes(template.AsSpan(0x6C), 6);
            Assert(ScdOgg.Extract(ScdMusic.Wrap(template, song.Ogg, 2, 44100)).AsSpan().SequenceEqual(song.Ogg));
            RejectData(() => ScdOgg.Extract(Game(4, 0)));
            RejectData(() => ScdOgg.Extract(Game(2, 0)[..0x90]));
            RejectData(() => ScdOgg.Extract(Encoding.ASCII.GetBytes(new string('x', 200))));
        });

        await check("Haven recognises its own custom song by the file Penumbra serves, so it can stop it", () =>
        {
            var hash = "4184aea90503b88f274965998bf0fc7c60a9678e8cba0d6fdc3500af5103f880";
            var requested = $"music/haven/{hash[..32]}l2.scd";
            Assert(MusicPlaybackPolicy.SameFile(requested, requested));
            Assert(MusicPlaybackPolicy.SameFile(requested, $@"|default_1|C:\Users\x\pluginConfigs\Haven\content\music\41\{hash}.l2.scd"));
            Assert(MusicPlaybackPolicy.SameFile(requested, $"C:/Users/x/content/music/41/{hash.ToUpperInvariant()}.l2.scd"));
            Assert(!MusicPlaybackPolicy.SameFile(requested, $@"|default_1|C:\content\music\aa\{new string('a', 64)}.l2.scd"));
            // A game track must match exactly.
            Assert(MusicPlaybackPolicy.SameFile("music/ffxiv/Orchestrion/BGM_ORCH_001.scd", "music/ffxiv/Orchestrion/BGM_ORCH_001.scd"));
            // A game track a Penumbra mod replaces loads from the mod's folder under the same game path.
            Assert(MusicPlaybackPolicy.SameFile("music/ffxiv/Orchestrion/BGM_ORCH_001.scd", @"|default_1|C:\Mods\Tunes\music\ffxiv\Orchestrion\BGM_ORCH_001.scd"));
            Assert(!MusicPlaybackPolicy.SameFile("music/ffxiv/Orchestrion/BGM_ORCH_001.scd", @"|default_1|C:\Mods\Tunes\music\ffxiv\Orchestrion\BGM_ORCH_0011.scd"));
            Assert(!MusicPlaybackPolicy.SameFile("music/ffxiv/Orchestrion/BGM_ORCH_001.scd", "music/ffxiv/Orchestrion/BGM_ORCH_002.scd"));
            Assert(!MusicPlaybackPolicy.SameFile("music/haven/short.scd", "music/haven/other.scd"));
        });

        await check("Editing a playing playlist keeps the playing track at the same moment", () =>
        {
            MusicTrack A = new() { OrchestrionId = 1 }, B = new() { OrchestrionId = 2 }, C = new() { CustomMusic = new string('c', 64), Title = "C", Milliseconds = 50_000 };
            static long Length(MusicTrack t) => t.IsCustom ? 50_000 : t.OrchestrionId == 1 ? 100_000 : 60_000;
            long[] Of(MusicOptions o) => [.. o.Playlist.Select(Length)];
            var old = new MusicOptions { Playing = true, StartedAtUnixMs = 1_000_000, GapSeconds = 2, Playlist = [A, B] };
            var now = 1_000_000L + 102_000 + 20_000;                      // 20 s into B
            void Keeps(MusicOptions updated)
            {
                var started = MusicSchedule.Rebase(old, updated, now, Of(old), i => Length(updated.Playlist[i])) ?? throw new Exception("Expected a new start.");
                var slot = MusicSchedule.Locate(updated with { StartedAtUnixMs = started }, now, Of(updated))!.Value;
                Assert(updated.Playlist[slot.PlaylistIndex].OrchestrionId == 2 && !slot.Silent && Math.Abs(slot.RemainingSeconds - 40) < 0.01);
            }
            Keeps(old with { Playlist = [A, B, C] });                      // added after
            Keeps(old with { Playlist = [C, A, B] });                      // added before
            Keeps(old with { Playlist = [B] });                            // another removed
            Keeps(old with { Playlist = [B, A] });                         // moved
            Keeps(old with { Playlist = [A, B], Shuffle = true, ShuffleSeed = 7 });
            Keeps(old with { Playlist = [A, B with { Repeats = 3 }], GapSeconds = 0 });
            // The playing track removed, between tracks, an unknown length, or not playing: nothing to keep.
            Assert(MusicSchedule.Rebase(old, old with { Playlist = [A, C] }, now, Of(old), i => 1000) == null);
            Assert(MusicSchedule.Rebase(old, old with { Playlist = [A, B, C] }, 1_000_000L + 101_000, Of(old), i => 1000) == null);
            Assert(MusicSchedule.Rebase(old, old with { Playlist = [C, A, B] }, now, Of(old), i => i == 0 ? null : 1000) == null);
            Assert(MusicSchedule.Rebase(old with { Playing = false }, old, now, Of(old), i => 1000) == null);
            // The local clock takes the new schedule from where the track is on this PC, even if it began late here.
            var updated = old with { Playlist = [C, A, B] };
            var rebased = updated with { StartedAtUnixMs = MusicSchedule.Rebase(old, updated, now, Of(old), i => Length(updated.Playlist[i]))!.Value };
            var clock = new MusicPlaybackClock();
            clock.Continue(rebased, now, Of(rebased), playedMs: 5_000);
            var local = clock.Locate(rebased, now, Of(rebased), beginPlayback: true)!.Value;
            Assert(rebased.Playlist[local.PlaylistIndex] == B && Math.Abs(local.RemainingSeconds - 55) < 0.01);
        });

        await check("Custom songs join a playlist with a name and length; game and custom tracks never mix fields", () =>
        {
            var song = new MusicTrack { CustomMusic = new string('c', 64), Title = "Rainy Day", Milliseconds = 183_000 };
            var house = new House { Name = "Music test", TerritoryId = 282, Stage = System.Text.Json.JsonDocument.Parse("{\"FormatVersion\":2,\"Objects\":{}}").RootElement.Clone(), Music = new() { Playlist = [new MusicTrack { OrchestrionId = 3 }, song] } };
            HouseFormat.Validate(house);
            Assert(HouseFormat.Parse(HouseFormat.Serialize(house)).Music.Playlist[1] == song);
            foreach (var bad in new[] {
                song with { Title = "" }, song with { Milliseconds = 0 }, song with { Milliseconds = MusicEncoder.MaxMilliseconds + 1 },
                song with { CustomMusic = "not-a-fingerprint" }, song with { OrchestrionId = 3 }, song with { Title = new string('x', 61) },
                new MusicTrack { OrchestrionId = 3, Title = "Named game track" }, new MusicTrack() })
                RejectData(() => HouseFormat.Validate(house with { Music = house.Music with { Playlist = [bad] } }));
            var game = new MusicOptions { Playing = true, Playlist = [new MusicTrack { OrchestrionId = 3 }] };
            var custom = game with { Playlist = [song] };
            var slot = new MusicSlot(0, 0, 0, false, 10);
            Assert(MusicPlaybackPolicy.Key(game, slot, false) != MusicPlaybackPolicy.Key(custom, slot, false));
        });
    }

    private static void Assert(bool value) { if (!value) throw new Exception("Assertion failed."); }
    private static void RejectData(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception("Expected the song to be refused.");
    }

    // A PCM WAV of a quiet tone.
    private static byte[] Wav(int rate, int channels, int bits, double seconds)
    {
        var frames = (int)(rate * seconds); var bytes = bits / 8;
        using var output = new MemoryStream();
        using var w = new BinaryWriter(output);
        w.Write("RIFF"u8); w.Write(36 + frames * channels * bytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)channels); w.Write(rate); w.Write(rate * channels * bytes); w.Write((short)(channels * bytes)); w.Write((short)bits);
        w.Write("data"u8); w.Write(frames * channels * bytes);
        for (var i = 0; i < frames; i++)
            for (var c = 0; c < channels; c++)
            {
                var v = Math.Sin(2 * Math.PI * (330 + 110 * c) * i / rate) * 0.3;
                if (bits == 8) w.Write((byte)(128 + v * 127)); else w.Write((short)(v * 32767));
            }
        return output.ToArray();
    }

    // Writes just enough FLAC to exercise the decoder: STREAMINFO, then fixed-size frames with explicit 16-bit sizes.
    private static class Flac
    {
        public sealed class Bits
        {
            private readonly List<byte> bytes = []; private int current, count;
            public void Write(long value, int n) { for (var i = n - 1; i >= 0; i--) { current = current << 1 | (int)(value >> i & 1); if (++count == 8) { bytes.Add((byte)current); current = count = 0; } } }
            public void Align() { while (count != 0) Write(0, 1); }
            public byte[] ToArray() { Align(); return [.. bytes]; }
        }
        public static byte[] Stream(int rate, int channels, int bps, long total, byte[][] frames)
        {
            var w = new Bits();
            w.Write(0x664C6143, 32);
            w.Write(1, 1); w.Write(0, 7); w.Write(34, 24);
            w.Write(64, 16); w.Write(64, 16); w.Write(0, 24); w.Write(0, 24);
            w.Write(rate, 20); w.Write(channels - 1, 3); w.Write(bps - 1, 5); w.Write(total, 36);
            for (var i = 0; i < 16; i++) w.Write(0, 8);
            return [.. w.ToArray(), .. frames.SelectMany(f => f)];
        }
        public static byte[] Frame(int number, int size, int assignment, Action<Bits> subframes)
        {
            var w = new Bits();
            w.Write(0x3FFE, 14); w.Write(0, 1); w.Write(0, 1);
            w.Write(7, 4); w.Write(0, 4); w.Write(assignment, 4); w.Write(4, 3); w.Write(0, 1);
            w.Write(number, 8); w.Write(size - 1, 16); w.Write(0, 8);
            subframes(w);
            w.Align(); w.Write(0, 16);
            return w.ToArray();
        }
        private static void Header(Bits w, int type) { w.Write(0, 1); w.Write(type, 6); w.Write(0, 1); }
        public static void Constant(Bits w, int value, int bps) { Header(w, 0); w.Write(value, bps); }
        public static void Verbatim(Bits w, int[] samples, int bps) { Header(w, 1); foreach (var s in samples) w.Write(s, bps); }
        public static void Fixed(Bits w, int[] s, int bps, int order)
        {
            Header(w, 8 + order);
            for (var i = 0; i < order; i++) w.Write(s[i], bps);
            Residual(w, [.. Enumerable.Range(order, s.Length - order).Select(i => (long)s[i] - (order == 1 ? s[i - 1] : 2L * s[i - 1] - s[i - 2]))]);
        }
        // Order 1, precision 4, shift 0, coefficient 1: predicts the previous sample.
        public static void Lpc(Bits w, int[] s, int bps)
        {
            Header(w, 32);
            w.Write(s[0], bps); w.Write(3, 4); w.Write(0, 5); w.Write(1, 4);
            Residual(w, [.. Enumerable.Range(1, s.Length - 1).Select(i => (long)s[i] - s[i - 1])]);
        }
        private static void Residual(Bits w, long[] residual)
        {
            const int k = 9;
            w.Write(0, 2); w.Write(0, 4); w.Write(k, 4);
            foreach (var r in residual)
            {
                var u = r >= 0 ? r << 1 : (-r << 1) - 1;
                for (var q = u >> k; q > 0; q--) w.Write(0, 1);
                w.Write(1, 1); w.Write(u & ((1 << k) - 1), k);
            }
        }
    }
}
