using System.Text;

namespace Haven.Core.Music;

/// <summary>
/// Wraps a custom song (Ogg Vorbis) in the game's music container, SCD, so the orchestrion plays it like any track.
/// Haven ships no game file: it takes an orchestrion SCD from the player's own game data as a template, keeps everything
/// up to its audio entry (sounds, tracks, layouts, attributes), and writes a new last entry holding the Ogg file as it
/// is (Vorbis, unencrypted), with a seek table built from its pages. Layout as read by VFXEditor (Formats/ScdFormat,
/// MIT; see THIRD_PARTY_NOTICES.md): a 0x30-byte header with the file size at 0x10; at 0x30 the sound, track and audio
/// counts; at 0x3C the position of the audio offset table; audio entries last.
/// </summary>
public static class ScdMusic
{
    private const int VorbisFormat = 0x06;

    /// <summary>Whether a game SCD can serve as a template: one audio entry, the last thing in the file.</summary>
    public static bool IsTemplate(byte[] scd) => TemplateAudio(scd) is not null;

    public static byte[] Wrap(byte[] template, byte[] ogg, int channels, int sampleRate)
    {
        if (TemplateAudio(template) is not (int entry, int flags)) throw new InvalidDataException("That SCD can't serve as a music template.");
        var seekStep = 0.1f;
        var seek = SeekTable(ogg, sampleRate, ref seekStep);
        using var output = new MemoryStream();
        using (var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(template, 0, entry);
            writer.Write(ogg.Length);
            writer.Write(channels);
            writer.Write(sampleRate);
            writer.Write(VorbisFormat);
            // A loop over the whole song, in bytes of the audio data, as every orchestrion track has one: the game's
            // orchestrion player never reaches the end of a stream, and one that does left it unable to play anything
            // again (0.34.0). Haven stops or restarts the song on its own schedule before the loop is heard.
            writer.Write(FirstAudioPage(ogg));      // loop start: the first page after the Vorbis headers
            writer.Write(ogg.Length);               // loop end
            writer.Write(0x20 + seek.Count * 4);    // the Vorbis header that follows, without the audio
            writer.Write(flags & ~0x03);            // no marker chunk, not split mono
            writer.Write((short)0);                 // encode mode: plain
            writer.Write((short)0);                 // encode byte
            writer.Write(0);                        // XOR offset
            writer.Write(0);                        // XOR size
            writer.Write(seekStep);
            writer.Write(seek.Count * 4);
            writer.Write(0);                        // Vorbis header size: the headers stay in the Ogg data
            writer.Write(0);
            writer.Write(0);
            foreach (var position in seek) writer.Write(position);
            writer.Write(ogg);
            var unpadded = output.Length;
            while (output.Length % 16 != 0) writer.Write((byte)0);
            writer.Flush();
            output.Position = 0x10;
            writer.Write((int)unpadded);
        }
        return output.ToArray();
    }

    // The template's only audio entry (its offset and flags), which must be the last thing in the file.
    private static (int Entry, int Flags)? TemplateAudio(byte[] scd)
    {
        if (scd.Length < 0x60 || !scd.AsSpan(0, 8).SequenceEqual("SEDBSSCF"u8)) return null;
        var audioCount = BitConverter.ToInt16(scd, 0x34);
        var table = BitConverter.ToInt32(scd, 0x3C);
        if (audioCount != 1 || table <= 0 || table + 4 > scd.Length) return null;
        var entry = BitConverter.ToInt32(scd, table);
        if (entry <= table || entry + 0x20 > scd.Length) return null;
        // Nothing may follow the audio entry: its header, sub-info and data reach the end of the file (padding aside).
        var dataLength = BitConverter.ToInt32(scd, entry);
        var subInfo = BitConverter.ToInt32(scd, entry + 0x18);
        var end = (long)entry + 0x20 + subInfo + dataLength;
        if (dataLength < 0 || subInfo < 0 || end > scd.Length || end < scd.Length - 16) return null;
        return (entry, BitConverter.ToInt32(scd, entry + 0x1C));
    }

    /// <summary>The byte offset of the first Ogg page after the three Vorbis header packets.</summary>
    public static int FirstAudioPage(byte[] ogg)
    {
        var packets = 0;
        for (var at = 0; at + 27 <= ogg.Length && ogg.AsSpan(at, 4).SequenceEqual("OggS"u8);)
        {
            if (packets >= 3) return at;
            int segments = ogg[at + 26], body = 0;
            if (at + 27 + segments > ogg.Length) break;
            for (var i = 0; i < segments; i++)
            {
                var lace = ogg[at + 27 + i];
                body += lace;
                if (lace < 255) packets++;
            }
            at += 27 + segments + body;
        }
        throw new InvalidDataException("The song has no audio after its Vorbis headers.");
    }

    // Byte offsets of the Ogg pages at each seek step, as the game's own Vorbis music has them (after VFXEditor).
    private static List<int> SeekTable(byte[] ogg, int sampleRate, ref float seekStep)
    {
        var table = new List<int>();
        for (var at = 0; at + 14 <= ogg.Length; at++)
        {
            if (ogg[at] != 'O' || ogg[at + 1] != 'g' || ogg[at + 2] != 'g' || ogg[at + 3] != 'S' || ogg[at + 4] != 0) continue;
            var samples = BitConverter.ToInt32(ogg, at + 6);
            var time = (float)samples / sampleRate;
            // A page timed past any song Haven takes is not one (a huge granule would grow the table without end).
            if (!float.IsFinite(time) || time > MusicEncoder.MaxMilliseconds / 1000f + 60) continue;
            if (table.Count == 1 && time > seekStep) seekStep = time;
            while (seekStep * table.Count - time < 0.02f) table.Add(at);
        }
        return table;
    }
}
