using Haven.Core;

// Saved NPCs (NpcRoster) and visiting NPCs (VisitingNpcs).
internal static class NpcRosterTests
{
    private static readonly NpcLook Look = new() { Customize = Enumerable.Range(1, 26).Select(x => (byte)x).ToArray(), Gear = [0x02_03_01_04D2UL, 0, 0, 0, 0, 0, 0, 0, 0, 7] };

    public static async Task Run(Func<string, Action, Task> check, House sample)
    {
        await check("Saved NPCs are kept per character, atomically, and a damaged file reads as empty", () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "haven-roster-" + Guid.NewGuid().ToString("N"));
            try
            {
                var file = Path.Combine(folder, "npc-roster.json");
                string me = HomeOwners.Key(0x0040000112345678), alt = HomeOwners.Key(0x00400001ABCDEF01);
                var maid = new HomeNpc { Name = "Mira Haven", Level = 50, Emote = 50, Position = [1, 2, 3], Look = Look with { Design = "AbC=", Body = "QkFE" } };
                // The mods kept for an NPC follow it into Your NPCs (under the entry's id), and go when the entry goes.
                var copied = new List<(Guid From, Guid To)>();
                var dropped = new List<Guid>();
                var roster = new NpcRoster(file) { CopyMods = (from, to) => copied.Add((from, to)), DropMods = dropped.Add };
                var saved = roster.Add(me, maid);
                Assert(copied is [var copy] && copy.From == maid.Id && copy.To == saved.Id && dropped.Count == 0);
                Assert(saved.Id != maid.Id && saved.Source == maid.Id && saved.Name == "Mira Haven" && saved.Level == 50 && saved.Emote == 50 && saved.Look.Design == "AbC=" && saved.Look.Body == "QkFE");
                Assert(roster.Of(alt).Count == 0 && roster.Of("").Count == 0);
                roster = new NpcRoster(file) { CopyMods = roster.CopyMods, DropMods = roster.DropMods };
                Assert(roster.Of(me) is [{ Name: "Mira Haven" } back] && back.Look.SameAs(maid.Look));
                roster.Rename(me, saved.Id, "Mira Tealeaf");
                Assert(new NpcRoster(file).Find(me, saved.Id)?.Name == "Mira Tealeaf");
                Refused(() => roster.Rename(me, saved.Id, "mira"));
                Refused(() => roster.Add("", maid));
                // Brought into a home: a new id every time, and the saved spot is where it's put.
                var home = saved.ToHome([4, 5, 6], 1);
                Assert(home.Id != saved.Id && home.Id != maid.Id && home.Position.SequenceEqual([4f, 5, 6]) && home.Yaw == 1 && home.Seat == null && !home.Away);
                Assert(saved.ToHome([0, 0, 0], 0).Id != home.Id);
                HouseFormat.Validate(sample with { Npcs = [home] });
                // Turning visitors away is remembered per character.
                roster.SetRefusing(me, true);
                Assert(new NpcRoster(file).Refuses(me) && !new NpcRoster(file).Refuses(alt));
                roster.Rename(me, saved.Id, "Mira Haven");
                Assert(dropped.Count == 0);
                roster.Remove(me, saved.Id);
                Assert(dropped is [var gone] && gone == saved.Id);
                roster.Remove(me, saved.Id);
                Assert(dropped.Count == 1);
                Assert(new NpcRoster(file).Of(me).Count == 0 && Directory.GetFiles(folder).Length == 1);
                for (var i = 0; i < NpcRoster.Max; i++) roster.Add(me, maid);
                Refused(() => roster.Add(me, maid));
                File.WriteAllText(file, "{ not json");
                Assert(new NpcRoster(file).Of(me).Count == 0);
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        });

