using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using HavenNative.Services;

namespace Haven.Native;

/// <summary>
/// Lets the friend menu's server-backed items work inside a home: View Search Info, View Adventurer Plate, View
/// Company Profile, Remove from Friend List and Assign Friend Group. Each sends one request through the zone connection
/// (which Haven's filter blocks inside) and gets one reply (which it drops). Here, the game's own request function for
/// each runs with that request's packet let out (its opcode read from the function's own code), and only the matching
/// reply is let back in: its opcode found at startup in the zone dispatcher's switch (the one case block that calls
/// that reply's handler), within ten seconds, and naming the character asked about. Nothing is hard-coded per patch:
/// an item whose function, opcode or reply can't be found stays blocked (fail closed) and the log says so. Also the
/// party (invite, leave, disband, kick, promote, cancel invitations, answer an invite), friend requests (send, answer,
/// remove by name) and the player's own search info and online status. Unasked notices the player needs (an invite or
/// friend request arriving, the party list) are let in after a check. Estate Teleportation never goes out from a home:
/// it asks to leave first (EstateTeleportAsked).
/// </summary>
internal sealed unsafe class SocialRequests : IDisposable
{
    // Up to five integer arguments (the fifth on the stack, forwarded from the caller's frame); every hooked request
    // function takes no more, and none takes floats.
    private delegate nint Request(nint a1, nint a2, nint a3, nint a4, nint a5);
    private static nint textStart, textEnd;
    // An address inside the game's code (a call read from code bytes may be a stray E8 inside another instruction).
    private static bool InText(nint address, int length = 0x200) => address >= textStart && address + length <= textEnd;
    private const long ReplyWindowMs = 10_000;
    private readonly IPluginLog log;
    private readonly PacketFilterService filter;
    private readonly List<IDisposable> hooks = [];

    // A reply's handler, as the dispatcher's case block calls it.
    private enum Reply { Detail, Plate, PlateError, Company, Remove, Group, InviteResult, ListPage, PartyList, PartyMessage, Answer, InvitesCanceled, Notice, SearchInfo }
    // How a reply is matched to its request: the payload's first u64 is the character asked about (out +0x20), the
    // company reply also by object (u32 payload +0x20 = out +0x28), the plate by time alone, and an invite's result by
    // character and its invite category (payload +0xE = out +0x2A).
    // Party messages carry their type at +0x22 and the member concerned at +0x18; answers their category at +0xC; the
    // cancel-all result its category at +4 (= the request's first payload byte).
    private enum Match { Character, CharacterOrObject, Window, CharacterAndCategory, CharacterOrNameAndCategory, PartyLeft, PartyDisbanded, PartyKicked, PartyPromoted,
        CanceledCategory, PartyAnswer, FriendAnswer }
    // OpcodeAt is in the request function itself, (Leaf) in the first function it calls that has the opcode there, or
    // (OpcodeIn) in a sender function found by its own signature.
    private sealed record Item(string Name, string Signature, int OpcodeAt, Reply[] Replies, Match Match, bool Leaf = false, string? OpcodeIn = null,
        Shape Shape = Shape.Any);

    // A hooked function's exact shape, where the game's documentation (ClientStructs) knows it: Dalamud checks hooks on
    // such functions against it and logs "Hook verification failed" for any other delegate (a Dalamud that checks hooks
    // listed 11 on 0.57.1). Pointers are passed as nint; bools are one byte. Every other function takes Request.
    private enum Shape { Any, Card, CardById, CardByObject, CompanyById, PartyBool, Detail, InviteById, InviteByName }
    private delegate void CardUpdate(nint card);                                    // CharaCard.RequestCharaCardUpdate
    private delegate void CardForId(nint card, ulong character);                    // CharaCard.RequestCharaCardForContentId
    private delegate void CardForObject(nint card, nint gameObject);                // CharaCard.RequestCharaCardForGameObject
    private delegate void CompanyForCharacter(nint proxy, uint entity);             // InfoProxyFreeCompany.RequestDataForCharacter
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool PartyCommand(nint proxy);                                 // InfoProxyPartyMember.LeaveParty, DisbandParty
    private delegate void DetailUpdate(nint proxy, nint data);                      // InfoProxyDetail.SendUpdateData
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool InviteById(nint proxy, ulong character, ushort world);    // InfoProxyPartyInvite.InviteToPartyContentId
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool InviteByName(nint proxy, ulong character, InteropGenerator.Runtime.CStringPointer name, ushort world);   // InviteToParty

