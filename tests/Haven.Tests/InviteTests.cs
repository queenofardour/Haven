using Haven.Core;
using Haven.Relay;
using HavenNative.Wire;
using MessagePack;

// Gathering invitations: the host's and invitee's planning, the Worker's answer, and the
// relay's invite-only lock.
internal static class InviteTests
{
    public static async Task Run(Func<string, Action, Task> check, Func<string, Func<Task>, Task> checkAsync)
    {
        await check("Hosts invite nearby friends once, refresh before expiry, and respect choices and withdrawals", () =>
        {
            var plan = new InviteHostPlan();
            Assert(plan.Due([1, 2], automatic: true, 0).Order().SequenceEqual([1UL, 2UL]));
            plan.Sent([1, 2], 0);
            Assert(plan.Due([1, 2, 3], true, 1_000).Length == 0);                    // paced
            Assert(plan.Due([1, 2, 3], true, InviteHostPlan.SendEveryMs).SequenceEqual([3UL]));   // only the newcomer
            plan.Sent([3], InviteHostPlan.SendEveryMs);
            // Locked (automatic off): nobody new, but a chosen friend goes, and the automatic ones are taken back.
            Assert(plan.Due([4], automatic: false, 60_000).Length == 0);
            plan.Choose(4);
            Assert(plan.Due([4], false, 60_000).SequenceEqual([4UL]));
            plan.Sent([4], 60_000);
            Assert(plan.WithdrawAutomatic().Order().SequenceEqual([1UL, 2UL, 3UL]));
            Assert(plan.Invited.SequenceEqual([4UL]) && plan.IsInvited(4) && !plan.IsInvited(1));
            // Withdrawn friends aren't invited again automatically, only if chosen again.
            Assert(plan.Due([1, 2, 3], true, 70_000).Length == 0);
            // Refresh: the chosen friend again once RefreshMs has passed, even when not nearby.
            Assert(plan.Due([], false, 60_000 + InviteHostPlan.RefreshMs).SequenceEqual([4UL]));
            Assert(plan.Withdraw(4) && !plan.Withdraw(4));
            var gathering = plan.Gathering;
            plan.Reset();
            Assert(plan.Gathering != gathering && plan.Gathering.Length == 16 && plan.Due([1], true, 0).SequenceEqual([1UL]));
            // At most 16 a send and 32 out at once.
            var crowd = new InviteHostPlan();
            var many = Enumerable.Range(100, 40).Select(i => (ulong)i).ToArray();
            var first = crowd.Due(many, true, 0); crowd.Sent(first, 0);
            var second = crowd.Due(many, true, 10_000); crowd.Sent(second, 10_000);
            Assert(first.Length == 16 && second.Length == 16 && crowd.Due(many, true, 20_000).Length == 0);
        });
        await check("Invitees ask only when they could join, and see each gathering's invitation once, from a friend nearby", () =>
        {
            var inbox = new InviteInbox();
            Assert(inbox.ShouldPoll(true, false, true, false, 0));
            Assert(!inbox.ShouldPoll(false, false, true, false, 0) && !inbox.ShouldPoll(true, true, true, false, 0)
                && !inbox.ShouldPoll(true, false, false, false, 0) && !inbox.ShouldPoll(true, false, true, true, 0));
            inbox.Polling(0);
            Assert(!inbox.ShouldPoll(true, false, true, false, 1_000) && inbox.ShouldPoll(true, false, true, false, InviteInbox.PollMs));
            inbox.Failed(0, busy: true);
            Assert(!inbox.ShouldPoll(true, false, true, false, InviteInbox.PollMs) && inbox.ShouldPoll(true, false, true, false, InviteInbox.BusyMs));
            var got = GatheringInvite.Parse("""
                {"invites":[{"from":"1001","name":"Zoe\u0007 Host","home":"Cosy Cottage","code":"visit-code-1","gathering":"0123456789abcdef","expires":5000},
                 {"from":"0","name":"Bad","home":"","code":"visit-code-1","gathering":"0123456789abcdef","expires":5000},
                 {"from":"1002","name":"Short","home":"","code":"short","gathering":"0123456789abcdef","expires":5000},
                 {"from":"1003","name":"Other","home":"Inn","code":"visit-code-3","gathering":"fedcba9876543210","expires":5000}]}
                """);
            Assert(got.Length == 2 && got[0].Host == 1001 && got[0].Name == "Zoe Host");
            Assert(GatheringInvite.Parse("""{"error":"slow"}""").Length == 0);
            inbox.Polled(got);
            var nearby = new HashSet<ulong> { 1003 };
            Assert(inbox.Showing(nearby, 1_000)?.Host == 1003);              // only a host standing nearby
            Assert(inbox.Showing(new HashSet<ulong>(), 1_000) == null);
            Assert(inbox.Showing(nearby, 6_000) == null);                   // expired
            inbox.Answer(got[1]);
            Assert(inbox.Showing(nearby, 1_000) == null);
            // The same host's next gathering asks again.
            inbox.Polled([got[1] with { Gathering = "00000000000000ff" }]);
            Assert(inbox.Showing(nearby, 1_000) != null);
        });
        await check("Guest lists are bounded and travel intact", () =>
        {
            var list = GuestList.Decode(new GuestList(true, [5, 6, 5]).Encode());
            Assert(list.InviteOnly && list.Invited.SequenceEqual([5UL, 6UL]));
            foreach (var bad in new[] { new GuestList(true, [0]), new GuestList(false, [.. Enumerable.Range(1, 65).Select(i => (ulong)i)]) })
                Assert(Throws(() => GuestList.Decode(bad.Encode())));
            Assert(Throws(() => GuestList.Decode("null"u8.ToArray())) && Throws(() => GuestList.Decode(new byte[5000])));
        });
        await checkAsync("An invite-only gathering admits only invited characters; the owner alone sets the list", async () =>
        {
            const string relayKey = "invite-test-recovery-key-0123456789ab";
            await using var relay = RelayApp.Build(["--urls", "http://127.0.0.1:0", "--HAVEN_RECOVERY_KEY", relayKey, "--HAVEN_ACCOUNTS", "required"]);
            await relay.StartAsync();
            var at = new Uri(relay.Urls.Single().Replace("http://", "ws://") + "/");
            var soon = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 30_000;
            TestJoin Join(ulong id, bool host) => new() { ContentId = id, CharacterName = "C" + id, CreateIfMissing = host, RoomPassword = "invite-room-code", NearbyContentIds = [801] };
            Task<Client> Connect(ulong id, bool host) => Client.Connect(at, Join(id, host), AccountStamp.Sign(relayKey, id, soon, (9100 + id).ToString(), "C" + id));
            string Refusal(FrameHeader.Parsed frame) => MessagePackSerializer.Deserialize<ErrorPayload>(frame.Payload, WireFormat.Options).Message;
            using var owner = await Connect(801, true);
            await owner.Expect(WireKind.RoomJoined);
            await owner.Send(HousingWire.GuestList, new GuestList(true, [802]).Encode());
            var kept = GuestList.Decode((await owner.Expect(HousingWire.GuestList)).Payload);
            Assert(kept.InviteOnly && kept.Invited.SequenceEqual([802UL]));
            using (var uninvited = await Connect(803, false))
                Assert(Refusal(await uninvited.Expect(WireKind.Error)).Contains("invite-only"));
            using var invited = await Connect(802, false);
            await invited.Expect(WireKind.RoomJoined);
            // A guest's list is ignored: 803 is still refused.
            await invited.Send(HousingWire.GuestList, new GuestList(false, []).Encode());
            await Task.Delay(200);
            using (var still = await Connect(803, false))
                Assert(Refusal(await still.Expect(WireKind.Error)).Contains("invite-only"));
            // Unlocked, the code works for anyone nearby again.
            await owner.Send(HousingWire.GuestList, new GuestList(false, [802]).Encode());
            Assert(!GuestList.Decode((await owner.Expect(HousingWire.GuestList)).Payload).InviteOnly);
            using var open = await Connect(803, false);
            await open.Expect(WireKind.RoomJoined);
            await owner.Send(WireKind.SessionEnd, []);
        });
    }
    private static bool Throws(Action action) { try { action(); return false; } catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException) { return true; } }
    private static void Assert(bool value) { if (!value) throw new Exception("Invite assertion failed."); }
}
