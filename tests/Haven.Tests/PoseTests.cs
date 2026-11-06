using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Haven.Core;
using Haven.Core.Animation;
using Haven.Core.Furniture;

internal static class PoseTests
{
    public static async Task Run(Func<string, Action, Task> check, House sample)
    {
        var package = new string('a', 64);
        PoseSet Cuddle(int carrierA = 0, int carrierB = 0) => new()
        {
            Name = "Spooning",
            Package = package,
            Roles = [new PoseRole { Name = "Big spoon", Animation = 0, Carrier = carrierA, Position = [0, .6f, 0] }, new PoseRole { Name = "Little spoon", Animation = 1, Carrier = carrierB, Position = [.3f, .6f, 0], Color = 0x8CC8F2 }],
        };
        House With(params PoseSet[] sets) => sample with { Furnishings = [sample.Furnishings[0] with { Poses = sets }, .. sample.Furnishings.Skip(1)] };

        await check("Pose sets round-trip in a home and their carriers stay unique per animation", () =>
        {
            var home = With(PoseSets.AssignCarriers(sample, Cuddle()), new PoseSet { Name = "Doze", Roles = [new PoseRole { Emote = 88 }] });
            var read = HouseFormat.Parse(HouseFormat.Serialize(home));
            var sets = read.Furnishings[0].Poses!;
            Assert(sets.Length == 2 && sets[0].Roles.All(r => PoseCarriers.Get(r.Carrier) != null) && sets[0].Roles[0].Carrier != sets[0].Roles[1].Carrier);
            Assert(sets[1].Roles[0].Emote == 88 && sets[1].Package == null && sets[1].Roles[0].Carrier == 0);
            // A home without poses writes no poses field (older homes are unchanged).
            Assert(!Encoding.UTF8.GetString(HouseFormat.Serialize(sample)).Contains("poses", StringComparison.OrdinalIgnoreCase));
            // The same animation in a second set keeps its carrier; a different one never shares it.
            var second = PoseSets.AssignCarriers(home, Cuddle() with { Id = Guid.NewGuid(), Name = "Again" });
            Assert(second.Roles[0].Carrier == sets[0].Roles[0].Carrier && second.Roles[1].Carrier == sets[0].Roles[1].Carrier);
            var other = PoseSets.AssignCarriers(home, Cuddle() with { Id = Guid.NewGuid(), Package = new string('b', 64) });
            Assert(other.Roles.All(r => r.Carrier != sets[0].Roles[0].Carrier && r.Carrier != sets[0].Roles[1].Carrier));
            // The preferred carrier depends only on the animation, so it is the same in every home.
            Assert(PoseSets.AssignCarriers(sample, Cuddle()).Roles[0].Carrier == sets[0].Roles[0].Carrier);
        });
        await check("NPCs on pose balls: keys never clash with characters, entries round-trip as NPC messages, and a person keeps a ball an NPC also claims", () =>
        {
            var npc = Guid.NewGuid(); var other = Guid.NewGuid();
            var key = PoseNpcs.Key(npc);
            Assert(PoseNpcs.IsNpc(key) && key == PoseNpcs.Key(npc) && key != PoseNpcs.Key(other) && !PoseNpcs.IsNpc(0x0040_0000_1234_5678));
            var furnishing = Guid.NewGuid(); var set = Guid.NewGuid();
            var entry = new PoseEntry(key, furnishing, set, 1, 1.25f, 12.5f, -7.25f, new(.5f, -.25f, 1.125f), 33.5f);
            var values = PoseNpcs.Encode(entry);
            Assert(values.Length == PoseNpcs.Values && values.Length <= GameMessage.MaxValues);
            // Through the relay's own message check, as an NPC message the relay already carries.
            var message = GameMessage.Decode(new GameMessage(7, GameMessage.Everyone, "npc", "pose", npc, values).Encode());
            Assert(PoseNpcs.Decode(npc, message.Values) == entry);
            // A stand-up (an empty entry), a viewer's, another NPC's key, and nonsense.
            Assert(PoseNpcs.Decode(npc, PoseNpcs.Encode(PoseEntry.None(key))) is { Empty: true } none && none.Character == key);
            Assert(PoseNpcs.Decode(npc, PoseNpcs.Encode(entry with { Role = PoseEntry.Viewing, Scale = 1, Pitch = 0, Roll = 0, Move = default, Turn = 0 })) is { Holds: false, Empty: false });
            Assert(PoseNpcs.Decode(other, values) is { } wrong && wrong.Character == PoseNpcs.Key(other));
            Assert(PoseNpcs.Decode(npc, [.. values[..9], 99_000, .. values[10..]]) == null && PoseNpcs.Decode(npc, values[..10]) == null && PoseNpcs.Decode(npc, null) == null);
            // A person on the ball keeps it; of two NPCs the lower key does; an NPC alone holds it.
            var person = new PoseEntry(0x0040_0000_0000_0001, furnishing, set, 1, 1);
            var merged = PoseNpcs.Merge([person], [entry]).ToArray();
            Assert(merged.Length == 2 && merged.Single(e => e.Character == key).Role == PoseEntry.Viewing);
            var second = entry with { Character = PoseNpcs.Key(other) };
            var pair = PoseNpcs.Merge([], [entry, second]).ToArray();
            Assert(pair.Count(e => e.Holds) == 1 && pair.Single(e => e.Holds).Character == Math.Min(key, second.Character));
            Assert(PoseNpcs.Merge([], [entry]).Single().Holds);
            // An NPC on one ball and a person on the other: the set plays.
            var cuddle = Cuddle() with { Id = set };
            Assert(PoseSessions.Playing(PoseNpcs.Merge([person with { Role = 0 }], [entry]), furnishing, cuddle));
        });
        await check("An NPC's saved pose ball round-trips in a home, gives its entry back, and must name a ball the home has", () =>
        {
            var set = PoseSets.AssignCarriers(sample, Cuddle());
            var home = With(set);
            var furnishing = home.Furnishings[0].Id;
            var seat = new NpcSeat { Furnishing = furnishing, Set = set.Id, Role = 1, Move = [.1f, .25f, -.5f], Turn = 15, Pitch = 5, Roll = -3, Scale = .9f };
            var npc = new HomeNpc { Name = "Maid Bnuuy", Seat = seat };
            var read = HouseFormat.Parse(HouseFormat.Serialize(home with { Npcs = [npc] }));
            Assert(read.Npcs![0].Seat is { } back && back.Furnishing == furnishing && back.Role == 1 && back.Move.SequenceEqual(seat.Move) && back.Scale == .9f);
            // A saved NPC without a ball writes no seat field (older homes are unchanged).
            Assert(!Encoding.UTF8.GetString(HouseFormat.Serialize(home with { Npcs = [npc with { Seat = null }] })).Contains("seat", StringComparison.OrdinalIgnoreCase));
            // Its entry carries the ball's own tilt; a seat made from that entry is the same seat.
            var role = set.Roles[1] with { Pitch = 10 };
            var entry = seat.Entry(PoseNpcs.Key(npc.Id), role);
            Assert(entry.Holds && entry.Role == 1 && entry.Pitch == 15 && entry.Character == PoseNpcs.Key(npc.Id));
            var again = NpcSeat.From(entry, role);
            Assert(again.Pitch == 5 && again.Roll == -3 && again.Move.SequenceEqual(seat.Move) && again.Turn == 15 && again.Scale == .9f);
            // Removing the furnishing (or its set) lets the saved ball go rather than spoil the home.
            var kept = HomeNpcs.DropLostSeats(home with { Npcs = [npc] });
            var lost = HomeNpcs.DropLostSeats(home with { Furnishings = [.. home.Furnishings.Skip(1)], Npcs = [npc] });
            Assert(kept.Npcs![0].Seat != null && lost.Npcs![0].Seat == null);
            // A ball the home doesn't have, an empty set, or an impossible fit is refused.
            foreach (var bad in new[] { seat with { Role = 2 }, seat with { Set = Guid.NewGuid() }, seat with { Furnishing = Guid.NewGuid() }, seat with { Scale = 9 }, seat with { Move = [0, 99, 0] } })
            {
                try { HouseFormat.Parse(HouseFormat.Serialize(home with { Npcs = [npc with { Seat = bad }] })); throw new Exception("A bad NPC seat was accepted."); }
                catch (InvalidDataException) { }
            }
        });
        await check("Invalid pose sets are refused", () =>
        {
            void Refused(House home) { try { HouseFormat.Validate(home); throw new Exception("A bad pose set was accepted."); } catch (InvalidDataException) { } }
            Refused(With(Cuddle(1, 1)));                                                     // two animations on one carrier
            Refused(With(Cuddle()));                                                         // custom roles without carriers
            Refused(With(Cuddle(1, 2) with { Package = null }));                             // no package
            Refused(With(Cuddle(1, 2) with { Roles = [] }));
            Refused(With(Cuddle(1, 2) with { Roles = [.. Enumerable.Range(0, 5).Select(i => new PoseRole { Emote = 88 })] }));
            Refused(With(new PoseSet { Name = "Far", Roles = [new PoseRole { Emote = 88, Position = [0, 0, 99] }] }));
            Refused(With(new PoseSet { Name = " ", Roles = [new PoseRole { Emote = 88 }] }));
            Refused(With(new PoseSet { Name = "Emote and carrier", Roles = [new PoseRole { Emote = 88, Carrier = 3 }] }));
            Refused(With(new PoseSet { Name = "Over-tilted", Roles = [new PoseRole { Emote = 88, Pitch = 400 }] }));
            Refused(With(new PoseSet { Name = "Bad lean", Roles = [new PoseRole { Emote = 88, Roll = float.NaN }] }));
            HouseFormat.Validate(With(new PoseSet { Name = "Lying", Roles = [new PoseRole { Emote = 88, Pitch = -90, Roll = 15 }] }));
            var same = Guid.NewGuid();
            Refused(With(new PoseSet { Id = same, Name = "A", Roles = [new PoseRole { Emote = 88 }] }, new PoseSet { Id = same, Name = "B", Roles = [new PoseRole { Emote = 88 }] }));
            Refused(With(Enumerable.Range(0, 9).Select(i => new PoseSet { Name = "S" + i, Roles = [new PoseRole { Emote = 88 }] }).ToArray()));
            // Unknown fields in a pose ball are refused like any other.
            var json = Encoding.UTF8.GetString(HouseFormat.Serialize(With(new PoseSet { Name = "Doze", Roles = [new PoseRole { Emote = 88 }] })));
            try { HouseFormat.Parse(Encoding.UTF8.GetBytes(json.Replace("\"emote\": 88", "\"emote\": 88, \"speed\": 2"))); throw new Exception("An unknown pose field was accepted."); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException) { }
        });
        await check("A ball's place follows its furnishing's position, turn and scale, and back", () =>
        {
            var item = new Furnishing { ItemId = 1, Position = [2, 0, 3], Rotation = 90, Scale = 2 };
            var role = new PoseRole { Emote = 88, Position = [0, .5f, 1], Rotation = 10 };
            var (position, degrees) = PoseSets.Place(item, role);
            Assert(Vector3.Distance(position, new Vector3(4, 1, 3)) < 1e-4f && Math.Abs(degrees - 100) < 1e-4f);
            Assert(Vector3.Distance(PoseSets.OffsetFor(item, position), role.Offset) < 1e-4f);
            var balls = PoseSets.DefaultPositions(2, new Vector3(-1, 0, -.5f), new Vector3(1, .6f, .5f));
            Assert(balls.Length == 2 && balls.All(b => Math.Abs(b[1] - .6f) < 1e-5f) && balls[0][0] < 0 && balls[1][0] > 0);
        });
        await check("Races borrow the nearest animation a package offers", () =>
        {
            Assert(PoseCarriers.Nearest("c1401", ["c1401", "c0101"]) == "c1401");
            Assert(PoseCarriers.Nearest("c1401", ["c0101", "c0201"]) == "c0201");
            Assert(PoseCarriers.Nearest("c0901", ["c0101", "c0201"]) == "c0101");
            Assert(PoseCarriers.Nearest("c0801", ["c1501"]) == "c1501");
            Assert(PoseCarriers.All.Select(c => c.Emote).Distinct().Count() == PoseCarriers.All.Length && PoseCarriers.Races.Length == 18);
        });
        await check("Pose entries travel strictly and a held ball can't be taken twice", () =>
        {
            var f = Guid.NewGuid(); var s = Guid.NewGuid();
            var entries = new[] { new PoseEntry(7, f, s, 1, 2.2f, 35, -12.5f), PoseEntry.None(8), new PoseEntry(9, f, s, PoseEntry.Viewing, .3f) };
            Assert(PoseWire.Decode(PoseWire.Encode(entries)).SequenceEqual(entries));
            foreach (var bad in new[] { new PoseEntry(1, f, s, 4, 1), new PoseEntry(1, f, s, 0, 3), new PoseEntry(1, f, Guid.Empty, 0, 1), new PoseEntry(1, Guid.Empty, s, 255, 1), new PoseEntry(1, f, s, 0, float.NaN), new PoseEntry(1, f, s, 0, 1, float.NaN), new PoseEntry(1, f, s, 0, 1, 0, 400), new PoseEntry(1, f, s, 0, .2f) })
                try { PoseWire.Encode([bad]); throw new Exception("A bad pose entry was encoded."); } catch (InvalidDataException) { }
            try { PoseWire.Decode(PoseWire.Encode(entries)[..^1]); throw new Exception("A truncated pose entry was read."); } catch (InvalidDataException) { }
            var table = new PoseTable();
            Assert(table.Apply(new(7, f, s, 0, 1)).Role == 0);
            Assert(table.Apply(new(8, f, s, 0, 1)).Role == PoseEntry.Viewing);   // refused: 7 holds it
            Assert(table.Apply(new(8, f, s, 1, 1)).Role == 1);
            var set = new PoseSet { Id = s, Name = "Pair", Roles = [new PoseRole { Emote = 88 }, new PoseRole { Emote = 88 }] };
            Assert(PoseSessions.Playing(table.Entries, f, set) && PoseSessions.Holders(table.Entries, f, set).SequenceEqual(new ulong[] { 7, 8 }));
            Assert(table.Remove(7) == PoseEntry.None(7) && table.Remove(7) == null && !PoseSessions.Playing(table.Entries, f, set) && PoseSessions.Open(table.Entries, f, s));
            table.Keep(sample);   // the sample home has no such set
            Assert(table.Entries.Count == 0);
        });
        await check("Saved fits prefer the same partners, then the latest, and stay bounded", () =>
        {
            var set = Guid.NewGuid();
            var partners = PoseFits.PartnersKey([42, 0, 42]);
            Assert(partners == HomeOwners.Key(42) && !partners.Contains("42,", StringComparison.Ordinal));
            var older = new PoseFit { Character = "me", Set = set, Role = 0, Partners = partners, Offset = [.1f, 0, 0], Saved = DateTimeOffset.UtcNow.AddDays(-1) };
            var newer = new PoseFit { Character = "me", Set = set, Role = 0, Partners = "", Offset = [.2f, 0, 0] };
            var fits = PoseFits.Keep(PoseFits.Keep([], older), newer);
            Assert(PoseFits.Find(fits, "me", set, 0, partners) == older && PoseFits.Find(fits, "me", set, 0, "someone else") == newer && PoseFits.Find(fits, "me", set, 1, "") == null);
            Assert(PoseFits.Keep(fits, newer with { Offset = [.3f, 0, 0] }).Count == 2);
            Assert(!PoseFits.Valid(newer with { Scale = 3 }) && PoseFits.Valid(newer));
        });
        await check("Animation files are checked and renamed for the emote that carries them", () =>
        {
            var pap = Pap(["cbem_pose04_2lp"]);
            PapFiles.Check(pap);
            var renamed = PapFiles.Rename(pap, ["cbem_loop_emot10_2lp"]);
            Assert(PapFiles.Names(renamed).SequenceEqual(["cbem_loop_emot10_2lp"]) && TimelineNames(renamed).SequenceEqual(["cbem_loop_emot10_2lp"]));
            // Two animations, each with its own timeline; only names given are changed.
            var two = Pap(["cbep_u_cry", "cbep_e_u_cry"]);
            var one = PapFiles.Rename(two, ["cbem_sp10_2lp"]);
            Assert(PapFiles.Names(one).SequenceEqual(["cbem_sp10_2lp", "cbep_e_u_cry"]) && TimelineNames(one).SequenceEqual(["cbem_sp10_2lp", "cbep_e_u_cry"]));
            foreach (var broken in new[] { pap[..^1], Encoding.ASCII.GetBytes("pap!").Concat(pap[4..]).ToArray(), Corrupt(pap, 0x12, 0x50), Corrupt(pap, 0x42, 0) })
                try { PapFiles.Check(broken); throw new Exception("A broken animation file passed."); } catch (InvalidDataException) { }
            try { PapFiles.Rename(pap, [new string('x', 40)]); throw new Exception("A long name was accepted."); } catch (ArgumentException) { }
        });
        await check("Animation packages hold chosen animations once per file and serve every race at the carrier", () =>
        {
            var folder = Directory.CreateTempSubdirectory("haven-anim-");
            try
            {
                var shared = Pap(["cbem_pose04_2lp"]);
                var own = Pap(["cbem_kneel"]);
                var files = new Dictionary<string, string>();
                foreach (var race in PoseCarriers.Races.Where(r => r != "c0101")) files[$"chara/human/{race}/animation/a0001/bt_common/emote/pose04_loop.pap"] = "files/a.pap";
                files["chara/human/c0101/animation/a0001/bt_common/emote/pose04_loop.pap"] = "files/b.pap";
                files["chara/human/c0201/animation/a0001/bt_common/emote/kneel.pap"] = "files/c.pap";
                files["chara/monster/m0001/animation/a0001/bt_common/emote/kneel.pap"] = "files/c.pap";   // not a playable race
                Directory.CreateDirectory(Path.Combine(folder.FullName, "files"));
                File.WriteAllBytes(Path.Combine(folder.FullName, "files", "a.pap"), shared);
                File.WriteAllBytes(Path.Combine(folder.FullName, "files", "b.pap"), shared);
                File.WriteAllBytes(Path.Combine(folder.FullName, "files", "c.pap"), own);
                File.WriteAllText(Path.Combine(folder.FullName, "meta.json"), JsonSerializer.Serialize(new { Name = "Couple pack" }));
                File.WriteAllText(Path.Combine(folder.FullName, "default_mod.json"), JsonSerializer.Serialize(new { Files = files }));
                using var mod = PenumbraMod.Open(folder.FullName);
                var candidates = PoseAnimations.Candidates(mod, key => key == "emote/kneel" ? "Kneel (/kneel)" : null);
                Assert(candidates.Count == 2 && candidates[0].Key == "emote/kneel" && candidates[0].Races.Count == 1 && candidates[1].Races.Count == 18);
                var (zip, manifest) = PoseAnimations.Build(mod, "Couple pack", [(candidates[1], "Big spoon"), (candidates[0], "Little spoon")], _ => null);
                var read = PoseAnimations.Read(zip);
                Assert(read.Files.Count == 2 && read.Manifest.Animations.Length == 2 && read.Manifest.Animations[0].Name == "Big spoon" && manifest.Animations[1].Source == "Kneel (/kneel)");
                Assert(PoseAnimations.Build(mod, "Couple pack", [(candidates[1], "Big spoon"), (candidates[0], "Little spoon")], _ => null).Zip.SequenceEqual(zip));
                var hash = ContentLocker.Fingerprint(zip);
                var redirections = PoseAnimations.Redirections([(hash, 1, 24)], h => h == hash ? read.Manifest : null, (p, file, key) => $"{file}|{key}", (_, _) => null, (_, _) => null);
                var carrier = PoseCarriers.Get(24)!.Value;
                Assert(redirections.Count == 36 && redirections[PoseCarriers.PapPath("c1801", carrier.Loop)] == $"{read.Manifest.Animations[1].Races["c0201"]}|{carrier.Loop}");
                // A package someone tampered with is refused.
                Assert(Refuses(() => PoseAnimations.Read(Tamper(zip, "manifest.json", b => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(b).Replace("\"c0201\"", "\"c9999\""))))));
                Assert(Refuses(() => PoseAnimations.Read(Tamper(zip, read.Files.Keys.First(), b => b[..^2]))));
            }
            finally { folder.Delete(true); }
        });
    }

    public static async Task RunMore(Func<string, Action, Task> check)
    {
        await check("A tilt and lean turn the drawn character about its own axes, after its facing", () =>
        {
            var yaw = .7f;
            Assert(Quaternion.Dot(PoseTilt.Rotation(yaw, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw)) > .99999f);
            Assert(Vector3.Distance(Vector3.Transform(Vector3.UnitY, PoseTilt.Rotation(0, 90, 0)), Vector3.UnitZ) < 1e-5f);
            // Lean first, then tilt, then facing: a lean of 90 lays the character on its side whatever it faces.
            var side = Vector3.Transform(Vector3.UnitY, PoseTilt.Rotation(MathF.PI / 2, 0, 90));
            Assert(MathF.Abs(side.Y) < 1e-5f);
            Assert(PoseTilt.Wrap(190) == -170 && PoseTilt.Wrap(-190) == 170 && PoseTilt.Wrap(45) == 45);
        });
        await check("Timeline sounds are listed and moved, and renaming keeps them", () =>
        {
            var pap = Pap(["cbem_dance04_2lp"], "sound/dance.scd");
            PapFiles.Check(pap);
            Assert(PapFiles.Sounds(pap).SequenceEqual(["sound/dance.scd"]));
            var moved = PapFiles.RepointSounds(pap, path => path == "sound/dance.scd" ? "sound/haven/0123456789abcdef.scd" : null);
            Assert(PapFiles.Sounds(moved).SequenceEqual(["sound/haven/0123456789abcdef.scd"]) && PapFiles.Names(moved).SequenceEqual(["cbem_dance04_2lp"]));
            var renamed = PapFiles.Rename(moved, ["cbem_sp10_2lp"]);
            Assert(PapFiles.Sounds(renamed).SequenceEqual(["sound/haven/0123456789abcdef.scd"]) && TimelineNames(renamed).SequenceEqual(["cbem_sp10_2lp"]));
        });
        await check("A mod's own music travels with its animation, at a path of Haven's own; game sounds stay as they are", () =>
        {
            var folder = Directory.CreateTempSubdirectory("haven-anim-sound-");
            try
            {
                var music = Scd(4096);
                Directory.CreateDirectory(Path.Combine(folder.FullName, "files"));
                File.WriteAllBytes(Path.Combine(folder.FullName, "files", "dance.pap"), Pap(["cbem_dance04_2lp"], "sound/dance.scd"));
                File.WriteAllBytes(Path.Combine(folder.FullName, "files", "kneel.pap"), Pap(["cbem_kneel"], "sound/vfx/vanilla.scd"));
                File.WriteAllBytes(Path.Combine(folder.FullName, "files", "dance.scd"), music);
                File.WriteAllText(Path.Combine(folder.FullName, "meta.json"), JsonSerializer.Serialize(new { Name = "Dance!" }));
                File.WriteAllText(Path.Combine(folder.FullName, "default_mod.json"), JsonSerializer.Serialize(new { Files = new Dictionary<string, string>
                {
                    ["chara/human/c0101/animation/a0001/bt_common/emote/dance04_loop.pap"] = "files/dance.pap",
                    ["chara/human/c0101/animation/a0001/bt_common/emote/kneel.pap"] = "files/kneel.pap",
                    ["sound/dance.scd"] = "files/dance.scd",
                } }));
                using var mod = PenumbraMod.Open(folder.FullName);
                var candidates = PoseAnimations.Candidates(mod);
                var (zip, manifest) = PoseAnimations.Build(mod, "Dance!", [.. candidates.Select(c => (c, c.FileName))], _ => null);
                var read = PoseAnimations.Read(zip);
                var name = manifest.Sounds!.Single();
                Assert(read.Files[name].SequenceEqual(music) && name == Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(music))[..16] + ".scd");
                var dance = read.Files[read.Manifest.Animations.Single(a => a.Name == "dance04_loop").Races["c0101"]];
                var kneel = read.Files[read.Manifest.Animations.Single(a => a.Name == "kneel").Races["c0101"]];
                Assert(PapFiles.Sounds(dance).SequenceEqual([PoseAnimations.SoundRoot + name]) && PapFiles.Sounds(kneel).SequenceEqual(["sound/vfx/vanilla.scd"]));
                var hash = ContentLocker.Fingerprint(zip);
                Assert(PoseAnimations.SoundRedirections([hash, hash], h => h == hash ? read.Manifest : null, (p, n) => "disk/" + n).Single().Key == PoseAnimations.SoundRoot + name);
                Assert(Refuses(() => PoseAnimations.Read(Tamper(zip, name, b => b[..^1]))));
                Assert(Refuses(() => PoseAnimations.CheckSound(music[..^4])));
                // The audio entries the game's sound thread reads: no channels, an unknown format, MS-ADPCM without its
                // format block, audio running past the end, a loop past the audio.
                Assert(Refuses(() => PoseAnimations.CheckSound(Scd(4096, channels: 0))) && Refuses(() => PoseAnimations.CheckSound(Scd(4096, format: 0x99)))
                    && Refuses(() => PoseAnimations.CheckSound(Scd(4096, format: 0x0C))) && Refuses(() => PoseAnimations.CheckSound(Corrupt(music, 0x60, 8000)))
                    && Refuses(() => PoseAnimations.CheckSound(Corrupt(music, 0x74, 5000))));
                // An animation entry naming a Havok animation the file doesn't hold.
                var stray = Pap(["dance04_loop"]);
                PapFiles.Check(stray);
                BinaryPrimitives.WriteInt16LittleEndian(stray.AsSpan(0x1A + 34), 1);
                Assert(Refuses(() => PapFiles.Check(stray)));
                // Padding past the recorded size is allowed (the game plays such files).
                PoseAnimations.CheckSound([.. music, .. new byte[16]]);
                // MS-ADPCM audio claiming more than the file holds ([BDG] Prone-Bone's sounds: the file's length less the
                // entry's place) is cut to the whole blocks it holds; a sound that fits comes back as it was.
                var adpcm = Scd(0x94 + 31 * 256, channels: 1, format: 0x0C);
                foreach (var (at, value) in new[] { (0x60, adpcm.Length - 0x60), (0x74, 0), (0x78, 20), (0x80, 0x10002), (0x84, 44100), (0x8C, 0x40100), (0x90, 500 << 16 | 32) })
                    BinaryPrimitives.WriteInt32LittleEndian(adpcm.AsSpan(at), value);
                Assert(Refuses(() => PoseAnimations.CheckSound(adpcm)));
                var mended = PoseAnimations.MendSound(adpcm);
                PoseAnimations.CheckSound(mended);
                Assert(BinaryPrimitives.ReadInt32LittleEndian(mended.AsSpan(0x60)) == 31 * 256 && ReferenceEquals(PoseAnimations.MendSound(mended), mended)
                    && ReferenceEquals(PoseAnimations.MendSound(music), music));
            }
            finally { folder.Delete(true); }
        });
    }

    public static async Task RunProps(Func<string, Action, Task> check)
    {
        await check("A mod's prop gear is found, packed with its own manipulations only, and checked on reading", () =>
        {
            var folder = Directory.CreateTempSubdirectory("haven-anim-prop-");
            try
            {
                byte[] Header(uint magic, int size) { var bytes = new byte[size]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, magic); return bytes; }
                Directory.CreateDirectory(Path.Combine(folder.FullName, "files"));
                File.WriteAllBytes(Path.Combine(folder.FullName, "files", "pose.pap"), Pap(["emot24_loop"]));
                File.WriteAllBytes(Path.Combine(folder.FullName, "files", "chain.mdl"), Header(0x01000006, 0x80));
                File.WriteAllBytes(Path.Combine(folder.FullName, "files", "chain.mtrl"), Header(0x01030000, 0x40));
                // A real 4×4 B8G8R8A8 texture: a prop's texture must hold the mips its header claims (TextureDecoding.CheckMips).
                var chain = new byte[80 + 4 * 4 * 4];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(chain.AsSpan(4), 0x1450);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(chain.AsSpan(8), 4);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(chain.AsSpan(10), 4);
                chain[14] = 1;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(chain.AsSpan(28), 80);
                File.WriteAllBytes(Path.Combine(folder.FullName, "files", "chain_d.tex"), chain);
                File.WriteAllBytes(Path.Combine(folder.FullName, "files", "shirt.mdl"), Header(0x01000006, 0x80));
                object Eqdp(int set, string slot) => new { Type = "Eqdp", Manipulation = new { Gender = "Female", Race = "Miqote", SetId = set, Slot = slot, Entry = 3 } };
                File.WriteAllText(Path.Combine(folder.FullName, "meta.json"), JsonSerializer.Serialize(new
                {
                    FileVersion = 4, Name = "Spread",
                    DefaultData = new
                    {
                        Files = new Dictionary<string, string>
                        {
                            ["chara/human/c0101/animation/a0001/bt_common/emote/emot24_loop.pap"] = "files\\pose.pap",
                            ["chara/accessory/a0053/model/c0101a0053_ear.mdl"] = "files\\chain.mdl",
                            ["chara/accessory/a0053/model/c0201a0053_ear.mdl"] = "files\\chain.mdl",
                            ["chara/accessory/a0053/material/v0001/mt_c0101a0053_ear_a.mtrl"] = "files\\chain.mtrl",
                            ["chara/accessory/a0053/texture/c0101a0053_ear_a_d.tex"] = "files\\chain_d.tex",
                            ["chara/equipment/e0001/model/c0101e0001_top.mdl"] = "files\\shirt.mdl",
                        },
                        Manipulations = new object[]
                        {
                            Eqdp(53, "Ears"), Eqdp(53, "Neck"), Eqdp(1, "Body"),
                            new { Type = "Imc", Manipulation = new { ObjectType = "Accessory", PrimaryId = 53, SecondaryId = 0, Variant = 1, EquipSlot = "Ears", BodySlot = "Unknown" } },
                            new { Type = "Est", Manipulation = new { SetId = 53, Slot = "Ears" } },
                        },
                    },
                }));
                using var mod = PenumbraMod.Open(folder.FullName);
                Assert(mod.Meta.Count == 5);
                var props = PoseProps.Detect(mod);
                Assert(props.Count == 2 && props[0].Slot == "Body" && props[0].Set == 1 && props[1].Slot == "Ears" && props[1].Set == 53);
                var ears = props[1];
                Assert(ears.Paths.Length == 4 && ears.Paths.All(p => p.StartsWith("chara/accessory/a0053/", StringComparison.Ordinal)));
                var candidates = PoseAnimations.Candidates(mod);
                var (zip, manifest) = PoseAnimations.Build(mod, "Spread", [.. candidates.Select(c => (c, "Spread"))], _ => null, [ears]);
                var read = PoseAnimations.Read(zip);
                var prop = read.Manifest.Props!.Single();
                Assert(read.Manifest.Format == 3 && prop.Slot == "Ears" && prop.Set == 53 && prop.Files.Count == 4);
                // Both race models are one file in the package.
                Assert(prop.Files["chara/accessory/a0053/model/c0101a0053_ear.mdl"] == prop.Files["chara/accessory/a0053/model/c0201a0053_ear.mdl"]);
                Assert(prop.Files.Values.All(f => read.Files.ContainsKey(f)));
                // Only the Ears entries for set 53 (Eqdp and Imc) travel; another slot's, another set's and other kinds stay behind.
                Assert(prop.Meta.Length == 2 && prop.Meta.All(m => PoseProps.Concerns(m, "Ears", 53)));
                // Without a prop: format 2, so Havens before props still read it.
                var (plain, plainManifest) = PoseAnimations.Build(mod, "Spread", [.. candidates.Select(c => (c, "Spread"))], _ => null);
                Assert(plainManifest.Format == 2 && plainManifest.Props == null && PoseAnimations.Read(plain).Manifest.Props == null);
                // A package whose prop reaches outside its own gear is refused.
                byte[] Edit(Action<System.Text.Json.Nodes.JsonObject> change) => Tamper(zip, "manifest.json", bytes =>
                {
                    var node = System.Text.Json.Nodes.JsonNode.Parse(bytes)!.AsObject();
                    change(node["props"]![0]!.AsObject());
                    return Encoding.UTF8.GetBytes(node.ToJsonString());
                });
                Assert(Refuses(() => PoseAnimations.Read(Edit(p => { var files = p["files"]!.AsObject(); var first = files.First(); files.Remove(first.Key); files["chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.sklb"] = "f0.mdl"; }))));
                Assert(Refuses(() => PoseAnimations.Read(Edit(p => p["meta"]!.AsArray().Add(JsonSerializer.Serialize(Eqdp(1, "Body")))))));
                Assert(Refuses(() => PoseAnimations.Read(Edit(p => p["slot"] = "Weapon"))));
                Assert(!PoseProps.Concerns("{\"Type\":\"Gmp\",\"Manipulation\":{\"SetId\":53}}", "Ears", 53));
            }
            finally { folder.Delete(true); }
        });
    }

    public static async Task RunEffects(Func<string, Action, Task> check)
    {
        await check("A mod's own effects and their textures travel with its animation at paths of Haven's own; the game's stay", () =>
        {
            var folder = Directory.CreateTempSubdirectory("haven-anim-fx-");
            try
            {
                const string glow = "vfx/emote_sp/mod_emt/eff/glow0f.avfx", sparkle = "vfx/emote_sp/cyalume_emt/eff/em_cyalu_blue_p_0c.avfx";
                const string modTexture = "vfx/emote_sp/mod_emt/texture/glow01s.atex", gameTexture = "vfx/emote_sp/cyalume_emt/texture/chem_lgt01s.atex";
                var effect = Avfx(modTexture, gameTexture);
                var texture = Atex(0x77);
                var vanillaSparkle = Avfx(gameTexture);
                Directory.CreateDirectory(Path.Combine(folder.FullName, "files"));
                File.WriteAllBytes(Path.Combine(folder.FullName, "files", "wave.pap"), Pap(["cbem_sp10_2lp"], glow, tag: "C012"));
                File.WriteAllBytes(Path.Combine(folder.FullName, "files", "sparkle.pap"), Pap(["cbem_sp11_2lp"], sparkle, tag: "C012"));
                File.WriteAllBytes(Path.Combine(folder.FullName, "files", "plain.pap"), Pap(["cbem_sp12_2lp"], "vfx/common/eff/game_only.avfx", tag: "C012"));
                File.WriteAllBytes(Path.Combine(folder.FullName, "files", "glow.avfx"), effect);
                File.WriteAllBytes(Path.Combine(folder.FullName, "files", "glow.atex"), texture);
                File.WriteAllText(Path.Combine(folder.FullName, "meta.json"), JsonSerializer.Serialize(new { Name = "Glow" }));
                File.WriteAllText(Path.Combine(folder.FullName, "default_mod.json"), JsonSerializer.Serialize(new { Files = new Dictionary<string, string>
                {
                    ["chara/human/c0101/animation/a0001/bt_common/emote_sp/sp10_loop.pap"] = "files/wave.pap",
                    ["chara/human/c0101/animation/a0001/bt_common/emote_sp/sp11_loop.pap"] = "files/sparkle.pap",
                    ["chara/human/c0101/animation/a0001/bt_common/emote_sp/sp12_loop.pap"] = "files/plain.pap",
                    [glow] = "files/glow.avfx",
                    // The mod recolours a texture the game's own sparkle uses: that sparkle travels too, pointed at the copy.
                    [gameTexture] = "files/glow.atex",
                } }));
                Assert(PapFiles.Effects(Pap(["x"], glow, tag: "C012")).SequenceEqual([glow]) && PapFiles.Sounds(Pap(["x"], glow, tag: "C012")).Count == 0);
                Assert(AvfxFiles.Textures(effect).SequenceEqual([modTexture, gameTexture]));
                using var mod = PenumbraMod.Open(folder.FullName);
                var candidates = PoseAnimations.Candidates(mod);
                byte[]? Game(string path) => path switch { sparkle => vanillaSparkle, "vfx/common/eff/game_only.avfx" => Avfx("vfx/common/texture/other.atex"), _ => null };
                var (zip, manifest) = PoseAnimations.Build(mod, "Glow", [.. candidates.Select(c => (c, c.FileName))], Game);
                var read = PoseAnimations.Read(zip);
                Assert(read.Manifest.Format == 4 && read.Manifest.Effects!.Length == 3);
                byte[] Anim(string name) => read.Files[read.Manifest.Animations.Single(a => a.Name == name).Races["c0101"]];
                var waveEffect = PapFiles.Effects(Anim("sp10_loop")).Single();
                var sparkleEffect = PapFiles.Effects(Anim("sp11_loop")).Single();
                Assert(waveEffect.StartsWith(PoseAnimations.EffectRoot, StringComparison.Ordinal) && sparkleEffect.StartsWith(PoseAnimations.EffectRoot, StringComparison.Ordinal));
                // An effect the mod doesn't touch (nor its textures) stays the game's.
                Assert(PapFiles.Effects(Anim("sp12_loop")).SequenceEqual(["vfx/common/eff/game_only.avfx"]));
                // Both carried effects point at Haven's copy of the recoloured texture; the mod's own texture path was only
                // in the mod's effect and isn't supplied, so it stays.
                var copy = PoseAnimations.EffectRoot + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(texture))[..16] + ".atex";
                var carried = read.Files[waveEffect[PoseAnimations.EffectRoot.Length..]];
                Assert(AvfxFiles.Textures(carried).SequenceEqual([modTexture, copy]));
                Assert(AvfxFiles.Textures(read.Files[sparkleEffect[PoseAnimations.EffectRoot.Length..]]).SequenceEqual([copy]));
                var hash = ContentLocker.Fingerprint(zip);
                var served = PoseAnimations.EffectRedirections([hash], h => h == hash ? read.Manifest : null, (p, n) => "disk/" + n);
                Assert(served.Count == 3 && served.ContainsKey(waveEffect) && served.ContainsKey(copy));
                // A free trial checks what the package points at without carrying it: here only the base game's effects and
                // textures, never asked about; an effect's texture or a sound from Dawntrail's data, which a trial lacks, is found.
                static bool Trial(string path) => GameDataPaths.Expansion(path) <= 3;
                Assert(PackageNeeds.Missing(read.Files.Select(f => (f.Key, f.Value)), _ => false) == null);
                Assert(PackageNeeds.Missing([("e.avfx", Avfx(modTexture, "bg/ex5/02_ykt_y6/fld/y6f1/texture/y6f1_a1_wate1_d.tex"))], Trial) is { Expansion: 5 });
                Assert(PackageNeeds.Missing([("a.pap", Pap(["x"], "music/ex5/BGM_EX5_Field_01.scd"))], Trial) is { Path: "music/ex5/bgm_ex5_field_01.scd", Expansion: 5 });
                Assert(PackageNeeds.Missing([("a.pap", Pap(["x"], "music/ex3/BGM_EX3_Field_01.scd"))], Trial) == null);
                // Damaged or mismatched effects are refused.
                Assert(Refuses(() => PoseAnimations.Read(Tamper(zip, waveEffect[PoseAnimations.EffectRoot.Length..], b => b[..^4]))));
                Assert(Refuses(() => AvfxFiles.Check(effect[..^4])) && Refuses(() => AvfxFiles.Check(Corrupt(effect, 4, 9))));
                // A walk replacement (several movements in one file, as Geek Time's) isn't offered, and the mod says why.
                File.WriteAllBytes(Path.Combine(folder.FullName, "files", "walk.pap"), Pap(["cbnm_01b_lp0", "cbnm_01f_lp0", "cbnm_02f_f"]));
                PapFiles.Check(File.ReadAllBytes(Path.Combine(folder.FullName, "files", "walk.pap")));
                File.WriteAllText(Path.Combine(folder.FullName, "default_mod.json"), JsonSerializer.Serialize(new { Files = new Dictionary<string, string>
                    { ["chara/human/c0101/animation/a0001/bt_common/resident/move_a.pap"] = "files/walk.pap" } }));
                using (var walk = PenumbraMod.Open(folder.FullName)) Assert(PoseAnimations.Candidates(walk).Count == 0 && PoseAnimations.MovementOnly(walk));
                Assert(!PoseAnimations.MovementOnly(mod));
                // Without effects of the mod's own, the format stays where it was.
                Assert(PoseAnimations.Build(mod, "Plain", [(candidates.Single(c => c.FileName == "sp12_loop"), "Plain")], Game).Manifest.Format == 2);
            }
            finally { folder.Delete(true); }
        });
    }

    public static async Task RunStarts(Func<string, Action, Task> check)
    {
        await check("A start plays before its loop, held effects travel, a treadmill option plays its own run, and options default as the mod sets them", () =>
        {
            var folder = Directory.CreateTempSubdirectory("haven-anim-gym-");
            try
            {
                const string bench = "vfx/lumiry/gym/bench.avfx", pop = "vfx/lumiry/gym/bench_pop.avfx", screen = "vfx/lumiry/gym/treadmill_screen.avfx";
                Directory.CreateDirectory(Path.Combine(folder.FullName, "files"));
                void Write(string name, byte[] bytes) => File.WriteAllBytes(Path.Combine(folder.FullName, "files", name), bytes);
                Write("press_start.pap", Pap(["cbem_loop_emot08_1"], pop, tag: "C012"));
                Write("press_loop.pap", Pap(["cbem_loop_emot08_2lp"], bench, tag: "C173"));
                Write("treadmill.pap", Pap(["cbem_treadmill_01b_lp0", "cbem_treadmill_01f_lp0", "cbem_treadmill_sprint_lp0"]));
                Write("sprint.tmb", Tmb("cbem_treadmill_sprint_lp0", screen));
                Write("screen.avfx", Avfx("vfx/common/texture/c.atex"));
                Write("bench.avfx", Avfx("vfx/common/texture/a.atex"));
                Write("bench_solid.avfx", Avfx("vfx/common/texture/b.atex"));
                Write("pop.avfx", Avfx("vfx/common/texture/a.atex"));
                File.WriteAllText(Path.Combine(folder.FullName, "meta.json"), JsonSerializer.Serialize(new { Name = "Gym" }));
                File.WriteAllText(Path.Combine(folder.FullName, "default_mod.json"), JsonSerializer.Serialize(new { Files = new Dictionary<string, string> { [bench] = "files/bench.avfx", [pop] = "files/pop.avfx", [screen] = "files/screen.avfx" } }));
                File.WriteAllText(Path.Combine(folder.FullName, "group_001_animations.json"), JsonSerializer.Serialize(new { Name = "Animations", Type = "Multi", DefaultSettings = 7, Options = new object[]
                {
                    new { Name = "Bench Press", Files = new Dictionary<string, string>
                    {
                        ["chara/human/c0101/animation/a0001/bt_common/emote/loop_emot08_start.pap"] = "files/press_start.pap",
                        ["chara/human/c0101/animation/a0001/bt_common/emote/loop_emot08_loop.pap"] = "files/press_loop.pap",
                    } },
                    // The same bench press under another option: listed once.
                    new { Name = "Bench Press again", Files = new Dictionary<string, string>
                    {
                        ["chara/human/c0101/animation/a0001/bt_common/emote/loop_emot08_start.pap"] = "files/press_start.pap",
                        ["chara/human/c0101/animation/a0001/bt_common/emote/loop_emot08_loop.pap"] = "files/press_loop.pap",
                    } },
                    new { Name = "Sprint", Files = new Dictionary<string, string>
                    {
                        ["chara/human/c0101/animation/a0001/bt_common/emote/loop_emot11_loop.pap"] = "files/treadmill.pap",
                        ["chara/action/emote/loop_emot11_loop.tmb"] = "files/sprint.tmb",
                    } },
                } }));
                // An option of plain equipment, off by default: the package keeps the mod's default equipment.
                File.WriteAllText(Path.Combine(folder.FullName, "group_002_options.json"), JsonSerializer.Serialize(new { Name = "Options", Type = "Multi", DefaultSettings = 0, Options = new object[]
                    { new { Name = "Solid Color", Files = new Dictionary<string, string> { [bench] = "files/bench_solid.avfx" } } } }));
                Assert(PapFiles.Effects(File.ReadAllBytes(Path.Combine(folder.FullName, "files", "press_loop.pap"))).SequenceEqual([bench]));
                Assert(PapFiles.TimelineAnimations(Tmb("cbem_treadmill_sprint_lp0")).SequenceEqual(["cbem_treadmill_sprint_lp0"]));
                Assert(PapFiles.TimelineEntries(Tmb("cbem_treadmill_sprint_lp0", screen)).SequenceEqual([("C010", "cbem_treadmill_sprint_lp0"), ("C012", screen)]));
                Assert(Refuses(() => PapFiles.CheckTimeline(Tmb("cbem_treadmill_sprint_lp0", screen)[..^2])));
                var three = File.ReadAllBytes(Path.Combine(folder.FullName, "files", "treadmill.pap"));
                var sprint = PapFiles.Only(three, "cbem_treadmill_sprint_lp0");
                Assert(PapFiles.Names(sprint).SequenceEqual(["cbem_treadmill_sprint_lp0"]) && TimelineNames(sprint).SequenceEqual(["cbem_treadmill_sprint_lp0"]));
                Assert(Refuses(() => PapFiles.Only(three, "cbem_nothing")));
                using var mod = PenumbraMod.Open(folder.FullName);
                var candidates = PoseAnimations.Candidates(mod);
                // The start is folded into its loop; the treadmill knows its option's timeline.
                Assert(candidates.Count == 2 && candidates[0].Key == "emote/loop_emot08_loop" && candidates[0].Intro?.Count == 1 && candidates[1].Action != null);
                var (zip, _) = PoseAnimations.Build(mod, "Gym", [.. candidates.Select(c => (c, c.FileName))], _ => null);
                var read = PoseAnimations.Read(zip);
                var press = read.Manifest.Animations[0];
                Assert(read.Manifest.Format == 6 && press.Intro != null && press.Timeline == null);
                Assert(PapFiles.Names(read.Files[press.Intro!["c0101"]]).SequenceEqual(["cbem_loop_emot08_1"]) && PapFiles.Names(read.Files[press.Races["c0101"]]).SequenceEqual(["cbem_loop_emot08_2lp"]));
                var treadmill = read.Manifest.Animations[1];
                Assert(PapFiles.Names(read.Files[treadmill.Races["c0101"]]).SequenceEqual(["cbem_treadmill_sprint_lp0"]));
                // The option's timeline travels (it starts the screen), its effect pointed at Haven's copy.
                var entries = PapFiles.TimelineEntries(read.Files[treadmill.Timeline!]);
                Assert(entries.Single(e => e.Tag == "C012").Value.StartsWith(PoseAnimations.EffectRoot, StringComparison.Ordinal) && entries.Single(e => e.Tag == "C010").Value == "cbem_treadmill_sprint_lp0");
                // Served for a carrier, it plays the carrier's animation.
                var renamed = PapFiles.RewriteTimeline(read.Files[treadmill.Timeline!], (tag, value) => tag == "C010" && value == "cbem_treadmill_sprint_lp0" ? "cbem_sp10_2lp" : null);
                Assert(PapFiles.TimelineAnimations(renamed).SequenceEqual(["cbem_sp10_2lp"]));
                // The held bench travels as the mod's default, not the Solid Color one.
                var held = PapFiles.Effects(read.Files[press.Races["c0101"]]).Single();
                Assert(AvfxFiles.Textures(read.Files[held[PoseAnimations.EffectRoot.Length..]]).SequenceEqual(["vfx/common/texture/a.atex"]));
                // The carrier's start serves the start, its loop the loop.
                var hash = ContentLocker.Fingerprint(zip);
                var carrier = PoseCarriers.Get(1)!.Value;
                var served = PoseAnimations.Redirections([(hash, 0, 1)], h => h == hash ? read.Manifest : null, (_, file, key) => file + "@" + key, (_, _) => null, (_, _) => "c0101");
                Assert(served[PoseCarriers.PapPath("c0101", carrier.Intro)] == press.Intro!["c0101"] + "@" + carrier.Intro && served[PoseCarriers.PapPath("c0101", carrier.Loop)] == press.Races["c0101"] + "@" + carrier.Loop);
                var timelines = PoseAnimations.TimelineRedirections([(hash, 0, 1), (hash, 1, 2)], h => h == hash ? read.Manifest : null, (_, index, key) => $"{index}@{key}");
                Assert(timelines.Count == 1 && timelines[PoseAnimations.TimelinePath(PoseCarriers.Get(2)!.Value.Loop)] == "1@" + PoseCarriers.Get(2)!.Value.Loop);
            }
            finally { folder.Delete(true); }
        });
    }

    // A minimal emote timeline file: one C010 naming the animation it plays (at +32), and a C012 starting an effect (at +20) when given.
    private static byte[] Tmb(string animation, string? effect = null)
    {
        var text = Encoding.ASCII.GetBytes(animation + "\0");
        var path = effect == null ? Array.Empty<byte>() : Encoding.ASCII.GetBytes(effect + "\0");
        var c012 = effect == null ? 0 : 24;
        var strings = 12 + 40 + c012;
        var output = new MemoryStream();
        var writer = new BinaryWriter(output);
        writer.Write(Encoding.ASCII.GetBytes("TMLB")); writer.Write(strings + text.Length + path.Length); writer.Write(effect == null ? 1 : 2);
        writer.Write(Encoding.ASCII.GetBytes("C010")); writer.Write(40); writer.Write((short)1); writer.Write((short)0);
        writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(1065353216);
        writer.Write(strings - (12 + 8)); writer.Write(0);   // the name, counted from the C010's start plus 8
        if (effect != null)
        {
            writer.Write(Encoding.ASCII.GetBytes("C012")); writer.Write(24); writer.Write((short)2); writer.Write((short)30); writer.Write(0); writer.Write(0);
            writer.Write(strings + text.Length - (52 + 8));
        }
        writer.Write(text); writer.Write(path);
        return output.ToArray();
    }

    // A minimal effect file: a version block, one texture block per path, and a model block.
    private static byte[] Avfx(params string[] textures)
    {
        var blocks = new MemoryStream();
        void Block(string name, byte[] data)
        {
            blocks.Write(Encoding.ASCII.GetBytes(name)); blocks.Write(BitConverter.GetBytes(data.Length)); blocks.Write(data);
            while (blocks.Length % 4 != 0) blocks.WriteByte(0);
        }
        Block("reV\0", [1, 0, 0, 0]);
        foreach (var texture in textures) Block("xeT\0", Encoding.ASCII.GetBytes(texture + "\0"));
        Block("ldoM", new byte[24]);
        return [.. "XFVA"u8, .. BitConverter.GetBytes((int)blocks.Length), .. blocks.ToArray()];
    }

    // A minimal effect texture: a header naming 4 x 4 pixels, and some data.
    private static byte[] Atex(byte fill)
    {
        // A real 4×4 B8G8R8A8 texture with one mip, its pixels the fill (effect textures' mips are checked).
        var bytes = new byte[80 + 4 * 4 * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 0x1450);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), 4); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), 4);
        bytes[14] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), 80);
        bytes.AsSpan(80).Fill(fill);
        return bytes;
    }

    public static async Task RunWorldChat(Func<string, Action, Task> check)
    {
        await check("Tells, free company and linkshells are the real-world chat let through; the rest stays in the room or blocked", () =>
        {
            Assert(WorldChat.Command(["/tell", "/t"]) == WorldChannels.Tells && WorldChat.Command(["/reply", "/r"]) == WorldChannels.Tells);
            Assert(WorldChat.Command(["/freecompany", "/fc"]) == WorldChannels.FreeCompany && WorldChat.Command(["/linkshell3", "/l3"]) == WorldChannels.Linkshells);
            Assert(WorldChat.Command(["/cwlinkshell1", "/cwl1"]) == WorldChannels.CrossLinkshells && WorldChat.Command(["/cwlinkshell"]) == WorldChannels.CrossLinkshells);
            Assert(WorldChat.Command(["/party", "/p"]) == WorldChannels.Party && WorldChat.Log(14) == WorldChannels.Party && WorldChat.Log(32) == WorldChannels.Party);
            foreach (var other in new[] { "/say", "/s", "/shout", "/yell", "/em", "/beginner", "/n", "/linkshell9", "/l", "/alliance" })
                Assert(WorldChat.Command([other]) == WorldChannels.None);
            Assert(WorldChat.Log(12) == WorldChannels.Tells && WorldChat.Log(13) == WorldChannels.Tells && WorldChat.Log(24) == WorldChannels.FreeCompany);
            Assert(WorldChat.Log(16) == WorldChannels.Linkshells && WorldChat.Log(23) == WorldChannels.Linkshells && WorldChat.Log(37) == WorldChannels.CrossLinkshells && WorldChat.Log(107) == WorldChannels.CrossLinkshells);
            foreach (var other in new[] { 10, 11, 15, 27, 28, 29, 30, 57 }) Assert(WorldChat.Log(other) == WorldChannels.None);
        });
    }

    public static async Task RunAdjust(Func<string, Action, Task> check)
    {
        await check("Pose entries carry each holder's move and turn, kept within reach, and partners adjust only on the same set", () =>
        {
            Guid f = Guid.NewGuid(), s = Guid.NewGuid();
            var entry = new PoseEntry(7, f, s, 1, 1.1f, 10, -5, new(.25f, -1.5f, 2), -90);
            Assert(PoseWire.Decode(PoseWire.Encode([entry])).Single() == entry);
            Assert(PoseWire.Decode(PoseWire.Encode([entry with { Move = new(0, 12, 0) }])).Single().Move.Y == 12);
            foreach (var bad in new[] { entry with { Move = new(0, 25, 0) }, entry with { Move = new(float.NaN, 0, 0) }, entry with { Turn = 400 } })
                try { PoseWire.Encode([bad]); throw new Exception("A pose entry out of reach was encoded."); } catch (InvalidDataException) { }
            Assert(PoseWire.Limit(new(3, -9, .5f)) == new System.Numerics.Vector3(2, -2, .5f) && PoseWire.Limit(new(3, -30, .5f), far: true) == new System.Numerics.Vector3(3, -20, .5f));
            Assert(PoseWire.Beyond(new(0, 2.5f, 0)) && !PoseWire.Beyond(new(2, -2, 1)));
            var adjust = new PoseAdjust(1, 2, f, s, 0, new(0, -.4f, .1f), 30, 0, 0, .9f);
            Assert(PoseAdjust.Decode(adjust.Encode()) == adjust);
            foreach (var bad in new[] { adjust with { Target = 0 }, adjust with { Move = new(0, 0, 25) }, adjust with { Scale = 3 }, adjust with { Role = 9 } })
                try { bad.Encode(); throw new Exception("A bad pose adjustment was encoded."); } catch (InvalidDataException) { }
            try { PoseAdjust.Decode(adjust.Encode()[..^1]); throw new Exception("A truncated pose adjustment was read."); } catch (InvalidDataException) { }
            var table = new PoseTable();
            table.Apply(new(1, f, s, 0, 1)); table.Apply(new(2, f, s, 1, 1)); table.Apply(new(3, f, s, PoseEntry.Viewing, 1));
            Assert(table.MayAdjust(1, adjust with { Target = 2, Role = 1 }) && !table.MayAdjust(1, adjust with { Target = 2, Role = 0 }));
            Assert(!table.MayAdjust(3, adjust with { Target = 2, Role = 1 }) && !table.MayAdjust(2, adjust with { Target = 2, Role = 1 }) && !table.MayAdjust(1, adjust with { Target = 3, Role = 0 }));
            Assert(!table.MayAdjust(1, adjust with { Target = 2, Role = 1, Set = Guid.NewGuid() }));
            var close = new PoseClose(f, s);
            Assert(PoseClose.Decode(close.Encode()) == close);
            try { PoseClose.Decode(new byte[31]); throw new Exception("A short pose close was read."); } catch (InvalidDataException) { }
            Assert(table.Close(f, s).Length == 3 && table.Entries.Count == 0);
        });
        await check("Axis drags can move at a rate and take bounded steps", () =>
        {
            var drag = AxisDrag.Begin(0, System.Numerics.Vector3.Zero, System.Numerics.Matrix4x4.Identity, new(0, 0, -5), new(0, 0, 1))!;
            // The pointer travels 1 m along X: at a third, a third; with a 3 cm cap, 3 cm.
            var slow = drag.Update(new(1, 0, -5), new(0, 0, 1), 0, false, 1 / 3f)!.Value;
            Assert(Math.Abs(slow.X - 1 / 3f) < .001f);
            var capped = AxisDrag.Begin(0, System.Numerics.Vector3.Zero, System.Numerics.Matrix4x4.Identity, new(0, 0, -5), new(0, 0, 1))!.Update(new(1, 0, -5), new(0, 0, 1), 0, false, 1, .03f)!.Value;
            Assert(Math.Abs(capped.X - .03f) < .0001f);
        });
    }

    public static async Task RunSkeletons(Func<string, Action, Task> check)
    {
        await check("The game's animation load table says whose copy each body reads", () =>
        {
            var table = PapLoadTable.Parse(LoadTable())!;
            Assert(table.Reads("c0801", "emote/a") == "c0101" && table.Reads("c0801", "emote/b") == "c0801" && table.Reads("c0801", "emote/c") == "c0801");
            Assert(table.Reads("c0101", "emote/a") == "c0101" && table.Reads("c0801", "emote/missing") == null && table.Reads("c0301", "emote/a") == null);
            Assert(PapLoadTable.Parse([1, 2, 3]) == null);
        });
        await check("An animation is served only where the game reads it as the body it was made for", () =>
        {
            // A mod made for women's bodies: a Miqo'te woman's file and a Roegadyn woman's.
            var animation = new PoseAnimation("Lex", "", new() { ["c0801"] = "p0.pap", ["c1001"] = "p1.pap" });
            string? Skeleton(string file) => file == "p0.pap" ? "c0801" : file == "p1.pap" ? "c1001" : null;
            Assert(PoseAnimations.FileFor(animation, "c0801", Skeleton) == "p0.pap" && PoseAnimations.FileFor(animation, "c0101", Skeleton) == null);
            // A file made for the Hyur man fits anywhere.
            var dance = new PoseAnimation("Dance", "", new() { ["c0101"] = "d.pap", ["c0901"] = "d.pap" });
            Assert(PoseAnimations.FileFor(dance, "c0901", _ => "c0101") == "d.pap" && PoseAnimations.FileFor(dance, "c0801", _ => "c0101") == "d.pap");
            // A carrier where women read the Hyur man's copy fits no woman; one where every body reads its own fits both.
            var shared = new PoseCarrier(1, "emote/shared", "emote/shared_start"); var own = new PoseCarrier(2, "emote/own", "emote/own_start");
            string? Reads(string race, string key) => key.StartsWith("emote/shared", StringComparison.Ordinal) ? "c0101" : race;
            Assert(PoseAnimations.Fit(animation, shared, Reads, Skeleton) == 0 && PoseAnimations.Fit(animation, own, Reads, Skeleton) == 2);
            // Serving: at the shared carrier every path read is the Hyur man's, which no file fits: the standing pose goes there.
            var manifest = new PoseAnimationManifest(2, "Lex", [animation]);
            var package = new string('c', 64);
            List<(string Path, string Key)> still = [];
            var carrier = Array.FindIndex(PoseCarriers.All, c => c.Emote == 114) + 1;   // Most Gentlemanly: every body its own
            var served = PoseAnimations.Redirections([(package, 0, carrier)], _ => manifest, (_, f, key) => f + "|" + key, (race, _) => race, (_, f) => Skeleton(f), still);
            Assert(served[PoseCarriers.PapPath("c0801", PoseCarriers.All[carrier - 1].Loop)] == "p0.pap|" + PoseCarriers.All[carrier - 1].Loop && still.Count == 32);
            still.Clear();
            Assert(PoseAnimations.Redirections([(package, 0, 1)], _ => manifest, (_, f, key) => f + "|" + key, (_, _) => "c0101", (_, f) => Skeleton(f), still).Count == 0 && still.Count == 2);
        });
        await check("Each animation goes to the carrier that fits it best, and a mod's variants are separate animations", () =>
        {
            var sample = new House { Name = "Fit", TerritoryId = 282, Furnishings = [new() { ItemId = 1 }] };
            var set = new PoseSet { Name = "Pair", Package = new string('d', 64), Roles = [new PoseRole { Animation = 0 }, new PoseRole { Animation = 1 }] };
            var assigned = PoseSets.AssignCarriers(sample, set, (_, animation, carrier) => carrier == 40 + animation ? 18 : carrier == 40 ? 17 : 1);
            Assert(assigned.Roles[0].Carrier == 40 && assigned.Roles[1].Carrier == 41);
            // Three dancing one animation: the first plays its music, the others are silent on a carrier of their own.
            var dance = set with { Roles = [new PoseRole { Animation = 2 }, new PoseRole { Animation = 2 }, new PoseRole { Animation = 2 }] };
            Assert(PoseSets.NeedsSilence(dance));
            var group = PoseSets.AssignCarriers(sample, dance);
            Assert(!group.Roles[0].Silent && group.Roles[1].Silent && group.Roles[2].Silent && group.Roles[1].Carrier == group.Roles[2].Carrier && group.Roles[0].Carrier != group.Roles[1].Carrier);
            Assert(!PoseSets.NeedsSilence(group));
            var housed = sample with { Furnishings = [sample.Furnishings[0] with { Poses = [group] }] };
            PoseSets.Validate(housed);
            Assert(PoseSets.SilentCarriers(housed).Single() == group.Roles[1].Carrier);
            var folder = Directory.CreateTempSubdirectory("haven-anim-options-");
            try
            {
                foreach (var option in new[] { "lock", "normal", "top" }) Directory.CreateDirectory(Path.Combine(folder.FullName, option));
                File.WriteAllBytes(Path.Combine(folder.FullName, "lock", "a.pap"), Pap(["cbem_j_pose01_2lp"], model: 801));
                File.WriteAllBytes(Path.Combine(folder.FullName, "normal", "a.pap"), Pap(["cbem_j_pose01_2lp", "x_face"], model: 801));
                File.WriteAllBytes(Path.Combine(folder.FullName, "top", "b.pap"), Pap(["cbem_j_pose02_2lp"], model: 801));
                const string Bottom = "chara/human/c0801/animation/a0001/bt_common/emote/j_pose01_loop.pap";
                File.WriteAllText(Path.Combine(folder.FullName, "meta.json"), JsonSerializer.Serialize(new { Name = "Lex" }));
                File.WriteAllText(Path.Combine(folder.FullName, "group_001_bottom.json"), JsonSerializer.Serialize(new { Name = "Bottom Options", Type = "Single", Options = new object[]
                {
                    new { Name = "Leg Lock", Files = new Dictionary<string, string> { [Bottom] = "lock/a.pap" } },
                    new { Name = "Normal", Files = new Dictionary<string, string> { [Bottom] = "normal/a.pap" } },
                } }));
                File.WriteAllText(Path.Combine(folder.FullName, "group_002_top.json"), JsonSerializer.Serialize(new { Name = "Top Toggle", Type = "Multi", Options = new object[]
                {
                    new { Name = "Enable", Files = new Dictionary<string, string> { ["chara/human/c0801/animation/a0001/bt_common/emote/j_pose02_loop.pap"] = "top/b.pap" } },
                } }));
                using var mod = PenumbraMod.Open(folder.FullName);
                var candidates = PoseAnimations.Candidates(mod);
                Assert(candidates.Count == 3 && candidates.Count(c => c.Key == "emote/j_pose01_loop") == 2 && candidates.Any(c => c.Option == "Leg Lock") && candidates.Any(c => c.Option == "Normal"));
                Assert(candidates.Single(c => c.Key == "emote/j_pose02_loop").Option == "");
                var (zip, _) = PoseAnimations.Build(mod, "Lex", [.. candidates.Select(c => (c, c.Option.Length > 0 ? c.Option : c.FileName))], _ => null);
                var read = PoseAnimations.Read(zip);
                var legLock = read.Files[read.Manifest.Animations.Single(a => a.Name == "Leg Lock").Races["c0801"]];
                var normal = read.Files[read.Manifest.Animations.Single(a => a.Name == "Normal").Races["c0801"]];
                Assert(PapFiles.Names(legLock).Count == 1 && PapFiles.Names(normal).Count == 2 && PapFiles.Skeleton(legLock) == "c0801");
            }
            finally { folder.Delete(true); }
        });
    }

    // A tiny load table: files emote/a, emote/b and emote/c under bt_common, bodies c0101 and c0801; c0801's first
    // group of four files is overridden (a: the Hyur man's copy, b: its own), c is left to its own copy.
    private static byte[] LoadTable()
    {
        var strings = new MemoryStream(); var folderBase = 0; var nameBase = 0;
        var output = new MemoryStream(); var w = new BinaryWriter(output);
        string[] names = ["emote/a", "emote/b", "emote/c", "emote/d", "emote/e"];
        var folderText = Encoding.ASCII.GetBytes("bt_common\0"); var nameText = names.SelectMany(n => Encoding.ASCII.GetBytes(n + "\0")).ToArray();
        var nameOffsets = new List<uint>(); uint at = 0; foreach (var n in names) { nameOffsets.Add(at); at += (uint)n.Length + 1; }
        var fileCount = names.Length; var entryCount = 2;
        var entryBase = 0x10 + fileCount * 12; var overrideBase = entryBase + entryCount * 168; var overrides = 4 * 8;
        folderBase = overrideBase + overrides; nameBase = folderBase + folderText.Length;
        w.Write((ushort)fileCount); w.Write((ushort)entryCount); w.Write(0); w.Write(folderBase); w.Write(nameBase);
        for (var i = 0; i < fileCount; i++) { w.Write(0); w.Write(0u); w.Write(nameOffsets[i]); }
        // c0101: no overrides. c0801: bit 0 set (files 0-3), first override record 0.
        w.Write(101u); w.Write((ushort)1); w.Write((ushort)0); w.Write(new byte[160]);
        w.Write(801u); w.Write((ushort)1); w.Write((ushort)0); w.Write(1UL); w.Write(new byte[152]);
        foreach (var body in new uint[] { 101, 801, 801, 801 }) { w.Write(body); w.Write((ushort)1); w.Write((ushort)0); }
        w.Write(folderText); w.Write(nameText);
        return output.ToArray();
    }

    // A minimal sound file: "SEDBSSCF", version 3, the whole size at +0x10, tables at 0x30 naming one audio entry (at
    // 0x60, through the table at 0x50): stereo 44.1 kHz Vorbis filling the rest, looped whole.
    private static byte[] Scd(int size, int channels = 2, int format = 0x06)
    {
        var bytes = new byte[size];
        Encoding.ASCII.GetBytes("SEDBSSCF").CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x0E), 0x30);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x10), (uint)size);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x34), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x3C), 0x50);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x50), 0x60);
        var audio = size - 0x80;
        foreach (var (at, value) in new[] { (0x60, audio), (0x64, channels), (0x68, 44100), (0x6C, format), (0x70, 0), (0x74, audio), (0x78, 0) })
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at), value);
        return bytes;
    }

    private static bool Refuses(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } }

    private static byte[] Corrupt(byte[] bytes, int at, int value) { var copy = (byte[])bytes.Clone(); BinaryPrimitives.WriteInt32LittleEndian(copy.AsSpan(at), value); return copy; }

    private static byte[] Tamper(byte[] zip, string entry, Func<byte[], byte[]> change)
    {
        using var input = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        using var output = new MemoryStream();
        using (var copy = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var e in input.Entries)
            {
                using var source = e.Open(); var bytes = new MemoryStream(); source.CopyTo(bytes);
                var data = e.FullName == entry ? change(bytes.ToArray()) : bytes.ToArray();
                using var target = copy.CreateEntry(e.FullName).Open(); target.Write(data);
            }
        return output.ToArray();
    }

    // A minimal .pap laid out as installed ones are: header, info table, a Havok tagfile stub, and one timeline per
    // animation (TMDH, then a C009 naming it, then its string), 4-byte aligned between timelines.
    public static byte[] Pap(string[] names, string? sound = null, ushort model = 101, string tag = "C063")
    {
        var output = new MemoryStream();
        var writer = new BinaryWriter(output);
        writer.Write(Encoding.ASCII.GetBytes("pap ")); writer.Write(0x20001); writer.Write((ushort)names.Length); writer.Write(model); writer.Write((byte)0); writer.Write((byte)0);
        var havok = 0x1A + 40 * names.Length;
        writer.Write(0x1A); writer.Write(havok); writer.Write(havok + 24);
        foreach (var name in names)
        {
            var slot = new byte[32]; Encoding.ASCII.GetBytes(name, slot); writer.Write(slot);
            writer.Write((short)0); writer.Write((short)0); writer.Write(0);
        }
        writer.Write(new byte[] { 0x1E, 0x0D, 0xB0, 0xCA, 0xCE, 0xFA, 0x11, 0xD0 }); writer.Write(new byte[16]);
        for (var i = 0; i < names.Length; i++)
        {
            if (i > 0) while ((output.Length - (havok + 24)) % 4 != 0) writer.Write((byte)0);
            var text = Encoding.ASCII.GetBytes(names[i] + "\0");
            byte[] tune = sound == null || i > 0 ? [] : Encoding.ASCII.GetBytes(sound + "\0");
            var c063 = tune.Length > 0 ? 32 : 0;
            var size = 12 + 16 + 24 + c063 + text.Length + tune.Length;
            writer.Write(Encoding.ASCII.GetBytes("TMLB")); writer.Write(size); writer.Write(c063 > 0 ? 3 : 2);
            writer.Write(Encoding.ASCII.GetBytes("TMDH")); writer.Write(16); writer.Write((short)1); writer.Write((short)0); writer.Write((short)60); writer.Write((short)0);
            var c009 = 12 + 16;
            var strings = 12 + 16 + 24 + c063;
            writer.Write(Encoding.ASCII.GetBytes("C009")); writer.Write(24); writer.Write((short)2); writer.Write((short)0); writer.Write(50); writer.Write(0);
            writer.Write(strings - (c009 + 8));   // the string, counted from the C009's start plus 8
            if (c063 > 0)
            {
                // A sound (loop 1, interrupt 0, path, index 1, position 2) at frame 30, as a dance mod's music.
                var at = c009 + 24;
                writer.Write(Encoding.ASCII.GetBytes(tag)); writer.Write(32); writer.Write((short)3); writer.Write((short)30); writer.Write(1); writer.Write(0);
                writer.Write(strings + text.Length - (at + 8)); writer.Write(1); writer.Write(2);
            }
            writer.Write(text); writer.Write(tune);
        }
        return output.ToArray();
    }

    // The names each timeline's C009 entries point at.
    private static List<string> TimelineNames(byte[] pap)
    {
        var names = new List<string>();
        var first = BinaryPrimitives.ReadInt32LittleEndian(pap.AsSpan(0x16));
        var at = first;
        while (at < pap.Length)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(pap.AsSpan(at + 4)); var items = BinaryPrimitives.ReadInt32LittleEndian(pap.AsSpan(at + 8));
            var item = at + 12;
            for (var i = 0; i < items; i++)
            {
                var length = BinaryPrimitives.ReadInt32LittleEndian(pap.AsSpan(item + 4));
                if (Encoding.ASCII.GetString(pap, item, 4) == "C009")
                {
                    var start = item + 8 + BinaryPrimitives.ReadInt32LittleEndian(pap.AsSpan(item + 20));
                    names.Add(Encoding.ASCII.GetString(pap, start, Array.IndexOf(pap, (byte)0, start) - start));
                }
                item += length;
            }
            at = first + ((at - first + size + 3) & ~3);
        }
        return names;
    }

    private static void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
}