    private static readonly Item[] Items =
    [
        new("Search Info", "48 89 5C 24 10 48 89 74 24 18 57 48 81 EC 90 0F 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 80 0F 00 00 48 8B D9 41 0F B6 F0 48 8B 0D ?? ?? ?? ?? 48 8B FA E8 ?? ?? ?? ?? 48 85 C0 0F 84 ?? ?? ?? ?? 45 33 C9 C7 44 24 30",
            0x4A, [Reply.Detail], Match.Character),
        new("Adventurer Plate", "40 53 48 81 EC 80 0F 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 70 0F 00 00 48 8B 0D ?? ?? ?? ?? 48 8B DA E8 ?? ?? ?? ?? 45 33 C9 C7 44 24 20 ?? ?? ?? ?? 45 33 C0 48 C7 44 24 28 20 00 00 00 48 8D 54 24 20 48 89 5C 24 40 48 8B C8 C7 44 24 48 01 00 00 00",
            0x31, [Reply.Plate, Reply.PlateError], Match.Window, Shape: Shape.CardById),
        new("Adventurer Plate", "40 53 48 81 EC 80 0F 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 70 0F 00 00 48 8B 0D ?? ?? ?? ?? 48 8B DA E8 ?? ?? ?? ?? 8B 4B 78 48 8D 54 24 20 48 89 4C 24 40 45 33 C9 48 8B C8 C7 44 24 20 ?? ?? ?? ?? 45 33 C0 48 C7 44 24 28 20 00 00 00 C7 44 24 48 01 00 00 00",
            0x41, [Reply.Plate, Reply.PlateError], Match.Window, Shape: Shape.CardByObject),
        // The player's own plate (RequestCharaCardUpdate: the local entity, mode 2).
        new("Adventurer Plate", "48 81 EC 88 0F 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 70 0F 00 00 48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 8B 0D ?? ?? ?? ?? 48 8D 54 24 20 48 89 4C 24 40 45 33 C9 48 8B C8 C7 44 24 20 ?? ?? ?? ?? 45 33 C0 48 C7 44 24 28 20 00 00 00 C7 44 24 48 02 00 00 00",
            0x3F, [Reply.Plate, Reply.PlateError], Match.Window, Shape: Shape.Card),
        new("Company Profile", "40 53 48 81 EC 80 0F 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 70 0F 00 00 48 8B 0D ?? ?? ?? ?? 48 8B DA E8 ?? ?? ?? ?? 48 85 C0 74 ?? 45 33 C9 C7 44 24 20 ?? ?? ?? ?? 45 33 C0 48 C7 44 24 28 20 00 00 00 48 8D 54 24 20 48 89 5C 24 40",
            0x36, [Reply.Company], Match.CharacterOrObject),
        new("Company Profile", "40 53 48 81 EC 80 0F 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 70 0F 00 00 48 8B 0D ?? ?? ?? ?? 8B DA E8 ?? ?? ?? ?? 48 85 C0 74 ?? 45 33 C9 C7 44 24 20 ?? ?? ?? ?? 45 33 C0 48 C7 44 24 28 20 00 00 00 48 8D 54 24 20 48 C7 44 24 40 00 00 00 00",
            0x35, [Reply.Company], Match.CharacterOrObject, Shape: Shape.CompanyById),
        new("Remove from Friend List", "48 89 5C 24 18 48 89 74 24 20 57 48 81 EC 80 0F 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 70 0F 00 00 48 8B 01 48 8B D9 8B FA FF 50 20 3B F8 0F 83",
            0xC2, [Reply.Remove], Match.Character),
        new("Assign Friend Group", "48 89 5C 24 10 48 89 74 24 18 57 48 81 EC 80 0F 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 70 0F 00 00 48 8B 01 41 0F B6 F0 8B FA 48 8B D9 FF 50 20 3B F8 73",
            0x7C, [Reply.Group], Match.Character),
        // The party: leave, disband, kick and promote (by member and by name), cancel every invitation sent (two packets,
        // party and cross-world categories), and answer an invite (InfoProxyPartyInvite slot 13; opcode in its sender).
        new("Leave Party", "48 89 5C 24 18 56 48 81 EC 80 0F 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 70 0F 00 00 48 8B F1 48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B D8 48 85 C0 0F 84 ?? ?? ?? ?? 48 8B 4E 08",
            0x109, [Reply.PartyMessage], Match.PartyLeft, Shape: Shape.PartyBool),
        new("Disband Party", "48 81 EC 88 0F 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 70 0F 00 00 E8 ?? ?? ?? ?? 48 8B 0D ?? ?? ?? ?? 84 C0 74",
            0x8D, [Reply.PartyMessage], Match.PartyDisbanded, Shape: Shape.PartyBool),
        new("Kick from Party", "48 89 5C 24 18 48 89 6C 24 20 57 48 81 EC 80 0F 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 70 0F 00 00 48 89 B4 24 98 0F 00 00 48 8B FA 48 8B F1 48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 33 DB 48 8B E8 48 85 FF 74 32",
            0xF1, [Reply.PartyMessage], Match.PartyKicked),
        new("Promote to Party Leader", "48 89 5C 24 10 48 89 6C 24 18 48 89 74 24 20 57 48 81 EC 80 0F 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 70 0F 00 00 48 8B F1 48 8B FA 48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 33 DB 48 8B E8 48 85 FF 74 35",
            0xEA, [Reply.PartyMessage], Match.PartyPromoted),
        new("Promote to Party Leader", "48 89 5C 24 18 48 89 74 24 20 57 48 81 EC 80 0F 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 70 0F 00 00 48 8B F1 41 0F B7 D8 48 8B 0D ?? ?? ?? ?? 48 8B FA E8 ?? ?? ?? ?? 44 0F B7 C3 48 8B D7 48 8B CE E8",
            0x11A, [Reply.PartyMessage], Match.PartyPromoted),
        new("Cancel Party Invitations", "40 53 48 81 EC 80 0F 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 70 0F 00 00 48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B D8 48 85 C0 74 ?? 45 33 C9 C7 44 24 20 ?? ?? ?? ?? 45 33 C0 48 C7 44 24 28 18 00 00 00 48 8D 54 24 20 C6 44 24 40 01",
            0x36, [Reply.InvitesCanceled], Match.CanceledCategory),
        new("Answer Party Invite", "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 48 89 7C 24 20 41 56 48 83 EC 40 48 8B F9 45 8B F0 48 8B 49 08 48 8B EA 48 8B 01 FF 50 40 0F B6 57 19",
            0x70, [Reply.Answer], Match.PartyAnswer, OpcodeIn: "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 48 81 EC 80 0F 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 70 0F 00 00 48 8B 0D ?? ?? ?? ?? 41 0F B6 F1 41 0F B7 D8 48 8B EA E8 ?? ?? ?? ?? 48 8B F8 48 85 C0 74 ?? 66 85 DB 75 08 E8 ?? ?? ?? ?? 0F B7 D8 0F B6 84 24 B0 0F 00 00 48 8D 54 24 20 45 33 C9 88 44 24 4A"),
        // Friend requests: by name (/friendlist add, the menu; opcode in the 0x19B sender) and by character, answering one
        // (accept, decline, cancel your own; both senders, nested), and removing by name.
        new("Send Friend Request", "48 89 5C 24 10 48 89 6C 24 18 48 89 74 24 20 41 56 48 83 EC 30 48 8B D9 49 8B F1 48 8B 49 08 49 8B E8 4C 8B F2 48 8B 01 FF 50 48 BA 97 00 00 00",
            0x6D, [Reply.InviteResult], Match.CharacterOrNameAndCategory, OpcodeIn: "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 48 81 EC 80 0F 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 70 0F 00 00 48 8B 0D ?? ?? ?? ?? 49 8B F1 41 0F B7 D8 48 8B EA E8 ?? ?? ?? ?? 48 8B F8 48 85 C0 74 ?? 66 85 DB 75 08 E8 ?? ?? ?? ?? 0F B7 D8 0F 10 06 0F B6 84 24 B0 0F 00 00 45 33 C9 0F 10 4E 10 45 33 C0 C7 44 24 20"),
        new("Send Friend Request", "48 83 EC 38 41 B1 0C C6 44 24 20 00 E8", 0x70, [Reply.InviteResult], Match.CharacterAndCategory, Leaf: true),
        new("Answer Friend Request", "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 48 83 EC 30 48 8B F1 41 0F B6 E8 48 8B 0D ?? ?? ?? ?? 48 8B FA E8 ?? ?? ?? ?? 48 85 C0 74 ?? 48 85 FF 74 ?? 8B 56 10",
            0x70, [Reply.Answer], Match.FriendAnswer, OpcodeIn: "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 48 81 EC 80 0F 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 70 0F 00 00 48 8B 0D ?? ?? ?? ?? 41 0F B6 F1 41 0F B7 D8 48 8B EA E8 ?? ?? ?? ?? 48 8B F8 48 85 C0 74 ?? 66 85 DB 75 08 E8 ?? ?? ?? ?? 0F B7 D8 0F B6 84 24 B0 0F 00 00 48 8D 54 24 20 45 33 C9 88 44 24 4A"),
        new("Answer Friend Request", "48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 40 48 8B F9 41 0F B6 F0 48 8B 49 08 48 8B DA 48 8B 01 FF 90 D0 00 00 00 48 8B C8 48 8B D3 E8",
            0x91, [Reply.Answer], Match.FriendAnswer, Leaf: true),
        new("Remove from Friend List", "48 89 5C 24 10 48 89 74 24 18 57 48 81 EC 80 0F 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 70 0F 00 00 41 0F B7 F0 48 8B FA 45 33 C0 E8",
            0x62, [Reply.Remove], Match.Character),
        // The player's own search info and online status (InfoProxyDetail SendUpdateData: the editor's Apply, the status
        // menu and commands, and the game's own idle status alike); only that player's result comes back.
        new("Search Info update", "48 89 5C 24 10 48 89 74 24 18 57 48 81 EC 80 0F 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 70 0F 00 00 80 B9 3D 02 00 00 00 48 8B DA 48 8B F9 0F 84",
            0xD7, [Reply.SearchInfo], Match.Window, Shape: Shape.Detail),
        // InviteToPartyContentId (calls the 0x2FF sender) and InviteToParty with a name (calls the 0x19B sender).
        new("Invite to Party", "48 83 EC 38 41 B1 09", 0x70, [Reply.InviteResult], Match.CharacterAndCategory, Leaf: true, Shape: Shape.InviteById),
        new("Invite to Party", "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 48 83 EC 30 48 8B D9 41 0F B7 F9 48 8B 49 08 49 8B F0 48 8B EA",
            0x6D, [Reply.InviteResult], Match.CharacterAndCategory, Leaf: true, Shape: Shape.InviteByName),
    ];