        await check("A visiting NPC travels in pieces any relay passes, and only a whole, valid one for its id reads back", () =>
        {
            var saved = new RosterNpc { Name = "Mira Haven", Level = 9, Look = Look with { Design = Random(9000), Body = Random(5000) } };
            var visitor = VisitingNpc.From(saved, [1, 2, 3], 0.5f, VisitRights.MeAndHost);
            Assert(visitor.Valid && visitor.Id == saved.Id && visitor.AsHomeNpc() is { Away: false, Seat: null } shown && shown.Id == visitor.Id && shown.Name == "Mira Haven");
            var parts = VisitingNpcs.Encode(visitor, 77);
            Assert(parts.Count > 1 && parts.All(p => p.Values is [VisitingNpcs.Part, 77, _, _] && p.Text.Length <= VisitingNpcs.PieceChars));
            // Each piece is a minigame message the relay (GameMessage.Decode) accepts, and older Haven ignores (a negative first value on "say").
            foreach (var (values, text) in parts)
            {
                var message = new GameMessage(1, GameMessage.Everyone, "npc", VisitingNpcs.Kind, visitor.Id, values, text);
                var bytes = message.Encode();
                Assert(bytes.Length <= GameMessage.MaxBytes && VisitingNpcs.IsVisit(GameMessage.Decode(bytes)));
            }
            foreach (var mark in new[] { VisitingNpcs.Welcome, VisitingNpcs.Away, VisitingNpcs.Gone, VisitingNpcs.Ask })
                Assert(VisitingNpcs.IsVisit(GameMessage.Decode(new GameMessage(1, 0, "npc", VisitingNpcs.Kind, visitor.Id, [mark]).Encode())));
            Assert(!VisitingNpcs.IsVisit(new GameMessage(1, 0, "npc", "say", visitor.Id, null, "Hello")));
            Assert(!VisitingNpcs.IsVisit(new GameMessage(1, 0, "npc", "state", visitor.Id, [VisitingNpcs.Part])));
            Assert(!VisitingNpcs.IsVisit(new GameMessage(1, 0, "npc", "end", visitor.Id, [NpcMods.StateMark, 0, 0])));

            // Out of order, repeated: whole once every piece is in.
            var assembler = new VisitingNpcs.Assembler();
            var now = DateTime.UtcNow;
            VisitingNpc? whole = null;
            foreach (var (values, text) in parts.Reverse().Concat(parts.Take(1)))
                whole ??= assembler.Add(5, visitor.Id, values, text, now);
            Assert(whole is { } got && got.Id == visitor.Id && got.Rights == VisitRights.MeAndHost && got.Look.SameAs(visitor.Look) && got.Look.Body == visitor.Look.Body
                && got.Position.SequenceEqual(visitor.Position) && got.Level == 9);
            // Missing a piece, it waits (and is asked for again after a while); a new version starts again.
            var waiting = new VisitingNpcs.Assembler();
            foreach (var (values, text) in parts.Skip(1)) Assert(waiting.Add(5, visitor.Id, values, text, now) == null);
            Assert(waiting.Waiting(now.AddSeconds(6), 5) is [(5, var asked, 77)] && asked == visitor.Id);
            Assert(waiting.Add(5, visitor.Id, [VisitingNpcs.Part, 78, 0, 1], parts[0].Text, now) == null && waiting.Waiting(now.AddSeconds(61), 5).Count == 0);
            // Pieces for another id don't read as this visitor; bad pieces are ignored.
            var wrong = new VisitingNpcs.Assembler();
            VisitingNpc? stolen = null;
            foreach (var (values, text) in parts) stolen ??= wrong.Add(5, Guid.NewGuid(), values, text, now);
            Assert(stolen == null);
            Assert(wrong.Add(5, visitor.Id, [VisitingNpcs.Part, 1, 3, 2], "AAAA", now) == null && wrong.Add(5, visitor.Id, [VisitingNpcs.Part, 1, 0, VisitingNpcs.MaxPieces + 1], "AAAA", now) == null);
            Assert(VisitingNpcs.Decode(visitor.Id, "not base64!") == null && VisitingNpcs.Decode(visitor.Id, Convert.ToBase64String([1, 2, 3])) == null);
            // A visitor that isn't valid (bad name, place, look, rights) is never sent, and doesn't read back.
            foreach (var bad in new[] { visitor with { Name = "" }, visitor with { Position = [float.NaN, 0, 0] }, visitor with { Yaw = float.PositiveInfinity },
                         visitor with { Rights = (VisitRights)9 }, visitor with { Level = 0 }, visitor with { Emote = 0 }, visitor with { Look = new() { Customize = new byte[3] } } })
                Refused(() => VisitingNpcs.Encode(bad, 1));
            var json = System.Text.Json.JsonSerializer.Serialize(visitor);
            Assert(VisitingNpcs.Decode(visitor.Id, Zip(json)) is { Name: "Mira Haven" });
            Assert(VisitingNpcs.Decode(visitor.Id, Zip(json.Replace("\"Rights\":1", "\"Rights\":9"))) == null);
            Assert(VisitingNpcs.Decode(visitor.Id, Zip(json.Replace("Mira Haven", ""))) == null);
            Assert(VisitingNpcs.Decode(visitor.Id, Zip(json.Replace("[1,2,3]", "[1,2,30000]"))) == null);
            Assert(VisitingNpcs.Decode(visitor.Id, Zip(new string(' ', VisitingNpcs.MaxJson + 1) + json)) == null);
        });

