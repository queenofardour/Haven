using Haven.Core;

internal static class NpcTests
{
    public static async Task Run(Func<string, Action, Task> check, House sample)
    {
        await check("NPCs travel in the home format, are omitted when there are none, and bad ones are refused", () =>
        {
            var look = new NpcLook { Customize = Enumerable.Range(1, 26).Select(x => (byte)x).ToArray(), Gear = [0x02_03_01_04D2UL, 0, 0, 0, 0, 0, 0, 0, 0, 7] };
            var maid = new HomeNpc { Name = "Maid", Position = [1, 2, 3], Yaw = 1.5f, Look = look };
            var read = HouseFormat.Parse(HouseFormat.Serialize(sample with { Npcs = [maid] }));
            Assert(read.Npcs is [var back] && back.Id == maid.Id && back.Name == "Maid" && back.Position.SequenceEqual([1f, 2, 3]) && back.Yaw == 1.5f
                && back.Look.Customize.SequenceEqual(look.Customize) && back.Look.Gear.SequenceEqual(look.Gear));
            Assert(!System.Text.Encoding.UTF8.GetString(HouseFormat.Serialize(sample)).Contains("npcs", StringComparison.OrdinalIgnoreCase));
            Assert(HomeNpcs.Stored([]) == null && HomeNpcs.Stored([maid]) is { Length: 1 });
            // Shared edits carry NPCs as whole objects, like furnishings.
            var batch = HostedEditing.Create(sample, sample with { Npcs = [maid] });
            Assert(batch.Changes is [{ Path: ["npcs", _] }] && HostedEditing.Apply(sample, batch).Npcs is [{ Name: "Maid" }]);
            var moved = HostedEditing.Create(sample with { Npcs = [maid] }, sample with { Npcs = [maid with { Yaw = 0 }] });
            Assert(moved.Changes.Length == 1 && HostedEditing.Apply(sample with { Npcs = [maid] }, moved).Npcs is [{ Yaw: 0 }]);
            Assert(HostedEditing.Apply(sample with { Npcs = [maid] }, HostedEditing.Create(sample with { Npcs = [maid] }, sample)).Npcs == null);
            void Refused(params HomeNpc[] bad) { try { HouseFormat.Validate(sample with { Npcs = bad }); throw new Exception("A bad NPC was accepted."); } catch (InvalidDataException) { } }
            Refused(maid with { Name = "" });
            Refused(maid with { Name = new string('a', 21) });
            Refused(maid with { Position = [float.NaN, 0, 0] });
            Refused(maid with { Yaw = float.PositiveInfinity });
            Refused(maid, maid);
            Refused(maid with { Look = new() { Customize = new byte[25] } });
            Refused(maid with { Look = new() { Gear = [1UL << 41, 0, 0, 0, 0, 0, 0, 0, 0, 0] } });
            Refused(maid with { Look = look with { Weapons = [1] } });
            // Weapons travel when kept, and looks without them still read.
            var armed = maid with { Look = look with { Weapons = [0x0001_0002_0003_0004UL, ulong.MaxValue] } };
            Assert(HouseFormat.Parse(HouseFormat.Serialize(sample with { Npcs = [armed] })).Npcs is [{ Look.Weapons: [0x0001_0002_0003_0004UL, ulong.MaxValue] }]);
            Assert(!System.Text.Encoding.UTF8.GetString(HouseFormat.Serialize(sample with { Npcs = [maid] })).Contains("weapons", StringComparison.OrdinalIgnoreCase));
            Assert(!armed.Look.SameAs(maid.Look) && maid.Look.SameAs(maid.Look with { }));
            // A Glamourer state travels as base64 text; anything else, or one past the limit, is refused.
            var dressed = maid with { Look = look with { Design = "AbC+/09=" } };
            Assert(HouseFormat.Parse(HouseFormat.Serialize(sample with { Npcs = [dressed] })).Npcs is [{ Look.Design: "AbC+/09=" }]);
            Assert(!dressed.Look.SameAs(maid.Look));
            Refused(maid with { Look = look with { Design = "not base64!" } });
            Refused(maid with { Look = look with { Design = "" } });
            Refused(maid with { Look = look with { Design = new string('A', NpcLook.MaxDesign + 1) } });
            // Away and a resting emote travel when set and are omitted otherwise.
            var resting = maid with { Away = true, Emote = 50 };
            Assert(HouseFormat.Parse(HouseFormat.Serialize(sample with { Npcs = [resting] })).Npcs is [{ Away: true, Emote: 50 }]);
            var plain = System.Text.Encoding.UTF8.GetString(HouseFormat.Serialize(sample with { Npcs = [maid] }));
            Assert(!plain.Contains("\"away\"", StringComparison.Ordinal) && !plain.Contains("\"emote\"", StringComparison.Ordinal));
            Refused(maid with { Emote = 0 });
            // A level travels when set (1 to 100) and is omitted otherwise.
            Assert(HouseFormat.Parse(HouseFormat.Serialize(sample with { Npcs = [maid with { Level = 90 }] })).Npcs is [{ Level: 90 }]);
            Assert(!plain.Contains("\"level\"", StringComparison.Ordinal));
            Refused(maid with { Level = 0 }); Refused(maid with { Level = 101 });
            Refused(Enumerable.Range(0, 9).Select(_ => maid with { Id = Guid.NewGuid() }).ToArray());
        });

        await check("Glamourer states decode from Glamourer's export, and only advanced colours that differ stay applied", () =>
        {
            const string json = """{"FileVersion":1,"Customize":{"SkinColor":{"Value":128,"Apply":true}},"Parameters":{"SkinDiffuse":{"Red":0.92156863,"Green":0.91764706,"Blue":0.92941177,"Apply":true},"HairDiffuse":{"Red":0.9240506,"Green":0.5887411,"Blue":0.6990962,"Apply":true},"MuscleTone":{"Percentage":1,"Apply":true}}}""";
            // As Glamourer exports it: a version byte, then gzip without its trailer.
            var gzip = new MemoryStream();
            using (var zip = new System.IO.Compression.GZipStream(gzip, System.IO.Compression.CompressionLevel.Optimal, true)) zip.Write(System.Text.Encoding.UTF8.GetBytes(json));
            var compressed = gzip.ToArray();
            var exported = Convert.ToBase64String([6, .. compressed.AsSpan(0, compressed.Length - 8)]);
            Assert(GlamourerStates.Decode(exported)?["Customize"]?["SkinColor"]?["Value"]?.GetValue<int>() == 128);
            Assert(GlamourerStates.PinsParameters(exported));
            var plain = GlamourerStates.WithoutParameters(exported)!;
            Assert(!GlamourerStates.PinsParameters(plain) && NpcLook.ValidDesign(plain));
            // The game draws the same skin and tone without them, a different hair colour: only the hair stays applied.
            var drawn = GlamourerStates.Encode(System.Text.Json.Nodes.JsonNode.Parse(json.Replace("0.5887411", "0.31"))!.AsObject());
            var kept = GlamourerStates.Decode(GlamourerStates.KeepChangedParameters(exported, drawn)!)!["Parameters"]!;
            Assert(kept["HairDiffuse"]!["Apply"]!.GetValue<bool>() && !kept["SkinDiffuse"]!["Apply"]!.GetValue<bool>() && !kept["MuscleTone"]!["Apply"]!.GetValue<bool>());
            Assert(GlamourerStates.Pinned(GlamourerStates.KeepChangedParameters(exported, drawn)!).SequenceEqual(["HairDiffuse"]) && GlamourerStates.Pinned(plain).Length == 0);
            Assert(GlamourerStates.Decode("not base64!") == null && GlamourerStates.Decode(Convert.ToBase64String([6, 1, 2])) == null);
            // What shows at a glance: the body and armour a look applies, compared with what a character is drawn as.
            var viera = GlamourerStates.Encode(System.Text.Json.Nodes.JsonNode.Parse("""{"Customize":{"Race":{"Value":8,"Apply":true},"Gender":{"Value":1,"Apply":true},"Clan":{"Value":16,"Apply":true},"Height":{"Value":50,"Apply":true}},"Equipment":{"Body":{"ItemId":40958,"Stain":0,"Apply":true},"Hands":{"ItemId":40959,"Apply":false}}}""")!.AsObject());
            var shown = GlamourerStates.Shown(viera);
            Assert(shown.Count == 4 && shown["Race"] == 8 && shown["Body"] == 40958 && !shown.ContainsKey("Hands") && !shown.ContainsKey("Height"));
            Assert(GlamourerStates.Differences(shown, new Dictionary<string, ulong>(shown)).Length == 0);
            Assert(GlamourerStates.Differences(shown, new Dictionary<string, ulong>(shown) { ["Race"] = 4 }) is ["Race 4, wanted 8"]);
            Assert(GlamourerStates.Shown("not base64!").Count == 0);
        });

        await check("An NPC's mod state tells a guest whether its copy is the owner's, older, or forgotten", () =>
        {
            NpcModFile File(string hash, params string[] paths) => new(paths, hash, 10, null);
            var a = new string('a', 64); var b = new string('b', 64); var c = new string('c', 64);
            var owner = new NpcModSet(Guid.NewGuid(), [File(a, "chara/x/a.mdl"), File(b, "chara/x/b.tex")], "meta", DateTime.UtcNow);
            var (version, layout) = NpcMods.Identify(owner);
            Assert(version > 0 && layout > 0 && NpcMods.Version([b, a], "meta") == version && NpcMods.Version([a, b], "other") != version);
            // A guest's copy whose bytes changed on the way (other fingerprints) is current by the version it recorded.
            var guest = owner with { Files = [File(c, "chara/x/a.mdl"), File(b, "chara/x/b.tex")], Origin = version };
            Assert(NpcMods.Current(guest, version, layout) && NpcMods.Identify(guest).Version == version);
            var changed = owner with { Files = [File(c, "chara/x/a.mdl"), File(b, "chara/x/b.tex")] };
            Assert(!NpcMods.Current(guest, NpcMods.Identify(changed).Version, NpcMods.Identify(changed).Layout));
            // Kept before versions: matched by the game paths it changes.
            var legacy = owner with { Files = [File(c, "chara/X/A.mdl"), File(b, "chara/x/b.tex")] };
            Assert(NpcMods.Current(legacy, 12345, layout) && !NpcMods.Current(legacy, version, NpcMods.Layout(["chara/x/a.mdl"])));
            // The messages: older Haven reads the first value as a share id (always positive), so it ignores them.
            Assert(NpcMods.ReadState(NpcMods.State(owner)) == (version, layout) && NpcMods.ReadState(NpcMods.State(null)) == (0, 0));
            Assert(NpcMods.State(owner)[0] < 0 && NpcMods.ReadState([NpcMods.StateMark, 5, 0]) == null && NpcMods.ReadState([7, 5, 5]) == null);
        });

        await check("NPC game names are in player-name form, unique, and keep a display name already in that form", () =>
        {
            Assert(HomeNpcs.PlayerStyleName("Nora Vale") && HomeNpcs.PlayerStyleName("Y'shtola Rhul") && HomeNpcs.PlayerStyleName("Npc One"));
            Assert(!HomeNpcs.PlayerStyleName("Nora") && !HomeNpcs.PlayerStyleName("nora vale") && !HomeNpcs.PlayerStyleName("Npc 1")
                && !HomeNpcs.PlayerStyleName("A Bc") && !HomeNpcs.PlayerStyleName("Ab Cd Ef") && !HomeNpcs.PlayerStyleName("Ab C--d")
                && !HomeNpcs.PlayerStyleName("Abcdefghijklmnop Qr"));
            HomeNpc Named(string name) => new() { Name = name };
            var names = HomeNpcs.GameNames([Named("Nora Vale"), Named("Nora"), Named("nora!"), Named("Npc 1"), Named("1"), Named("Nora Vale"),
                Named("Mira Kesh"), Named("Abcdefghijklmnopqrst")], avoid: ["Mira Kesh", "Aria Venn"]);
            Assert(HomeNpcs.GameNames([Named("Aria Venn")], ["Mira Kesh", "Aria Venn"]) is ["Aria Haven"]);
            Assert(names[0] == "Nora Vale" && names[1] == "Nora Haven" && names[2] == "Nora Havena" && names[3] == "Npc Haven" && names[4] == "Npc Havena");
            Assert(names[5] == "Nora Havenb" && names[6] == "Mira Haven" && names[7] == "Abcdefghijklmno Haven");
            Assert(names.All(HomeNpcs.PlayerStyleName) && names.Distinct().Count() == names.Length);
            Assert(HomeNpcs.NewName([]) == "Npc One" && HomeNpcs.NewName(["Npc One", "npc two"]) == "Npc Three");
        });

        await check("A copy of a home gives its NPCs new ids and says which old id each came from", () =>
        {
            var tea = new HomeNpc { Name = "Tea Lady" };
            var guard = new HomeNpc { Name = "Gate Guard", Level = 50 };
            var home = new House { Name = "Original", Npcs = [tea, guard] };
            var copy = HomeNpcs.Renumber(home, out var renamed);
            Assert(copy.Npcs is { Length: 2 });
            var (a, b) = (copy.Npcs![0], copy.Npcs[1]);
            Assert(a.Id != tea.Id && b.Id != guard.Id && a.Id != b.Id);
            Assert(renamed.Count == 2 && renamed[tea.Id] == a.Id && renamed[guard.Id] == b.Id);
            Assert(a with { Id = tea.Id } == tea && b with { Id = guard.Id } == guard);
            Assert(ReferenceEquals(HomeNpcs.Renumber(home with { Npcs = null }, out var none).Npcs, null) && none.Count == 0);
        });

        await check("NPC possession messages travel as minigame messages; spoken lines only on \"say\" and within bounds", () =>
        {
            var id = Guid.NewGuid();
            var state = GameMessage.Decode(new GameMessage(1, GameMessage.Everyone, "npc", "state", id, [1000, 0, -2500, 3141, 22, 1]).Encode());
            Assert(state.Values is [1000, 0, -2500, 3141, 22, 1]);
            Assert(GameMessage.Decode(new GameMessage(1, 0, "npc", "say", id, null, "Tea is served.").Encode()).Text == "Tea is served.");
            void Refused(GameMessage bad) { try { GameMessage.Decode(bad.Encode()); throw new Exception("A bad NPC message was accepted."); } catch (InvalidDataException) { } }
            Refused(new GameMessage(1, 0, "npc", "state", id, null, "words"));
            Refused(new GameMessage(1, 0, "npc", "say", id, null, new string('a', GameMessage.MaxText + 1)));
            Refused(new GameMessage(1, 0, "npc", "say", id, null, "linebell"));
        });
        await check("An NPC's Customize+ profile travels packed in its look, round-trips, and bad ones are refused", () =>
        {
            const string json = "{\"Bones\":{\"j_f_dlip_b\":{\"Translation\":{\"X\":0,\"Y\":0,\"Z\":0},\"Scaling\":{\"X\":1,\"Y\":1.3,\"Z\":1}}}}";
            var body = NpcBodies.Pack(json)!;
            Assert(body != null && NpcLook.ValidDesign(body) && NpcBodies.Unpack(body) == json);
            var maid = new HomeNpc { Name = "Maid Bnuuy", Look = new NpcLook { Body = body } };
            var read = HouseFormat.Parse(HouseFormat.Serialize(sample with { Npcs = [maid] }));
            Assert(read.Npcs is [{ Look.Body: var back }] && back == body);
            Assert(!System.Text.Encoding.UTF8.GetString(HouseFormat.Serialize(sample with { Npcs = [maid with { Look = new() }] })).Contains("body", StringComparison.OrdinalIgnoreCase));
            Assert(maid.Look.SameAs(maid.Look with { Body = null }));   // a new body isn't a redraw
            Assert(NpcBodies.Unpack("not base64!") == null && NpcBodies.Unpack(Convert.ToBase64String([1, 2, 3])) == null && NpcBodies.Pack("") == null);
            try { HouseFormat.Validate(sample with { Npcs = [maid with { Look = new() { Body = "@@" } }] }); throw new Exception("A bad body was accepted."); } catch (InvalidDataException) { }
            Assert(HousingWire.ProtocolRevision >= 36);
        });
        await check("An NPC's mod fingerprints travel in minigame messages the relay already passes, and only character files are kept", () =>
        {
            var hashes = Enumerable.Range(0, 25).Select(i => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData([(byte)i]))).ToArray();
            var prints = hashes.Select(NpcMods.Print).ToList();
            var chunks = NpcMods.Chunks(77, prints);
            Assert(chunks.Count == 3 && chunks.All(c => c.Length <= GameMessage.MaxValues));
            var npc = Guid.NewGuid();
            var back = new HashSet<ulong>();
            foreach (var chunk in chunks)
            {
                // Through the relay's own decoding, as any minigame message.
                var message = GameMessage.Decode(new GameMessage(1, 0, "npc", NpcMods.Prints, npc, chunk).Encode());
                var part = NpcMods.Read(77, message.Values);
                Assert(part != null);
                back.UnionWith(part!.Value.Prints);
                Assert(NpcMods.Read(78, message.Values) == null);
            }
            Assert(back.SetEquals(prints));
            Assert(GameMessage.Decode(new GameMessage(1, 0, "npc", NpcMods.Begin, npc, [77, 25, 3, 270]).Encode()).Values is var begin && NpcMods.ReadBegin(begin) == (77, 25, 3));
            Assert(NpcMods.ReadBegin([77, 25, 2, 270]) == null && NpcMods.ReadBegin([77, 0, 0, 0]) == null);
            // 0.57.2: the opening also counts game paths; the announcement and path fingerprints pass the relay too.
            Assert(NpcMods.ReadBegin([77, 25, 3, 270, 30, 3]) == (77, 25, 3) && NpcMods.ReadPaths([77, 25, 3, 270, 30, 3]) == (30, 3) && NpcMods.ReadPaths([77, 25, 3, 270]) == null);
            Assert(NpcMods.PathPrint("chara/a.tex") == NpcMods.PathPrint("chara/a.tex") && NpcMods.PathPrint("chara/a.tex") != NpcMods.PathPrint("chara/b.tex"));
            GameMessage.Decode(new GameMessage(1, 0, "npc", NpcMods.Announce, npc, [77]).Encode());
            // A sync plugin's copy is recognised by the SHA-1 it is named after, in any of its folders.
            Assert(NpcMods.SyncName(@"C:\FFXIVMods\Lightless\downscaled\A59BD99F42AE3114375E6BE0AD1848C16E2ED612.tex") == "a59bd99f42ae3114375e6be0ad1848c16e2ed612");
            Assert(NpcMods.SyncName(@"C:\Penumbra\Mod\top.tex") == null && NpcMods.Print("a59bd99f42ae3114375e6be0ad1848c16e2ed612") == 0xa59bd99f42ae3114UL);
            Assert(NpcMods.ReadSyncMessages([77, 25, 3, 270, 30, 3, 3]) == 3 && NpcMods.ReadSyncMessages([77, 25, 3, 270, 30, 3]) == 0);
            GameMessage.Decode(new GameMessage(1, 0, "npc", NpcMods.SyncPrints, npc, chunks[0]).Encode());
            GameMessage.Decode(new GameMessage(1, 0, "npc", NpcMods.PathPrints, npc, chunks[0]).Encode());
            foreach (var kind in new[] { NpcMods.Finish, NpcMods.Took, NpcMods.Missed }) GameMessage.Decode(new GameMessage(1, 2, "npc", kind, npc, [77, 1, 2]).Encode());
            Assert(NpcMods.Shareable("chara/human/c0801/obj/body/b0001/texture/c0801b0001_base.tex") && NpcMods.Shareable("vfx/haven/x.avfx"));
            Assert(!NpcMods.Shareable("ui/icon/000000/000001.tex") && !NpcMods.Shareable("chara/../ui/a.tex") && !NpcMods.Shareable("chara/human/A.tex") && !NpcMods.Shareable("chara/a.exe"));
            var file = new NpcModFile(["chara/human/c0801/obj/body/b0001/model/c0801b0001_top.mdl"], hashes[0], 1000);
            Assert(NpcMods.Valid(new NpcModSet(npc, [file], "", DateTime.UtcNow)));
            Assert(!NpcMods.Valid(new NpcModSet(npc, [file with { Hash = "ABC" }], "", DateTime.UtcNow)) && !NpcMods.Valid(new NpcModSet(npc, [], "", DateTime.UtcNow))
                && !NpcMods.Valid(new NpcModSet(npc, [file with { GamePaths = ["ui/x.tex"] }], "", DateTime.UtcNow)));
            // Sets record whose they are; sets written before that read back without it (and stay valid).
            var mine = new NpcModSet(npc, [file], "", DateTime.UtcNow, From: HomeOwners.Key(42));
            var read = System.Text.Json.JsonSerializer.Deserialize<NpcModSet>(System.Text.Json.JsonSerializer.Serialize(mine))!;
            Assert(read.From == HomeOwners.Key(42) && read.From != HomeOwners.Key(43) && NpcMods.Valid(read));
            var older = System.Text.Json.JsonSerializer.Deserialize<NpcModSet>(System.Text.Json.JsonSerializer.Serialize(mine with { From = null }).Replace(",\"From\":null", ""))!;
            Assert(older.From == null && NpcMods.Valid(older));
        });
    }

    private static void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
}