    // The reply handlers the dispatcher's case blocks call (the plate and company replies through a small stub).
    private static readonly (Reply Reply, string Signature)[] Handlers =
    [
        (Reply.Detail, "40 53 48 83 EC 20 48 8B 0D ?? ?? ?? ?? 48 8B DA E8 ?? ?? ?? ?? 48 85 C0 74 ?? 4C 8B 00 48 8B C8 41 FF 90 18 01 00 00 48 8B C8 BA 0A 00 00 00 E8 ?? ?? ?? ?? 48 85 C0 74 ?? 4C 8B 08 41 B8 01 00 00 00 48 8B D3 48 8B C8 48 83 C4 20 5B 49 FF 61 08"),
        (Reply.Remove, "40 56 41 56 48 83 EC 48 48 8B 0D ?? ?? ?? ?? 4C 8B F2 E8 ?? ?? ?? ?? 48 8B F0 48 85 C0 0F 84 ?? ?? ?? ?? 41 83 7E 08 00 48 8B CE 48 8B 00 0F 85 ?? ?? ?? ?? 4C 89 7C 24 40 FF 90 18 01 00 00 48 8B C8 BA 06 00 00 00"),
        (Reply.Group, "40 53 48 83 EC 20 48 8B 0D ?? ?? ?? ?? 48 8B DA E8 ?? ?? ?? ?? 48 85 C0 74 ?? 4C 8B 00 48 8B C8 41 FF 90 18 01 00 00 48 8B C8 BA 06 00 00 00 E8 ?? ?? ?? ?? 48 85 C0 74 ?? 44 0F B6 4B 09 48 8B C8 44 0F B6 43 08 48 8B 13"),
        // CharaCard's packet handler (the case calls a stub: mov rdx,rcx; lea rcx,[CharaCard]; jmp here).
        (Reply.Plate, "40 53 48 83 EC 20 48 8B DA 0F B6 52 28 84 D2 74 ?? 66 83 7B 7A 00 75 ?? B8 01 00 00 00 66 89 43 7A 8B 05"),
        (Reply.PlateError, "80 79 06 27 75 ?? 48 8B D1 48 8D 0D"),
        // The free company proxy's reply (the case calls a stub that fetches proxy 13 and jumps here).
        // The common list page handler (every social list's pages; the friend list is list type 2).
        (Reply.ListPage, "48 89 5C 24 10 56 48 83 EC 20 48 8B 0D ?? ?? ?? ?? 48 8B F2 E8 ?? ?? ?? ?? 48 8B D8 48 85 C0 0F 84 ?? ?? ?? ?? 0F B6 4E 0C FF C9 83 F9 0C"),
        // The party list handler (the case calls a packet recorder first, then this).
        (Reply.PartyList, "48 89 5C 24 08 55 56 57 41 54 41 55 41 56 41 57 48 81 EC F0 00 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 E0 00 00 00 48 8B 0D ?? ?? ?? ?? 41 0F B6 D8 88 5C 24 40 48 8B EA E8 ?? ?? ?? ?? 0F B6 D3 48 89 44 24 68 48 8D 0D ?? ?? ?? ?? 4C 8B F0 E8 ?? ?? ?? ?? 48 8B C8 4C 8B F8 8B 90 D8 7F 00 00"),
        (Reply.PartyMessage, "40 53 56 48 81 EC F8 01 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 D0 01 00 00 48 8B 0D ?? ?? ?? ?? 48 8B F2 48 89 54 24 70 E8"),
        (Reply.Answer, "4C 8B DC 41 55 48 81 EC E0 00 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 B0 00 00 00 0F B6 42 0C 4C 8B EA FF C8 83 F8 0E"),
        (Reply.InvitesCanceled, "48 89 5C 24 08 57 48 83 EC 20 48 8B 0D ?? ?? ?? ?? 48 8B FA E8 ?? ?? ?? ?? 48 8B D8 48 85 C0 74 ?? 0F B6 57 04 83 EA 01 74 ?? 83 EA 0A"),
        (Reply.Notice, "4C 8B DC 53 48 81 EC 30 01 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 20 01 00 00 0F B6 42 1E 48 8B DA FF C8 83 F8 0E"),
        (Reply.SearchInfo, "48 89 5C 24 18 57 48 83 EC 20 48 8B 0D ?? ?? ?? ?? 48 8B FA E8 ?? ?? ?? ?? 48 8B D8 48 85 C0 0F 84 ?? ?? ?? ?? 4C 8B 00 48 8B C8 48 89 74 24 38 41 FF 90 18 01 00 00 48 8B C8 BA 0A 00 00 00 E8 ?? ?? ?? ?? 48 8B F0 8B 47 10 85 C0 0F 85"),
        (Reply.InviteResult, "4C 8B DC 41 56 48 81 EC 40 01 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 20 01 00 00 0F B6 42 0E 4C 8B F2 FF C8 83 F8 0E"),
        (Reply.Company, "48 89 5C 24 08 57 48 83 EC 20 48 8B F9 48 8B DA 48 8B 49 08 48 8B 01 FF 90 28 01 00 00 48 8B C8 BA 33 00 00 00 E8 ?? ?? ?? ?? 48 85 C0 74 ?? 48 8B D3 48 8B C8 E8 ?? ?? ?? ?? 48 8B 4F 08 48 8B 01 FF 90 28 01 00 00 48 8B C8 BA 51 00 00 00"),
    ];