        await check("Only a visitor's bringer shares its mods; only the home's owner shares the home's NPCs'", () =>
        {
            const ulong bringer = 10, owner = 20, guest = 30;
            // The home's NPCs: the owner only.
            Assert(VisitingNpcs.ModsFrom(owner, null, owner) && !VisitingNpcs.ModsFrom(guest, null, owner) && !VisitingNpcs.ModsFrom(bringer, null, owner));
            Assert(!VisitingNpcs.ModsFrom(0, null, 0));
            // A visitor: its bringer only, never the home's owner or a third player.
            Assert(VisitingNpcs.ModsFrom(bringer, bringer, owner) && !VisitingNpcs.ModsFrom(owner, bringer, owner) && !VisitingNpcs.ModsFrom(guest, bringer, owner));
            Assert(!VisitingNpcs.ModsFrom(0, bringer, owner));
            // A set kept from its bringer is theirs: nobody else replaces or drops it.
            var set = new NpcModSet(Guid.NewGuid(), [], "", DateTime.UtcNow, From: HomeOwners.Key(bringer));
            Assert(VisitingNpcs.MayReplace(set, bringer) && !VisitingNpcs.MayReplace(set, owner) && !VisitingNpcs.MayReplace(set, guest));
            Assert(VisitingNpcs.MayReplace(null, guest) && VisitingNpcs.MayReplace(set with { From = null }, guest));
            // A visitor's mod messages are ordinary NPC mod messages with its id (relay 0.42.x passes them); they never
            // read as visit messages, and visit messages never use the mod kinds.
            var id = Guid.NewGuid();
            foreach (var kind in new[] { NpcMods.Announce, NpcMods.Begin, NpcMods.Prints, NpcMods.Finish, NpcMods.SyncPrints, NpcMods.PathPrints, NpcMods.Progress, NpcMods.Took, NpcMods.Missed })
            {
                var message = GameMessage.Decode(new GameMessage(bringer, 0, "npc", kind, id, kind == NpcMods.Finish ? NpcMods.State(null) : [1, 2, 3]).Encode());
                Assert(!VisitingNpcs.IsVisit(message) && message.Match == id && kind != VisitingNpcs.Kind);
            }
            Assert(NpcMods.ReadState(GameMessage.Decode(new GameMessage(bringer, 0, "npc", NpcMods.Finish, id, NpcMods.State(null)).Encode()).Values) is { Version: 0 });
        });

