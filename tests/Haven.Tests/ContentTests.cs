using System.Text;
using Haven.Core;

internal static class ContentTests
{
    public static async Task Run(Func<string, Action, Task> check)
    {
        await check("Failed server deletes are remembered per uploading character, wait longer after each failure, and are dropped once the server would have expired them", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"haven-pending-{Guid.NewGuid():N}.json");
            try
            {
                var now = new DateTimeOffset(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);
                var pending = new PendingDeletes(path);
                pending.Add(ServerContent.Furniture, "aa", "owner1", now);
                pending.Add(ServerContent.Furniture, "aa", "owner1", now);       // the same delete once
                pending.Add(ServerContent.Picture, "bb", "owner2", now);
                pending.Add(ServerContent.Song, "cc", "", now);                  // no character: nothing to retry as
                Assert(pending.All.Count == 2 && pending.Due("owner1", now).Count == 0 && pending.Due("owner1", now + PendingDeletes.FirstRetry).Count == 1);
                Assert(pending.Due("owner3", now.AddHours(1)).Count == 0);
                var entry = pending.Due("owner1", now.AddMinutes(1))[0];
                pending.Failed(entry, now.AddMinutes(1));
                Assert(pending.Due("owner1", now.AddMinutes(2)).Count == 0 && pending.Due("owner1", now.AddMinutes(4)).Count == 1);
                for (var i = 0; i < 20; i++) pending.Failed(entry, now);
                Assert(new PendingDeletes(path).Due("owner1", now + PendingDeletes.LongestRetry).Count == 1);   // kept on disk, waits at most an hour
                pending.Done(entry);
                Assert(new PendingDeletes(path).All.Count == 1 && new PendingDeletes(path).Due("owner2", now + PendingDeletes.GiveUpAfter + TimeSpan.FromDays(1)).Count == 0
                    && new PendingDeletes(path).All.Count == 0);
                File.WriteAllText(path, "not json");
                Assert(new PendingDeletes(path).All.Count == 0);
            }
            finally { File.Delete(path); File.Delete(path + ".tmp"); }
        });

        await check("Content locker tickets sign exactly what the Worker checks", () =>
        {
            // The same vectors as infra/cloudflare/test/content.test.mjs.
            var key = new string('k', 40);
            Assert(ContentLocker.Sign(key, ContentLocker.Canonical("get", new string('a', 64), 0, "", 0, 1793000000)) == "ZXdRh38x2NORnm10hnnBp_xvlNDgqPaA14J-ssQ6FA0");
            Assert(ContentLocker.Sign(key, ContentLocker.Canonical("put", new string('b', 64), 12345, "0123456789abcdef", 1, 1793000600)) == "6mKFO9fzhhtU7ZY0th3pGUWF_d2GUKBHJfVW3cEQrc4");
            // An upload by a verified character names it (by), signed too.
            Assert(ContentLocker.Sign(key, ContentLocker.Canonical("put", new string('b', 64), 12345, "0123456789abcdef", 1, 1793000600, 4001)) == "EDmDLT01uHDSqp5VkV82zGGqgt-6dJxclPWIgZAxyn4");
            Assert(ContentLocker.TicketPath(key, "put", new string('b', 64), 12345, "0123456789abcdef", 1, DateTimeOffset.UnixEpoch, 4001).Contains("&by=4001&sig="));
            Assert(!ContentLocker.TicketPath(key, "put", new string('b', 64), 12345, "0123456789abcdef", 1, DateTimeOffset.UnixEpoch).Contains("by="));
            // A song's ticket adds the line "music" (and media=music); pictures' tickets are unchanged.
            Assert(ContentLocker.Sign(key, ContentLocker.Canonical("put", new string('b', 64), 12345, "0123456789abcdef", 1, 1793000600, 4001, ContentLocker.Music)) == "RohGXrQAvsJoW2ZRPKsJtJxSHnNgTZg9BH7p8BxHtkg");
            Assert(ContentLocker.TicketPath(key, "get", new string('b', 64), 0, "", 0, DateTimeOffset.UnixEpoch, media: ContentLocker.Music).Contains("&media=music&sig="));
            Assert(ContentLocker.Sign(key, ContentLocker.Canonical("put", new string('b', 64), 12345, "0123456789abcdef", 1, 1793000600, 4001, ContentLocker.FurnitureMedia)) == "GAmm_JIJWmao-ds75DlBifajL8Qhz4ekItv7gxNo0j8");
            Assert(ContentLocker.TicketPath(key, "get", new string('b', 64), 0, "", 0, DateTimeOffset.UnixEpoch, media: ContentLocker.FurnitureMedia).Contains("&media=furniture&sig="));
            Assert(!ContentLocker.TicketPath(key, "get", new string('b', 64), 0, "", 0, DateTimeOffset.UnixEpoch).Contains("media="));
            var now = DateTimeOffset.FromUnixTimeSeconds(1793000000);
            var path = ContentLocker.TicketPath(key, "get", new string('a', 64), 0, "", 0, now);
            Assert(path == $"/content/{new string('a', 64)}?op=get&size=0&owner=&agreement=0&exp=1793000600&sig="
                + ContentLocker.Sign(key, ContentLocker.Canonical("get", new string('a', 64), 0, "", 0, 1793000600)));
            Assert(ContentLocker.IsFingerprint(ContentLocker.Fingerprint(Encoding.UTF8.GetBytes("picture"))));
            Assert(!ContentLocker.IsFingerprint(new string('A', 64)) && !ContentLocker.IsFingerprint(new string('a', 63)) && !ContentLocker.IsFingerprint(null));
            // The Worker accepts only lowercase owner keys. A content ID alone (public) no longer names an owner: the
            // character's secret does, so claiming someone's content ID gets a different key.
            var secret = ContentLocker.NewSecret();
            Assert(ContentLocker.IsSecret(secret) && !ContentLocker.IsSecret("short") && !ContentLocker.IsSecret(new string('a', 40) + "/") && !ContentLocker.IsSecret(null));
            var ownerKey = ContentLocker.OwnerKey(key, 0x0040000112345678, secret);
            Assert(System.Text.RegularExpressions.Regex.IsMatch(ownerKey, "^[0-9a-f]{16}$"));
            Assert(ownerKey == ContentLocker.OwnerKey(key, 0x0040000112345678, secret));
            Assert(ownerKey != ContentLocker.OwnerKey(key, 0x0040000112345678, ContentLocker.NewSecret()));
            Assert(ownerKey != ContentLocker.OwnerKey("another-content-key-0123456789abcdef", 0x0040000112345678, secret));
            // A ticket path from the relay must be a locker path for that picture on the relay's own origin.
            var good = new ContentTicket("download", new string('a', 64), $"/content/{new string('a', 64)}?op=get&size=0&owner=&agreement=0&exp=1&sig=abc_-");
            Assert(good.Url("https://relay.example", new string('a', 64)).ToString().StartsWith("https://relay.example/content/"));
            foreach (var evil in new[] { "@evil.example/content/x", $"/content/{new string('a', 64)}?op=get@evil.example", $"//evil.example/content/{new string('a', 64)}?op=get",
                $"/content/{new string('b', 64)}?op=get", $"/content/{new string('a', 64)}/../x?op=get", $"/content/{new string('a', 64)}?op=get#x", "https://evil.example/" })
                Reject(() => (good with { Path = evil }).Url("https://relay.example", new string('a', 64)));
            Reject(() => good.Url("https://user@relay.example", new string('a', 64)));
            Reject(() => (good with { Path = null }).Url("https://relay.example", new string('a', 64)));
        });
        await check("Ticket requests are bounded; anything else is refused before the relay acts on it", () =>
        {
            var hash = new string('c', 64);
            Assert(ContentWire.DecodeRequest(ContentWire.Encode(new ContentTicketRequest("upload", hash, 1000, 1))) == new ContentTicketRequest("upload", hash, 1000, 1));
            // A request from before songs (no media) is a picture's; a song may be up to 8 MB.
            Assert(ContentWire.DecodeRequest("{\"kind\":\"download\",\"hash\":\""u8.ToArray().Concat(Encoding.ASCII.GetBytes(hash)).Concat("\"}"u8.ToArray()).ToArray()).Media == ContentLocker.Picture);
            var song = new ContentTicketRequest("upload", hash, ContentLocker.MaxMusicBytes, 1, Media: ContentLocker.Music);
            Assert(ContentWire.DecodeRequest(ContentWire.Encode(song)) == song);
            foreach (var bad in new[] { new ContentTicketRequest("erase", hash), new ContentTicketRequest("download", "x"),
                new ContentTicketRequest("upload", hash, ContentLocker.MaxPictureBytes + 1, 1), new ContentTicketRequest("upload", hash, -1, 1),
                song with { Size = ContentLocker.MaxMusicBytes + 1 }, song with { Media = "video" }, song with { Media = null! } })
                Reject(() => ContentWire.DecodeRequest(ContentWire.Encode(bad)));
            Reject(() => ContentWire.DecodeRequest(new byte[600]));
        });
        await check("A frame's custom picture is a fingerprint, on catalogue furnishings only, never beside a painting", () =>
        {
            var hash = new string('d', 64);
            var home = new House { TerritoryId = 282, Stage = System.Text.Json.JsonSerializer.SerializeToElement(new { FormatVersion = 2, Objects = new { } }) };
            var frame = new Furnishing { ItemId = 6500, CustomPicture = hash };
            Assert(HouseFormat.Parse(HouseFormat.Serialize(home with { Furnishings = [frame] })).Furnishings[0].CustomPicture == hash);
            Assert(!Encoding.UTF8.GetString(HouseFormat.Serialize(home with { Furnishings = [frame with { CustomPicture = null }] })).Contains("customPicture"));
            foreach (var bad in new[] { frame with { CustomPicture = "not a hash" }, frame with { Picture = 4 }, frame with { CustomPicture = hash.ToUpperInvariant() } })
                Reject(() => HouseFormat.Serialize(home with { Furnishings = [bad] }));
            var custom = home with { TerritoryId = CustomCanvas.TerritoryId, CustomLayout = true };
            Reject(() => HouseFormat.Serialize(custom with { Furnishings = [new() { BuildingPart = "bgcommon/hou/dyna/lmp/lp/0030/asset/lmp_s0_m0030.sgb", CustomPicture = hash }] }));
        });
        await check("Framing fills the frame's window, carries the picture past it, and turns for portrait frames", () =>
        {
            byte[] Image(int w, int h, Func<int, int, byte[]> colour) => Enumerable.Range(0, w * h).SelectMany(i => colour(i % w, i / w)).ToArray();
            byte[] red = [255, 0, 0, 255], blue = [0, 0, 255, 255];
            byte[] At(byte[] texture, double u, double v) => texture.AsSpan(((int)(v * PictureCanvas.Height) * PictureCanvas.Width + (int)(u * PictureCanvas.Width)) * 4, 4).ToArray();
            double U(double across) => PictureCanvas.Left + across * (PictureCanvas.Right - PictureCanvas.Left);
            double V(double down) => PictureCanvas.Top + down * (PictureCanvas.Bottom - PictureCanvas.Top);
            // A 5:3 picture, red left and blue right, fills a landscape frame's window exactly; white lies beyond it.
            var wide = Image(500, 300, (x, _) => x < 250 ? red : blue);
            var landscape = PictureCanvas.Compose(wide, 500, 300, new(), false);
            Assert(At(landscape, U(.25), V(.5)).SequenceEqual(red) && At(landscape, U(.75), V(.5)).SequenceEqual(blue));
            Assert(At(landscape, .05, V(.5)).SequenceEqual(new byte[] { 255, 255, 255, 255 }));
            // A 3:5 picture, red above and blue below, in a portrait frame: the frame turns a quarter counter-clockwise,
            // so the view's top is the texture's right.
            var tall = Image(300, 500, (_, y) => y < 250 ? red : blue);
            var portrait = PictureCanvas.Compose(tall, 300, 500, new(), true);
            Assert(At(portrait, U(.75), V(.5)).SequenceEqual(red) && At(portrait, U(.25), V(.5)).SequenceEqual(blue));
            var shown = PictureCanvas.Visible(portrait, true, 30, 50);
            Assert(shown.AsSpan(0, 4).SequenceEqual(red) && shown.AsSpan(shown.Length - 4, 4).SequenceEqual(blue));
            // Turning the wide picture once clockwise puts its red half on top, as the portrait view shows it.
            var turned = PictureCanvas.Turn(wide, 500, 300, 1, out var tw, out var th);
            Assert(tw == 300 && th == 500 && turned.AsSpan(0, 4).SequenceEqual(red) && turned.AsSpan(turned.Length - 4, 4).SequenceEqual(blue));
            // Fit shows the whole wide picture in a portrait frame, with black borders above and below when chosen.
            var fit = PictureCanvas.FitZoom(500, 300, true);
            var bordered = PictureCanvas.Compose(wide, 500, 300, new(Zoom: fit, Dark: true), true);
            Assert(At(bordered, U(.98), V(.5)).SequenceEqual(new byte[] { 0, 0, 0, 255 }) && At(bordered, U(.5), V(.25)).SequenceEqual(red));
            // A huge picture is averaged down rather than sampled, and moved framing shifts what shows.
            var huge = Image(5000, 3000, (x, _) => x % 2 == 0 ? red : blue);
            var mixed = At(PictureCanvas.Compose(huge, 5000, 3000, new(), false), U(.5), V(.5));
            Assert(mixed[0] is > 100 and < 155 && mixed[2] is > 100 and < 155);
            Assert(At(PictureCanvas.Compose(wide, 500, 300, new(X: .75), false), U(.5), V(.5)).SequenceEqual(red));
            foreach (var bad in new PictureLayout[] { new(Turns: 4), new(Zoom: 0), new(Zoom: double.NaN), new(X: 9) })
                Reject(() => PictureCanvas.Compose(wide, 500, 300, bad, false));
            Reject(() => PictureCanvas.Compose(new byte[3], 1, 1, new(), false));
            // Portrait pictures say so in the file, so every PC offers them only to portrait frames.
            byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, 1, 2, 3];
            var marked = PictureCanvas.MarkPortrait(jpeg);
            Assert(PictureCanvas.IsPortrait(marked) && !PictureCanvas.IsPortrait(jpeg) && marked[^3..].SequenceEqual(jpeg[^3..]) && marked[0] == 0xFF && marked[1] == 0xD8);
            Reject(() => PictureCanvas.MarkPortrait([1, 2, 3, 4]));
            // A picture framed again from itself (no kept original) hangs where it did: its colours stay where they were.
            foreach (var upright in new[] { false, true })
            {
                var again = PictureCanvas.Compose(upright ? portrait : landscape, PictureCanvas.Width, PictureCanvas.Height, PictureCanvas.AsPlaced(upright), upright);
                var before = PictureCanvas.Visible(upright ? portrait : landscape, upright, 30, 30);
                var after = PictureCanvas.Visible(again, upright, 30, 30);
                Assert(before.AsSpan((22 * 30 + 22) * 4, 3).SequenceEqual(after.AsSpan((22 * 30 + 22) * 4, 3)));
                Assert(before.AsSpan((7 * 30 + 7) * 4, 3).SequenceEqual(after.AsSpan((7 * 30 + 7) * 4, 3)));
            }
        });
        await check("A frame carries its framing with the home; each framing builds its own picture from one original", () =>
        {
            var hash = new string('e', 64);
            var home = new House { TerritoryId = 282, Stage = System.Text.Json.JsonSerializer.SerializeToElement(new { FormatVersion = 2, Objects = new { } }) };
            var framed = new Furnishing { ItemId = 6500, CustomPicture = hash, Framing = new PictureLayout(1, 1.5, .4, .6, true) };
            Assert(HouseFormat.Parse(HouseFormat.Serialize(home with { Furnishings = [framed] })).Furnishings[0].Framing == framed.Framing);
            Assert(!Encoding.UTF8.GetString(HouseFormat.Serialize(home with { Furnishings = [framed with { Framing = null }] })).Contains("framing", StringComparison.OrdinalIgnoreCase));
            foreach (var bad in new[] { framed with { CustomPicture = null }, framed with { Framing = new PictureLayout(Zoom: 0) }, framed with { Framing = new PictureLayout(Turns: 7) } })
                Reject(() => HouseFormat.Serialize(home with { Furnishings = [bad] }));
            // One original, different framings or orientations: different pictures; the same: the same picture.
            var key = PictureCanvas.FrameKey(hash, framed.Framing, false);
            Assert(key == PictureCanvas.FrameKey(hash, new PictureLayout(1, 1.5, .4, .6, true), false) && ContentLocker.IsFingerprint(key));
            Assert(key != PictureCanvas.FrameKey(hash, framed.Framing, true) && key != PictureCanvas.FrameKey(hash, framed.Framing with { X = .41 }, false));
            Assert(PictureCanvas.FrameKey(hash, null, false) == PictureCanvas.FrameKey(hash, new PictureLayout(), false));
        });
        await check("A portrait frame stands upright about its canvas centre, and only catalogue frames can", () =>
        {
            var home = new House { TerritoryId = 282, Stage = System.Text.Json.JsonSerializer.SerializeToElement(new { FormatVersion = 2, Objects = new { } }) };
            var frame = new Furnishing { ItemId = 6500, Portrait = true, Rotation = 90, Position = [1, 2, 3] };
            Assert(HouseFormat.Parse(HouseFormat.Serialize(home with { Furnishings = [frame] })).Furnishings[0].Portrait);
            Assert(!Encoding.UTF8.GetString(HouseFormat.Serialize(home with { Furnishings = [frame with { Portrait = false }] })).Contains("portrait", StringComparison.OrdinalIgnoreCase));
            // The canvas's width (local X) now runs up, and its centre stays where it was.
            var matrix = PlacementMath.ItemMatrix(frame);
            Assert(System.Numerics.Vector3.Distance(System.Numerics.Vector3.TransformNormal(System.Numerics.Vector3.UnitX, matrix), System.Numerics.Vector3.UnitY) < 1e-5f);
            Assert(System.Numerics.Vector3.Distance(System.Numerics.Vector3.Transform(System.Numerics.Vector3.Zero, matrix), new(1, 2, 3)) < 1e-5f);
            var custom = home with { TerritoryId = CustomCanvas.TerritoryId, CustomLayout = true };
            Reject(() => HouseFormat.Serialize(custom with { Furnishings = [new() { BuildingPart = "bgcommon/hou/dyna/lmp/lp/0030/asset/lmp_s0_m0030.sgb", Portrait = true }] }));
        });
        await check("A custom picture's texture and material point only at Haven's own paths", () =>
        {
            var hash = ContentLocker.Fingerprint(Encoding.UTF8.GetBytes("frame"));
            Assert(CustomPictureFiles.TexturePath(hash).StartsWith("bgcommon/hou/indoor/pic/hv/" + hash[..32] + "/", StringComparison.Ordinal));
            // 4 x 2 BGRA with mips 2 x 1 and 1 x 1, after an 80-byte header.
            var rgba = Enumerable.Range(0, 8).SelectMany(_ => new byte[] { 10, 20, 30, 255 }).ToArray();
            var tex = CustomPictureFiles.Texture(rgba, 4, 2, new byte[80]);
            Assert(tex.Length == 80 + 32 + 8 + 4 && BitConverter.ToUInt32(tex, 4) == 0x1450 && BitConverter.ToUInt16(tex, 8) == 4 && BitConverter.ToUInt16(tex, 10) == 2);
            Assert(tex[14] == 3 && BitConverter.ToUInt32(tex, 28) == 80 && BitConverter.ToUInt32(tex, 32) == 112 && BitConverter.ToUInt32(tex, 36) == 120);
            Assert(tex[80] == 30 && tex[81] == 20 && tex[82] == 10 && tex[^4] == 30);
            // A material with one texture, "bgcommon/hou/indoor/pic/ta/0000/texture/x_d.tex", and a shader name after it.
            var first = Encoding.UTF8.GetBytes("bgcommon/hou/indoor/pic/ta/0000/texture/x_d.tex\0shader.shpk\0\0\0");
            var placeholder = new byte[16 + 4 + first.Length + 6];
            BitConverter.GetBytes((ushort)placeholder.Length).CopyTo(placeholder, 4);
            BitConverter.GetBytes((ushort)first.Length).CopyTo(placeholder, 8);
            placeholder[12] = 1;
            first.CopyTo(placeholder, 20);
            new byte[] { 1, 2, 3, 4, 5, 6 }.CopyTo(placeholder, 20 + first.Length);
            var material = CustomPictureFiles.Material(placeholder, CustomPictureFiles.TexturePath(hash));
            var table = BitConverter.ToUInt16(material, 8); var offset = BitConverter.ToUInt16(material, 16);
            Assert(BitConverter.ToUInt16(material, 4) == material.Length && material[^6..].SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6 }));
            var text = Encoding.UTF8.GetString(material, 20 + offset, table - offset);
            Assert(text.StartsWith(CustomPictureFiles.TexturePath(hash) + "\0", StringComparison.Ordinal));
            Assert(Encoding.UTF8.GetString(material, 20, first.Length).StartsWith("bgcommon/hou/indoor/pic/ta/0000/texture/x_d.tex\0shader.shpk", StringComparison.Ordinal));
            Reject(() => CustomPictureFiles.Material(new byte[20], "x"));
        });
    }
    private static void Assert(bool value) { if (!value) throw new Exception("Content locker assertion failed."); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException) { return; }
        throw new Exception("Invalid content locker data was accepted.");
    }
}