    private ushort listPage;

    public SocialRequests(ISigScanner scanner, IGameInteropProvider interop, PacketFilterService filter, IPluginLog log)
    {
        this.log = log; this.filter = filter;
        // The real module's code section: Dalamud's scanner searches a copy, whose TextSectionBase is in the copy, while
        // ScanText answers real addresses (0.41.0 used the copy's range, so every address looked foreign).
        textStart = scanner.Module.BaseAddress + (nint)scanner.TextSectionOffset; textEnd = textStart + scanner.TextSectionSize;
        log.Information("[Haven] [SOCIAL] Game code at {Start:X}..{End:X}.", textStart, textEnd);
        var replies = ReplyOpcodes(scanner);
        listPage = replies.GetValueOrDefault(Reply.ListPage);
        // The party list is let in whenever it comes (the server sends it unasked as the party changes): the only
        // packet that tells the client who is in the player's real party, so /p and the party list work inside a home
        // and are right after leaving. Checked first: up to 8 members, a leader among them, each with a character and a
        // player's entity id, and the player among them. The members' HP and status stream stays out.
        if (replies.TryGetValue(Reply.PartyList, out var partyList))
        {
            filter.Allow(partyList, PartyListValid, "party list");
            log.Information("[Haven] [SOCIAL] Party list: 0x{Opcode:X3}.", partyList);
        }
        else log.Warning("[Haven] [SOCIAL] The party list packet wasn't found; parties formed inside a home show after a zone change.");
        // An invite or friend request arriving (or being withdrawn, declined or accepted by the other side): unasked, so
        // let in when it's a party or friend notice with a character and a proper name.
        if (replies.TryGetValue(Reply.Notice, out var notice)) filter.Allow(notice, NoticeValid, "party or friend notice");
        if (replies.TryGetValue(Reply.PartyMessage, out var partyMessage)) filter.Allow(partyMessage, PartyMessageValid, "party message");
        HookEstateTeleport(scanner, interop);
        foreach (var item in Items)
        {
            try
            {
                if (item.Replies.Any(r => !replies.ContainsKey(r)))
                { log.Warning("[Haven] [SOCIAL] {Item} stays blocked inside homes: its reply wasn't found in the game's packet table.", item.Name); continue; }
                var function = scanner.ScanText(item.Signature);
                if ((item.OpcodeIn is { } sender ? Opcode(scanner.ScanText(sender), item.OpcodeAt, false) : Opcode(function, item.OpcodeAt, item.Leaf)) is not { } opcode)
                { log.Warning("[Haven] [SOCIAL] {Item} stays blocked inside homes: its request opcode couldn't be read.", item.Name); continue; }
                var expect = item.Replies.Select(r => (Reply: r, Opcode: replies[r])).ToArray();
                hooks.Add(HookItem(interop, item, function, opcode, expect));
                log.Information("[Haven] [SOCIAL] {Item}: request 0x{Request:X3}, replies {Replies}.", item.Name, opcode, string.Join(", ", expect.Select(e => $"0x{e.Opcode:X3}")));
            }
            catch (Exception ex) { log.Warning(ex, "[Haven] [SOCIAL] {Item} stays blocked inside homes: its request function wasn't found.", item.Name); }
        }
    }