        await check("Who may possess a visiting NPC, and which visitors a home lets in", () =>
        {
            const ulong bringer = 10, owner = 20, guest = 30;
            foreach (var rights in Enum.GetValues<VisitRights>()) Assert(VisitingNpcs.MayPossess(rights, bringer, bringer, owner, true));
            Assert(!VisitingNpcs.MayPossess(VisitRights.OnlyMe, owner, bringer, owner, true) && !VisitingNpcs.MayPossess(VisitRights.OnlyMe, guest, bringer, owner, true));
            Assert(VisitingNpcs.MayPossess(VisitRights.MeAndHost, owner, bringer, owner, true) && !VisitingNpcs.MayPossess(VisitRights.MeAndHost, guest, bringer, owner, true));
            Assert(VisitingNpcs.MayPossess(VisitRights.Anyone, guest, bringer, owner, true) && !VisitingNpcs.MayPossess(VisitRights.Anyone, guest, bringer, owner, false));
            Assert(!VisitingNpcs.MayPossess(VisitRights.Anyone, 0, bringer, owner, true) && !VisitingNpcs.MayPossess(VisitRights.MeAndHost, 0, bringer, 0, true));

            var npc = VisitingNpc.From(new RosterNpc { Name = "Mira Haven", Look = Look }, [0, 0, 0], 0, VisitRights.OnlyMe);
            var none = new Dictionary<Guid, ulong>();
            Assert(VisitingNpcs.Refusal(npc, bringer, owner, none, [], false) == null);
            Assert(VisitingNpcs.Refusal(npc, bringer, owner, none, [], true) != null);
            Assert(VisitingNpcs.Refusal(npc, owner, owner, none, [], false) != null);
            // Ids never clash with the home's NPCs or another guest's visitor; a visitor already in stays in.
            Assert(VisitingNpcs.Refusal(npc, bringer, owner, none, [npc.Id], false) != null);
            Assert(VisitingNpcs.Refusal(npc, bringer, owner, new Dictionary<Guid, ulong> { [npc.Id] = guest }, [], false) != null);
            Assert(VisitingNpcs.Refusal(npc, bringer, owner, new Dictionary<Guid, ulong> { [npc.Id] = bringer, [Guid.NewGuid()] = bringer }, [], false) == null);
            // Two per guest, four in all.
            Assert(VisitingNpcs.Refusal(npc, bringer, owner, new Dictionary<Guid, ulong> { [Guid.NewGuid()] = bringer, [Guid.NewGuid()] = bringer }, [], false) != null);
            Assert(VisitingNpcs.Refusal(npc, bringer, owner, Enumerable.Range(0, VisitingNpcs.Max).ToDictionary(_ => Guid.NewGuid(), i => (ulong)(100 + i)), [], false) != null);
            Assert(VisitingNpcs.Refusal(npc, bringer, owner, new Dictionary<Guid, ulong> { [Guid.NewGuid()] = bringer, [Guid.NewGuid()] = guest }, [], false) == null);

            // A guest's PC applies the same caps to what it holds, so endless ids from one bringer can't fill its list.
            Assert(!VisitingNpcs.Crowded(bringer, [bringer, guest]) && VisitingNpcs.Crowded(bringer, [bringer, bringer]));
            Assert(VisitingNpcs.Crowded(bringer, [guest, guest, 40, 50]) && !VisitingNpcs.Crowded(bringer, [guest, guest, 40]));

            // Drawn beside the home's NPCs, a visitor's game name never takes one of theirs.
            var homeNpc = new HomeNpc { Name = "Mira Haven", Look = Look };
            var names = HomeNpcs.GameNames([homeNpc, npc.AsHomeNpc()]);
            Assert(names[0] == "Mira Haven" && names[1] != names[0] && HomeNpcs.PlayerStyleName(names[1]));
            Assert(HomeNpcs.GameNames([homeNpc])[0] == names[0]);
        });
    }

    private static string Random(int length)
    {
        var bytes = new byte[length * 3 / 4];
        System.Random.Shared.NextBytes(bytes);
        return Convert.ToBase64String(bytes)[..length];
    }

    private static string Zip(string json)
    {
        var buffer = new MemoryStream();
        using (var zip = new System.IO.Compression.GZipStream(buffer, System.IO.Compression.CompressionLevel.Fastest, true)) zip.Write(System.Text.Encoding.UTF8.GetBytes(json));
        return Convert.ToBase64String(buffer.ToArray());
    }

    private static void Refused(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new Exception("That should have been refused.");
    }

    private static void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
}
