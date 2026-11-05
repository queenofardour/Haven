using System.Collections.Concurrent;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Haven.Core;
using HavenNative;

namespace Haven;

/// <summary>
/// An NPC's mods. Haven never carries the files: guests' sync plugins (Lightless, PlayerSync) already
/// download whatever the owner's character is drawn with, so an NPC's mods reach guests while the owner wears its look.
/// Three ways in, all from the NPCs panel:
/// - Copy my look: the NPC takes the mods the owner is drawn with right now (read from Penumbra, kept in the store). In
///   a gathering, guests already see the owner in that look, so their Haven can copy the same files at once.
/// - Send its mods to guests: the owner wears the NPC's kept files for a moment (a temporary Penumbra collection made
///   from them, from a copy in Penumbra's mod folder, nothing written to Penumbra's settings) and its Glamourer look,
///   so guests' sync plugins fetch them. Not faded out: a fade (the character's alpha) outlived the share on guests' PCs
///   and left the owner invisible there, through redraws and later visits (0.58.8, in game).
/// - From a Penumbra collection: as Send, but the owner wears a collection they chose (their individual collection is
///   switched and put back), for an NPC whose look isn't one they have worn.
/// Guests are asked whether to keep each share; those who agree read what Penumbra draws the owner with on their PC and
/// keep the files whose fingerprints (NpcMods) the owner sends. Each PC keeps the NPC's files (NpcModStore) and builds its collection from them (NpcCollections); they
/// outlive the visit. Framework thread, apart from reading and copying files, which run in the background.
/// </summary>
internal sealed class NpcModShare(IDalamudPluginInterface plugin, HousingController housing, NativeEngine engine, NpcPossession npcs, NpcGlamour glamour,
    IObjectTable objects, IPluginLog log) : IDisposable
{
    // When dressed, the owner is read once what they're drawn with has been unchanged for SettleQuiet seconds (a race
    // change redraws twice and loads gear later; 0.57.3 read at a fixed 5 s and caught a body without its gear), from
    // SettleFrom seconds and at most SettleMost. Copy my look reads sooner: nothing changes.
    private const double SettleFrom = 3.5, SettleQuiet = 3, SettleMost = 25, SettlePoll = .5, CaptureFrom = .5, CaptureQuiet = 1.5;
    // The NPC's look is put on the owner again after the redraw: a redraw lets Glamourer apply the player's automation
    // again, which put the player's own clothes back over the NPC's (0.57.4). The same after the restore's redraw.
    private static readonly double[] ReapplyAfter = [1.5, 3];
    // Times the player is dressed again when they aren't drawn in the NPC's look, before the share stops.
    private const int DressRetries = 1;
    private static readonly double[] AfterLeaving = [5, 6.5, 8];
    private const double OwnerWait = 150, GuestWait = 180, PollSeconds = 2, SendGap = .25, CollectionsAge = 5;
    // A guest's answer that they didn't keep a share (sent as Missed's count), so the owner stops waiting for them.
    private const int Declined = -1;
    private const int Success = 0, NothingChanged = 1;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly NpcModStore store = new(plugin, log);
    private string GuardPath => Path.Combine(store.Root, "share-guard.json");

    // What the owner's character wore before being dressed as an NPC: put back afterwards, also after a crash or unload.
    // Character: whose look it is. SetCollection: their individual collection was switched (Collection is the one to
    // put back); Worn: a temporary collection they were dressed in, deleted to undress them.
    private sealed record Guard(Guid? Collection, string? State, ulong Character = 0, bool SetCollection = true, Guid? Worn = null);

    private enum ShareKind { Capture, Collection, Resend }

    private sealed class Outgoing(Guid npc, string name, int share, ShareKind kind)
    {
        public readonly Guid Npc = npc;
        public readonly string Name = name;
        public readonly int Share = share;
        public readonly ShareKind Kind = kind;
        public bool Dresses => Kind != ShareKind.Capture;
        public bool Read;                       // the owner's files were read (or, sending again, taken from the store)
        public bool Waiting;                    // the fingerprints went out; guests are fetching
        public DateTime ReadAt, Until, Started = DateTime.UtcNow, StableSince;
        public string Drawn = "";               // what the owner was drawn with at the last look (to see it settle)
        public int Files, Guests;               // files shared, and the other members there when they went out
        public string Design = "";
        public NpcModSet? Set;                  // sending again: the kept set
        public int? Mods;                       // how many Penumbra mods the files come from, when known
        public int Version;                     // the set's version (NpcMods.Version), for guests to record
        public readonly Queue<DateTime> Reapply = new();
        public bool Dressed;                    // the player was seen drawn in the NPC's look
        public int DressTries;                  // times dressed again because they weren't
        public readonly Dictionary<ulong, (bool Took, int Copied)> Replies = [];
        public readonly Dictionary<ulong, (int Have, int Wanted)> Progress = [];
    }

    // A share reaching this guest. A file counts when its fingerprint or its sync-plugin name (the SHA-1 of the owner's
    // file) is listed or, once the sync plugin has changed what the owner is drawn with at one of the NPC's game paths
    // (against Baseline, read when the owner announced the share, before dressing), when it is what now stands there:
    // sync plugins can downscale or recompress files on the way.
    private sealed class Incoming(ulong owner, Guid npc, string name, int share)
    {
        public readonly ulong Owner = owner;
        public readonly Guid Npc = npc;
        public readonly string Name = name;
        public readonly int Share = share;
        public int Files, Messages = -1, Paths, PathMessages, SyncMessages;   // Messages -1: the counts haven't come yet
        public readonly HashSet<int> Numbers = [], PathNumbers = [], SyncNumbers = [];
        public readonly HashSet<ulong> Prints = [], PathPrints = [], SyncPrints = [];
        public Dictionary<string, string>? Baseline;                // game path -> file the owner was drawn with before
        public readonly Dictionary<string, NpcModFound> Found = [];   // by hash
        public readonly HashSet<ulong> Covered = [];                // the NPC's game paths with a file
        public int ByPrint, BySyncName, ByChange, Differing;
        public bool Changed;
        public string Meta = "";
        public int? Mods;                                           // the owner's count of mods, when sent
        public int? Origin;                                         // the owner's version of the set (0.59.0), when sent
        public DateTime Until = DateTime.UtcNow.AddSeconds(GuestWait), NextPoll;
        public bool Ended, Keeping;
        public bool? Accepted;                                      // the guest's answer to keeping it (asked when it starts)
        public int SentHave = -1;
        public DateTime SentAt;
        public bool Complete => Messages >= 0 && Numbers.Count >= Messages && PathNumbers.Count >= PathMessages && SyncNumbers.Count >= SyncMessages;
        public int Wanted => Paths > 0 ? Paths : Files;
        public int Have => Paths > 0 ? Covered.Count : Found.Count;
    }

    private readonly ConcurrentQueue<GameMessage> inbox = new();
    private readonly ConcurrentQueue<Action> finished = new();
    private readonly Queue<GameMessage> outbox = new();
    private DateTime nextSend;
    private Outgoing? sharing;
    private Incoming? incoming;
    private bool busy;
    private ulong guardCheckedFor;
    private Guid? pausesNoted;
    private DateTime enteredAt;
    // The last share from here, for a while after it ended: guests' answers arriving late still count in the note.
    private Outgoing? recent;
    private DateTime recentUntil;
    // The download bar's "done" moment after a share ends here (owner or guest).
    private (string Title, string Detail, bool Done, DateTime Until)? ended;
    // The player's own look, put on again after the restore's redraw; Redraw: the redraw itself is still to come (a share
    // ended by leaving the home: a redraw during the trip home would land in the zone reload).
    private (string State, Queue<DateTime> At, bool Redraw)? restoring;
    private (DateTime At, IReadOnlyList<(Guid Id, string Name)> List) collections = (DateTime.MinValue, []);
    private string? lastNote;

    private ulong Me => engine.HousingCharacterId;

    /// <summary>Whether the player may share mods for this home's NPCs: in their own home, alone or hosting it.</summary>
    public bool MayShare => housing.CanFurnish && (!housing.IsSharedVisit || engine.HousingRelay.Editing?.Owner == engine.HousingRelay.LocalContentId);
    /// <summary>Set by the plugin: visiting NPCs, whose mods only their bringer shares.</summary>
    public NpcVisitors? Visitors { get; set; }
    // An NPC here: the home's own, or a visitor drawn here.
    private HomeNpc? Npc(Guid id) => housing.Npcs.FirstOrDefault(x => x.Id == id) ?? Visitors?.Find(id);
    // Who brought an NPC, when it's a visitor (null: one of the home's).
    private ulong? BringerOf(Guid id) => housing.Npcs.Any(x => x.Id == id) ? null : Visitors?.BringerOf(id);
    // Whether a sender's word counts for an NPC's mods: the home's owner for its NPCs, a visitor's bringer for it.
    private bool SharerOf(Guid id, ulong sender) => Npc(id) != null && VisitingNpcs.ModsFrom(sender, BringerOf(id), engine.HousingRelay.Editing?.Owner ?? 0);
    /// <summary>Whether the player may share an NPC's mods: the home's NPCs as MayShare says; a visitor only if they brought it.</summary>
    public bool MayShareFor(Guid id) => BringerOf(id) is { } bringer ? bringer == Me && housing.IsSharedVisit && housing.IsReady && !housing.EndingVisit : MayShare;
    /// <summary>A visitor of this player's was let in: everyone is told which mods it carries, a moment later (they need the welcome first).</summary>
    public void VisitorWelcomed(Guid id) => stateDue[id] = DateTime.UtcNow.AddSeconds(VisitorStateDelay - StateDelay);
    private readonly Dictionary<Guid, DateTime> stateDue = [];
    /// <summary>A copy of the mods this PC keeps for one NPC under another's id (saving to Your NPCs, adding from it).</summary>
    public void CopyKept(Guid from, Guid to) => store.Copy(from, to);
    /// <summary>Drops the mods this PC keeps for an NPC that's gone for good (a roster entry removed), and its share folder.</summary>
    public void DropKept(Guid npc)
    {
        store.Drop(npc);
        Uninstall(npc);
        older.Remove(npc);
    }
    /// <summary>The NPC whose mods are being shared from here, if any.</summary>
    public Guid? SharingNpc => sharing?.Npc ?? preparing;
    /// <summary>Said before the player is dressed as an NPC (kept with the panel, which the UI preview compiles).</summary>
    public const string AutomationNote = NpcPanelUi.AutomationNote;
    // Sending again: the NPC whose files are being set out in Penumbra's mod folder before the player is dressed.
    private Guid? preparing;
    private const string ShareSortFolder = "Haven NPC Mods";
    /// <summary>The last thing a share said (finished, nothing found…), for the panel.</summary>
    public string? LastNote => lastNote;

    public void Receive(GameMessage message)
    {
        if (message.Game == "npc" && message.Kind is NpcMods.Progress or NpcMods.SyncPrints or NpcMods.Announce or NpcMods.Begin or NpcMods.Prints
                or NpcMods.PathPrints or NpcMods.Finish or NpcMods.Took or NpcMods.Missed)
            inbox.Enqueue(message);
    }

    // What this PC keeps --------------------------------------------------------

    public NpcModSet? Stored(Guid npc) => store.Stored(npc);

    // Sets kept before mods were counted are counted once a visit, together, in the background (NpcModStore.CountMods).
    private bool counting;
    private Guid? countedHome;
    /// <summary>How many mods an NPC's kept files come from; null while unknown (a first look starts the count).</summary>
    public int? ModCountOf(Guid npc)
    {
        if (store.Stored(npc) is not { } set) return null;
        var home = housing.ActiveHouse?.Id;
        if (set.Mods != null || counting || countedHome == home) return set.Mods;
        counting = true;
        string root;
        try { root = plugin.GetIpcSubscriber<string>("Penumbra.GetModDirectory").InvokeFunc(); }
        catch (Exception) { root = ""; }
        var uncounted = (housing.ActiveHouse?.Npcs ?? []).Select(n => store.Stored(n.Id)).OfType<NpcModSet>().Where(s => s.Mods == null).ToList();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root) || uncounted.Count == 0) { counting = false; countedHome = home; return null; }
        Task.Run(() =>
        {
            Dictionary<Guid, int> found;
            try { found = store.CountMods(uncounted, root); }
            catch (Exception ex) { log.Warning("[NPC-MODS] the NPCs' mods couldn't be counted ({Message}).", ex.Message); found = []; }
            finished.Enqueue(() =>
            {
                foreach (var (id, mods) in found) store.SetCount(id, mods);
                counting = false; countedHome = home;
                log.Information("[NPC-MODS] counted the mods of {Counted} of {Sets} NPC(s) kept before counts.", found.Count, uncounted.Count);
            });
        });
        return null;
    }
    /// <summary>For NpcCollections: an NPC's game paths, the store's files for them, and its meta.</summary>
    public (Dictionary<string, string> Paths, string Meta)? CollectionFiles(Guid npc) => store.CollectionFiles(npc);
    public Guid? CollectionOf(Guid npc) => store.CollectionOf(npc);
    /// <summary>A copied home's NPCs (old id to new) start with the mods their originals keep here (framework thread).</summary>
    public void CopyKept(IReadOnlyDictionary<Guid, Guid> renamed)
    {
        foreach (var (from, to) in renamed) store.Copy(from, to);
    }

    /// <summary>Forgets the mods this PC keeps for an NPC; it is drawn without them.</summary>
    public void Forget(Guid npc)
    {
        store.Drop(npc, forgotten: true);
        Uninstall(npc);
        older.Remove(npc);
        TellState(npc);
        npcs.Remod(npc);
    }

    /// <summary>The player's Penumbra collections by name (asked again every few seconds); empty without Penumbra.</summary>
    public IReadOnlyList<(Guid Id, string Name)> Collections()
    {
        if ((DateTime.UtcNow - collections.At).TotalSeconds < CollectionsAge) return collections.List;
        try
        {
            var list = plugin.GetIpcSubscriber<Dictionary<Guid, string>>("Penumbra.GetCollections.V5").InvokeFunc()
                .Select(x => (x.Key, x.Value)).OrderBy(x => x.Value, StringComparer.CurrentCultureIgnoreCase).ToList();
            collections = (DateTime.UtcNow, list);
        }
        catch (Exception ex) { collections = (DateTime.UtcNow, []); Tell($"Penumbra isn't available ({ex.Message})."); }
        return collections.List;
    }

    /// <summary>The NPCs panel's line about an NPC's mods.</summary>
    public string Status(Guid npc)
    {
        if (sharing?.Npc == npc)
            return sharing.Kind == ShareKind.Capture && !sharing.Waiting ? "Copying the mods you're drawn with…"
                : !sharing.Read ? "Dressing you as it for a moment, so your guests' sync plugins pick up its mods…"
                : !sharing.Waiting ? "Reading the files it's drawn with…"
                : $"Waiting for your guests: {sharing.Replies.Count} of {Others()} answered ({sharing.Replies.Values.Count(x => x.Took)} have its mods).";
        if (incoming?.Npc == npc) return $"Getting its mods from the owner's sync: {incoming.Have} of {incoming.Wanted}.";
        if (Stored(npc) is not { } set) return "";
        var stale = OlderFor(npc) is > 0 and var n ? $"{n} guest{(n == 1 ? " has" : "s have")} an older version: send its mods to guests again. " : "";
        var mods = set.Mods is { } count ? $"{count} mod{(count == 1 ? "" : "s")}, " : "";
        return $"{stale}{mods}{set.Files.Length} files, {set.Files.Sum(f => f.Size) / 1048576.0:F0} MB, kept {set.Shared.ToLocalTime():d MMM HH:mm}.";
    }

    // Starting a share (the owner) ------------------------------------------------

    /// <summary>
    /// Copy my look: the NPC takes the mods the player is drawn with now (its look itself is copied by the caller). In a
    /// gathering guests copy the same files. An error message, or null when it began.
    /// </summary>
    public string? Capture(Guid id)
    {
        if (Refusal(id, dresses: false) is { } refused) return refused;
        var npc = Npc(id)!;
        sharing = new Outgoing(id, npc.Name, NewShare(), ShareKind.Capture) { ReadAt = DateTime.UtcNow.AddSeconds(CaptureFrom) };
        lastNote = null;
        log.Information("[NPC-MODS] copying the mods you're drawn with to {Name}.", npc.Name);
        return null;
    }

    /// <summary>
    /// Send its mods to guests: the player wears the NPC's kept files and look for a moment. The files are worn from a
    /// copy in Penumbra's mod folder (NpcModStore.ShareFolder), set out first in the background: sync plugins send only
    /// files there or in their own cache.
    /// </summary>
    public string? Send(Guid id)
    {
        if (!housing.IsSharedVisit || Others() == 0) return "There's nobody here to send its mods to.";
        if (Refusal(id, dresses: true) is { } refused) return refused;
        if (store.Stored(id) is not { } set || store.CollectionFiles(id) is null) return "This PC keeps no mods for it. Copy your look to it first.";
        string root;
        try { root = plugin.GetIpcSubscriber<string>("Penumbra.GetModDirectory").InvokeFunc(); }
        catch (Exception ex) { return $"Penumbra didn't answer ({ex.Message})."; }
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return "Penumbra's mod folder isn't set, so your sync plugin couldn't send the NPC's files.";
        var npc = Npc(id)!;
        preparing = id;
        Background(() => store.ShareFolder(set, root, npc.Name), files =>
        {
            preparing = null;
            var why = Refusal(id, dresses: true) ?? (housing.IsSharedVisit && Others() > 0 ? null : "There's nobody here to send its mods to.")
                ?? Install(ShareModOf(id), npc.Name)
                ?? Dress(npc, ShareKind.Resend, own => WriteGuard(new Guard(null, own, Me, SetCollection: false)) is { } failed ? failed
                    : npcs.Collections?.Wear($"Haven sharing {npc.Name}", files) is { } worn ? Noted(own, worn)
                    : "Penumbra wouldn't dress you in its mods.", set);
            if (why != null) NotSent(npc.Name, why);
        }, () => { preparing = null; NotSent(npc.Name, "Its files couldn't be copied to Penumbra's mod folder; see the log."); });
        return null;
    }

    private static string ShareModOf(Guid npc) => NpcModStore.ShareMod(npc);

    // The guard again, now naming the collection the player wears; if it can't be written, the collection comes off
    // here (the guard on disk doesn't name it, so putting the player back wouldn't).
    private string? Noted(string own, Guid worn)
    {
        if (WriteGuard(new Guard(null, own, Me, SetCollection: false, Worn: worn)) is not { } failed) return null;
        npcs.Collections?.Unwear(worn);
        return failed;
    }

    // A share that couldn't start once its files were ready: said in the panel and the bar, as a share that stopped.
    private void NotSent(string name, string why)
    {
        lastNote = why;
        ended = ($"Sharing {name}'s mods", "Stopped", false, DateTime.UtcNow.AddSeconds(4));
        log.Warning("[NPC-MODS] {Name}'s mods weren't sent: {Why}", name, why);
    }

    // The NPC's folder known to Penumbra as a mod (added once), sorted into Haven's folder; an error, or null.
    private string? Install(string mod, string name)
    {
        try
        {
            if (plugin.GetIpcSubscriber<Dictionary<string, string>>("Penumbra.GetModList").InvokeFunc().ContainsKey(mod)) return null;
            var added = plugin.GetIpcSubscriber<string, int>("Penumbra.AddMod.V5").InvokeFunc(mod);
            if (added != Success) return $"Penumbra didn't add the NPC's mods as a mod (code {added}).";
            plugin.GetIpcSubscriber<string, string, string, int>("Penumbra.SetModPath.V5").InvokeFunc(mod, "", $"{ShareSortFolder}/{name.Replace('/', '-')}");
            log.Information("[NPC-MODS] {Name}'s files are in Penumbra as the mod {Mod}, for your sync plugin to send.", name, mod);
            return null;
        }
        catch (Exception ex) { return $"Penumbra didn't answer ({ex.Message})."; }
    }

    // Removes an NPC's share folder from Penumbra (and its files), if Haven made one.
    private void Uninstall(Guid npc)
    {
        var mod = ShareModOf(npc);
        try
        {
            if (!plugin.GetIpcSubscriber<Dictionary<string, string>>("Penumbra.GetModList").InvokeFunc().ContainsKey(mod)) return;
            var code = plugin.GetIpcSubscriber<string, string, int>("Penumbra.DeleteMod.V5").InvokeFunc(mod, "");
            if (code != Success) log.Warning("[NPC-MODS] Penumbra didn't delete the mod {Mod} (code {Code}).", mod, code);
        }
        catch (Exception ex) { log.Information("[NPC-MODS] Penumbra couldn't delete the mod {Mod} ({Message}).", mod, ex.Message); }
    }

    /// <summary>From a Penumbra collection: the player wears that collection and the NPC's look, and its mods are kept.</summary>
    public string? ShareFrom(Guid id, Guid collection)
    {
        if (Refusal(id, dresses: true) is { } refused) return refused;
        store.Choose(id, collection);
        var npc = Npc(id)!;
        return Dress(npc, ShareKind.Collection, own =>
        {
            var (valid, individual, effective) = plugin.GetIpcSubscriber<int, (bool, bool, (Guid, string))>("Penumbra.GetCollectionForObject.V5").InvokeFunc(0);
            if (!valid) return "Penumbra doesn't know your character right now.";
            return WriteGuard(new Guard(individual ? effective.Item1 : null, own, Me)) ?? SetPlayerCollection(collection);
        }, null);
    }

    // Why a share can't start now, or null.
    private string? Refusal(Guid id, bool dresses)
    {
        if (sharing != null || preparing != null) return "Already sharing an NPC's mods. Wait for it to finish.";
        if (!MayShareFor(id)) return BringerOf(id) != null ? "Only the player who brought it shares a visiting NPC's mods." : "Only the home's owner shares its NPCs' mods.";
        if (busy) return "Still keeping the last NPC's files. Try again in a moment.";
        if (Npc(id) is not { } npc) return "That NPC isn't in this home.";
        if (objects.LocalPlayer == null) return "Your character isn't here.";
        if (!dresses) return null;
        if (npcs.Possessing) return "Let go of the NPC first.";
        if (npc.Look.Design == null) return "Give it a Glamourer look first: you're dressed in it to share its mods.";
        if (File.Exists(GuardPath) || restoring != null) return "Your own look from the last share is still being put back. Try again in a moment.";
        return null;
    }

    private static int NewShare() => Random.Shared.Next(1, int.MaxValue);

    // Dresses the player as the NPC: wear (writes the guard and changes their collection; an error, or null) then its
    // Glamourer look, with guests told first so they note how the player looks now.
    private string? Dress(HomeNpc npc, ShareKind kind, Func<string, string?> wear, NpcModSet? set)
    {
        if (glamour.State(0) is not { } own) return "Glamourer didn't give your current look, so it couldn't be put back afterwards.";
        var shareId = NewShare();
        try
        {
            if (wear(own) is { } refused) { if (File.Exists(GuardPath)) RestoreFromGuard(); return refused; }
            if (housing.IsSharedVisit) Send(new GameMessage(Me, GameMessage.Everyone, "npc", NpcMods.Announce, npc.Id, [shareId]), now: true);
            if (!glamour.ApplyToPlayer(npc.Look.Design!))
            {
                // Sharing in the player's own look would keep their own gear and race as the NPC's; guests were told a
                // share is starting, so tell them it's off (or they'd keep asking and waiting).
                if (housing.IsSharedVisit) Send(new GameMessage(Me, GameMessage.Everyone, "npc", NpcMods.Finish, npc.Id, [shareId]), now: true);
                RestoreFromGuard();
                return "Glamourer wouldn't put the NPC's look on you (another plugin may hold your look).";
            }
            Redraw(0);
        }
        catch (Exception ex)
        {
            if (File.Exists(GuardPath)) RestoreFromGuard();
            return $"Penumbra or Glamourer didn't answer ({ex.Message}).";
        }
        sharing = new Outgoing(npc.Id, npc.Name, shareId, kind) { ReadAt = DateTime.UtcNow.AddSeconds(SettleFrom), Design = npc.Look.Design!, Set = set, Mods = set?.Mods, Version = set != null ? NpcMods.Identify(set).Version : 0 };
        foreach (var after in ReapplyAfter) sharing.Reapply.Enqueue(DateTime.UtcNow.AddSeconds(after));
        lastNote = null;
        log.Information("[NPC-MODS] sharing {Name}'s mods: you wear its {What} and look for now.", npc.Name, kind == ShareKind.Resend ? "kept files" : "collection");
        return null;
    }

    // Writes the guard (atomically); an error, or null.
    private string? WriteGuard(Guard guard)
    {
        try
        {
            Directory.CreateDirectory(store.Root);
            File.WriteAllText(GuardPath + ".tmp", JsonSerializer.Serialize(guard, Json));
            File.Move(GuardPath + ".tmp", GuardPath, true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return $"Your look couldn't be noted to put it back afterwards ({ex.Message})."; }
    }

    // Ending a share ------------------------------------------------------------

    /// <summary>The visit is ending: a share stops now, before the trip home (its redraw waits until the player is back).</summary>
    public void Leaving() => FinishSharing("The share stopped when you left the home.", leaving: true);

    /// <summary>Ends a share now: a dressed player's own collection and look come back.</summary>
    public void FinishSharing(string? why = null, bool leaving = false)
    {
        if (sharing is not { } done) return;
        sharing = null;
        outbox.Clear();
        if (housing.IsSharedVisit) Send(new GameMessage(Me, GameMessage.Everyone, "npc", NpcMods.Finish, done.Npc, [done.Share]), now: true);
        if (done.Dresses) RestoreFromGuard(leaving: leaving);
        lastNote = why ?? (done.Waiting && housing.IsSharedVisit ? Summary(done) : $"{done.Name} keeps {done.Files} file(s) of mods here.");
        ended = (done.Kind == ShareKind.Capture ? $"Copied your mods to {done.Name}" : $"Shared {done.Name}'s mods", why != null ? "Stopped" : done.Waiting && housing.IsSharedVisit
            ? $"{done.Replies.Values.Count(x => x.Took)} of {done.Guests} guest(s) have them" : "Kept here", why == null, DateTime.UtcNow.AddSeconds(4));
        if (done.Waiting) { recent = done; recentUntil = DateTime.UtcNow.AddSeconds(OwnerWait); }
        log.Information("[NPC-MODS] share of {Name} ended: {Note}", done.Name, lastNote);
    }

    private static string Summary(Outgoing done)
    {
        var took = done.Replies.Values.Count(x => x.Took);
        var declined = done.Replies.Values.Count(x => x.Copied == Declined);
        var silent = Math.Max(0, done.Guests - done.Replies.Count);
        return $"Shared {done.Name}'s mods: {took} of {done.Guests} guest(s) have them."
            + (declined > 0 ? $" {declined} chose not to keep them." : "")
            + (silent > 0 ? $" {silent} haven't answered yet (still fetching, not paired with you in a sync plugin, or on an older Haven)." : "");
    }

    private void RestoreFromGuard(bool later = true, bool leaving = false)
    {
        Guard? guard = null;
        try { if (File.Exists(GuardPath)) guard = JsonSerializer.Deserialize<Guard>(File.ReadAllText(GuardPath)); }
        catch (Exception ex) { log.Warning("[NPC-MODS] the look to put back couldn't be read ({Message}).", ex.Message); }
        try
        {
            if (guard != null)
            {
                if (guard.Character != 0 && guard.Character != Me)
                {
                    log.Information("[NPC-MODS] the look to put back belongs to another of your characters; it waits for them.");
                    return;
                }
                if (guard.SetCollection && SetPlayerCollection(guard.Collection) is { } refused)
                {
                    // Kept, so the next login tries again rather than leaving the NPC's collection as the player's own.
                    log.Warning("[NPC-MODS] your own collection couldn't be put back yet ({Refused}); it is tried again next time.", refused);
                    if (guard.State != null) glamour.ApplyToPlayer(guard.State, restore: true);
                    return;
                }
                if (guard.Worn is { } worn) npcs.Collections?.Unwear(worn);
                if (guard.State != null) glamour.ApplyToPlayer(guard.State, restore: true); else glamour.RevertPlayer();
                if (!leaving) Redraw(0);
                if (later && guard.State != null)
                    restoring = (guard.State, new((leaving ? AfterLeaving : ReapplyAfter).Select(s => DateTime.UtcNow.AddSeconds(s))), leaving);
            }
            File.Delete(GuardPath);
        }
        catch (Exception ex) { log.Warning("[NPC-MODS] your own collection or look couldn't be put back ({Message}); it is tried again next time.", ex.Message); }
    }

    // The player's own collection: an individual one (null removes Haven's, leaving Penumbra's usual choice).
    private string? SetPlayerCollection(Guid? collection)
    {
        var (code, _) = plugin.GetIpcSubscriber<int, Guid?, bool, bool, (int, (Guid, string)?)>("Penumbra.SetCollectionForObject.V5").InvokeFunc(0, collection, true, true);
        if (code is Success or NothingChanged) return null;
        log.Warning("[NPC-MODS] Penumbra wouldn't set your collection (code {Code}).", code);
        return $"Penumbra wouldn't change your collection (code {code}).";
    }

    private void Redraw(int index)
    {
        try { plugin.GetIpcSubscriber<int, int, object?>("Penumbra.RedrawObject.V5").InvokeAction(index, 0); }
        catch (Exception ex) { log.Information("[NPC-MODS] Penumbra couldn't redraw object {Index} ({Message}).", index, ex.Message); }
    }

    private int Others() => engine.HousingRelay.HousingMembers.Values.Count(m => m.ContentId != Me);

    // The owner: guests told which mods the NPCs carry this visit, and those whose copy of an NPC's mods is older.
    private readonly HashSet<ulong> statedTo = [];
    private readonly Dictionary<ulong, DateTime> seenAt = [];
    private const double StateDelay = 20, VisitorStateDelay = 35;
    private readonly HashSet<ulong> visitorStatedTo = [];

    // A visitor's state: only a set this PC kept, or forgot on purpose (as for the home's NPCs).
    private void VisitorState(Guid id, ulong target)
    {
        if (store.Stored(id) is { } kept) outbox.Enqueue(new GameMessage(Me, target, "npc", NpcMods.Finish, id, NpcMods.State(kept)));
        else if (store.Forgotten(id)) outbox.Enqueue(new GameMessage(Me, target, "npc", NpcMods.Finish, id, NpcMods.State(null)));
    }
    private readonly Dictionary<Guid, HashSet<ulong>> older = [];

    /// <summary>How many guests here have an older version of an NPC's mods than the owner's (the owner's PC only).</summary>
    public int OlderFor(Guid npc) => older.TryGetValue(npc, out var guests) ? guests.Count(g => engine.HousingRelay.HousingMembers.Values.Any(m => m.ContentId == g)) : 0;

    // The owner: tells everyone here which mods an NPC carries now (after they were kept or forgotten).
    private void TellState(Guid npc)
    {
        if (!housing.IsSharedVisit || !MayShareFor(npc) || Others() == 0) return;
        outbox.Enqueue(new GameMessage(Me, GameMessage.Everyone, "npc", NpcMods.Finish, npc, NpcMods.State(store.Stored(npc))));
    }

    // Where the player's look differs from the share's: race, gender and clan as their model is drawn, armour as
    // Glamourer holds it. Empty when they match (or nothing could be read).
    private string[] Undressed(Outgoing share)
    {
        var drawn = glamour.State(0) is { } state ? GlamourerStates.Shown(state) : [];
        if (engine.HousingDrawnBody() is { } body) { drawn["Race"] = body.Race; drawn["Gender"] = body.Gender; drawn["Clan"] = body.Clan; }
        return GlamourerStates.Differences(GlamourerStates.Shown(share.Design), drawn);
    }

    // Each frame ----------------------------------------------------------------

    public void Update()
    {
        while (finished.TryDequeue(out var then)) then();
        if (sharing == null && restoring is { } back && back.At.TryPeek(out var at) && DateTime.UtcNow >= at && objects.LocalPlayer != null)
        {
            back.At.Dequeue();
            if (back.Redraw) { Redraw(0); restoring = back with { Redraw = false }; }   // the first step after leaving
            else glamour.ApplyToPlayer(back.State, restore: true);
            if (back.At.Count == 0) restoring = null;
        }
        if (objects.LocalPlayer != null && Me != 0 && guardCheckedFor != Me)
        {
            guardCheckedFor = Me;
            if (sharing == null && File.Exists(GuardPath)) { log.Information("[NPC-MODS] putting back your look from a share that didn't finish."); RestoreFromGuard(); }
        }
        var inHome = housing.ActiveHouse != null && housing.IsReady && !housing.EndingVisit;
        if (!inHome)
        {
            if (sharing != null) FinishSharing("The share stopped when you left the home.");
            if (incoming != null) StopIncoming(false);
            pausesNoted = null;
            statedTo.Clear();
            visitorStatedTo.Clear();
            stateDue.Clear();
            seenAt.Clear();
            older.Clear();
            inbox.Clear();
            return;
        }
        while (inbox.TryDequeue(out var message)) Handle(message);
        var now = DateTime.UtcNow;
        // The owner tells each guest who joins which mods its NPCs carry now, so a guest's older or forgotten copy shows;
        // StateDelay after they appear, as a guest's Haven drops NPC messages until their home is ready.
        if (housing.IsSharedVisit && MayShare)
            foreach (var member in engine.HousingRelay.HousingMembers.Values)
            {
                if (member.ContentId == 0 || member.ContentId == Me || statedTo.Contains(member.ContentId)) continue;
                if (!seenAt.TryGetValue(member.ContentId, out var seen)) { seenAt[member.ContentId] = now; continue; }
                if ((now - seen).TotalSeconds < StateDelay) continue;
                statedTo.Add(member.ContentId);
                // Only sets this PC keeps, or forgot on purpose: a PC that merely has none (another of the owner's PCs, a
                // fresh install) said "forgotten" for every NPC, and guests dropped mods they had kept.
                foreach (var npc in housing.Npcs)
                    if (store.Stored(npc.Id) is { } kept) outbox.Enqueue(new GameMessage(Me, member.ContentId, "npc", NpcMods.Finish, npc.Id, NpcMods.State(kept)));
                    else if (store.Forgotten(npc.Id)) outbox.Enqueue(new GameMessage(Me, member.ContentId, "npc", NpcMods.Finish, npc.Id, NpcMods.State(null)));
            }
        // The bringer, for their visitors: the same, later (a guest needs the visitor's definition and welcome first), and
        // to everyone a moment after one is let in.
        if (housing.IsSharedVisit && Visitors != null)
        {
            var visiting = Visitors.Listed.Where(v => v.Bringer == Me && v.Welcomed).Select(v => v.Npc.Id).ToList();
            foreach (var member in engine.HousingRelay.HousingMembers.Values)
            {
                if (member.ContentId == 0 || member.ContentId == Me || visitorStatedTo.Contains(member.ContentId)) continue;
                if (!seenAt.TryGetValue(member.ContentId, out var seen)) { seenAt[member.ContentId] = now; continue; }
                if ((now - seen).TotalSeconds < VisitorStateDelay) continue;
                visitorStatedTo.Add(member.ContentId);
                foreach (var id in visiting) VisitorState(id, member.ContentId);
            }
            foreach (var (id, due) in stateDue.ToList())
                if (now >= due) { stateDue.Remove(id); if (visiting.Contains(id)) VisitorState(id, GameMessage.Everyone); }
        }
        // A share of an NPC that isn't here any more (a visitor sent away or taken back) stops on this PC.
        if (sharing is { } going && Npc(going.Npc) == null) FinishSharing($"{going.Name} isn't here any more.");
        if (incoming is { } coming && Npc(coming.Npc) == null) { if (coming.Accepted == true) StopIncoming(coming, false); else Decline(coming, $"{coming.Name} isn't here any more."); }
        // Once per home, a few seconds in: which conditions that pause sync plugins are on (Lightless sent no new looks
        // from inside a home on 0.57.1).
        if (pausesNoted != housing.ActiveHouse!.Id)
        {
            if (enteredAt == default) enteredAt = now;
            else if ((now - enteredAt).TotalSeconds > 5)
            {
                pausesNoted = housing.ActiveHouse.Id; enteredAt = default;
                log.Information("[NPC-MODS] conditions that pause sync plugins, on now: {On}.", engine.HousingSyncPauses());
            }
        }
        if (outbox.Count > 0 && now >= nextSend) { Send(outbox.Dequeue(), now: true); nextSend = now.AddSeconds(SendGap); }
        if (sharing is { } share) UpdateSharing(share, now);
        if (incoming is { } fetch) UpdateIncoming(fetch, now);
    }

    private void UpdateSharing(Outgoing share, DateTime now)
    {
        if (!share.Read && share.Reapply.TryPeek(out var again) && now >= again)
        {
            share.Reapply.Dequeue();
            var taken = glamour.ApplyToPlayer(share.Design);
            log.Information("[NPC-MODS] {Name}'s look put on you again after the redraw ({Result}).", share.Name, taken ? "taken" : "refused");
        }
        if (share.Waiting)
        {
            if (now >= share.Until || share.Replies.Count >= Others()) FinishSharing();
            return;
        }
        if (share.Read || share.Reapply.Count > 0 || now < share.ReadAt || busy) return;
        // Dressed: is the player drawn in the NPC's look? If Glamourer (an automation) or another plugin kept their own,
        // the sync plugins have nothing new to send, and guests waited a minute for nothing (0.58.5, in game).
        if (share.Dresses && !share.Dressed)
        {
            var off = Undressed(share);
            if (off.Length > 0)
            {
                if (++share.DressTries <= DressRetries)
                {
                    log.Warning("[NPC-MODS] you aren't drawn as {Name} ({Off}); dressing you again.", share.Name, string.Join("; ", off));
                    glamour.ApplyToPlayer(share.Design);
                    Redraw(0);
                    foreach (var after in ReapplyAfter) share.Reapply.Enqueue(now.AddSeconds(after));
                    share.ReadAt = now.AddSeconds(SettleFrom);
                    return;
                }
                log.Warning("[NPC-MODS] you couldn't be dressed as {Name}: {Off}.", share.Name, string.Join("; ", off));
                FinishSharing($"You couldn't be dressed as {share.Name} ({string.Join("; ", off)}). Glamourer put your own look back: switch off its automation for your character (Glamourer, Automation) while you share, then send again.");
                return;
            }
            share.Dressed = true;
            log.Information("[NPC-MODS] you're drawn as {Name}: its race and armour.", share.Name);
            if (share.Set != null && Read(0) is { } worn) Describe(share.Name, worn, (now - share.Started).TotalSeconds);
        }
        if (share.Set is { } set)
        {
            // Sending again: the files are the kept set's; the player only had to be dressed in them.
            share.Read = true;
            Background(() => store.Sent(set), found => { if (sharing == share) SendPrints(share, found); },
                () => { if (sharing == share) FinishSharing("Its kept files couldn't be read."); });
            return;
        }
        share.ReadAt = now.AddSeconds(SettlePoll);
        var waited = (now - share.Started).TotalSeconds;
        if (Read(0) is not { } drawn)
        {
            // Mid-redraw there may be nothing to read yet.
            if (waited >= SettleMost) FinishSharing("Penumbra didn't say what you're drawn with.");
            return;
        }
        var signature = $"{drawn.Meta.Length}|{string.Join("|", drawn.Paths.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + ">" + string.Join(",", x.Value.Order())))}";
        if (signature != share.Drawn) { share.Drawn = signature; share.StableSince = now; }
        if ((now - share.StableSince).TotalSeconds < (share.Dresses ? SettleQuiet : CaptureQuiet) && waited < SettleMost) return;
        share.Read = true;
        Describe(share.Name, drawn, waited);
        Background(() => store.Read(drawn.Paths), found =>
        {
            if (sharing != share) return;
            if (found.Count == 0)
            {
                if (share.Kind == ShareKind.Capture)
                {
                    // A copy of a look without mods has none either: mods kept from an older look would be wrong now.
                    store.Drop(share.Npc, forgotten: true);
                    npcs.Remod(share.Npc);
                    TellState(share.Npc);
                    FinishSharing($"Your look has no mods, so {share.Name} has none either.");
                }
                else FinishSharing($"That collection changes nothing on your character, so {share.Name} has no mods to keep.");
                return;
            }
            share.Files = found.Count;
            // Sending the kept files again reads them from Haven's store, not Penumbra's mods: keep the count the set has.
            share.Mods = ModCount(found) ?? share.Mods;
            share.Version = NpcMods.Version(found.Select(f => f.Hash), drawn.Meta);
            KeepFor(share.Npc, share.Name, found, drawn.Meta, share.Mods, null, HomeOwners.Key(Me), kept =>
            {
                if (kept) TellState(share.Npc);
                else if (sharing == share) FinishSharing($"{share.Name}'s files couldn't all be kept here; see the log.");
            });
            if (!housing.IsSharedVisit || Others() == 0) { FinishSharing(); return; }
            SendPrints(share, found);
        }, () => { if (sharing == share) FinishSharing("Your look's files couldn't be read, or there are more than an NPC can carry; see the log."); });
    }

    // The fingerprints go out (four messages a second); the wait for guests starts once they have.
    private void SendPrints(Outgoing share, IReadOnlyList<NpcModFound> found)
    {
        share.Files = found.Count;
        var prints = found.Select(f => NpcMods.Print(f.Hash)).ToList();
        var chunks = NpcMods.Chunks(share.Share, prints);
        var pathPrints = found.SelectMany(f => f.GamePaths).Select(NpcMods.PathPrint).Distinct().ToList();
        var pathChunks = NpcMods.Chunks(share.Share, pathPrints);
        var syncChunks = NpcMods.Chunks(share.Share, found.Select(f => NpcMods.Print(f.Sha1)).ToList());
        // Value 7: 1 when the player isn't dressed for it (a copy of their own look; 0.58.8 guests fade the owner otherwise); value 8:
        // how many mods the files come from (-1: not known), for guests' NPCs panel.
        outbox.Enqueue(new GameMessage(Me, GameMessage.Everyone, "npc", NpcMods.Begin, share.Npc,
            [share.Share, prints.Count, chunks.Count, (int)Math.Min(int.MaxValue, found.Sum(f => f.Size) >> 20), pathPrints.Count, pathChunks.Count, syncChunks.Count, share.Dresses ? 0 : 1, share.Mods ?? -1, share.Version]));
        foreach (var chunk in chunks) outbox.Enqueue(new GameMessage(Me, GameMessage.Everyone, "npc", NpcMods.Prints, share.Npc, chunk));
        foreach (var chunk in syncChunks) outbox.Enqueue(new GameMessage(Me, GameMessage.Everyone, "npc", NpcMods.SyncPrints, share.Npc, chunk));
        foreach (var chunk in pathChunks) outbox.Enqueue(new GameMessage(Me, GameMessage.Everyone, "npc", NpcMods.PathPrints, share.Npc, chunk));
        share.Waiting = true;
        share.Guests = Others();
        share.Until = DateTime.UtcNow.AddSeconds(OwnerWait + outbox.Count * SendGap);
        log.Information("[NPC-MODS] {Name}: {Count} file(s) at {Paths} game path(s), {Messages} fingerprint message(s) to the gathering.",
            share.Name, prints.Count, pathPrints.Count, chunks.Count + pathChunks.Count + syncChunks.Count);
    }

    // Keeps the files in the store (background, all or nothing) and draws the NPC with them.
    private void KeepFor(Guid npc, string name, List<NpcModFound> found, string meta, int? mods, int? origin, string from, Action<bool> then)
        => Background(() => store.Keep(npc, name, found, meta, mods, origin, from), set =>
        {
            store.Remember(set);
            npcs.Remod(npc);
            log.Information("[NPC-MODS] kept {Name}'s mods: {Count} file(s).", name, set.Files.Length);
            then(true);
        }, () => then(false));

    // Receiving a share (a guest) ---------------------------------------------------

    private void UpdateIncoming(Incoming fetch, DateTime now)
    {
        if (fetch.Keeping) return;
        if (fetch.Ended || now >= fetch.Until)
        {
            if (fetch.Accepted != true) Decline(fetch, $"{fetch.Name}'s share ended before you answered.");
            else if (!busy) Conclude(fetch);
            return;
        }
        if (fetch.Accepted != true) return;   // waiting for the guest's answer
        if (!fetch.Complete || now < fetch.NextPoll || busy || engine.HousingMemberObjectIndex(fetch.Owner) is not { } index) return;
        fetch.NextPoll = now.AddSeconds(PollSeconds);
        if (Read(index) is not { } drawn) return;
        // Only files at the NPC's game paths are read (the owner's other mods could crowd them out of the limits).
        var npcPaths = fetch.Paths > 0 ? fetch.PathPrints.ToHashSet() : null;
        Background(() => store.Read(drawn.Paths, npcPaths), all =>
        {
            if (incoming != fetch) return;
            Match(fetch, all);
            fetch.Meta = drawn.Meta;
            // The owner's bar follows each guest.
            if (fetch.Have != fetch.SentHave && DateTime.UtcNow >= fetch.SentAt)
            {
                fetch.SentHave = fetch.Have; fetch.SentAt = DateTime.UtcNow.AddSeconds(3);
                Send(new GameMessage(Me, fetch.Owner, "npc", NpcMods.Progress, fetch.Npc, [fetch.Share, fetch.Have, fetch.Wanted]), now: true);
            }
            if (fetch.Have >= fetch.Wanted) Conclude(fetch);
        });
    }

    // Which of the files the owner is drawn with here are the NPC's (see Incoming).
    private static void Match(Incoming fetch, List<NpcModFound> all)
    {
        var byPath = fetch.Paths > 0;
        bool Npcs(string game) => fetch.PathPrints.Contains(NpcMods.PathPrint(game));
        if (byPath && fetch.Baseline is { } before && !fetch.Changed)
            fetch.Changed = all.Any(f => f.GamePaths.Any(g => Npcs(g) && (!before.TryGetValue(g, out var was) || !string.Equals(was, f.File, StringComparison.OrdinalIgnoreCase))));
        var differing = 0;
        foreach (var file in all)
        {
            var named = NpcMods.SyncName(file.File) is { } sha1 && fetch.SyncPrints.Contains(NpcMods.Print(sha1));
            var listed = named || fetch.Prints.Contains(NpcMods.Print(file.Hash));
            var games = byPath ? file.GamePaths.Where(Npcs).ToArray() : file.GamePaths;
            if (games.Length == 0) continue;
            if (!listed)
            {
                differing++;
                // Accepted only where the sync plugin put something new: the owner's own file still standing at an
                // NPC path (not yet replaced) isn't the NPC's.
                if (!byPath || !fetch.Changed || fetch.Baseline is not { } baseline) continue;
                games = games.Where(g => !baseline.TryGetValue(g, out var was) || !string.Equals(was, file.File, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (games.Length == 0) continue;
            }
            if (fetch.Found.TryGetValue(file.Hash, out var known)) fetch.Found[file.Hash] = known with { GamePaths = [.. known.GamePaths.Union(games)] };
            else
            {
                fetch.Found[file.Hash] = file with { GamePaths = games };
                if (named && !fetch.Prints.Contains(NpcMods.Print(file.Hash))) fetch.BySyncName++;
                else if (listed) fetch.ByPrint++;
                else fetch.ByChange++;
            }
            if (byPath) fetch.Covered.UnionWith(games.Select(NpcMods.PathPrint));
        }
        fetch.Differing = differing;
    }

    // A guest's share ends: what was found is kept (only all of it) and the owner told.
    private void Conclude(Incoming fetch)
    {
        if (fetch.Keeping) return;
        fetch.Keeping = true;
        var found = fetch.Found.Values.ToList();
        // Only a complete set: part of one can carry meta pointing at models it lacks, which draws a broken NPC.
        var enough = found.Count > 0 && fetch.Have >= fetch.Wanted;
        void Answer(bool took)
        {
            Send(new GameMessage(Me, fetch.Owner, "npc", took ? NpcMods.Took : NpcMods.Missed, fetch.Npc, [fetch.Share, fetch.Have, fetch.Wanted]), now: true);
            StopIncoming(fetch, took);
        }
        log.Information("[NPC-MODS] {Name}: {Have} of {Wanted} {Unit} found in what the owner is drawn with here ({ByPrint} file(s) by fingerprint, {BySyncName} by the sync plugin's name for the owner's file, {ByChange} changed on the way by the sync plugin; {Differing} at its paths didn't match; sync change seen: {Changed}, noted before: {Baseline}){Kept}.",
            fetch.Name, fetch.Have, fetch.Wanted, fetch.Paths > 0 ? "game path(s)" : "file(s)", fetch.ByPrint, fetch.BySyncName, fetch.ByChange, fetch.Differing, fetch.Changed, fetch.Baseline != null,
            enough ? "; kept" : found.Count == 0 ? " (not paired with the owner in a sync plugin?)" : "; not all, so none kept");
        if (!enough) { Answer(false); return; }
        KeepFor(fetch.Npc, fetch.Name, found, fetch.Meta, fetch.Mods, fetch.Origin, HomeOwners.Key(fetch.Owner), Answer);
    }

    private void StopIncoming(bool took) { if (incoming is { } fetch) StopIncoming(fetch, took); }

    /// <summary>A share reaching this guest that waits for their answer: who shares it and for which NPC; else null.</summary>
    public (string Owner, string Npc)? Asking => incoming is { Accepted: null, Ended: false } fetch
        ? (engine.HousingRelay.HousingMembers.Values.FirstOrDefault(m => m.ContentId == fetch.Owner)?.Name is { Length: > 0 } owner ? owner : "The home's owner", fetch.Name)
        : null;

    /// <summary>The guest's answer: keep the NPC's mods (Haven looks for them as the sync plugin brings them in), or not.</summary>
    public void Answer(bool keep)
    {
        if (incoming is not { Accepted: null } fetch) return;
        if (!keep) { Decline(fetch, $"You didn't keep {fetch.Name}'s mods."); return; }
        fetch.Accepted = true;
        // The wait counts from now: the owner keeps the look on until every guest has answered.
        fetch.Until = DateTime.UtcNow.AddSeconds(GuestWait);
        log.Information("[NPC-MODS] you chose to keep {Name}'s mods.", fetch.Name);
    }

    // The guest won't keep this share (they said no, or it ended unanswered): the owner is told, so it stops waiting.
    private void Decline(Incoming fetch, string note)
    {
        fetch.Accepted = false;
        Send(new GameMessage(Me, fetch.Owner, "npc", NpcMods.Missed, fetch.Npc, [fetch.Share, Declined, fetch.Wanted]), now: true);
        if (incoming == fetch) incoming = null;
        lastNote = note;
        log.Information("[NPC-MODS] {Name}'s mods not kept: {Note}", fetch.Name, note);
    }

    // Ends this share (a newer one that replaced it meanwhile carries on).
    private void StopIncoming(Incoming fetch, bool took)
    {
        if (incoming == fetch) incoming = null;
        ended = (took ? $"{fetch.Name}'s mods are here" : $"{fetch.Name}'s mods didn't all arrive",
            took ? "Shown on it now, and on later visits." : fetch.Found.Count == 0 ? "Your sync plugin may not be paired with its owner." : $"{fetch.Have} of {fetch.Wanted} arrived; it keeps the look it had.",
            took, DateTime.UtcNow.AddSeconds(4));
        lastNote = took ? $"{fetch.Name}'s mods are here now." : fetch.Found.Count == 0
            ? $"{fetch.Name}'s mods didn't reach you: your sync plugin may not be paired with its owner."
            : $"Only {fetch.Have} of {fetch.Wanted} of {fetch.Name}'s mod files reached you; it keeps the look it had.";
    }

    private void Handle(GameMessage message)
    {
        if (message.Sender == Me) return;
        switch (message.Kind)
        {
            // A guest: which mods the owner's NPC carries now. A set the owner forgot is dropped here too (the NPC shows
            // without mods); a set older than the owner's is reported, so the owner can send it again.
            case NpcMods.Finish when NpcMods.ReadState(message.Values) is { } state && SharerOf(message.Match, message.Sender) && Npc(message.Match) is { } stated:
                // Mid-share (Copy my look tells everyone as soon as it keeps the files) this copy is about to change.
                if (store.Stored(stated.Id) is not { } mine || incoming?.Npc == stated.Id) break;
                // Someone else's set for an NPC of the same id (a copy of a home keeps its NPCs' ids): not theirs to change.
                if (!VisitingNpcs.MayReplace(mine, message.Sender)) break;
                if (state.Version == 0)
                {
                    store.Drop(stated.Id);
                    npcs.Remod(stated.Id);
                    log.Information("[NPC-MODS] the owner forgot {Name}'s mods; it shows without them here too.", stated.Name);
                }
                else if (!NpcMods.Current(mine, state.Version, state.Layout))
                {
                    Send(new GameMessage(Me, message.Sender, "npc", NpcMods.Progress, stated.Id, [NpcMods.StaleMark]), now: true);
                    log.Information("[NPC-MODS] the owner has changed {Name}'s mods since you kept them; they were told.", stated.Name);
                }
                break;
            // The owner: a guest's copy of an NPC's mods is older than this PC's.
            case NpcMods.Progress when message.Values is [NpcMods.StaleMark, ..] && MayShareFor(message.Match) && Npc(message.Match) is { } outdated:
                (older.TryGetValue(outdated.Id, out var who) ? who : older[outdated.Id] = []).Add(message.Sender);
                var guest = engine.HousingRelay.HousingMembers.Values.FirstOrDefault(m => m.ContentId == message.Sender)?.Name is { Length: > 0 } named ? named : "A guest";
                lastNote = $"{guest} has an older version of {outdated.Name}'s mods. Send its mods to guests to update them.";
                ended = ($"{guest} has older mods for {outdated.Name}", "Send its mods to guests again (NPCs panel)", false, DateTime.UtcNow.AddSeconds(8));
                log.Information("[NPC-MODS] {Guest} has an older version of {Name}'s mods.", guest, outdated.Name);
                break;
            case NpcMods.Took or NpcMods.Missed when (sharing ?? (DateTime.UtcNow < recentUntil ? recent : null)) is { Waiting: true } share
                                                   && message.Match == share.Npc && message.Values is [var id, var copied, ..] && id == share.Share:
                share.Replies[message.Sender] = (message.Kind == NpcMods.Took, copied);
                if (message.Kind == NpcMods.Took && older.TryGetValue(share.Npc, out var waiting)) waiting.Remove(message.Sender);
                if (copied == Declined) log.Information("[NPC-MODS] a guest didn't want {Name}'s mods.", share.Name);
                else log.Information("[NPC-MODS] a guest {Answer} {Name}'s mods ({Copied} found).", message.Kind == NpcMods.Took ? "has" : "couldn't get", share.Name, copied);
                if (share == recent) lastNote = Summary(share);
                break;
            case NpcMods.Progress when sharing is { Waiting: true } going && message.Match == going.Npc && message.Values is [var at, var have, var wanted, ..] && at == going.Share:
                going.Progress[message.Sender] = (have, wanted);
                break;
            // Only the home's owner shares its NPCs' mods. The announcement comes before the owner's look changes.
            // A visitor's set kept here from its bringer is theirs alone: another sender can't replace it (MayReplace).
            case NpcMods.Announce when SharerOf(message.Match, message.Sender) && message.Values is [var announced, ..] && Npc(message.Match) is { } announcedNpc
                                       && (BringerOf(announcedNpc.Id) == null || VisitingNpcs.MayReplace(store.Stored(announcedNpc.Id), message.Sender)):
                if (incoming != null) StopIncoming(false);
                incoming = new Incoming(message.Sender, announcedNpc.Id, announcedNpc.Name, announced) { Baseline = Baseline(message.Sender) };
                log.Information("[NPC-MODS] the owner is about to share {Name}'s mods ({Count} game path(s) noted as they are now).", announcedNpc.Name, incoming.Baseline?.Count ?? 0);
                break;
            case NpcMods.Begin when SharerOf(message.Match, message.Sender) && NpcMods.ReadBegin(message.Values) is { } begin && Npc(message.Match) is { } npc
                                    && (BringerOf(npc.Id) == null || VisitingNpcs.MayReplace(store.Stored(npc.Id), message.Sender)):
                if (incoming is not { } open || open.Owner != message.Sender || open.Npc != npc.Id || open.Share != begin.Share)
                {
                    if (incoming != null) StopIncoming(false);
                    incoming = new Incoming(message.Sender, npc.Id, npc.Name, begin.Share);
                }
                incoming.Files = begin.Files; incoming.Messages = begin.Messages;
                if (NpcMods.ReadPaths(message.Values) is { } paths) { incoming.Paths = paths.Paths; incoming.PathMessages = paths.Messages; }
                incoming.SyncMessages = NpcMods.ReadSyncMessages(message.Values);
                if (message.Values is { Length: >= 9 } counted && counted[8] is >= 0 and <= NpcMods.MaxFiles) incoming.Mods = counted[8];
                if (message.Values is { Length: >= 10 } versioned && versioned[9] > 0) incoming.Origin = versioned[9];
                log.Information("[NPC-MODS] the owner is sharing {Name}'s mods: {Files} file(s) at {Paths} game path(s).", npc.Name, begin.Files, incoming.Paths);
                break;
            case NpcMods.SyncPrints when incoming is { } syncFetch && message.Sender == syncFetch.Owner && message.Match == syncFetch.Npc
                                         && NpcMods.Read(syncFetch.Share, message.Values) is { } syncPart && syncPart.Number < syncFetch.SyncMessages:
                if (syncFetch.SyncNumbers.Add(syncPart.Number)) syncFetch.SyncPrints.UnionWith(syncPart.Prints);
                break;
            case NpcMods.PathPrints when incoming is { } pathFetch && message.Sender == pathFetch.Owner && message.Match == pathFetch.Npc
                                         && NpcMods.Read(pathFetch.Share, message.Values) is { } pathPart && pathPart.Number < pathFetch.PathMessages:
                if (pathFetch.PathNumbers.Add(pathPart.Number)) pathFetch.PathPrints.UnionWith(pathPart.Prints);
                break;
            case NpcMods.Prints when incoming is { } fetch && message.Sender == fetch.Owner && message.Match == fetch.Npc
                                     && NpcMods.Read(fetch.Share, message.Values) is { } part && part.Number < fetch.Messages:
                if (fetch.Numbers.Add(part.Number)) fetch.Prints.UnionWith(part.Prints);
                break;
            case NpcMods.Finish when incoming is { } fetch && message.Sender == fetch.Owner && message.Values is [var id, ..] && id == fetch.Share:
                fetch.Ended = true;
                break;
        }
    }

    // The bar ---------------------------------------------------------------------

    /// <summary>The download bar under the toolbar while a share runs here (as owner or guest), or null.</summary>
    public DownloadBarModel? Bar()
    {
        if (sharing is { } share)
        {
            if (!share.Waiting)
                return share.Kind == ShareKind.Capture
                    ? new($"Copying your mods to {share.Name}", "Reading the files you're drawn with…", "", null, false, 1)
                    : new($"Sharing {share.Name}'s mods", "Dressing you as it for a moment…", "", null, false, 1);
            var took = share.Replies.Values.Count(x => x.Took);
            var guests = Math.Max(1, share.Guests);
            // Answered guests count whole; the others as far as they've said they got.
            var progress = (float)((share.Replies.Count
                + share.Progress.Where(p => !share.Replies.ContainsKey(p.Key)).Sum(p => p.Value.Wanted > 0 ? (double)p.Value.Have / p.Value.Wanted : 0)) / guests);
            var fetching = share.Progress.Where(p => !share.Replies.ContainsKey(p.Key)).Select(p => $"{p.Value.Have} of {p.Value.Wanted}").ToList();
            var detail = fetching.Count > 0 ? $"Fetching {string.Join(", ", fetching)} · keep sharing" : "Keep sharing until guests have it (NPCs panel)";
            return new($"Sharing {share.Name}'s mods", detail, $"{took} of {share.Guests} guest(s)", Math.Clamp(progress, 0, 1), false, 1);
        }
        if (incoming is { Accepted: true } fetch)
            return new($"Getting {fetch.Name}'s mods", "From its owner, through your sync plugin.",
                fetch.Wanted > 0 && fetch.Complete ? $"{fetch.Have} of {fetch.Wanted}" : "", fetch.Wanted > 0 && fetch.Complete ? (float)fetch.Have / fetch.Wanted : null, false, 1);
        if (ended is { } last && DateTime.UtcNow < last.Until) return new(last.Title, last.Detail, "", 1, last.Done, 1);
        return null;
    }

    // Helpers ---------------------------------------------------------------------

    // How many Penumbra mods the files come from: the distinct folders under Penumbra's mod directory. Null when
    // Penumbra doesn't say, or when no file is in its mods (files from a sync plugin's cache or a temporary collection).
    private int? ModCount(IReadOnlyList<NpcModFound> found)
    {
        string root;
        try { root = plugin.GetIpcSubscriber<string>("Penumbra.GetModDirectory").InvokeFunc(); }
        catch (Exception) { return null; }
        if (string.IsNullOrEmpty(root)) return null;
        var mods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in found)
        {
            var relative = Path.GetRelativePath(root, file.File);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) continue;
            var folder = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (folder.Length > 0 && folder != relative) mods.Add(folder);
        }
        return mods.Count == 0 ? null : mods.Count;
    }

    // What a member is drawn with here now: game path -> file (modded ones only), or null when they aren't drawn here.
    private Dictionary<string, string>? Baseline(ulong member)
    {
        if (engine.HousingMemberObjectIndex(member) is not { } index || Read(index) is not { } drawn) return null;
        var before = new Dictionary<string, string>();
        foreach (var (actual, games) in drawn.Paths)
            if (Path.IsPathRooted(actual))
                foreach (var game in games) before[game.Replace('\\', '/').ToLowerInvariant()] = actual;
        return before;
    }

    // What the owner was drawn with when read: the collection Penumbra used, how many files came from mods, and every
    // equipment model with where it came from, so a share that missed gear can be told apart from one that never had it.
    private void Describe(string name, (Dictionary<string, HashSet<string>> Paths, string Meta) drawn, double waited)
    {
        var collection = "?";
        try { collection = plugin.GetIpcSubscriber<int, (bool, bool, (Guid, string))>("Penumbra.GetCollectionForObject.V5").InvokeFunc(0).Item3.Item2; }
        catch (Exception) { }
        var modded = drawn.Paths.Count(x => Path.IsPathRooted(x.Key));
        log.Information("[NPC-MODS] {Name}: read after {Seconds:F1} s in collection \"{Collection}\": {Total} file(s), {Modded} from mods, meta {Meta} characters.",
            name, waited, collection, drawn.Paths.Count, modded, drawn.Meta.Length);
        foreach (var (actual, games) in drawn.Paths)
            foreach (var game in games.Where(g => g.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) && (g.Contains("/equipment/") || g.Contains("/accessory/"))))
                log.Information("[NPC-MODS]   {Game} {From}", game, Path.IsPathRooted(actual) ? "from a mod" : actual == game ? "(the game's)" : $"→ {actual}");
    }

    private void Send(GameMessage message, bool now)
    {
        if (!now) { outbox.Enqueue(message); return; }
        if (housing.IsSharedVisit) _ = engine.HousingRelay.SendMiniGame(message);
    }

    private void Tell(string text) { if (lastNote != text) log.Information("[NPC-MODS] {Text}", text); lastNote = text; }

    // Penumbra: which files a character is drawn with (disk path → game paths), and its meta.
    private (Dictionary<string, HashSet<string>> Paths, string Meta)? Read(int index)
    {
        try
        {
            var paths = plugin.GetIpcSubscriber<ushort[], Dictionary<string, HashSet<string>>?[]>("Penumbra.GetGameObjectResourcePaths.V5").InvokeFunc([(ushort)index])[0];
            var meta = plugin.GetIpcSubscriber<int, string>("Penumbra.GetMetaManipulations.V5").InvokeFunc(index) ?? "";
            return paths == null ? null : (paths, meta);
        }
        catch (Exception ex) { Tell($"Penumbra didn't answer ({ex.Message})."); return null; }
    }

    // Background work: one at a time; its result (or its failure) is handled on the framework thread.
    private void Background<T>(Func<T> work, Action<T> then, Action? failed = null)
    {
        busy = true;
        Task.Run(() =>
        {
            try { var result = work(); finished.Enqueue(() => { busy = false; then(result); }); }
            catch (Exception ex) { finished.Enqueue(() => { busy = false; log.Warning("[NPC-MODS] {Message}", ex.Message); failed?.Invoke(); }); }
        });
    }

    public void Dispose()
    {
        if (sharing != null) FinishSharing();
    }
}