    // Hooks one request function with its own shape (Shape); each detour runs the original through Detour.
    private IDisposable HookItem(IGameInteropProvider interop, Item item, nint function, ushort opcode, (Reply Reply, ushort Opcode)[] expect)
    {
        nint Run(Func<nint> original) => Detour(item, opcode, expect, original);
        static nint Done(Action call) { call(); return 0; }
        static nint Flag(bool value) => value ? 1 : 0;
        switch (item.Shape)
        {
            case Shape.Card:
            { Hook<CardUpdate>? h = null; h = interop.HookFromAddress<CardUpdate>(function, a => Run(() => Done(() => h!.Original(a)))); h.Enable(); return h; }
            case Shape.CardById:
            { Hook<CardForId>? h = null; h = interop.HookFromAddress<CardForId>(function, (a, b) => Run(() => Done(() => h!.Original(a, b)))); h.Enable(); return h; }
            case Shape.CardByObject:
            { Hook<CardForObject>? h = null; h = interop.HookFromAddress<CardForObject>(function, (a, b) => Run(() => Done(() => h!.Original(a, b)))); h.Enable(); return h; }
            case Shape.CompanyById:
            { Hook<CompanyForCharacter>? h = null; h = interop.HookFromAddress<CompanyForCharacter>(function, (a, b) => Run(() => Done(() => h!.Original(a, b)))); h.Enable(); return h; }
            case Shape.PartyBool:
            { Hook<PartyCommand>? h = null; h = interop.HookFromAddress<PartyCommand>(function, a => Run(() => Flag(h!.Original(a))) != 0); h.Enable(); return h; }
            case Shape.Detail:
            { Hook<DetailUpdate>? h = null; h = interop.HookFromAddress<DetailUpdate>(function, (a, b) => Run(() => Done(() => h!.Original(a, b)))); h.Enable(); return h; }
            case Shape.InviteById:
            { Hook<InviteById>? h = null; h = interop.HookFromAddress<InviteById>(function, (a, b, c) => Run(() => Flag(h!.Original(a, b, c))) != 0); h.Enable(); return h; }
            case Shape.InviteByName:
            { Hook<InviteByName>? h = null; h = interop.HookFromAddress<InviteByName>(function, (a, b, c, d) => Run(() => Flag(h!.Original(a, b, c, d))) != 0); h.Enable(); return h; }
            default:
            { Hook<Request>? h = null; h = interop.HookFromAddress<Request>(function, (a1, a2, a3, a4, a5) => Run(() => h!.Original(a1, a2, a3, a4, a5))); h.Enable(); return h; }
        }
    }

    // Outside a home the request runs as ever. Inside, only its own packet is let out, and its reply is expected back.
    private nint Detour(Item item, ushort opcode, (Reply Reply, ushort Opcode)[] replies, Func<nint> original)
    {
        if (!filter.IsActive) return original();
        return filter.PassRequest(opcode, packet =>
        {
            // Outbound layout: payload from +0x20 (the character asked about is its first u64; an object id at +0x28).
            var character = *(ulong*)(packet + 0x20);
            var entity = *(uint*)(packet + 0x28);
            var category = *(byte*)(packet + 0x2A);
            var first = *(byte*)(packet + 0x20);
            var name = new ReadOnlySpan<byte>((byte*)(packet + 0x2B), 32).ToArray();
            var until = Environment.TickCount64 + ReplyWindowMs;
            foreach (var (reply, replyOpcode) in replies)
            {
                Func<nint, bool> matches = reply switch
                {
                    Reply.PlateError => payload => *(byte*)(payload + 6) == 0x27,
                    _ => item.Match switch
                    {
                        Match.Character => payload => character != 0 && *(ulong*)payload == character,
                        Match.CharacterOrObject => payload => character != 0 && *(ulong*)payload == character || entity != 0 && *(uint*)(payload + 0x20) == entity,
                        Match.CharacterAndCategory => payload => character != 0 && *(ulong*)payload == character && *(byte*)(payload + 0xE) == category,
                        Match.CharacterOrNameAndCategory => payload => *(byte*)(payload + 0xE) == category
                            && (character != 0 && *(ulong*)payload == character || name[0] != 0 && new ReadOnlySpan<byte>((byte*)(payload + 0x10), 32).SequenceEqual(name)),
                        Match.PartyLeft => payload => *(byte*)(payload + 0x22) is 5 or 7 or 13,
                        Match.PartyDisbanded => payload => *(byte*)(payload + 0x22) == 3,
                        Match.PartyKicked => payload => *(byte*)(payload + 0x22) == 4 && *(ulong*)(payload + 0x18) == character,
                        Match.PartyPromoted => payload => *(byte*)(payload + 0x22) == 2 && *(ulong*)(payload + 0x18) == character,
                        Match.CanceledCategory => payload => *(byte*)(payload + 4) == first,
                        Match.PartyAnswer => payload => *(byte*)(payload + 0xC) is 1 or 9 or 14,
                        Match.FriendAnswer => payload => *(byte*)(payload + 0xC) is 2 or 12,
                        _ => _ => true,
                    },
                };
                filter.Expect(replyOpcode, matches, until, item.Name);
            }
        }, original);
    }

    // An invite or friend notice (payload: +8 the other character, +0x1C world, +0x1E category, +0x20 kind, +0x22 name):
    // party categories 1 and 9 (kinds 1-5) and friend categories 2 and 12 (kinds 1, 2, 4, 5) only. Never cross-world
    // party (14): the game may answer that one itself.
    private static bool NoticeValid(nint payload)
    {
        var category = *(byte*)(payload + 0x1E); var kind = *(byte*)(payload + 0x20);
        var party = category is 1 or 9 && kind is >= 1 and <= 5;
        var friend = category is 2 or 12 && kind is 1 or 2 or 4 or 5;
        if (!party && !friend || *(ulong*)(payload + 8) == 0 || *(ushort*)(payload + 0x1C) >= 0x1000) return false;
        var name = new ReadOnlySpan<byte>((byte*)(payload + 0x22), 32);
        var end = name.IndexOf((byte)0);
        if (end is < 1 or > 31) return false;
        foreach (var b in name[..end]) if (b < 0x20 || b == 0x7F) return false;
        return true;
    }

    // Estate Teleportation (AgentFriendlist.OpenFriendEstateTeleportation: the friend's estates, then a real teleport and
    // zone change) never runs from inside a home. It asks to leave first; after the return it runs (TeleportToEstate).
    private delegate void EstateTeleport(nint agent, ulong character);   // AgentFriendlist.OpenFriendEstateTeleportation (returns nothing)
    private Hook<EstateTeleport>? estateHook;
    /// <summary>Raised (on the game thread) when the player chose Estate Teleportation for a friend inside a home.</summary>
    public event Action<ulong>? EstateTeleportAsked;
    private nint estateAgent;
    private void HookEstateTeleport(ISigScanner scanner, IGameInteropProvider interop)
    {
        try
        {
            // The call site's signature: Dalamud follows the call to the function.
            var function = scanner.ScanText("E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? 48 8B CB E8 ?? ?? ?? ?? 33 D2 48 8B CB");
            estateHook = interop.HookFromAddress<EstateTeleport>(function, (agent, character) =>
            {
                if (!filter.IsActive) { estateHook!.Original(agent, character); return; }
                estateAgent = agent;
                EstateTeleportAsked?.Invoke(character);
            });
            estateHook.Enable();
        }
        catch (Exception ex) { log.Warning(ex, "[Haven] [SOCIAL] Estate Teleportation couldn't be watched; it stays blocked inside homes."); }
    }
    /// <summary>Runs Estate Teleportation for a friend, outside a home (after the player chose to leave for it).</summary>
    public bool TeleportToEstate(ulong character)
    {
        if (filter.IsActive || estateHook == null || estateAgent == 0) return false;
        estateHook.Original(estateAgent, character);
        return true;
    }

    /// <summary>
    /// Runs a social list's own refresh (InfoProxy RequestData, e.g. the friend list's) inside a home with its request
    /// let out and its pages let back in: each page matched by list type, request key and parameter (payload +0xC..+0xE =
    /// the request's +0xA..+0xC), within ten seconds, and each page's handling allowed to send the request for the next.
    /// Null when it can't (its request opcode or the page reply wasn't found): the caller keeps the list instead.
    /// </summary>
    public bool? PassListRefresh(nint requestData, int opcodeAt, Func<bool> request)
    {
        if (listPage == 0 || Opcode(requestData, opcodeAt, false) is not { } opcode) return null;
        void ExpectPage(nint sent)
        {
            var (type, key, parameter) = (*(byte*)(sent + 0x2A), *(byte*)(sent + 0x2B), *(byte*)(sent + 0x2C));
            filter.Expect(listPage, payload => *(byte*)(payload + 0xC) == type && *(byte*)(payload + 0xD) == key && *(byte*)(payload + 0xE) == parameter,
                Environment.TickCount64 + ReplyWindowMs, "social list page", handle => filter.PassRequest(opcode, ExpectPage, () => { handle(); return 0; }));
        }
        return filter.PassRequest(opcode, ExpectPage, () => request() ? 1 : 0) != 0;
    }

    // The party list payload: 8 entries of 0x1C8 (ContentId +0x30, EntityId +0x38), then the leader index at +0xE50
    // and the member count at +0xE51.
    // 0.41.0 also required each member's entity id to look like a player's and the player among the members: a real
    // party's list failed that (user test, 28 October 2026), so only the structure is checked, and what else looks
    // off is logged.
    private bool PartyListValid(nint payload)
    {
        var count = *(byte*)(payload + 0xE51);
        var leader = *(byte*)(payload + 0xE50);
        if (count > 8 || count > 0 && leader >= count) { log.Warning("[Haven] [SOCIAL] Party list refused: {Count} members, leader {Leader}.", count, leader); return false; }
        var me = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance() is var state && state != null ? state->ContentId : 0;
        var found = count == 0;
        var entities = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var entry = payload + i * 0x1C8;
            var character = *(ulong*)(entry + 0x30);
            if (character == 0) { log.Warning("[Haven] [SOCIAL] Party list refused: member {Index} has no character.", i); return false; }
            entities.Add((*(uint*)(entry + 0x38)).ToString("X8"));
            if (character == me) found = true;
        }
        log.Information("[Haven] [SOCIAL] Party list: {Count} members, leader {Leader}, entities {Entities}, you {Found}.", count, leader, string.Join(" ", entities), found ? "among them" : "not found");
        return true;
    }

    // A party message (payload: +0x10 who acted, +0x18 who it's about, +0x22 type, +0x25 and +0x45 names): joins,
    // leaves, kicks, promotions, disbands and members going offline or returning reach the client whenever they come
    // (a friend accepting the player's invite is one). Ready checks (types 15-19) stay out.
    private static bool PartyMessageValid(nint payload)
    {
        if (*(byte*)(payload + 0x22) is not ((>= 1 and <= 7) or (>= 10 and <= 13))) return false;
        foreach (var at in new[] { 0x25, 0x45 })
            if (new ReadOnlySpan<byte>((byte*)(payload + at), 32).IndexOf((byte)0) < 0) return false;
        return true;
    }

    // Each reply's opcode, from the zone dispatcher's switch: the one case block whose first call reaches its handler.
    private Dictionary<Reply, ushort> ReplyOpcodes(ISigScanner scanner)
    {
        var found = new Dictionary<Reply, ushort>();
        try
        {
            var handlers = new Dictionary<Reply, nint>();
            foreach (var (reply, signature) in Handlers)
                try { handlers[reply] = scanner.ScanText(signature); }
                catch (Exception) { log.Warning("[Haven] [SOCIAL] The {Reply} reply's handler wasn't found.", reply); }
            if (!Switch(filter.ReceiveFunction, out var bias, out var count, out var moduleBase, out var table)) { log.Warning("[Haven] [SOCIAL] The zone packet switch wasn't found; the friend menu's server items stay blocked inside homes."); return found; }
            var cases = new Dictionary<Reply, List<ushort>>();
            for (var i = 0; i < count; i++)
            {
                var block = (byte*)(moduleBase + ((uint*)table)[i]);
                if (FirstCall(block, 16) is { } target)
                    foreach (var (reply, handler) in handlers)
                        if (reply != Reply.PartyList && Reaches(reply, target, handler)) (cases.TryGetValue(reply, out var list) ? list : cases[reply] = []).Add((ushort)(i + bias));
                if (handlers.TryGetValue(Reply.PartyList, out var party) && Calls(block, 0x30).Contains(party))
                    (cases.TryGetValue(Reply.PartyList, out var parties) ? parties : cases[Reply.PartyList] = []).Add((ushort)(i + bias));
            }
            foreach (var (reply, opcodes) in cases)
            {
                if (opcodes.Distinct().Count() == 1) found[reply] = opcodes[0];
                else log.Warning("[Haven] [SOCIAL] The {Reply} reply matched {Count} packet types; it stays blocked.", reply, opcodes.Distinct().Count());
            }
        }
        catch (Exception ex) { log.Warning(ex, "[Haven] [SOCIAL] The zone packet switch couldn't be read."); }
        return found;
    }

    // Whether a case's first call reaches a reply's handler: directly, or (plate, company) through its stub's jump.
    private static bool Reaches(Reply reply, nint target, nint handler)
    {
        if (target == handler) return true;
        if (!InText(target, 0x80)) return false;
        var code = (byte*)target;
        switch (reply)
        {
            case Reply.Plate:
                // mov rdx,rcx; lea rcx,[rip+x]; jmp handler
                return code[0] == 0x48 && code[1] == 0x8B && code[2] == 0xD1 && code[3] == 0x48 && code[4] == 0x8D && code[5] == 0x0D && code[10] == 0xE9
                    && (nint)(code + 15 + *(int*)(code + 11)) == handler;
            case Reply.Company:
                // fetches InfoProxy 13 (mov edx,0Dh) and tail-jumps to the handler, within its first bytes.
                var proxy = false;
                for (var i = 0; i < 0x60; i++)
                {
                    if (code[i] == 0xBA && code[i + 1] == 0x0D && code[i + 2] == 0 && code[i + 3] == 0 && code[i + 4] == 0) proxy = true;
                    if (code[i] == 0xE9 && proxy && (nint)(code + i + 5 + *(int*)(code + i + 1)) == handler) return true;
                }
                return false;
            default: return false;
        }
    }

    // The request's opcode: the immediate of `mov dword ptr [rsp+..], imm32` (C7 44 24 xx) at a known place in its own
    // code, or in the first function it calls (within its first bytes) that has one there.
    private static ushort? Opcode(nint function, int at, bool leaf)
    {
        static ushort? Read(byte* code) => code[-4] == 0xC7 && code[-3] == 0x44 && code[-2] == 0x24 && *(uint*)code is > 0 and <= 0xFFFF ? (ushort)*(uint*)code : null;
        if (!leaf) return Read((byte*)function + at);
        var code = (byte*)function;
        for (var i = 0; i < 0xC0; i++)
            if (code[i] == 0xE8 && (nint)(code + i + 5 + *(int*)(code + i + 1)) is var callee && InText(callee + at - 4, 8) && Read((byte*)callee + at) is { } found) return found;
        return null;
    }

    private static List<nint> Calls(byte* code, int within)
    {
        List<nint> calls = [];
        for (var i = 0; i < within; i++) if (code[i] == 0xE8 && (nint)(code + i + 5 + *(int*)(code + i + 1)) is var target && InText(target, 1)) calls.Add(target);
        return calls;
    }

    private static nint? FirstCall(byte* code, int within)
    {
        for (var i = 0; i < within; i++) if (code[i] == 0xE8 && (nint)(code + i + 5 + *(int*)(code + i + 1)) is var target && InText(target, 1)) return target;
        return null;
    }

    // The dispatcher's switch: add r13d,-bias; mov rdi,r12; cmp r13d,last; ja; lea rdx,[module base]; movsxd rax,r13d;
    // mov ecx,[rdx+rax*4+table].
    private static bool Switch(nint function, out int bias, out int count, out nint moduleBase, out nint table)
    {
        bias = count = 0; moduleBase = table = 0;
        if (function == 0) return false;
        var code = (byte*)function;
        byte?[] pattern = [0x41, 0x83, 0xC5, null, 0x49, 0x8B, 0xFC, 0x41, 0x81, 0xFD, null, null, null, null, 0x0F, 0x87, null, null, null, null, 0x48, 0x8D, 0x15, null, null, null, null, 0x49, 0x63, 0xC5, 0x8B, 0x8C, 0x82];
        for (var i = 0; i < 0x4000; i++)
        {
            var hit = true;
            for (var j = 0; j < pattern.Length && hit; j++) if (pattern[j] is { } b && code[i + j] != b) hit = false;
            if (!hit) continue;
            var at = code + i;
            bias = -(sbyte)at[3];
            count = *(int*)(at + 10) + 1;
            moduleBase = (nint)(at + 27 + *(int*)(at + 23));
            table = moduleBase + *(int*)(at + 33);
            return count is > 0 and < 0x1000 && bias is >= 0 and < 0x1000;
        }
        return false;
    }

    public void Dispose()
    {
        estateHook?.Dispose();
        foreach (var hook in hooks) hook.Dispose();
        hooks.Clear();
    }
}
