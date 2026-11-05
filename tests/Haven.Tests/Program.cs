using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Haven.Core;
using Haven.Relay;
using HavenNative.Wire;
using MessagePack;
using System.Numerics;

if (args is ["--live-content", var live]) { await LiveContentCheck(new Uri(live), Sample()); Console.WriteLine("PASS live content locker upload and download"); return; }
// `--quick` (scripts/build.ps1 -Quick) leaves out the Odyssey AI's three strength checks, about half the run's time;
// releases and CI run everything.
var quick = args.Contains("--quick");
if (quick) args = [.. args.Where(a => a != "--quick")];
var passed = 0;
await DoorTests.Run(Check);
await ClockTests.Run(Check);
await SpinTests.Run(Check);
await OscillationTests.Run(Check);
await LightingTests.Run(Check);
await LayoutTests.Run(Check);
await AmbienceTests.Run(Check);
await FurnishListTests.Run(Check);
await ReturnGuardTests.Run(Check);
await EntryGuardTests.Run(Check);
await SwitchTests.Run(Check);
await ContentTests.Run(Check);
await MusicTests.Run(Check);
await FurnitureModTests.Run(Check);
await LoopTests.Run(Check);
await WeatherTests.Run(Check);
await WorldTests.Run(Check, Sample());
await AtmosphereTests.Run(Check, () => Sample());
await AtmosphereTests.District(Check);
await InteriorTests.Run(Check);
await ReviewTests.Run(Check, Sample());
await InviteTests.Run(Check, CheckAsync);
await Check("The sign-in code Haven shows is the one the Worker's page names (accounts.ts signInCode)", () =>
{
    Assert(AccountSignIn.CodeOf(new string('A', 43)) == "TAM-K5D");
    var code = AccountSignIn.CodeOf(AccountSignIn.StateOf(AccountSignIn.NewVerifier()));
    Assert(code.Length == 7 && code[3] == '-' && code.Where(c => c != '-').All(c => "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".Contains(c)));
});
await PoseTests.Run(Check, Sample() with { Furnishings = [new() { ItemId = 6500 }] });
await PoseTests.RunMore(Check);
await PoseTests.RunSkeletons(Check);
await PoseTests.RunAdjust(Check);
await PoseTests.RunWorldChat(Check);
await PoseTests.RunProps(Check);
await PoseTests.RunEffects(Check);
await Haven.Tests.LockerUploadTests.Run(CheckAsync);
await PoseTests.RunStarts(Check);
await ChangelogTests.Run(Check);
await DisplayTests.Run(Check, Sample());
await NpcTests.Run(Check, Sample());
await NpcRosterTests.Run(Check, Sample());
await TriadTests.Run(Check);
if (!quick) await OdysseyAiTests.Run(Check);
else Console.WriteLine("SKIP the Odyssey AI's strength checks (quick build; releases run them)");
await ArcadeTests.Run(Check);
await OdysseyTests.Run(Check);
await Check("House serialization preserves identity, stage, and transform", () =>
{
    var house = Sample() with { Transform = new() { Translation = [10, 2, -3], Scale = 2 } };
    var copy = HouseFormat.Parse(HouseFormat.Serialize(house));
    Assert(copy.Id == house.Id && copy.Transform.Translation[0] == 10 && copy.Transform.Scale == 2);
    Assert(!Encoding.UTF8.GetString(HouseFormat.Serialize(copy)).Contains("orientation", StringComparison.OrdinalIgnoreCase));
});
await Check("Reject unsupported furniture sources, object types, and modpacks", () =>
{
    Reject(() => HouseFormat.Serialize(Sample("C:/Users/private/model.mdl")));
    Reject(() => HouseFormat.Serialize(Sample("bgcommon/hou/../private.mdl")));
    Reject(() => HouseFormat.Serialize(Sample("https://example.com/model.mdl")));
    Reject(() => HouseFormat.Serialize(WithStage("""{"FormatVersion":2,"Objects":{"x":{"Type":"VfxObject"}}}""")));
    Reject(() => HouseFormat.Serialize(WithStage("""{"FormatVersion":2,"Objects":{"x":{"Type":4}}}""")));
    Reject(() => HouseFormat.Serialize(WithStage("""{"FormatVersion":2,"Objects":{},"EmbeddedModpacks":{"x":{}}}""")));
    Reject(() => HouseFormat.Serialize(WithStage("""{"FormatVersion":2,"Objects":{"x":{"Type":"Light","ModpackId":"local"}}}""")));
});
await Check("Reject invalid transforms, duplicate fields, and excessive budgets", () =>
{
    Reject(() => HouseFormat.Serialize(Sample() with { Transform = new() { Rotation = [0, 0, 0, 0] } }));
    Reject(() => HouseFormat.Serialize(Sample() with { Transform = new() { Scale = float.NaN } }));
    Reject(() => HouseFormat.Serialize(Sample() with { Transform = new() { Translation = null! } }));
    Reject(() => HouseFormat.Parse(Encoding.UTF8.GetBytes("""{"name":"a","Name":"b"}""")));
    Reject(() => HouseFormat.Parse(new byte[HouseFormat.MaxBytes + 1]));
    var items = string.Join(',', Enumerable.Range(0, HouseFormat.MaxObjects + 1).Select(i => $"\"{i}\":{{\"Type\":\"Light\"}}"));
    Reject(() => HouseFormat.Serialize(WithStage("{\"FormatVersion\":2,\"Objects\":{" + items + "}}")));
});
await Check("Persistence retains last good save and isolates corrupt files", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "haven-tests-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new HouseStore(directory);
        var house = Sample();
        store.Save(house);
        Reject(() => store.Save(house with { Name = "" }));
        Assert(HouseStore.Read(store.PathFor(house.Id)).Name == house.Name);
        store.Save(house with { Name = "Updated" });
        File.WriteAllText(Path.Combine(directory, "corrupt.haven.json"), "bad json");
        var errors = 0;
        Assert(store.LoadAll((_, _) => errors++).Single().Name == "Updated" && errors == 1);
        Assert(Directory.GetFiles(directory, "*.tmp").Length == 0);
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
});
await Check("Deleting a home archives its exact save, preserves neighbors and supports restoration", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "haven-delete-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new HouseStore(directory); var home = Sample(); var neighbor = Sample() with { Name = "Neighbor" };
        store.Save(home); store.Save(neighbor);
        var bytes = File.ReadAllBytes(store.PathFor(home.Id));
        var source = Path.Combine(directory, "stagehand-source.json"); File.WriteAllText(source, "original source");
        var recovery = store.Delete(home.Id);
        Assert(!File.Exists(store.PathFor(home.Id)) && File.ReadAllBytes(recovery).SequenceEqual(bytes));
        Assert(new HouseStore(directory).LoadAll().Single().Id == neighbor.Id);
        Assert(File.ReadAllText(source) == "original source");
        store.Save(home with { Name = "Another save with the same identity" });
        var second = store.Delete(home.Id);
        Assert(second != recovery && File.ReadAllBytes(recovery).SequenceEqual(bytes));
        var restored = HouseStore.Read(recovery) with { Id = Guid.NewGuid() }; store.Save(restored);
        Assert(store.LoadAll().Count == 2 && store.LoadAll().Any(x => x.Id == restored.Id));
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
});
await Check("Failed home archiving leaves the original save intact", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "haven-delete-failure-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new HouseStore(directory); var home = Sample(); store.Save(home);
        File.WriteAllText(store.DeletedDirectoryPath, "block archive directory");
        var failed = false;
        try { store.Delete(home.Id); } catch (IOException) { failed = true; }
        Assert(failed && HouseStore.Read(store.PathFor(home.Id)).Id == home.Id);
        failed = false;
        try { store.Delete(Guid.NewGuid()); } catch (FileNotFoundException) { failed = true; }
        Assert(failed && store.LoadAll().Single().Id == home.Id);
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
});
await Check("Wire headers preserve timestamps and reject truncated messages", () =>
{
    var upleg = FrameHeader.ParseUpleg(FrameHeader.BuildUpleg(0x60, 123, [1, 2, 3]));
    Assert(upleg.Ok && upleg.Timestamp == 123 && upleg.Payload.SequenceEqual(new byte[] { 1, 2, 3 }));
    var downleg = FrameHeader.ParseDownleg(FrameHeader.BuildDownleg(0x11, "trusted-peer", 456, [4, 5]));
    Assert(downleg.Ok && downleg.SenderId == "trusted-peer" && downleg.Timestamp == 456);
    Assert(!FrameHeader.ParseUpleg([0xA5]).Ok && !FrameHeader.ParseDownleg([0xA5, 1]).Ok);
});

await Check("Furniture suppression keeps shell fixtures, doors, windows and scene assets", () =>
{
    Assert(HousingAssetPolicy.IsPlacedFurniture("bgcommon/hou/indoor/general/0004/asset/fun_b0_m0004.sgb"));
    Assert(HousingAssetPolicy.IsPlacedFurniture("bgcommon/hou/outdoor/general/0042/bgparts/yard.mdl"u8));
    foreach (var path in new[] { "bgcommon/hou/indoor/base/s1i1/door.mdl", "bgcommon/hou/indoor/parts/window.mdl", "bg/ffxiv/sea_s1/ind/s1i1/level/light.sgb", "bgcommon/hou/indoor/light/chandelier.sgb", "", "other.mdl" })
    { Assert(!HousingAssetPolicy.IsPlacedFurniture(path)); Assert(!HousingAssetPolicy.IsPlacedFurniture(Encoding.UTF8.GetBytes(path))); }
});
await Check("Original version 1 homes remain readable", () =>
{
    var id = Guid.NewGuid();
    var old = "{\"formatVersion\":1,\"id\":\"" + id + "\",\"name\":\"Legacy\",\"territoryId\":282,\"stage\":{\"FormatVersion\":2,\"Objects\":{}}}";
    var house = HouseFormat.Parse(Encoding.UTF8.GetBytes(old));
    Assert(house.Id == id && house.Furnishings.Length == 0 && house.Interior.Brightness == null && house.Music.Playlist.Length == 0);
});
await Check("Upgrading a version 1 home preserves an original backup", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "haven-upgrade-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new HouseStore(directory); var original = Sample() with { FormatVersion = 1 };
        store.Save(original);
        var originalBytes = File.ReadAllBytes(store.PathFor(original.Id));
        var upgraded = original with { FormatVersion = 2, Name = "Upgraded", Furnishings = [new() { ItemId = 6500 }] };
        store.Save(upgraded); store.Save(upgraded with { Name = "Edited again" });
        Assert(File.ReadAllBytes(Path.Combine(directory, "backups", $"{original.Id:N}-v1.haven.json")).SequenceEqual(originalBytes));
        Assert(store.LoadAll().Single().Name == "Edited again");
        Assert(HouseStore.Read(store.PathFor(original.Id)).FormatVersion == 2);
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
});
await Check("Item identities, floor fixtures and shared music survive a save round-trip", () =>
{
    var original = Sample() with
    {
        Furnishings = [new() { ItemId = 6500, Position = [1, 2, 3], Rotation = 90, StainId = 5 }],
        Interior = new() { Brightness = 3, MainHallCeilingLight = 65816, Floors = [new() { Wallpaper = 66068, Flooring = 65537, CeilingLight = 65795 }, new(), new()] },
        Music = new() { Playing = true, Volume = 0.4f, StartedAtUnixMs = 1000, GapSeconds = 20, Playlist = [new() { OrchestrionId = 12, Repeats = 2 }] }
    };
    var copy = HouseFormat.Parse(HouseFormat.Serialize(original));
    Assert(copy.Furnishings[0].Id == original.Furnishings[0].Id && copy.Furnishings[0].Rotation == 90 && copy.Furnishings[0].Collision);
    Assert(copy.Interior.Floors[0].Wallpaper == 66068 && copy.Interior.Brightness == 3 && copy.Interior.MainHallCeilingLight == 65816);
    Assert(copy.Music.Playlist[0].Repeats == 2 && copy.Music.StartedAtUnixMs == 1000 && copy.Music.Volume == 0.4f);
});
await Check("Existing homes retain the original main hall fixture and omit an unset override", () =>
{
    var bytes = HouseFormat.Serialize(Sample());
    using var json = JsonDocument.Parse(bytes);
    Assert(!json.RootElement.GetProperty("interior").TryGetProperty("mainHallCeilingLight", out _));
    Assert(HouseFormat.Parse(bytes).Interior.MainHallCeilingLight == 0);
});
await Check("Custom homes round-trip without serializing their protected scaffold and old homes stay presets", () =>
{
    var custom = Sample() with { TerritoryId = CustomCanvas.TerritoryId, CustomLayout = true };
    var bytes = HouseFormat.Serialize(custom); var copy = HouseFormat.Parse(bytes);
    Assert(copy.CustomLayout && copy.TerritoryId == CustomCanvas.TerritoryId && copy.Furnishings.Length == 0);
    Assert(!Encoding.UTF8.GetString(bytes).Contains(CustomCanvas.PlatformId.ToString(), StringComparison.OrdinalIgnoreCase));
    var preset = HouseFormat.Serialize(Sample());
    Assert(!HouseFormat.Parse(preset).CustomLayout && !Encoding.UTF8.GetString(preset).Contains("customLayout"));
});
await Check("Custom scene imports reject unsupported contexts, shell finishes and scaffold identity collisions", () =>
{
    var custom = Sample() with { TerritoryId = CustomCanvas.TerritoryId, CustomLayout = true };
    Reject(() => HouseFormat.Serialize(custom with { TerritoryId = 651 }));
    Reject(() => HouseFormat.Serialize(custom with { Transform = new() { Translation = [1, 0, 0] } }));
    Reject(() => HouseFormat.Serialize(custom with { Transform = new() { Scale = 2 } }));
    Reject(() => HouseFormat.Serialize(custom with { Interior = new() { MainHallCeilingLight = 65816 } }));
    Reject(() => HouseFormat.Serialize(custom with { Interior = new() { Floors = [new() { Wallpaper = 66068 }, new(), new()] } }));
    Reject(() => HouseFormat.Serialize(custom with { Furnishings = [CustomCanvas.Platform] }));
    Assert(HouseFormat.Parse(HouseFormat.Serialize(custom with { Interior = new() { Brightness = 2 } })).Interior.Brightness == 2);
});
await Check("Custom arrival waits for the platform, recovers falls and resets ownership between scenes", () =>
{
    var arrival = new CanvasArrival();
    Assert(!arrival.Update(false, new(0, -50, 0)) && !arrival.Ready);
    Assert(!arrival.Update(true, null) && !arrival.Ready);
    Assert(arrival.Update(true, Vector3.Zero) && arrival.Ready);
    Assert(!arrival.Update(true, CustomCanvas.Spawn));
    Assert(!arrival.Update(true, new(30, 2, 20))); // Walking on a user extension is not tethered to the starter tile.
    foreach (var position in new[] { new Vector3(0, -9, 0), new(257, 0, 0), new(0, 0, -257), new(float.NaN, 0, 0), new(0, float.PositiveInfinity, 0) })
        Assert(arrival.Update(true, position));
    arrival.Clear(); Assert(!arrival.Ready);
    Assert(!arrival.Update(false, Vector3.Zero) && arrival.Update(true, Vector3.Zero));
});
await Check("Custom building identity, independent surface finishes and edits survive persistence", () =>
{
    var path = "bg/ffxiv/sea_s1/ind/common/bgparts/s1i1_s_f1_wl_0000.mdl";
    var wall = new Furnishing { BuildingPart = path, SurfaceFinish = 66068, Position = [4, 0, 8], Rotation = 90, Scale = .5f };
    var neighbor = wall with { Id = Guid.NewGuid(), SurfaceFinish = 0, Position = [12, 0, 8] };
    var arch = new Furnishing { BuildingPart = "bgcommon/hou/indoor/general/0799/asset/fun_b0_m0799.sgb", StainId = 12 };
    var house = Sample() with { TerritoryId = CustomCanvas.TerritoryId, CustomLayout = true, Furnishings = [wall, neighbor, arch] };
    var copy = HouseFormat.Parse(HouseFormat.Serialize(house));
    Assert(copy.Furnishings[0].BuildingPart == path && copy.Furnishings[0].ItemId == 0 && copy.Furnishings[0].SurfaceFinish == 66068);
    Assert(copy.Furnishings[1].SurfaceFinish == 0 && copy.Furnishings[0].Scale == .5f && copy.Furnishings[0].Rotation == 90);
    Assert(copy.Furnishings[2].StainId == 12 && copy.Furnishings[2].BuildingPart == arch.BuildingPart);
    var edit = copy with { Furnishings = [copy.Furnishings[1] with { Position = [14, 2, 6] }] };
    Assert(HouseFormat.Parse(HouseFormat.Serialize(edit)).Furnishings.Single().Id == neighbor.Id);
    Assert(HouseFormat.Parse(HouseFormat.Serialize(house)).Furnishings.Length == 3);
    var old = Encoding.UTF8.GetString(HouseFormat.Serialize(Sample() with { Furnishings = [new() { ItemId = 6500 }] }));
    Assert(!old.Contains("buildingPart") && !old.Contains("surfaceFinish"));
});
await Check("Structural enclosure bounds allow nearer furniture to be selected from inside", () =>
{
    var wall = PlacementMath.RayBox(Vector3.Zero, Vector3.UnitZ, new(-10, -2, -10), new(10, 6, 10), Matrix4x4.Identity, exitWhenInside: true);
    var chair = PlacementMath.RayBox(Vector3.Zero, Vector3.UnitZ, new(-1), Vector3.One, Matrix4x4.CreateTranslation(0, 0, 5));
    Assert(wall == 10 && chair == 4 && chair < wall);
    Assert(PlacementMath.RayBox(new(0, 0, -20), Vector3.UnitZ, new(-10), new(10), Matrix4x4.Identity, true) == 10);
    var transform = Matrix4x4.CreateScale(2) * Matrix4x4.CreateRotationY(MathF.PI / 2) * Matrix4x4.CreateTranslation(20, 0, 10);
    Assert(Math.Abs(PlacementMath.RayBox(new(20, 0, 10), Vector3.UnitX, new(-10), new(10), transform, true)!.Value - 20) < .001f);
});
await Check("Pieces are picked by the triangles under the pointer, not their bounding boxes", () =>
{
    // A unit quad facing +Z at the origin, as two triangles.
    Vector3[] quad = [new(-1, -1, 0), new(1, -1, 0), new(1, 1, 0), new(-1, 1, 0)];
    int[] triangles = [0, 1, 2, 0, 2, 3];
    Assert(PlacementMath.RayTriangles(new(0, 0, -5), Vector3.UnitZ, quad, triangles, Matrix4x4.Identity) is float front && Math.Abs(front - 5) < .001f);
    Assert(PlacementMath.RayTriangles(new(0, 0, 5), -Vector3.UnitZ, quad, triangles, Matrix4x4.Identity) is float back && Math.Abs(back - 5) < .001f);
    Assert(PlacementMath.RayTriangles(new(1.5f, 0, -5), Vector3.UnitZ, quad, triangles, Matrix4x4.Identity) == null);
    Assert(PlacementMath.RayTriangles(new(0, 0, 5), Vector3.UnitZ, quad, triangles, Matrix4x4.Identity) == null);
    // Scaled, turned and moved: the distance stays in world units.
    var placed = Matrix4x4.CreateScale(3) * Matrix4x4.CreateRotationY(MathF.PI / 2) * Matrix4x4.CreateTranslation(10, 0, 0);
    Assert(PlacementMath.RayTriangles(new(0, 2, 0), Vector3.UnitX, quad, triangles, placed) is float far && Math.Abs(far - 10) < .001f);
    Assert(PlacementMath.RayTriangles(new(0, 4, 0), Vector3.UnitX, quad, triangles, placed) == null);
    // A ray through an empty part of a big box misses the box's model, so the small piece behind it is picked.
    Assert(PlacementMath.RayBox(new(0, 0, -5), Vector3.UnitZ, new(-5, -5, -1), new(5, 5, 1), Matrix4x4.Identity) is not null);
    Assert(PlacementMath.RayTriangles(new(0, 0, -5), Vector3.UnitZ, quad, triangles, Matrix4x4.CreateTranslation(3, 0, 0)) == null);
    Assert(PlacementMath.RayTriangles(new(0, 0, -5), Vector3.UnitZ, [], [0, 1, 2], Matrix4x4.Identity) == null);
});
await Check("Remodelling hides and restores shell pieces, copies an upright piece exactly where it stood, refuses a tilted one, and brings a fallen player back", () =>
{
    var interior = new InteriorOptions();
    interior = ShellRemodel.Hide(interior, 4195306, false);
    interior = ShellRemodel.Hide(interior, 4195302, true);
    interior = ShellRemodel.Hide(interior, 4195306, true);           // the same piece again: its choice changes
    Assert(interior.HiddenPieces is { Length: 2 } && interior.HiddenPieces.All(x => x.KeepCollision) && ShellRemodel.IsHidden(interior, 4195302));
    Assert(ShellRemodel.Restore(interior, [4195302]).HiddenPieces is [{ Instance: 4195306 }] && ShellRemodel.Restore(interior, null).HiddenPieces == null);
    // Travels in the home, omitted when none, refused on a custom layout or when doubled.
    var home = Sample() with { Interior = interior };
    Assert(HouseFormat.Parse(HouseFormat.Serialize(home)).Interior.HiddenPieces is { Length: 2 });
    Assert(!System.Text.Encoding.UTF8.GetString(HouseFormat.Serialize(Sample())).Contains("hiddenPieces", StringComparison.OrdinalIgnoreCase));
    Reject(() => HouseFormat.Serialize(home with { TerritoryId = CustomCanvas.TerritoryId, CustomLayout = true }));
    Reject(() => HouseFormat.Serialize(home with { Interior = interior with { HiddenPieces = [new() { Instance = 7 }, new() { Instance = 7 }] } }));
    Reject(() => HouseFormat.Serialize(home with { Interior = interior with { HiddenPieces = [new() { Instance = 0 }] } }));
    // A pillar turned a quarter: the copy's foot (the middle of its bounds' bottom) lands where the instance puts it.
    var min = new Vector3(-.5f, 0, -3); var max = new Vector3(.5f, 3, 3);
    var placed = LayoutRotation.Transform(new(6, 0, 6), new(0, MathF.PI / 2, 0), Vector3.One);
    var copy = ShellRemodel.Copy(placed, min, max)!.Value;
    Assert(Vector3.Distance(copy.Position, new Vector3(6, 0, 6)) < 1e-4f && Math.Abs(copy.Yaw - 90) < .01f && copy.Scale == 1);
    var item = new Furnishing { Position = [copy.Position.X, copy.Position.Y, copy.Position.Z], Rotation = copy.Yaw, Scale = copy.Scale };
    var corner = Vector3.Transform(new Vector3(.5f, 3, 3) - new Vector3(0, 0, 0), Matrix4x4.CreateTranslation(-new Vector3(0, min.Y, 0)) * PlacementMath.ItemMatrix(item));
    Assert(Vector3.Distance(corner, Vector3.Transform(new Vector3(.5f, 3, 3), placed)) < 1e-4f);
    // Windows authored as (pi, 0, -pi) are upright too; a real tilt is refused.
    Assert(ShellRemodel.Copy(LayoutRotation.Transform(new(4, 1, 6), new(MathF.PI, 0, -MathF.PI), Vector3.One), min, max) is { } window && Math.Abs(Math.Abs(window.Yaw) - 180) < .01f);
    Assert(ShellRemodel.Copy(LayoutRotation.Transform(Vector3.Zero, new(.3f, 0, 0), Vector3.One), min, max) == null);
    Assert(ShellRemodel.Copy(LayoutRotation.Transform(Vector3.Zero, Vector3.Zero, new(1, 2, 1)), min, max) == null);
    // A moved inn piece is a building piece of an inn's own files (housing protocol 37): a city's models and groups, the
    // game's common items, The Unending Journey's book. Other game files stay refused.
    foreach (var inn in new[] { "bg/ffxiv/sea_s1/twn/s1ti/bgparts/s1ti_ia_chr2.mdl", "bg/ex1/01_roc_r2/twn/common/bgparts/r2t0_x1_bed1.mdl",
                 "bg/ex4/03_kld_k5/shared/for_bg/sgbg_k5ti_a_lmp01.sgb", "bgcommon/world/itm/012/bgparts/w_itm_012a.mdl",
                 "bgcommon/world/evt/shared/for_pln/sgpl_a_evt_openbook01.sgb" })
    {
        Assert(BuildingParts.IsHousingAsset(inn));
        HouseFormat.Validate(Sample() with { Furnishings = [new Furnishing { BuildingPart = inn, Position = [1, 0, 1] }] });
    }
    foreach (var other in new[] { "bg/ffxiv/sea_s1/twn/s1ti/level/bg.lgb", "chara/monster/m0001/obj/body/b0001/model/m0001b0001.mdl",
                 "bg/ffxiv/sea_s1/twn/s1ti/bgparts/../../x.mdl", "bgcommon/world/itm/12/bgparts/a.mdl.tex", "bg/ffxiv/sea_s1/twn/s1ti/bgparts/A.mdl" })
        Assert(!BuildingParts.IsHousingAsset(other));
    Assert(HousingWire.ProtocolRevision >= 37);
    // Falling: well below the lowest floor, or far out.
    Assert(!ShellRemodel.NeedsRecovery(new(0, -6, 0), -7) && ShellRemodel.NeedsRecovery(new(0, -16, 0), -7) && ShellRemodel.NeedsRecovery(new(300, 0, 0), -7));
    // A moved piece names its stand-in (since housing protocol 35): a changed collision choice keeps it, putting the piece
    // back names it for removal, an empty id is refused.
    var stand = Guid.NewGuid();
    var moved = ShellRemodel.Hide(ShellRemodel.Hide(interior, 4195309, false, stand), 4195309, true);
    Assert(moved.HiddenPieces!.Single(x => x.Instance == 4195309).MovedTo == stand);
    Assert(ShellRemodel.MovedCopies(moved, [4195309]).SetEquals([stand]) && ShellRemodel.MovedCopies(moved, [4195302]).Count == 0 && ShellRemodel.MovedCopies(moved, null).Count == 1);
    Assert(HouseFormat.Parse(HouseFormat.Serialize(home with { Interior = moved })).Interior.HiddenPieces!.Single(x => x.Instance == 4195309).MovedTo == stand);
    Reject(() => HouseFormat.Serialize(home with { Interior = interior with { HiddenPieces = [new() { Instance = 7, MovedTo = Guid.Empty }] } }));
    // The shells' own Exit markers, not the doors to additional chambers.
    Assert(ShellRemodel.ExitMarkers.Contains(2002738) && ShellRemodel.ExitMarkers.Contains(2007444) && !ShellRemodel.ExitMarkers.Contains(2011571));
});
await Check("Structural imports reject external paths and ambiguous identities; building pieces belong in any home since housing protocol 34", () =>
{
    var house = Sample() with { TerritoryId = CustomCanvas.TerritoryId, CustomLayout = true };
    var piece = new Furnishing { BuildingPart = "bgcommon/hou/dyna/lmp/lp/0030/asset/lmp_s0_m0030.sgb" };
    HouseFormat.Serialize(house with { Furnishings = [piece] });
    foreach (var path in new[] { "", "C:/private/model.mdl", "https://example.com/x.sgb", "bg/ffxiv/sea_s1/ind/common/bgparts/../x.mdl",
        "bg/ffxiv/sea_s1/ind/common/bgparts/x.mdl\0evil", "bg/ffxiv/sea_s1/ind/common/bgparts/x.mdl\n", "bg/ffxiv/sea_s1/ind/common/bgparts/x.pcb", "chara/human/x.mdl" })
        Reject(() => HouseFormat.Serialize(house with { Furnishings = [piece with { BuildingPart = path }] }));
    Reject(() => HouseFormat.Serialize(house with { Furnishings = [piece with { ItemId = 6500 }] }));
    Reject(() => HouseFormat.Serialize(house with { Furnishings = [piece with { StainId = 65536 }] }));
    Reject(() => HouseFormat.Serialize(house with { Furnishings = [piece with { SurfaceFinish = uint.MaxValue }] }));
    // Remodelling moves and replaces a preset shell's pieces with building pieces.
    HouseFormat.Serialize(Sample() with { Furnishings = [piece] });
    // A catalogue partition takes a wallpaper too; the catalogue checks that the piece has the slot.
    Assert(HouseFormat.Parse(HouseFormat.Serialize(Sample() with { Furnishings = [new() { ItemId = 6500, SurfaceFinish = 66068 }] })).Furnishings[0].SurfaceFinish == 66068);
});
await Check("Each painting draws with its own texture, not the one numbered like its row", () =>
{
    // Castrum Marinum (row 50) is texture 51; Jijiroon's Trading Post (row 84) was slotted in at 32.
    Assert(PaintingTextures.Texture(1) == 1 && PaintingTextures.Texture(50) == 51 && PaintingTextures.Texture(84) == 32 && PaintingTextures.Texture(100) == 1005);
    Assert(PaintingTextures.Texture(0) == null && PaintingTextures.Texture(320) == null && PaintingTextures.Texture(9999) == null);
    Assert(PaintingTextures.Material(51) == "bgcommon/hou/indoor/pic/ta/0051/material/pic_ta_20051a.mtrl");
});
await Check("A frame's painting is saved with the home, only on catalogue furnishings", () =>
{
    var frame = new Furnishing { ItemId = 6500 };
    Assert(!Encoding.UTF8.GetString(HouseFormat.Serialize(Sample() with { Furnishings = [frame] })).Contains("picture"));
    var copy = HouseFormat.Parse(HouseFormat.Serialize(Sample() with { Furnishings = [frame with { Picture = 42 }] }));
    Assert(copy.Furnishings[0].Picture == 42);
    Reject(() => HouseFormat.Serialize(Sample() with { Furnishings = [frame with { Picture = uint.MaxValue }] }));
    var custom = Sample() with { TerritoryId = CustomCanvas.TerritoryId, CustomLayout = true };
    Reject(() => HouseFormat.Serialize(custom with { Furnishings = [new() { BuildingPart = "bgcommon/hou/dyna/lmp/lp/0030/asset/lmp_s0_m0030.sgb", Picture = 42 }] }));
});
await Check("Furnishing sound volume is optional, bounded and only on catalogue furnishings", () =>
{
    var item = new Furnishing { ItemId = 6500 };
    Assert(!Encoding.UTF8.GetString(HouseFormat.Serialize(Sample() with { Furnishings = [item] })).Contains("soundVolume"));
    var copy = HouseFormat.Parse(HouseFormat.Serialize(Sample() with { Furnishings = [item with { SoundVolume = .25f }] }));
    Assert(copy.Furnishings[0].SoundVolume == .25f);
    HouseFormat.Serialize(Sample() with { Furnishings = [item with { SoundVolume = 0 }] });
    foreach (var bad in new[] { -0.01f, 1.01f, float.NaN, float.PositiveInfinity })
        Reject(() => HouseFormat.Serialize(Sample() with { Furnishings = [item with { SoundVolume = bad }] }));
    var custom = Sample() with { TerritoryId = CustomCanvas.TerritoryId, CustomLayout = true };
    Reject(() => HouseFormat.Serialize(custom with { Furnishings = [new() { BuildingPart = "bgcommon/hou/dyna/lmp/lp/0030/asset/lmp_s0_m0030.sgb", SoundVolume = .5f }] }));
});
await Check("Invalid furnishing metadata, interior data and music controls are refused", () =>
{
    var item = new Furnishing { ItemId = 6500 };
    Reject(() => HouseFormat.Serialize(Sample() with { Furnishings = [item, item] }));
    Reject(() => HouseFormat.Serialize(Sample() with { Furnishings = [item with { Position = [float.NaN, 0, 0] }] }));
    Reject(() => HouseFormat.Serialize(Sample() with { Interior = new() { Brightness = 6 } }));
    Reject(() => HouseFormat.Serialize(Sample() with { Interior = new() { Floors = [] } }));
    Reject(() => HouseFormat.Serialize(Sample() with { Music = new() { Volume = float.NaN } }));
    Reject(() => HouseFormat.Serialize(Sample() with { Music = new() { StartedAtUnixMs = long.MaxValue } }));
    Reject(() => HouseFormat.Serialize(Sample() with { Music = new() { Playlist = [new() { OrchestrionId = 1, Repeats = 0 }] } }));
    Reject(() => HouseFormat.Serialize(Sample() with { Music = new() { Playlist = Enumerable.Repeat(new MusicTrack { OrchestrionId = 1 }, 65).ToArray() } }));
});
await Check("Playlist repetition, silent gaps and loop boundaries agree across clients", () =>
{
    var music = new MusicOptions { Playing = true, StartedAtUnixMs = 1000, GapSeconds = 5,
        Playlist = [new() { OrchestrionId = 1, Repeats = 2 }, new() { OrchestrionId = 2 }] };
    long[] durations = [10000, 20000];
    Assert(MusicSchedule.Locate(music, 1000, durations) is { PlaylistIndex: 0, Repetition: 0, Silent: false });
    Assert(MusicSchedule.Locate(music, 11000, durations) is { Silent: true });
    Assert(MusicSchedule.Locate(music, 16000, durations) is { PlaylistIndex: 0, Repetition: 1, Silent: false });
    Assert(MusicSchedule.Locate(music, 31000, durations) is { PlaylistIndex: 1, Silent: false });
    Assert(MusicSchedule.Locate(music, 56000, durations) is { Cycle: 1, PlaylistIndex: 0 });
    Assert(MusicSchedule.Locate(music with { Loop = false }, 56000, durations) == null);
    Assert(MusicSchedule.Locate(music with { Playing = false }, 1000, durations) == null);
});
await Check("Shuffled schedules are deterministic permutations", () =>
{
    var music = new MusicOptions { Playing = true, Shuffle = true, ShuffleSeed = 42,
        Playlist = Enumerable.Range(1, 8).Select(i => new MusicTrack { OrchestrionId = (uint)i }).ToArray() };
    var durations = Enumerable.Repeat(10000L, 8).ToArray();
    var order = Enumerable.Range(0, 8).Select(i => MusicSchedule.Locate(music, i * 10000, durations)!.Value.PlaylistIndex).ToArray();
    Assert(order.Distinct().Count() == 8);
    Assert(order.SequenceEqual(Enumerable.Range(0, 8).Select(i => MusicSchedule.Locate(music, i * 10000, durations)!.Value.PlaylistIndex)));
    Assert(!order.SequenceEqual(Enumerable.Range(0, 8).Select(i => MusicSchedule.Locate(music with { ShuffleSeed = 99 }, i * 10000, durations)!.Value.PlaylistIndex)));
});
await Check("Legacy manual song lengths are ignored and removed on save", () =>
{
    var original = Sample() with { Music = new() { Playing = true, Playlist = [new() { OrchestrionId = 1 }] } };
    var json = System.Text.Json.Nodes.JsonNode.Parse(HouseFormat.Serialize(original))!;
    json["music"]!["playlist"]![0]!["playSeconds"] = 10;
    var home = HouseFormat.Parse(Encoding.UTF8.GetBytes(json.ToJsonString()));
    Assert(!Encoding.UTF8.GetString(HouseFormat.Serialize(home)).Contains("playSeconds"));
    Assert(MusicSchedule.Locate(home.Music, 10000, [240001L]) is { Silent: false, RemainingSeconds: > 230 });
    Reject(() => MusicSchedule.Locate(home.Music, 1, []));
    Reject(() => MusicSchedule.Locate(home.Music, 1, [0L]));
    Assert(MusicSchedule.Locate(home.Music with { Loop = false }, 240000, [240001L]) != null);
    Assert(MusicSchedule.Locate(home.Music with { Loop = false }, 240001, [240001L]) == null);
});
await Check("Late entry, metadata delay and re-entry play complete songs", () =>
{
    var queue = new MusicOptions { Playing = true, StartedAtUnixMs = 1000, Playlist = [new() { OrchestrionId = 1 }, new() { OrchestrionId = 2 }] };
    long[] durations = [10001, 20000];
    var clock = new MusicPlaybackClock();
    Assert(clock.Locate(queue, 7000, durations) is { RemainingSeconds: < 5 }); // status must not start the clock
    Assert(clock.Locate(queue, 8000, durations, true) is { PlaylistIndex: 0, RemainingSeconds: 10.001 });
    Assert(clock.Locate(queue with { Volume = .1f }, 18000, durations, true) is { PlaylistIndex: 0, RemainingSeconds: .001 });
    Assert(clock.Locate(queue, 18001, durations, true) is { PlaylistIndex: 1, RemainingSeconds: 20 });
    Assert(clock.Locate(queue with { Playing = false }, 18002, durations, true) == null);
    Assert(clock.Locate(queue, 18003, durations, true) is { PlaylistIndex: 1, RemainingSeconds: 20 });
    clock.Reset();
    Assert(clock.Locate(queue with { StartedAtUnixMs = 20000 }, 20000, durations, true) is { PlaylistIndex: 0, RemainingSeconds: 10.001 });
});
await Check("Orchestrion loading time never consumes a song and recovery restarts its full duration", () =>
{
    var queue = new MusicOptions { Playing = true, StartedAtUnixMs = 1000, Playlist = [new() { OrchestrionId = 1 }, new() { OrchestrionId = 2 }] };
    long[] durations = [10000, 20000];
    var clock = new MusicPlaybackClock();
    Assert(clock.Locate(queue, 8000, durations, true) is { PlaylistIndex: 0, RemainingSeconds: 10 });
    clock.AwaitPlayback(8000);
    clock.ObservePlayback(10000, false);
    Assert(clock.Locate(queue, 10000, durations, true) is { RemainingSeconds: 10 });
    clock.ObservePlayback(14000, true);
    Assert(clock.Locate(queue, 14000, durations, true) is { RemainingSeconds: 10 });
    var interrupted = clock.Locate(queue, 19000, durations, true)!.Value;
    Assert(interrupted.RemainingSeconds == 5);
    clock.RestartTrack(interrupted, durations); clock.AwaitPlayback(19000);
    clock.ObservePlayback(21000, true);
    Assert(clock.Locate(queue, 30999, durations, true) is { PlaylistIndex: 0, RemainingSeconds: .001 });
    Assert(clock.Locate(queue, 31000, durations, true) is { PlaylistIndex: 1, RemainingSeconds: 20 });
    clock.AwaitPlayback(31000); clock.ObservePlayback(34000, false); clock.ObservePlayback(35000, true);
    Assert(clock.Locate(queue, 35000, durations, true) is { PlaylistIndex: 1, RemainingSeconds: 20 });
});
await Check("One-song native loops stay uninterrupted while finite songs, gaps and explicit restarts still transition", () =>
{
    var queue = new MusicOptions { Playing = true, StartedAtUnixMs = 1000, Playlist = [new() { OrchestrionId = 1, Repeats = 3 }] };
    var first = MusicSchedule.Locate(queue, 1000, [10000L]);
    var later = MusicSchedule.Locate(queue, 96000, [10000L]);
    Assert(first != later);
    Assert(MusicPlaybackPolicy.Key(queue, first, true) == MusicPlaybackPolicy.Key(queue, later, true));
    Assert(MusicPlaybackPolicy.Key(queue, first, false) != MusicPlaybackPolicy.Key(queue, later, false));
    var gap = queue with { GapSeconds = 5 };
    Assert(!MusicPlaybackPolicy.Continuous(gap, true));
    Assert(!MusicPlaybackPolicy.Continuous(queue with { Loop = false }, true));
    Assert(!MusicPlaybackPolicy.Continuous(queue with { Playlist = [new() { OrchestrionId = 1 }, new() { OrchestrionId = 2 }] }, true));
    Assert(MusicPlaybackPolicy.Key(queue with { StartedAtUnixMs = 96000 }, first, true) != MusicPlaybackPolicy.Key(queue, first, true));
    Assert(MusicPlaybackPolicy.Key(queue with { Volume = .1f }, first, true) == MusicPlaybackPolicy.Key(queue, first, true));
});
await Check("Music scene restoration is idempotent and respects external changes", () =>
{
    var lease = new BgmSceneLease();
    Assert(lease.Release(42) == null);
    lease.Engage(42, 1); lease.Engage(1, 1); lease.Engage(1, 77);
    Assert(lease.Release(77) == 42 && lease.Release(77) == null);
    lease.Engage(0, 1); Assert(lease.Release(1) == 0);
    lease.Engage(42, 1); Assert(lease.Release(99) == null);
    lease.Engage(42, 1); lease.Engage(99, 1); Assert(lease.Release(1) == 99);
    lease.Engage(42, 1); Assert(lease.Release(null) == null && lease.Release(1) == null);
});
await Check("Ogg durations count final samples, chained streams and partial milliseconds", () =>
{
    byte[] Page(ulong granule, uint serial = 0)
    {
        var page = new byte[29]; "OggS"u8.CopyTo(page); page[26] = 1; page[27] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(6), granule);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14), serial);
        return page;
    }
    Assert(OggDuration.Milliseconds([..Page(0), ..Page(441001), ..Page(ulong.MaxValue)], 44100) == 10001);
    Assert(OggDuration.Milliseconds([..Page(44100), ..Page(88200, 2)], 44100) == 3000);
    Reject(() => OggDuration.Milliseconds(Page(44100)[..28], 44100));
    Reject(() => OggDuration.Milliseconds(Page(0), 44100));
    Reject(() => OggDuration.Milliseconds(Page(44100), 0));
    Reject(() => OggDuration.Milliseconds([1, 2], 44100));
});
await Check("HCA durations subtract encoder delay and padding without decoding music", () =>
{
    var header = new byte[24]; "HCA\0"u8.CopyTo(header); "fmt\0"u8.CopyTo(header.AsSpan(8));
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), 24);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), 0x0100AC44);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), 100);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(20), 128);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(22), 847);
    Assert(HcaDuration.Milliseconds(header, 44100) == 2300);
    Reject(() => HcaDuration.Milliseconds(header[..23], 44100));
    Reject(() => HcaDuration.Milliseconds(header, 48000));
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), 0);
    Reject(() => HcaDuration.Milliseconds(header, 44100));
});
await Check("Placement snapping preserves elevation and picking respects transforms", () =>
{
    var snapped = PlacementMath.Snap(new Vector3(0.24f, 2.5f, 0.36f), 0.1f);
    Assert(Vector3.Distance(snapped, new Vector3(0.2f, 2.5f, 0.4f)) < 0.0001f);
    var distance = PlacementMath.RayBox(new(0, 0, -10), Vector3.UnitZ, new(-1), new(1), Matrix4x4.CreateTranslation(0, 0, 5));
    Assert(distance is float d && Math.Abs(d - 14) < 0.001f);
    Assert(PlacementMath.RayBox(new(5, 0, -10), Vector3.UnitZ, new(-1), new(1), Matrix4x4.Identity) == null);
    Assert(PlacementMath.RayBox(Vector3.Zero, Vector3.UnitZ, new(-1), new(1), Matrix4x4.CreateScale(0)) == null);
    // A new piece faces the camera a quarter turn at a time: looking along +Z it turns round (180), looking along -X
    // it faces +X (90), a slanted view snaps to the nearest quarter, and a home turned a quarter turn counts.
    Assert(PlacementMath.FacingCamera(Vector3.Normalize(new(0, -.5f, 1)), Matrix4x4.Identity) == 180);
    Assert(PlacementMath.FacingCamera(-Vector3.UnitX, Matrix4x4.Identity) == 90);
    Assert(PlacementMath.FacingCamera(Vector3.Normalize(new(.3f, -.2f, -1)), Matrix4x4.Identity) == 0);
    Assert(PlacementMath.FacingCamera(Vector3.Normalize(new(1, 0, .4f)), Matrix4x4.Identity) == 270);
    Assert(PlacementMath.FacingCamera(-Vector3.UnitY, Matrix4x4.Identity) == 0);
    Assert(PlacementMath.FacingCamera(-Vector3.UnitX, Matrix4x4.CreateRotationY(MathF.PI / 2)) == 0);
});

await Check("Tilted furnishings: tilt before the turn, saved only when set, validated, and the game's angle order inverted", () =>
{
    static bool Near(Matrix4x4 a, Matrix4x4 b) { for (var r = 0; r < 4; r++) for (var c = 0; c < 4; c++) if (MathF.Abs(a[r, c] - b[r, c]) > 1e-4f) return false; return true; }
    var plain = new Furnishing { ItemId = 1, Position = [1, 2, 3], Rotation = 90, Scale = 2 };
    Assert(!plain.Tilted && Near(PlacementMath.ItemMatrix(plain), Matrix4x4.CreateScale(2) * Matrix4x4.CreateRotationY(MathF.PI / 2) * Matrix4x4.CreateTranslation(1, 2, 3)));
    // Tilted forward a quarter turn, the front (+Z) points down; then the turn carries the rest.
    var forward = plain with { Rotation = 0, Scale = 1, Pitch = 90 };
    Assert(forward.Tilted && Vector3.Distance(Vector3.TransformNormal(Vector3.UnitZ, PlacementMath.ItemMatrix(forward)), -Vector3.UnitY) < 1e-4f);
    var leaning = plain with { Rotation = 90, Scale = 1, Roll = 90 };
    // Leaning a quarter: up (+Y) goes to -X in the piece's frame, which the quarter turn carries to +Z.
    Assert(Vector3.Distance(Vector3.TransformNormal(Vector3.UnitY, PlacementMath.ItemMatrix(leaning)), Vector3.UnitZ) < 1e-4f);
    // Saved only when set, and round-tripped.
    var json = System.Text.Encoding.UTF8.GetString(HouseFormat.Serialize(Sample() with { Furnishings = [plain] }));
    Assert(!json.Contains("pitch", StringComparison.OrdinalIgnoreCase) && !json.Contains("roll", StringComparison.OrdinalIgnoreCase));
    var tilted = Sample() with { Furnishings = [plain with { Pitch = 30, Roll = -12.5f }] };
    var back = HouseFormat.Parse(HouseFormat.Serialize(tilted)).Furnishings[0];
    Assert(back.Pitch == 30 && back.Roll == -12.5f);
    foreach (var bad in new[] { float.NaN, float.PositiveInfinity, 1e9f })
    {
        Reject(() => HouseFormat.Validate(Sample() with { Furnishings = [plain with { Pitch = bad }] }));
        Reject(() => HouseFormat.Validate(Sample() with { Furnishings = [plain with { Roll = bad }] }));
    }
    // LayoutRotation.Angles gives back angles that rebuild the same rotation in the game's order (colliders).
    var random = new Random(7);
    for (var i = 0; i < 200; i++)
    {
        var x = (float)(random.NextDouble() * 6 - 3); var y = (float)(random.NextDouble() * 3 - 1.5); var z = (float)(random.NextDouble() * 6 - 3);
        var q = Quaternion.CreateFromRotationMatrix(LayoutRotation.Matrix(x, y, z));
        var a = LayoutRotation.Angles(q);
        Assert(Near(LayoutRotation.Matrix(a.X, a.Y, a.Z), LayoutRotation.Matrix(x, y, z)));
    }
    var locked = LayoutRotation.Angles(Quaternion.CreateFromRotationMatrix(LayoutRotation.Matrix(.4f, MathF.PI / 2, 0)));
    Assert(Near(LayoutRotation.Matrix(locked.X, locked.Y, locked.Z), LayoutRotation.Matrix(.4f, MathF.PI / 2, 0)));
    // A plain turn reads the same in both orders.
    var yaw = LayoutRotation.Angles(Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.1f));
    Assert(MathF.Abs(yaw.X) < 1e-5f && MathF.Abs(yaw.Y - 1.1f) < 1e-5f && MathF.Abs(yaw.Z) < 1e-5f);
});
await Check("World placement cancels on Escape, keeps on a right-click and rotates without a ray or surface hit", () =>
{
    var input = new WorldEditorInput();
    EditorCommands Frame(EditorButtons b) => input.Update(b, true, false, false, true, true, false);
    Frame(default);
    Assert(Frame(new(false, false, false, true, false)).Cancel);
    Frame(default);
    var right = Frame(new(false, true, false, false, false));
    Assert(right.Keep && !right.Cancel && !right.Place);
    Frame(default);
    Assert(Frame(new(false, false, true, false, false)).Rotation == 15);
    Frame(default);
    Assert(Frame(new(false, false, true, false, true)).Rotation == 1);
    Frame(default);
    Assert(!Frame(new(true, false, false, false, false)).Place);
});
await Check("World input respects UI, focus and held buttons", () =>
{
    var input = new WorldEditorInput();
    input.Update(new(true, false, false, false, false), true, true, false, false, true, true);
    Assert(!input.Update(new(true, false, false, false, false), true, false, false, true, true, true).Place);
    input.Update(default, true, false, false, true, true, true);
    Assert(input.Update(new(true, false, false, false, false), true, false, false, true, true, true).Place);
    input.Update(default, true, false, false, true, true, true);
    Assert(!input.Update(new(true, false, true, false, false), true, true, true, true, true, true).Place);
    input.Update(default, false, false, false, true, true, true);
    Assert(!input.Update(new(true, false, false, false, false), true, false, false, true, true, true).Place);
    Assert(input.Update(new(false, false, true, false, false), true, false, true, true, true, true).Rotation == 0);
});
await Check("With the free camera, a right-drag while placing turns the camera and only a right-click sets the piece down", () =>
{
    var input = new WorldEditorInput();
    EditorCommands Frame(bool right, bool free = true) => input.Update(new(false, right, false, false, false), true, false, false, true, true, true, rightTurnsCamera: free);
    Frame(false);
    Assert(!Frame(true).Keep && !Frame(true).Keep);
    var click = Frame(false);
    Assert(click.Keep && !click.Cancel);
    Frame(false);
    Assert(!Frame(true).Keep);
    input.RightDragged();
    Assert(!Frame(true).Keep && !Frame(false).Keep && !Frame(false).Keep);
    // A drag reported before any press doesn't excuse the next click.
    input.RightDragged();
    Assert(!Frame(true).Keep && Frame(false).Keep);
    // Without the free camera, the press keeps it at once; a right-click never cancels.
    Frame(false, false);
    var press = Frame(true, false);
    Assert(press.Keep && !press.Cancel);
    // Escape always cancels.
    Assert(input.Update(new(false, false, false, true, false), true, false, false, true, true, true, rightTurnsCamera: true).Cancel);
});
await Check("Physical click edges select and drive gizmos without ImGui button events", () =>
{
    var input = new WorldEditorInput();
    EditorCommands Frame(EditorButtons buttons, bool overUi = false, bool focused = true) => input.Update(buttons, focused, overUi, false, false, true, true);
    Frame(default);
    Assert(Frame(new(true, false, false, false, false)).Select && input.LeftPressed);
    Assert(!Frame(new(true, false, false, false, false)).Select && !input.LeftPressed);
    Frame(default);
    Assert(!Frame(new(true, false, false, false, false), overUi: true).Select);
    Assert(!Frame(new(true, false, false, false, false)).Select);
    Frame(default);
    Assert(Frame(new(false, true, false, false, false)).Context && input.RightPressed);
    Frame(default, focused: false); Assert(!input.RightPressed && !input.LeftPressed);
    Assert(!Frame(new(true, false, false, false, false)).Select);
    Frame(default); Assert(Frame(new(true, false, false, false, false)).Select);
});
await Check("Placement plane fallback rejects skyward and parallel rays", () =>
{
    Assert(PlacementMath.RayPlane(new(0, 5, 0), -Vector3.UnitY, 0) == Vector3.Zero);
    Assert(PlacementMath.RayPlane(new(0, 5, 0), Vector3.UnitY, 0) == null);
    Assert(PlacementMath.RayPlane(new(0, 5, 0), Vector3.UnitX, 0) == null);
    Assert(PlacementMath.RayPlane(new(0, 500, 0), -Vector3.UnitY, 0) == null);
});
await Check("Axis drags preserve the other coordinates and follow a rotated scaled house", () =>
{
    var start = new Vector3(1.25f, 2.3f, -4.5f);
    var house = Matrix4x4.CreateScale(new Vector3(2, 3, .5f)) * Matrix4x4.CreateRotationY(.7f) * Matrix4x4.CreateTranslation(10, 1, -5);
    var worldStart = Vector3.Transform(start, house);
    var eye = worldStart + new Vector3(8, 7, -9);
    for (var axis = 0; axis < 3; axis++)
    {
        var drag = AxisDrag.Begin(axis, start, house, eye, Vector3.Normalize(worldStart - eye));
        Assert(drag != null);
        var expected = start; expected[axis] += .65f;
        var direction = Vector3.Normalize(Vector3.Transform(expected, house) - eye);
        Assert(Vector3.Distance(drag!.Update(eye, direction)!.Value, expected) < .0001f);
        Assert(Vector3.Distance(drag.Update(eye, Vector3.Normalize(worldStart - eye))!.Value, start) < .0001f);
    }
});
await Check("Axis drags snap only the active coordinate and apply fine movement without jumps", () =>
{
    var start = new Vector3(.24f, 2.3f, -.46f); var eye = new Vector3(4, 6, -9);
    Vector3 Ray(float x) => Vector3.Normalize(new Vector3(x, start.Y, start.Z) - eye);
    var drag = AxisDrag.Begin(0, start, Matrix4x4.Identity, eye, Ray(start.X))!;
    var moved = drag.Update(eye, Ray(.71f), .1f)!.Value;
    Assert(Math.Abs(moved.X - .7f) < .0001f && moved.Y == start.Y && moved.Z == start.Z);
    moved = drag.Update(eye, Ray(1.71f), 0, true)!.Value;
    Assert(Math.Abs(moved.X - .81f) < .0001f);
    Assert(Vector3.Distance(drag.Update(eye, Ray(1.71f), 0, false)!.Value, moved) < .0001f);
});
await Check("Axis dragging rejects degenerate rays and screen handles use bounded segment hits", () =>
{
    Assert(AxisDrag.Begin(0, Vector3.Zero, Matrix4x4.Identity, new(-10, 0, 0), Vector3.UnitX) == null);
    Assert(AxisDrag.Begin(1, Vector3.Zero, Matrix4x4.CreateScale(0), new(5, 5, -5), Vector3.UnitZ) == null);
    Assert(AxisDrag.Begin(3, Vector3.Zero, Matrix4x4.Identity, new(5, 5, -5), Vector3.UnitZ) == null);
    var drag = AxisDrag.Begin(0, Vector3.Zero, Matrix4x4.Identity, new(0, 0, -10), Vector3.UnitZ)!;
    Assert(drag.Update(new(0, 0, -10), -Vector3.UnitZ) == null);
    Assert(drag.Update(new(float.NaN), Vector3.UnitZ) == null);
    Assert(drag.Update(new(0, 0, -10), Vector3.UnitX) == null);
    Assert(AxisDrag.ScreenDistance(new(50, 4), new(10, 0), new(90, 0)) == 4);
    Assert(AxisDrag.ScreenDistance(new(100, 0), new(10, 0), new(90, 0)) == 10);
    Assert(float.IsPositiveInfinity(AxisDrag.ScreenDistance(Vector2.Zero, Vector2.One, Vector2.One)));
});
await Check("Housing dye RGB preserves channel order and full strength for dark colors", () =>
{
    Assert(FurnitureDyes.Rgba(0x781A1A, true) == 0xFF1A1A78);
    Assert(FurnitureDyes.Rgba(0x000000, true) == 0xFF000000);
    Assert(FurnitureDyes.Rgba(0xABCDEF, false) == 0);
    // An undyed Steel Locker's material default, linear (0.715, 0.637, 0.438), is the cream a dye of (216, 204, 169) gives.
    Assert(FurnitureDyes.FromLinear(.715f, .637f, .438f) == 0xD8CCA9 && FurnitureDyes.FromLinear(1, 1, 1) == 0xFFFFFF && FurnitureDyes.FromLinear(-1, float.NaN, 2) == 0x0000FF);
    var linear = FurnitureDyes.Linear(0x804020);
    Assert(Math.Abs(linear.X - MathF.Pow(128 / 255f, 2)) < 0.00001f && linear.W == 1);
});
await Check("Furniture scaling persists, defaults to one for legacy saves and rejects invalid sizes", () =>
{
    var item = new Furnishing { ItemId = 6513, Position = [1, 2, 3], Rotation = 90, Scale = 2 };
    var house = Sample() with { Furnishings = [item] };
    Assert(HouseFormat.Parse(HouseFormat.Serialize(house)).Furnishings[0].Scale == 2);
    var node = System.Text.Json.Nodes.JsonNode.Parse(HouseFormat.Serialize(house))!;
    node["furnishings"]![0]!.AsObject().Remove("scale");
    Assert(HouseFormat.Parse(Encoding.UTF8.GetBytes(node.ToJsonString())).Furnishings[0].Scale == 1);
    foreach (var size in new[] { 0f, -1f, 11f, float.NaN, float.PositiveInfinity }) Reject(() => HouseFormat.Serialize(house with { Furnishings = [item with { Scale = size }] }));
    var point = Vector3.Transform(Vector3.UnitX, PlacementMath.ItemMatrix(item));
    Assert(Vector3.Distance(point, new Vector3(1, 2, 1)) < 0.0001f);
});
await Check("Exploration mode never produces world selection or object menus", () =>
{
    var editor = new WorldEditorInput(); editor.Update(default, true, false, false, false, true, true, false);
    var command = editor.Update(new(true, true, false, false, false), true, false, false, false, true, true, false);
    Assert(!command.Select && !command.Context && !command.Place);
});
await Check("Double-click movement requires the same object, location and short interval", () =>
{
    var clicks = new ObjectClickInput();
    Assert(!clicks.Click("chair", 0, new(5, 5)));
    Assert(clicks.Click("chair", 0.2, new(7, 6)));
    Assert(!clicks.Click("chair", 0.25, new(7, 6)));
    Assert(!clicks.Click("bed", 0.3, new(7, 6)));
    Assert(!clicks.Click("bed", 0.4, new(50, 50)));
    Assert(!clicks.Click("bed", 1, new(50, 50)));
    clicks.Reset(); Assert(!clicks.Click("bed", 1.1, new(50, 50)));
});
await Check("Home ownership is required even after a visitor becomes relay host", () =>
{
    Assert(EditorAccess.CanEditHome(true, true, true));
    Assert(!EditorAccess.CanEditHome(true, false, true));
    Assert(!EditorAccess.CanEditHome(true, true, false));
    Assert(!EditorAccess.CanEditHome(false, true, true));
    Assert(!EditorAccess.CanEditHome(true, true, true, publishedVisit: true));
});
await Check("Furniture emotes select the nearest matching anchor within reach", () =>
{
    var chair = new FurnitureAnchor(Guid.NewGuid(), 0, new(1, 0, 0), false);
    var bed = new FurnitureAnchor(Guid.NewGuid(), 1, new(0, .7f, 1), true);
    var upperFloor = chair with { Position = new(0, 2, 0) };
    var far = chair with { Position = new(10, 0, 0) };
    Assert(FurnitureInteractions.Nearest([bed, upperFloor, far, chair], Vector3.Zero, false) == chair);
    Assert(FurnitureInteractions.Nearest([chair, bed], Vector3.Zero, true) == bed);
    Assert(FurnitureInteractions.Nearest([upperFloor, far], Vector3.Zero, false) == null);
    var transformed = bed with { Position = Vector3.Transform(bed.Position, Matrix4x4.CreateScale(2) * Matrix4x4.CreateTranslation(10, 0, 5)) };
    Assert(FurnitureInteractions.Nearest([transformed], new(10, 0, 5), true) == transformed);
});
await Check("Native sit and doze route to markers while groundsit and cpose remain native", () =>
{
    Assert(FurnitureInteractions.WantsBed(50) == false && FurnitureInteractions.WantsBed(13) == true && FurnitureInteractions.WantsBed(88) == true);
    Assert(FurnitureInteractions.WantsBed(52) == null && FurnitureInteractions.WantsBed(90) == null && FurnitureInteractions.WantsBed(21) == null);
});
await Check("Precision keys repeat predictably and respect modifiers and text ownership", () =>
{
    var input = new PlacementKeyInput();
    Assert(input.Update(new(X: 1), 0, false).Delta.X == 0.1f);
    Assert(input.Update(new(X: 1), 0.1, false).Delta == Vector3.Zero);
    Assert(input.Update(new(X: 1, Fine: true), 0.4, false).Delta.X < 0.011f);
    Assert(input.Update(new(Y: 1, Coarse: true), 0.5, false).Delta.Y == 1);
    Assert(input.Update(new(Turn: 1, Fine: true), 0.6, false).Turn == 1);
    Assert(input.Update(new(Follow: true, Confirm: true), 0.7, true).Confirm == false);
    Assert(input.Update(new(Follow: true, Confirm: true), 0.8, false).Confirm == false);
    input.Update(default, 0.9, false);
    Assert(input.Update(new(Follow: true, Confirm: true), 1, false) is { ToggleFollow: true, Confirm: true });
});
await Check("Proportional imported scaling preserves shape and safe bounds", () =>
{
    var scaled = PlacementTools.ScaleProportionally(new(1, 2, 3), 2);
    Assert(scaled == new Vector3(2, 4, 6));
    Assert(PlacementTools.ScaleProportionally(new(1, 2, 3), 100).Z == 10);
    Assert(PlacementTools.ScaleProportionally(Vector3.One, 0.0001f).X == 0.05f);
    Assert(PlacementTools.ScaleProportionally(Vector3.Zero, 2) == Vector3.Zero);
});
await Check("Placement grid follows the saved house transform", () =>
{
    var matrix = Matrix4x4.CreateScale(2) * Matrix4x4.CreateRotationY(0.7f) * Matrix4x4.CreateTranslation(10, 1, -4);
    var local = new Vector3(0.24f, 2.5f, 0.36f);
    var point = PlacementTools.SnapInHouse(Vector3.Transform(local, matrix), matrix, 0.1f);
    Assert(Vector3.Distance(point, Vector3.Transform(new(0.2f, 2.5f, 0.4f), matrix)) < 0.0001f);
});
await Check("New pieces rest on floors and tables but keep their anchor on walls", () =>
{
    // A wall-mounted curtain hangs 2.8 m below its anchor: on a floor it is raised to stand on it.
    Assert(PlacementTools.Rest(-2.78f, PlacementTools.FacesUp(Vector3.UnitY)) == 2.78f);
    Assert(PlacementTools.Rest(-2.78f, PlacementTools.FacesUp(new Vector3(1, 0, 0))) == 0);
    Assert(PlacementTools.Rest(-2.78f, PlacementTools.FacesUp(-Vector3.UnitY)) == 0);
    Assert(PlacementTools.FacesUp(null) && PlacementTools.FacesUp(new Vector3(0.3f, 1, 0)) && !PlacementTools.FacesUp(new Vector3(1, 0.5f, 0)));
    // A piece already standing on its origin, or one with no bounds, is never lowered.
    Assert(PlacementTools.Rest(0.2f, true) == 0 && PlacementTools.Rest(float.NaN, true) == 0);
});
await Check("Linked groups move, turn, scale, align and space out as one, and unlink cleanly", () =>
{
    var group = Guid.NewGuid();
    Furnishing Piece(float x, float z, Guid? g = null, float rotation = 0) => new() { ItemId = 1, Position = [x, 0, z], Rotation = rotation, Group = g };
    var a = Piece(0, 0, group); var b = Piece(1, 0, group); var c = Piece(5, 5);
    var house = Sample() with { Furnishings = [a, b, c] };
    Assert(FurnishingGroups.Members(house, a.Id).Length == 2 && FurnishingGroups.Members(house, c.Id).Single() == c.Id);
    // The leader moves 2 m along Z, turns 90 degrees and doubles: the follower keeps its place relative to it.
    var moved = FurnishingGroups.Follow(b, a, a with { Position = [0, 0, 2], Rotation = 90, Scale = 2 });
    Assert(Vector3.Distance(moved.Translation, new Vector3(0, 0, 0)) < 1e-4f && moved.Rotation == 90 && moved.Scale == 2);
    // The turn matches how pieces are drawn: the follower's offset turns with the leader's own matrix.
    var expected = Vector3.Transform(new Vector3(1, 0, 0), Matrix4x4.CreateRotationY(MathF.PI / 2)) * 2 + new Vector3(0, 0, 2);
    Assert(Vector3.Distance(moved.Translation, expected) < 1e-4f);
    var turned = FurnishingGroups.Turn(house, [a.Id, b.Id], 180);
    Assert(Vector3.Distance(turned.Furnishings[0].Translation, new Vector3(1, 0, 0)) < 1e-4f && turned.Furnishings[0].Rotation == 180);
    var linked = FurnishingGroups.Link(house, [a.Id, c.Id]);
    Assert(linked.Furnishings[0].Group == linked.Furnishings[2].Group && linked.Furnishings[0].Group != group);
    var unlinked = FurnishingGroups.Unlink(house, [a.Id]);
    Assert(unlinked.Furnishings.All(f => f.Group == null)); // b alone is no longer a group
    PlacementBounds? Box(Furnishing f) => new(f.Translation - new Vector3(.5f, 0, .5f), f.Translation + new Vector3(.5f, 1, .5f));
    var aligned = FurnishingGroups.Align(house, [a.Id, b.Id, c.Id], AlignEdge.XMax, Box);
    Assert(aligned.Furnishings.All(f => Math.Abs(f.Translation.X - 5) < 1e-4f) && aligned.Furnishings[2].Translation.Z == 5);
    var three = Sample() with { Furnishings = [Piece(0, 0), Piece(1, 0), Piece(10, 0)] };
    var spread = FurnishingGroups.Distribute(three, [.. three.Furnishings.Select(f => f.Id)], 0, Box);
    Assert(spread.Furnishings.Select(f => f.Translation.X).SequenceEqual([0f, 5f, 10f]));
    HouseFormat.Validate(linked);
    var json = System.Text.Encoding.UTF8.GetString(HouseFormat.Serialize(linked));
    Assert(json.Contains("\"group\"") && HouseFormat.Parse(HouseFormat.Serialize(linked)).Furnishings[0].Group == linked.Furnishings[0].Group);
    Assert(!System.Text.Encoding.UTF8.GetString(HouseFormat.Serialize(Sample())).Contains("\"group\""));
});
await Check("Magnets line up a neighbour's edges and bottoms, not only its faces", () =>
{
    var target = new PlacementBounds(Vector3.Zero, Vector3.One);
    // Beside it on X, 0.05 m in front and 0.04 m higher: flush on X, front edges and bottoms lined up.
    var snap = PlacementTools.Snap(new(new(1.08f, .04f, .05f), new(2.08f, 1.04f, 1.05f)), [target]);
    Assert(Math.Abs(snap.Offset.X + .08f) < 1e-4f && Math.Abs(snap.Offset.Z + .05f) < 1e-4f && Math.Abs(snap.Offset.Y + .04f) < 1e-4f);
    Assert(snap.Targets.SequenceEqual([0]));
    Assert(PlacementTools.Snap(new(new(5, 0, 0), new(6, 1, 1)), [target]).Targets.Length == 0);
});
await Check("Edge magnets align nearby faces and top surfaces without distant attraction", () =>
{
    var moving = new PlacementBounds(new(1.1f, 0, 0), new(2.1f, 1, 1));
    var target = new PlacementBounds(Vector3.Zero, Vector3.One);
    Assert(Math.Abs(PlacementTools.Magnet(moving, [target]).X + 0.1f) < 0.0001f);
    Assert(PlacementTools.Magnet(new(new(20, 0, 0), new(21, 1, 1)), [target]) == Vector3.Zero);
    Assert(Math.Abs(PlacementTools.Magnet(new(new(0, 1.1f, 0), new(1, 2.1f, 1)), [target]).Y + 0.1f) < 0.0001f);
    var rotated = PlacementBounds.Transform(Vector3.Zero, new(2, 1, 1), Matrix4x4.CreateRotationY(MathF.PI / 2));
    Assert(Vector3.Distance(rotated.Minimum, new(0, 0, -2)) < 0.0001f && Vector3.Distance(rotated.Maximum, new(1, 1, 0)) < 0.0001f);
});
await Check("GPose entry and exit reapply the original room light even without a saved brightness override", () =>
{
    var light = new InteriorLighting(); light.Bind(.8f, false);
    Assert(light.Pending && light.Level == .8f);
    light.Applied(); Assert(!light.Observe(null, false) && !light.Pending);
    Assert(light.Observe(null, true) && light.Pending && light.Level == .8f);
    light.Applied(); Assert(!light.Observe(null, true) && !light.Pending);
    Assert(light.Observe(null, false) && light.Pending && light.Level == .8f);
    light.Applied(); Assert(!light.Pending);
});
await Check("Private brightness changes during GPose and resetting defaults preserve the shell baseline", () =>
{
    var light = new InteriorLighting(); light.Bind(.8f, false); light.Observe(2, false); light.Applied();
    Assert(light.Level == .4f);
    Assert(light.Observe(2, true) && light.Pending && light.Level == .4f);
    light.Applied(); light.Observe(4, true); Assert(light.Pending && light.Level == .8f);
    light.Applied(); light.Observe(0, true); Assert(light.Level == 0 && light.OriginalLevel == .8f);
    light.Applied(); light.Observe(null, true); Assert(light.Pending && light.Level == .8f);
    // Rebinding collision or transform state uses the original, not the active override.
    light.Observe(1, true); var original = light.OriginalLevel; light.Clear(); light.Bind(original, true);
    light.Observe(null, true); Assert(light.Level == .8f);
});
await Check("Lighting retries pending applications and clears scene ownership on leave", () =>
{
    var light = new InteriorLighting();
    Assert(!light.Observe(0, true) && !light.Pending);
    light.Bind(1, true); light.Observe(3, true);
    Assert(light.Pending && light.Level == .6f);
    light.Observe(3, true); Assert(light.Pending); // Native submission has not succeeded yet.
    light.Applied(); light.Invalidate(); Assert(light.Pending);
    light.Clear(); light.Invalidate();
    Assert(!light.Observe(0, false) && !light.Pending && light.Level == 1);
    light.Bind(.5f, false); light.Observe(null, false); Assert(light.Pending && light.Level == .5f);
    foreach (var invalid in new[] { float.NaN, float.PositiveInfinity, -1, 2 })
    { light.Bind(invalid, false); light.Observe(null, false); Assert(light.Level == 1); }
});
await Check("Cold interior plans submit full fixture IDs and only changed slots", () =>
{
    var options = new InteriorOptions { Floors = [new() { Wallpaper = 66076, Flooring = 65562, CeilingLight = 65807 }, new(), new()] };
    var cold = InteriorPlan.Changes(null, options).ToArray();
    Assert(cold.Length == 3 && cold[0] == new FixtureChange(0, 0, 66076) && cold[1] == new FixtureChange(0, 3, 65562) && cold[2] == new FixtureChange(0, 4, 65807));
    Assert(!InteriorPlan.Changes(options, options).Any());
    var changed = options with { Floors = [options.Floors[0] with { Wallpaper = 66077 }, new(), new()] };
    Assert(InteriorPlan.Changes(options, changed).Single() == new FixtureChange(0, 0, 66077));
    Assert(!InteriorPlan.NeedsShellReset(options, changed));
});
await Check("Restoring default fixtures requires a clean shell and lighting uses native scale", () =>
{
    var previous = new InteriorOptions { Floors = [new() { Wallpaper = 66076 }, new(), new()] };
    Assert(InteriorPlan.NeedsShellReset(previous, new()));
    Assert(!InteriorPlan.NeedsShellReset(null, new()));
    Assert(InteriorPlan.LightLevel(0) == 0 && InteriorPlan.LightLevel(5) == 1 && InteriorPlan.LightLevel(2.5f) == 0.5f);
});
await Check("Main hall chandelier changes independently and restoring it reloads only the original shell", () =>
{
    var previous = new InteriorOptions { Brightness = 2, MainHallCeilingLight = 65816,
        Floors = [new() { CeilingLight = 65808 }, new() { CeilingLight = 65828 }, new() { CeilingLight = 65799 }] };
    var next = previous with { MainHallCeilingLight = 65825 };
    Assert(InteriorPlan.Changes(previous, next).Single() == new FixtureChange(3, 4, 65825));
    Assert(!InteriorPlan.NeedsShellReset(previous, next));
    Assert(InteriorPlan.NeedsShellReset(next, next with { MainHallCeilingLight = 0 }));
    Assert(!InteriorPlan.NeedsShellReset(next with { MainHallCeilingLight = 0 }, next));
    Assert(!InteriorPlan.Changes(next, next).Any());
});
await Check("Wallpaper, flooring and chandeliers settle on all three floors without another selection", () =>
{
    for (uint floorIndex = 0; floorIndex < 3; floorIndex++)
    foreach (var part in new[] { 0u, 3u, 4u })
    {
        var preview = new InteriorPreview();
        var floor = part switch { 0 => new InteriorFloor { Wallpaper = 66068 }, 3 => new InteriorFloor { Flooring = 65577 }, _ => new InteriorFloor { CeilingLight = 65807 } };
        InteriorFloor[] floors = [new(), new(), new()]; floors[floorIndex] = floor;
        var options = new InteriorOptions { Floors = floors };
        var requested = new FixtureChange(floorIndex, part, part == 0 ? floor.Wallpaper : part == 3 ? floor.Flooring : floor.CeilingLight);
        FixtureChange? staged = null, visible = null; var writes = 0; var lights = 0;
        void Set(FixtureChange change) { if (staged == change) visible = change; staged = change; writes++; }
        Assert(preview.Update(options, false, true, Set, () => lights++));
        Assert(preview.Pending && visible == null && writes == 1);
        Assert(!preview.Update(options, false, false, Set, () => lights++));
        Assert(writes == 1 && preview.Pending);
        Assert(preview.Update(options, false, true, Set, () => lights++));
        Assert(visible == requested && writes == 2 && lights == 2 && !preview.Pending);
        Assert(!preview.Update(options, false, true, Set, () => lights++));
        Assert(writes == 2 && lights == 2);
    }
});
await Check("Distinct finishes on every floor use isolated native commands and retain global lighting", () =>
{
    var preview = new InteriorPreview();
    var options = new InteriorOptions { Brightness = 2, MainHallCeilingLight = 65816, Floors = Enumerable.Range(0, 3)
        .Select(f => new InteriorFloor { Wallpaper = (uint)(66068 + f), Flooring = (uint)(65577 + f), CeilingLight = (uint)(65828 + f) }).ToArray() };
    Dictionary<(uint Floor, uint Part), FixtureChange> staged = [], visible = [];
    var total = 0; var lights = 0;
    for (var frame = 0; frame < 30; frame++)
    {
        var writes = 0;
        void Set(FixtureChange change)
        {
            writes++; total++;
            var key = (change.Floor, change.Part);
            if (staged.TryGetValue(key, out var previous) && previous == change) visible[key] = change;
            staged[key] = change;
        }
        preview.Update(options, false, true, Set, () => lights++);
        Assert(writes <= 1); // The native interface gets one slot per consumed frame.
        if (!preview.Pending) break;
        var before = total;
        preview.Update(options, false, false, Set, () => lights++);
        Assert(before == total); // Loading does not restart another fixture.
    }
    Assert(!preview.Pending && visible.Count == 10 && total == 20 && lights == 20);
    foreach (var expected in InteriorPlan.Changes(null, options)) Assert(visible[(expected.Floor, expected.Part)] == expected);
    Assert(preview.Requested!.Brightness == 2);
});
await Check("Rapid fixture changes coalesce and unrelated slots retain their follow-up", () =>
{
    var preview = new InteriorPreview(); List<FixtureChange> writes = [];
    var first = new InteriorOptions { Floors = [new() { Wallpaper = 66068 }, new(), new()] };
    preview.Update(first, false, true, writes.Add, () => { });
    var second = first with { Floors = [first.Floors[0] with { Wallpaper = 66077, Flooring = 65577 }, new(), new()] };
    preview.Update(second, false, false, writes.Add, () => { });
    Assert(writes.Count == 1); // The in-flight request is allowed to finish consuming.
    preview.Update(second, false, true, writes.Add, () => { });
    Assert(writes.Count == 2 && writes[1].FixtureId == 66077);
    var third = second with { Floors = [second.Floors[0] with { CeilingLight = 65807 }, new(), new()] };
    for (var frame = 0; frame < 8; frame++) preview.Update(third, false, true, writes.Add, () => { });
    Assert(writes.Skip(1).All(x => x.FixtureId != 66068) && writes.Count(x => x.Part == 0) == 3);
    Assert(writes.Count(x => x.Part == 3) == 2 && writes.Count(x => x.Part == 4) == 2 && !preview.Pending);
});
await Check("Fixture failures retry their phase and scene cleanup drops deferred work", () =>
{
    var preview = new InteriorPreview(); var options = new InteriorOptions { Floors = [new() { Wallpaper = 66068, Flooring = 65577 }, new(), new()] };
    List<FixtureChange> writes = [];
    try { preview.Update(options, false, true, writes.Add, () => throw new InvalidOperationException("Temporary native failure")); throw new Exception("Expected submission failure."); }
    catch (InvalidOperationException) { }
    Assert(preview.Pending);
    writes.Clear(); preview.Update(options, false, true, writes.Add, () => { });
    Assert(writes.Count == 1 && preview.Pending);
    try { preview.Update(options, false, true, writes.Add, () => throw new InvalidOperationException("Temporary replay failure")); throw new Exception("Expected replay failure."); }
    catch (InvalidOperationException) { }
    Assert(preview.Pending);
    writes.Clear(); preview.Update(options, false, true, writes.Add, () => { });
    Assert(writes.Count == 1 && writes[0].Part == 0 && preview.Pending); // Flooring is still queued.
    preview.Clear(); Assert(!preview.Pending && preview.Requested == null);
    writes.Clear(); var lights = 0;
    // A fresh scene's light waits for the layout's own pass (InteriorTests), then goes in.
    preview.Update(new(), true, false, writes.Add, () => lights++);
    Assert(writes.Count == 0 && lights == 0 && !preview.Pending);
    preview.Update(new(), true, true, writes.Add, () => lights++);
    Assert(writes.Count == 0 && lights == 1 && !preview.Pending);
});

var importedFixture = WithStage("""
{"FormatVersion":2,"Info":{"Name":"Imported room"},"Objects":{
  "room/~one":{"Type":"Group","DisplayName":"Seating","Position":{"X":10,"Y":0,"Z":5},"RotationPitchYawRollDegrees":{"X":0,"Y":90,"Z":0},"Scale":{"X":2,"Y":2,"Z":2},"Objects":{
    "chair/~a":{"Type":"BgObject","DisplayName":"Imported chair","ModelGamePath":"bgcommon/hou/indoor/general/0004/bgparts/fun_b0_m0004.mdl","Position":{"X":1,"Y":0,"Z":0},"RotationPitchYawRollDegrees":{"X":10,"Y":15,"Z":20},"Scale":{"X":1,"Y":2,"Z":3},"Opacity":0.8,"DyeColor":{"X":1,"Y":0.2,"Z":0.3,"W":1}},
    "lamp":{"Type":"Light","DisplayName":"Lamp"}}},
  "other":{"Type":"BgObject","ModelGamePath":"bgcommon/hou/indoor/general/0004/bgparts/fun_b0_m0004.mdl"}}
}
""");
await Check("Imported scene traversal preserves escaped identity and parent transforms", () =>
{
    var objects = StageObjects.Read(importedFixture.Stage);
    Assert(objects.Length == 4 && objects[1].Key == "/room~1~0one/chair~1~0a");
    Assert(objects[1].Path.SequenceEqual(new[] { "room/~one", "chair/~a" }));
    var point = Vector3.Transform(Vector3.Zero, objects[1].Transform);
    Assert(Vector3.Distance(point, new(10, 0, 3)) < 0.001f);
    var world = objects[1].ParentTransform * Matrix4x4.CreateTranslation(4, 1, 2);
    Matrix4x4.Invert(world, out var inverse);
    var local = Vector3.Transform(new Vector3(20, 2, 8), inverse);
    Assert(Vector3.Distance(Vector3.Transform(local, world), new(20, 2, 8)) < 0.001f);
});
await Check("Deleting imported children or groups preserves neighbors and original snapshots", () =>
{
    var childRemoved = StageObjects.Remove(importedFixture.Stage, ["room/~one", "chair/~a"]);
    Assert(StageObjects.Read(childRemoved).Length == 3 && StageObjects.Read(childRemoved).Any(x => x.Name == "Lamp"));
    var groupRemoved = StageObjects.Remove(importedFixture.Stage, ["room/~one"]);
    Assert(StageObjects.Read(groupRemoved).Single().Key == "/other");
    Assert(StageObjects.Read(importedFixture.Stage).Length == 4);
    Assert(groupRemoved.GetProperty("Info").GetProperty("Name").GetString() == "Imported room");
});
await Check("Imported movement preserves scale, dye, opacity and unrelated object data", () =>
{
    var moved = StageObjects.Transform(importedFixture.Stage, ["room/~one", "chair/~a"], new(3, 4, 5), new(10, 90, 20), new(1, 2, 3));
    var target = StageObjects.Read(moved)[1];
    Assert(target.Position == new Vector3(3, 4, 5) && target.Rotation == new Vector3(10, 90, 20) && target.Scale == new Vector3(1, 2, 3));
    var node = moved.GetProperty("Objects").GetProperty("room/~one").GetProperty("Objects").GetProperty("chair/~a");
    Assert(node.GetProperty("Opacity").GetSingle() == 0.8f && node.GetProperty("DyeColor").GetProperty("Y").GetSingle() == 0.2f);
    Assert(StageObjects.Read(importedFixture.Stage)[1].Position == Vector3.UnitX);
});
await Check("Imported edits survive home serialization with Haven metadata intact", () =>
{
    var house = importedFixture with { Furnishings = [new() { ItemId = 6500 }], Interior = new() { Brightness = 3 } };
    var edited = house with { Stage = StageObjects.Remove(house.Stage, ["other"]) };
    var copy = HouseFormat.Parse(HouseFormat.Serialize(edited));
    Assert(StageObjects.Read(copy.Stage).Length == 3 && copy.Furnishings[0].Id == house.Furnishings[0].Id && copy.Interior.Brightness == 3);
    Assert(StageObjects.Read(HouseFormat.Parse(HouseFormat.Serialize(house)).Stage).Length == 4);
});
await Check("Imported paths cannot select neighbors and nonfinite transforms are rejected", () =>
{
    Reject(() => StageObjects.Transform(importedFixture.Stage, ["room/~one", "chair/~a"], new(float.NaN, 0, 0), Vector3.Zero, Vector3.One));
    try { StageObjects.Remove(importedFixture.Stage, ["room", "~one", "chair/~a"]); throw new Exception("An invalid path was accepted."); }
    catch (InvalidOperationException) { }
    var disabled = WithStage("""{"FormatVersion":2,"Objects":{"g":{"Type":"Group","IsDisabled":true,"Objects":{"light":{"Type":"Light"}}}}}""");
    Assert(StageObjects.Read(disabled.Stage).All(x => x.Disabled));
});

await Check("Published scenes ignore playback and timestamps but freeze all scene fields", () =>
{
    var house = Sample() with { Furnishings = [new() { ItemId = 6500, Position = [1, 0, 2] }] };
    var scene = new PublishedHouseScene(house);
    Assert(scene.Matches(HouseFormat.Parse(HouseFormat.Serialize(house))));
    Assert(scene.Matches(house with { SavedAt = house.SavedAt.AddHours(1), Music = new() { Volume = .25f } }));
    foreach (var changed in new[] {
        house with { Name = "Different" }, house with { Id = Guid.NewGuid() },
        house with { TerritoryId = 651 }, house with { CustomLayout = true, TerritoryId = CustomCanvas.TerritoryId },
        house with { Transform = new() { Scale = 2 } },
        house with { Interior = new() { Brightness = 1 } },
        house with { Furnishings = [house.Furnishings[0] with { StainId = 12 }] },
        house with { Stage = WithStage("""{"FormatVersion":2,"Objects":{}}""").Stage } })
        Assert(!scene.Matches(changed));
    // Arrays in a record are mutable; the published identity must not alias them.
    house.Furnishings[0].Position[0] = 9;
    Assert(!scene.Matches(house));
    Assert(scene.Hash.Length == 64 && scene.Hash == new PublishedHouseScene(Sample() with {
        Id = house.Id, SavedAt = house.SavedAt, Furnishings = [house.Furnishings[0] with { Position = [1, 0, 2] }] }).Hash);
});

await Check("Actor snapshots remain independent without JSON and contain no mutable references", () =>
{
    var original = new HavenNative.Sync.TransformData { X = 1, Seq = 2, MonikerName = "Before", MapHiddenNpcDataIds = [10] };
    var copy = original.Snapshot(); original.X = 99; original.MonikerName = "After";
    Assert(copy.X == 1 && copy.Seq == 2 && copy.MonikerName == "Before");
    original.MapHiddenNpcDataIds[0] = 20;
    Assert(copy.MapHiddenNpcDataIds![0] == 10);
    foreach (var field in typeof(HavenNative.Sync.TransformData).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
        Assert(field.FieldType.IsValueType || field.FieldType == typeof(string)
            || (field.FieldType == typeof(uint[]) && field.Name == "<MapHiddenNpcDataIds>k__BackingField"));
});
await Check("Motion interpolation ignores state-only lanes and duplicate or old movement", () =>
{
    var buffer = new PeerMotionBuffer<string>();
    Assert(buffer.Add("start", Vector3.Zero, 1, 0, true));
    Assert(buffer.Add("step", Vector3.UnitX, 2, .1, true));
    Assert(!buffer.Add("emote", Vector3.UnitX, 2, .18, false));
    Assert(!buffer.Add("old", Vector3.Zero, 1, .19, true));
    Assert(buffer.TrySample(.20, out var a, out var b, out var t));
    Assert(a == "start" && b == "step" && Math.Abs(t - .5) < .001);
});
await Check("Motion jitter never rewinds and stalls hold without prediction drift", () =>
{
    var buffer = new PeerMotionBuffer<string>();
    buffer.Add("a", Vector3.Zero, 1, 0, true);
    buffer.Add("b", Vector3.UnitX, 2, .1, true);
    buffer.TrySample(.24, out _, out _, out var before);
    buffer.Add("c", Vector3.UnitX * 2, 3, .35, true); // delay increases from .15 to .30
    buffer.TrySample(.35, out var a, out var b, out var after);
    Assert(a == "a" && b == "b" && after >= before);
    buffer.TrySample(10, out _, out var latest, out var held);
    Assert(latest == "c" && held == 1);
    buffer.TrySample(11, out _, out latest, out held);
    Assert(latest == "c" && held == 1);
});
await Check("Motion uses the relay's forwarding clock, so arrival jitter does not change a peer's speed", () =>
{
    var buffer = new PeerMotionBuffer<string>();
    const long start = 1_793_000_000_000; // Unix ms
    // Sent every 100 ms at 1 m a step; arrivals jitter by up to 80 ms and come in bunches.
    var arrivals = new[] { .00, .18, .19, .30, .48, .50, .61, .78, .80, .90, 1.08, 1.10 };
    for (var i = 0; i < arrivals.Length; i++) buffer.Add(i.ToString(), Vector3.UnitX * i * .5f, i + 1, 100 + arrivals[i], true, start + i * 100);
    // Rendering at a steady pace, the sampled position advances steadily too (no stop-then-snap).
    var previous = -1f; var steps = new List<float>();
    for (var at = 100.5; at <= 101.1; at += .05)
    {
        Assert(buffer.TrySample(at, out var a, out var b, out var t));
        var position = (int.Parse(a) + (int.Parse(b) - int.Parse(a)) * t) * .5f;
        if (previous >= 0) steps.Add(position - previous);
        previous = position;
    }
    Assert(steps.All(s => s >= -1e-4f));
    Assert(steps.Skip(2).All(s => Math.Abs(s - .25f) < .05f)); // 0.5 m per 100 ms sampled every 50 ms
});
await Check("Motion teleports reset history and idle gaps preserve a bounded arrival", () =>
{
    var buffer = new PeerMotionBuffer<string>();
    buffer.Add("origin", Vector3.Zero, 1, 0, true);
    buffer.Add("teleport", new Vector3(100, 0, 0), 2, .1, true);
    buffer.TrySample(.1, out var a, out var b, out _);
    Assert(a == "teleport" && b == "teleport");
    buffer.Add("idle", new Vector3(100, 0, 0), 3, 2.1, true);
    buffer.Add("walk", new Vector3(101, 0, 0), 4, 2.2, true);
    buffer.TrySample(2.3, out a, out b, out var t);
    Assert(a == "idle" && b == "walk" && Math.Abs(t - .5) < .001);
});
await Check("Client accepts its relay chat echo without replaying its own actor state", () =>
{
    Assert(RelayReceivePolicy.Accept(HousingWire.RoomChat, "self", "self"));
    Assert(RelayReceivePolicy.Accept(WireKind.HostTransfer, "self", "self"));
    foreach (var kind in new[] { WireKind.HotUpdate, WireKind.WarmUpdate, WireKind.ColdUpdate, WireKind.HostUpdate })
    {
        Assert(!RelayReceivePolicy.Accept(kind, "self", "self"));
        Assert(RelayReceivePolicy.Accept(kind, "friend", "self"));
    }
    Assert(RelayReceivePolicy.Accept(HousingWire.RoomChat, "friend", "self"));
});
await Check("Batched delivery keeps order, the newest movement per sender, and lone checkpoints", () =>
{
    byte[] F(byte kind, string sender, int size = 8) => FrameHeader.BuildDownleg(kind, sender, 1, new byte[size]);
    byte[] Wrap(byte[] payload) => FrameHeader.BuildDownleg(HousingWire.Batch, WireFormat.RelaySender, 2, payload);
    var hotA1 = F(WireKind.HotUpdate, "a"); var chat = F(HousingWire.RoomChat, "b"); var hotA2 = F(WireKind.HotUpdate, "a", 9);
    var hotB = F(WireKind.HotUpdate, "b"); var checkpoint = F(HousingWire.RecoveryCheckpoint, WireFormat.RelaySender); var after = F(HousingWire.Typing, "a");
    var messages = FrameBatch.Messages([hotA1, chat, hotA2, hotB, checkpoint, after], WireKind.HotUpdate, HousingWire.RecoveryCheckpoint, Wrap);
    Assert(messages.Count == 3 && FrameBatch.Header(messages[0]).Kind == HousingWire.Batch && messages[1] == checkpoint && messages[2] == after);
    var first = FrameBatch.Unpack(FrameHeader.ParseDownleg(messages[0]).Payload);
    Assert(first.Count == 3 && first[0].SequenceEqual(chat) && first[1].SequenceEqual(hotA2) && first[2].SequenceEqual(hotB));
    var big = F(HousingWire.CompressedSnapshot, WireFormat.RelaySender, FrameBatch.StandaloneBytes + 1);
    Assert(FrameBatch.Messages([chat, big], WireKind.HotUpdate, HousingWire.RecoveryCheckpoint, Wrap) is [var c, var b] && c == chat && b == big);
    Assert(FrameBatch.Header(chat) == (HousingWire.RoomChat, "b"));
    Reject(() => FrameBatch.Unpack([5, 0, 0, 0, 1]));
    Reject(() => FrameBatch.Unpack([0, 0, 0, 0]));
});
await Check("Imported stage lights are bounded when drawn", () =>
{
    Assert(SceneLights.Range(1e9f) == SceneLights.MaxRange && SceneLights.Range(-5) == 0 && SceneLights.Range(float.NaN) == 0 && SceneLights.Range(12) == 12);
    Assert(SceneLights.Intensity(float.PositiveInfinity) == 0 && SceneLights.Intensity(4) == 4 && SceneLights.Intensity(1000) == SceneLights.MaxIntensity);
    Assert(SceneLights.ShadowRange(500) == SceneLights.MaxShadowRange && SceneLights.MaxImported == 256);
});
await Check("Room chat validates channels, UTF-8 budgets and plain text without executable payloads", () =>
{
    foreach (var channel in new[] { 10, 11, 14, 28, 30 })
    {
        var message = new RoomChatMessage(channel, "Hello 世界 🌸 /sit");
        Assert(RoomChat.Decode(RoomChat.Encode(message)) == message);
    }
    Reject(() => RoomChat.Encode(new(12, "No world tells")));
    Reject(() => RoomChat.Encode(new(10, "\u0002payload\u0003")));
    Reject(() => RoomChat.Encode(new(10, new string('界', 334))));
    Reject(() => RoomChat.Encode(new(10, " ")));
    Reject(() => RoomChat.Decode(new byte[8193]));
    // An emote line names its emote and target, so each viewer words it ("blows you a kiss").
    var dote = new RoomChatMessage(28, "blows Aria a kiss.", "Zoe", 146, "Aria");
    Assert(RoomChat.Decode(RoomChat.Encode(dote)) == dote);
    Assert(RoomChat.Decode("{\"Channel\":28,\"Text\":\"waves.\",\"Name\":\"Zoe\"}"u8.ToArray()) == new RoomChatMessage(28, "waves.", "Zoe"));
    Reject(() => RoomChat.Encode(new(10, "Hello", Emote: 146)));
    Reject(() => RoomChat.Encode(new(28, "waves.", Emote: 70000)));
    Reject(() => RoomChat.Encode(new(28, "waves.", Emote: 1, Target: "ab")));
    Reject(() => RoomChat.Encode(new(28, "waves.", Emote: 1, Target: new string('a', 81))));
    Assert(RoomChatCommand.Channel("/party") == 0 && RoomChatCommand.Channel("/say") == 10 && RoomChatCommand.Channel("/tell") == 0);
    Assert(RoomChatCommand.Split("/say  hello there ") == ("/say", "hello there"));
});
await Check("Typing retains paused drafts through focus loss until sending or clearing", () =>
{
    var activity = new TypingActivity();
    Assert(activity.Observe(0, true, "abc"u8) == TypingState.Active);
    Assert(activity.Observe(1499, true, "abc"u8) == TypingState.Active);
    Assert(activity.Observe(1500, true, "abc"u8) == TypingState.Paused);
    Assert(activity.Observe(2000, true, "abd"u8) == TypingState.Active);
    Assert(activity.Observe(3500, true, "abd"u8) == TypingState.Paused);
    Assert(activity.Observe(32000, true, "abd"u8) == TypingState.Paused);
    Assert(activity.Observe(32001, true, "世界"u8) == TypingState.Active);
    activity.Submitted();
    Assert(activity.Observe(32002, true, "世界"u8) == TypingState.Idle);
    Assert(activity.Observe(32003, true, []) == TypingState.Idle);
    Assert(activity.Observe(32004, true, "世界"u8) == TypingState.Active);
    Assert(activity.Observe(32005, false, "世界"u8) == TypingState.Paused);
    activity.Reset();
    Assert(activity.Observe(32006, true, "世界"u8) == TypingState.Active);
});
await Check("Relay limits key on the client's address, an IPv6 network's /64, and leave unforwarded local sockets unlimited", () =>
{
    Assert(RelayAddress.Key("203.0.113.9", null) == "203.0.113.9");
    Assert(RelayAddress.Key("2001:db8:1:2:aaaa::1", null) == RelayAddress.Key("2001:db8:1:2:ffff::9", null));
    Assert(RelayAddress.Key("2001:db8:1:2::1", null) != RelayAddress.Key("2001:db8:1:3::1", null));
    Assert(RelayAddress.Key("", System.Net.IPAddress.Loopback) == "" && RelayAddress.Key("", System.Net.IPAddress.Parse("198.51.100.4")) == "198.51.100.4");
    Assert(RelayAddress.Key("::ffff:198.51.100.4", null) == "198.51.100.4" && RelayAddress.Key("not an address", null) == "");
    Assert(SafeAngles.Wrap(1e30f) is >= -MathF.PI and <= MathF.PI && SafeAngles.Wrap(float.PositiveInfinity) == 0 && MathF.Abs(SafeAngles.Wrap(3 * MathF.PI) - MathF.PI) < 1e-4f);
    Assert(!SafeAngles.Sane(float.NaN) && !SafeAngles.Sane(1e6f) && SafeAngles.Sane(-2500.5f));
});
await Check("Long posts split into whole-word room chat lines within the byte limit, never mid-character", () =>
{
    Assert(RoomChatText.Split("  hello there ", 1000) is ["hello there"]);
    var post = string.Join(' ', Enumerable.Repeat("The rain hammered on the tavern’s shutters — ", 60));
    var parts = RoomChatText.Split(post, RoomChat.MaxTextBytes);
    Assert(parts.Length > 1 && parts.All(p => System.Text.Encoding.UTF8.GetByteCount(p) <= RoomChat.MaxTextBytes && p.Length > 0));
    Assert(string.Join(' ', parts).Replace("  ", " ") == post.Trim().Replace("  ", " "));
    Assert(parts.All(p => !p.StartsWith(' ') && !p.EndsWith(' ')));
    var emoji = string.Concat(Enumerable.Repeat("😀", 600));
    var split = RoomChatText.Split(emoji, RoomChat.MaxTextBytes);
    Assert(string.Concat(split) == emoji && split.All(p => !char.IsLowSurrogate(p[0])));
    foreach (var part in parts) RoomChat.Encode(new RoomChatMessage(28, part));
});
await Check("Minions summon, swap and put away inside a home, and real-world minions come back after the return", () =>
{
    Assert(Minions.Decide(false, 52, 0, true, true) == MinionSummon.PassThrough);
    Assert(Minions.Decide(true, 52, 0, true, true) == MinionSummon.Summon && Minions.Decide(true, 52, 7, true, true) == MinionSummon.Summon);
    Assert(Minions.Decide(true, 52, 52, true, true) == MinionSummon.Dismiss && Minions.Decide(true, 52, 0, true, false) == MinionSummon.NotUnlocked);
    Assert(Minions.Decide(true, 0, 0, true, true) == MinionSummon.Unknown && Minions.Decide(true, 70000, 0, true, true) == MinionSummon.Unknown);
    Assert(Minions.Decide(true, 52, 0, false, true) == MinionSummon.Unknown);
    Assert(Minions.Restore(52, 52, false, 0, -1) == MinionRestoreStep.Done && Minions.Restore(0, 0, true, 5, -1) == MinionRestoreStep.Done);
    Assert(Minions.Restore(52, 0, false, 10, -1) == MinionRestoreStep.Wait && Minions.Restore(52, 0, true, 10, -1) == MinionRestoreStep.Setup);
    Assert(Minions.Restore(0, 52, true, 0, -1) == MinionRestoreStep.Setup);
    Assert(Minions.Restore(52, 0, true, 10 + Minions.RetryFrames - 1, 10) == MinionRestoreStep.Wait);
    Assert(Minions.Restore(52, 0, true, 10 + Minions.RetryFrames, 10) == MinionRestoreStep.Setup);
    Assert(Minions.Restore(52, 0, true, Minions.RestoreFrames, 10) == MinionRestoreStep.GiveUp && Minions.Restore(52, 52, true, Minions.RestoreFrames, 10) == MinionRestoreStep.Done);
});
await Check("Typing shows no bubble for commands, but does for room chat with a space", () =>
{
    Assert(TypingDraft.IsCommand("/"u8) && TypingDraft.IsCommand("/em"u8) && TypingDraft.IsCommand("/penumbra redraw"u8));
    Assert(TypingDraft.IsCommand("  /xlplugins"u8) && TypingDraft.IsCommand("/emotes now"u8));
    Assert(!TypingDraft.IsCommand("/em "u8) && !TypingDraft.IsCommand("/EM waves"u8) && !TypingDraft.IsCommand("/say hi"u8));
    Assert(!TypingDraft.IsCommand("/y\tsoon"u8) && TypingDraft.IsCommand("/p hi"u8) && !TypingDraft.IsCommand("hello /there"u8) && !TypingDraft.IsCommand("hello"u8));
    var activity = new TypingActivity();
    Assert(activity.Observe(0, true, "/penumbra"u8) == TypingState.Idle);
    Assert(activity.Observe(100, true, "/em "u8) == TypingState.Active);
    Assert(activity.Observe(200, true, "/em smiles"u8) == TypingState.Active);
});
await Check("Chat 2 metadata handles pause, unfocus, same-length edits, send, clear and malformed lengths", () =>
{
    var activity = new TypingActivity();
    Assert(activity.ObserveMetadata(0, true, true, 3, false) == TypingState.Active);
    Assert(activity.ObserveMetadata(1500, true, true, 3, false) == TypingState.Paused);
    Assert(activity.ObserveMetadata(1600, true, true, 3, true) == TypingState.Active);
    Assert(activity.ObserveMetadata(1700, false, true, 3, false) == TypingState.Paused);
    Assert(activity.ObserveMetadata(60000, false, true, 3, false) == TypingState.Paused);
    Assert(activity.ObserveMetadata(60001, true, true, 4, false) == TypingState.Active);
    activity.Submitted();
    Assert(activity.ObserveMetadata(60002, true, true, 4, false) == TypingState.Idle);
    Assert(activity.ObserveMetadata(60003, true, false, 0, false) == TypingState.Idle);
    Assert(activity.ObserveMetadata(60004, true, true, 4, false) == TypingState.Active);
    Assert(activity.ObserveMetadata(60005, true, true, -1, false) == TypingState.Idle);
    Assert(activity.ObserveMetadata(60006, true, true, 8193, false) == TypingState.Idle);
});
await Check("Typing bubble geometry respects scaled and offset viewports and keeps dots inside", () =>
{
    foreach (var scale in new[] { .75f, 1f, 2f })
    {
        var bubble = TypingBubbleLayout.At(new(700, 600), new(400, 200), new(800, 600), scale)!.Value;
        for (var i = 0; i < 3; i++)
        {
            var dot = bubble.Dot(i);
            Assert(dot.X > bubble.Min.X && dot.X < bubble.Max.X && dot.Y > bubble.Min.Y && dot.Y < bubble.Max.Y);
        }
        Assert(bubble.TailTip == new Vector2(700, 600) && bubble.Max.Y < bubble.TailTip.Y);
    }
    Assert(TypingBubbleLayout.At(new(5, 5), Vector2.Zero, new(800, 600), 1) == null);
    Assert(TypingBubbleLayout.At(new(float.NaN, 50), Vector2.Zero, new(800, 600), 1) == null);
});
await Check("Standing additive emotes sync on animation edges without replaying held lanes or idle", () =>
{
    var detector = new EmoteOverlayEdges();
    ushort Find(ushort timeline) => timeline == 8094 ? (ushort)252 : timeline == 8082 ? (ushort)251 : (ushort)0;
    Assert(detector.Observe([0, 0, 8094], Find) == 252);
    Assert(detector.Observe([0, 0, 8094], Find) == 0);
    Assert(detector.Observe([34, 0, 0], Find) == 0);
    Assert(detector.Observe([34, 0, 8094], Find) == 252);
    Assert(detector.Observe([0, 8082, 8094], Find) == 251);
    detector.Reset();
    Assert(detector.Observe([0, 0, 8094], Find) == 252);
});
await Check("Quest emote ownership ignores global bypass flags and uses the correct quest bit order", () =>
{
    var unlocks = new byte[92]; var quests = new byte[751];
    unlocks[245 / 8] |= 1 << (245 % 8); // Generic UI bypass is not Ear Wiggle ownership.
    Assert(!EmoteOwnership.IsOwned(70222, unlocks, quests));
    var quest = 70222 & 0xFFFF;
    quests[quest / 8] = (byte)(0x80 >> (quest % 8));
    Assert(EmoteOwnership.IsOwned(70222, unlocks, quests));
    Assert(!EmoteOwnership.IsOwned(70221, unlocks, quests));
    unlocks[445 / 8] |= 1 << (445 % 8);
    Assert(EmoteOwnership.IsOwned(445, unlocks, quests));
    Assert(!EmoteOwnership.IsOwned(446, unlocks, quests));
    Assert(EmoteOwnership.IsOwned(0, [], []));
    Assert(!EmoteOwnership.IsOwned(70222, [], []));
    Assert(!EmoteOwnership.IsOwned(65535, unlocks, quests));
});
await Check("Local emote dispatch precedes native parsing, preserves owned commands and rejects unknown commands", () =>
{
    var registry = new LocalEmoteCommands();
    registry.Add("/earwiggle", 252); registry.Add("/wave", 16); registry.Add("/ew", 252);
    var played = new List<ushort>();
    bool PlayLocked(ushort id) { if (id != 252) return false; played.Add(id); return true; }
    Assert(registry.TryPlay("/EARWIGGLE", PlayLocked));
    Assert(registry.TryPlay("/ew", PlayLocked));
    Assert(!registry.TryPlay("/wave", PlayLocked));
    Assert(!registry.TryPlay("/say", PlayLocked));
    Assert(!registry.TryPlay("/earwiggle", null));
    Assert(played.SequenceEqual(new ushort[] { 252, 252 }));
});
await Check("Typing wire accepts only a one-byte presence state", () =>
{
    foreach (var state in new[] { TypingState.Idle, TypingState.Active, TypingState.Paused })
        Assert(TypingWire.Decode([(byte)state]) == state);
    Reject(() => TypingWire.Decode([]));
    Reject(() => TypingWire.Decode([3]));
    Reject(() => TypingWire.Decode([1, 0]));
    Reject(() => TypingWire.Decode("draft text"u8));
});
await Check("Room chat rate limit bounds bursts and recovers without a clock rewind refill", () =>
{
    var limit = new RoomChatRateLimit();
    for (var i = 0; i < 8; i++) Assert(limit.Allow(10));
    Assert(!limit.Allow(10)); Assert(!limit.Allow(9));
    Assert(limit.Allow(10.5));
});
await Check("Relay connection errors distinguish local setup, DNS, timeouts and HTTP upgrade failures", () =>
{
    var endpoint = RelayConnectionFailure.HostedEndpoint;
    Assert(RelayConnectionFailure.Describe("ws://localhost:9420", new Exception()).Contains("Localhost points to this PC"));
    var dns = new WebSocketException("Connect failed", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound));
    Assert(RelayConnectionFailure.Describe(endpoint, dns).Contains("Cannot resolve"));
    Assert(RelayConnectionFailure.Describe(endpoint, new TaskCanceledException()).Contains("timed out"));
    Assert(RelayConnectionFailure.Describe(endpoint, new WebSocketException("The server returned status code '404' when status code '101' was expected.")).Contains("without /health"));
    Assert(RelayConnectionFailure.Describe(endpoint, new HttpRequestException("Unavailable", null, System.Net.HttpStatusCode.ServiceUnavailable)).Contains("HTTP 503"));
    Assert(RelayConnectionFailure.Describe("https://example.com", new Exception()).Contains("ws:// or wss://"));
    var secret = RelayConnectionFailure.Describe("wss://user:private-password@example.com/?k=secret-token", new Exception("secret-token"));
    Assert(!secret.Contains("private-password") && !secret.Contains("secret-token"));
});

await Check("Scene IDs use compact JSON regardless of Windows or Linux snapshot newlines", () =>
{
    var house = Sample();
    var windows = JsonSerializer.SerializeToUtf8Bytes(house, new JsonSerializerOptions(HouseFormat.Json) { NewLine = "\r\n" });
    var linux = JsonSerializer.SerializeToUtf8Bytes(house, new JsonSerializerOptions(HouseFormat.Json) { NewLine = "\n" });
    Assert(!windows.SequenceEqual(linux));
    Assert(new PublishedHouseScene(HouseFormat.Parse(windows)).Hash == new PublishedHouseScene(HouseFormat.Parse(linux)).Hash);

});

await Check("Compressed scenes preserve exact saves and enforce expansion bounds", () =>
{
    var house = Sample() with { Furnishings = Enumerable.Range(0, 500).Select(i => new Furnishing { ItemId = 6500, Position = [i / 10f, 0, 2] }).ToArray() };
    var raw = HouseFormat.Serialize(house); var encoded = HousingSnapshotCodec.Encode(house);
    Assert(encoded.Length < raw.Length / 2);
    Assert(raw.SequenceEqual(HouseFormat.Serialize(HousingSnapshotCodec.Decode(encoded))));
    foreach (var length in new[] { -1, 0, 1, HouseFormat.MaxBytes + 1 })
    {
        var bad = encoded.ToArray(); System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bad, length);
        Reject(() => HousingSnapshotCodec.Decode(bad));
    }
    Reject(() => HousingSnapshotCodec.Decode(encoded[..^1]));
    Reject(() => HousingSnapshotCodec.Decode([..encoded, 0]));
    Reject(() => HousingSnapshotCodec.Decode(new byte[HouseFormat.MaxBytes + 17]));
    Reject(() => HousingSnapshotCodec.Decode([0, 0, 0]));
});

await Check("Hosted transactions merge disjoint saves, preserve conflicts and validate bounded data", EditingIntegration.Core);
await Check("Automatic staging sends changed paths, supports undo and preserves newer edits through individual reviews", EditingIntegration.StagingCore);
await using var app = RelayApp.Build(["--urls", "http://127.0.0.1:0"]);
Uri endpoint;
var hosted = args.Length == 2 && args[0] == "--relay-endpoint";
if (hosted) endpoint = new Uri(args[1]);
else { await app.StartAsync(); endpoint = new Uri(app.Urls.Single().Replace("http://", "ws://") + "/"); }
await CheckAsync("Hosted editing preserves owner authority, explicit saves, proposals, music isolation and recovery", async () => await EditingIntegration.Relay(endpoint, Sample()));
await CheckAsync("Automatic proposals support replacement, individual review, atomic approve-all, undo and local chat identity", async () => await EditingIntegration.StagingRelay(endpoint));
await CheckAsync("Relay rejects clients without the current housing protocol", async () =>
{
    foreach (var revision in new[] { 0, 4, 5, 6, 7, 8, 9, 10, 11, 12 })
    {
        using var old = await Client.Connect(endpoint, Join(99, "Old", true) with { Revision = revision });
        var error = Decode<ErrorPayload>((await old.Expect(WireKind.Error)).Payload);
        Assert(error.Code == ErrCode.Generic && error.Message.Contains($"revision {HousingWire.ProtocolRevision}"));
    }
});
using var host = await Client.Connect(endpoint, Join(101, "Host", true));
var hostAck = Decode<RoomJoinedPayload>((await host.Expect(WireKind.RoomJoined)).Payload);
Assert(hostAck.IsHost && !string.IsNullOrEmpty(hostAck.AssignedPeerId));
await Check("Relay creates a room without a vendor or beta key", () => Assert(hostAck.RoomCap == 20));

await CheckAsync("Relay refuses wrong visit codes and peers outside render range", async () =>
{
    using var wrong = await Client.Connect(endpoint, Join(102, "Wrong", false) with { RoomPassword = "not-the-code" });
    Assert(Decode<ErrorPayload>((await wrong.Expect(WireKind.Error)).Payload).Code == ErrCode.RoomNotFound);
    using var far = await Client.Connect(endpoint, Join(102, "Far", false) with { NearbyContentIds = [] });
    Assert(Decode<ErrorPayload>((await far.Expect(WireKind.Error)).Payload).Code == ErrCode.NotHosting);
});
await CheckAsync("A guest whose game lacks the home's zone says so as it leaves, and the host is told", async () =>
{
    using (var trial = await Client.Connect(endpoint, Join(104, "Trial", false)))
    {
        await trial.Expect(WireKind.RoomJoined);
        await trial.Send(WireKind.LeaveRoom, GameDataPaths.LeaveReason(5));
        await trial.ExpectClose(); // the relay read the leave before the socket goes (else it may hold the seat for a reconnect)
    }
    var left = Decode<PeerLeftPayload>((await host.Expect(WireKind.PeerLeft)).Payload);
    Assert(left.MissingExpansion == 5);
    using (var plain = await Client.Connect(endpoint, Join(105, "Plain", false)))
    {
        await plain.Expect(WireKind.RoomJoined);
        await plain.Send(WireKind.LeaveRoom, []);
        await plain.ExpectClose(); // the relay read the leave before the socket goes (else it may hold the seat for a reconnect)
    }
    Assert(Decode<PeerLeftPayload>((await host.Expect(WireKind.PeerLeft)).Payload).MissingExpansion == 0);
});

using var guest = await Client.Connect(endpoint, Join(102, "Guest", false));
var guestAck = Decode<RoomJoinedPayload>((await guest.Expect(WireKind.RoomJoined)).Payload);
var original = Sample() with { Furnishings = [new() { ItemId = 6500, Position = [1, 0, 2], Scale = 1.5f, StainId = 12 }],
    TerritoryId = 651, Interior = new() { Brightness = 3, MainHallCeilingLight = 65816 }, Music = new() { Playing = true, StartedAtUnixMs = 1000, Playlist = [new() { OrchestrionId = 12 }] } };
await CheckAsync("Host furniture and actor movement reach guests with relay-stamped identity", async () =>
{
    await host.Send(HousingWire.CompressedSnapshot, HousingSnapshotCodec.Encode(original));
    var snapshot = await guest.Expect(HousingWire.Snapshot);
    Assert(snapshot.SenderId == WireFormat.RelaySender && HouseFormat.Parse(snapshot.Payload).Id == original.Id);
    var shared = HouseFormat.Parse(snapshot.Payload);
    Assert(shared.Furnishings[0].ItemId == 6500 && shared.Furnishings[0].Scale == 1.5f && shared.Furnishings[0].StainId == 12
        && shared.Interior.Brightness == 3 && shared.Interior.MainHallCeilingLight == 65816 && shared.Music.Playlist[0].OrchestrionId == 12);
    await host.Ready(original);
    await guest.Ready(original);
    var hot = new HotPayload { Protocol = 4, SenderContentId = 101, X = 8, Y = 9, Z = 10 };
    await host.Send(WireKind.HotUpdate, Encode(hot));
    var movement = await guest.Expect(WireKind.HotUpdate);
    Assert(movement.SenderId == hostAck.AssignedPeerId && Decode<HotPayload>(movement.Payload).X == 8);
});
await CheckAsync("Custom layout identity and furnishings pass through the shared snapshot lane", async () =>
{
    using var customHost = await Client.Connect(endpoint, Join(301, "CustomHost", true) with { RoomPassword = "custom-scene-code" });
    await customHost.Expect(WireKind.RoomJoined);
    var piece = new Furnishing { BuildingPart = "bg/ffxiv/sea_s1/ind/common/bgparts/s1i1_s_f1_wl_0000.mdl", SurfaceFinish = 66068,
        Position = [2, 0, -6], Rotation = 45, Scale = .75f };
    var custom = original with { TerritoryId = CustomCanvas.TerritoryId, CustomLayout = true, Interior = new() { Brightness = 2 },
        Furnishings = [..original.Furnishings, piece] };
    await customHost.Send(HousingWire.Snapshot, HouseFormat.Serialize(custom));
    using var customGuest = await Client.Connect(endpoint, Join(302, "CustomGuest", false) with { RoomPassword = "custom-scene-code", NearbyContentIds = [301] });
    await customGuest.Expect(WireKind.RoomJoined);
    var saved = HouseFormat.Parse((await customGuest.Expect(HousingWire.Snapshot)).Payload);
    Assert(saved.CustomLayout && saved.TerritoryId == CustomCanvas.TerritoryId && saved.Furnishings[0].Id == original.Furnishings[0].Id);
    Assert(saved.Interior.Brightness == 2 && saved.Furnishings.All(x => x.Id != CustomCanvas.PlatformId));
    Assert(saved.Furnishings[1].BuildingPart == piece.BuildingPart && saved.Furnishings[1].SurfaceFinish == 66068 && saved.Furnishings[1].Id == piece.Id);
    using (var customLate = await Client.Connect(endpoint, Join(303, "CustomLate", false) with { RoomPassword = "custom-scene-code", NearbyContentIds = [301] }))
    {
        await customLate.Expect(WireKind.RoomJoined);
        var cached = HouseFormat.Parse((await customLate.Expect(HousingWire.Snapshot)).Payload);
        Assert(cached.CustomLayout && cached.TerritoryId == CustomCanvas.TerritoryId && cached.Interior.Brightness == 2);
        Assert(cached.Furnishings[1].BuildingPart == piece.BuildingPart && cached.Furnishings[1].SurfaceFinish == 66068 && cached.Furnishings[1].Scale == .75f);
    }
    await customHost.Send(WireKind.SessionEnd, []);
    await customGuest.Expect(WireKind.SessionEnd);
});
await CheckAsync("Published visits reject furniture, finish and shell edits while music remains live", async () =>
{
    foreach (var changed in new[] {
        original with { Furnishings = [] }, original with { Interior = new() },
        original with { TerritoryId = 282 }, original with { Transform = new() { Scale = 2 } } })
    {
        await host.Send(HousingWire.Snapshot, HouseFormat.Serialize(changed));
        Assert(Decode<ErrorPayload>((await host.Expect(WireKind.Error)).Payload).Message.Contains("fixed"));
    }
    await host.Send(WireKind.ZoneLoadExecute, Encoding.UTF8.GetBytes("{\"zone\":282}"));
    Assert(Decode<ErrorPayload>((await host.Expect(WireKind.Error)).Payload).Message.Contains("shell"));
    await host.Send(HousingWire.Snapshot, HouseFormat.Serialize(original with { Music = original.Music with { Volume = .25f } }));
    var saved = HouseFormat.Parse((await guest.Expect(HousingWire.Snapshot)).Payload);
    Assert(saved.Music.Volume == .25f && new PublishedHouseScene(original).Matches(saved));
});
await CheckAsync("Guests cannot replace furniture, load zones, or end another host's session", async () =>
{
    foreach (var kind in new[] { HousingWire.Snapshot, WireKind.ZoneLoadExecute, WireKind.SessionEnd, WireKind.HostUpdate })
    {
        await guest.Send(kind, []);
        Assert(Decode<ErrorPayload>((await guest.Expect(WireKind.Error)).Payload).Code == ErrCode.NotHost);
    }
});
await CheckAsync("Fragmented furniture snapshots larger than 64 KiB are reassembled", async () =>
{
    var padded = original with { Stage = JsonDocument.Parse("{\"FormatVersion\":2,\"Info\":{\"Description\":\"" + new string('x', 70000) + "\"},\"Objects\":{}}").RootElement.Clone() };
    using var largeHost = await Client.Connect(endpoint, Join(401, "LargeHost", true) with { RoomPassword = "large-scene-code" });
    await largeHost.Expect(WireKind.RoomJoined);
    using var largeGuest = await Client.Connect(endpoint, Join(402, "LargeGuest", false) with { RoomPassword = "large-scene-code", NearbyContentIds = [401] });
    await largeGuest.Expect(WireKind.RoomJoined);
    await largeHost.SendFragmented(HousingWire.Snapshot, HouseFormat.Serialize(padded));
    Assert(HouseFormat.Parse((await largeGuest.Expect(HousingWire.Snapshot)).Payload).Stage.GetProperty("Info").GetProperty("Description").GetString()!.Length == 70000);
    await largeHost.Send(WireKind.SessionEnd, []);
    await largeGuest.Expect(WireKind.SessionEnd);
});

await CheckAsync("Private chat is ready-gated, room-scoped, identity-stamped and never replayed", async () =>
{
    using var chatHost = await Client.Connect(endpoint, Join(601, "ChatHost", true) with { RoomPassword = "chat-room-code" });
    await chatHost.Expect(WireKind.RoomJoined);
    using var chatGuest = await Client.Connect(endpoint, Join(602, "ChatGuest", false) with { RoomPassword = "chat-room-code", NearbyContentIds = [601] });
    var chatGuestId = Decode<RoomJoinedPayload>((await chatGuest.Expect(WireKind.RoomJoined)).Payload).AssignedPeerId;
    using var other = await Client.Connect(endpoint, Join(603, "OtherRoom", true) with { RoomPassword = "other-chat-room-code" });
    await other.Expect(WireKind.RoomJoined);
    await other.Send(HousingWire.CompressedSnapshot, HousingSnapshotCodec.Encode(original)); await other.Ready(original);
    await chatHost.Send(HousingWire.CompressedSnapshot, HousingSnapshotCodec.Encode(original)); await chatHost.Ready(original);
    await chatGuest.Expect(HousingWire.Snapshot);
    await chatGuest.Send(HousingWire.RoomChat, RoomChat.Encode(new(10, "not ready")));
    await chatGuest.Expect(HousingWire.RoomChatRejected);
    await chatHost.Send(HousingWire.RoomChat, RoomChat.Encode(new(10, "before arrival")));
    await chatHost.Expect(HousingWire.RoomChat);
    await chatHost.Send(WireKind.Ping, []); await chatHost.Expect(WireKind.Pong);
    await chatGuest.Send(WireKind.Ping, []); await chatGuest.ExpectWithout(WireKind.Pong, HousingWire.RoomChat);
    await chatGuest.Ready(original);
    await chatGuest.Send(WireKind.Ping, []); await chatGuest.ExpectWithout(WireKind.Pong, HousingWire.RoomChat);
    foreach (var channel in new[] { 10, 11, 14, 28, 30 })
    {
        var text = "private message " + channel;
        await chatGuest.Send(HousingWire.RoomChat, RoomChat.Encode(new(channel, text, "ForgedHost")));
        var delivered = await chatHost.Expect(HousingWire.RoomChat);
        Assert(delivered.SenderId == chatGuestId && RoomChat.Decode(delivered.Payload) == new RoomChatMessage(channel, text, "ChatGuest"));
        Assert(RoomChat.Decode((await chatGuest.Expect(HousingWire.RoomChat)).Payload).Text == text);
    }
    // A roll is the relay's: whatever number the roller claims, the relay picks one in range and words the line.
    for (var i = 0; i < 6; i++)
    {
        await chatGuest.Send(HousingWire.RoomChat, RoomChat.Encode(new(RoomRolls.Channel, "/random", Roll: 6)));
        var roll = RoomChat.Decode((await chatHost.Expect(HousingWire.RoomChat)).Payload);
        Assert(roll.Channel == RoomRolls.Channel && roll.Roll == 6 && roll.Rolled is >= 1 and <= 6 && roll.Text == RoomRolls.Text("ChatGuest", roll.Rolled, 6));
        Assert(RoomChat.Decode((await chatGuest.Expect(HousingWire.RoomChat)).Payload).Rolled == roll.Rolled);
        await Task.Delay(550); // within the relay's chat allowance
    }
    await chatHost.Send(WireKind.Ping, []); await chatHost.ExpectWithout(WireKind.Pong, HousingWire.RoomChat);
    await chatGuest.Send(WireKind.Ping, []); await chatGuest.ExpectWithout(WireKind.Pong, HousingWire.RoomChat);
    await other.Send(WireKind.Ping, []); await other.ExpectWithout(WireKind.Pong, HousingWire.RoomChat);
    using var newcomer = await Client.Connect(endpoint, Join(604, "Newcomer", false) with { RoomPassword = "chat-room-code", NearbyContentIds = [601] });
    await newcomer.Expect(WireKind.RoomJoined); await newcomer.Expect(HousingWire.Snapshot); await newcomer.Ready(original);
    await newcomer.Send(WireKind.Ping, []); await newcomer.ExpectWithout(WireKind.Pong, HousingWire.RoomChat);
    await chatHost.Send(WireKind.SessionEnd, []); await chatGuest.Expect(WireKind.SessionEnd);
    await other.Send(WireKind.SessionEnd, []);
});
await Check("Discord sign-in derives its state as the Worker checks it, and refusals explain themselves", () =>
{
    Assert(AccountSignIn.StateOf("abc") == "ungWv48Bz-pBQUDeXa4iI7ADYaOWF3qctBD_YfIAFa0");
    var verifier = AccountSignIn.NewVerifier();
    Assert(verifier.Length == 43 && AccountSignIn.StateOf(verifier).Length == 43 && AccountSignIn.IsKey(verifier));
    Assert(AccountSignIn.Origin("wss://haven-relay.eevicat.workers.dev") == "https://haven-relay.eevicat.workers.dev" && AccountSignIn.Origin("https://x") == null);
    Assert(AccountSignIn.Required("wss://haven-relay.eevicat.workers.dev") && AccountSignIn.Required("wss://haven-relay.eevicat.workers.dev/")
        && !AccountSignIn.Required("ws://127.0.0.1:5000") && !AccountSignIn.Required("wss://relay.example.com") && !AccountSignIn.Required(""));
    Assert(AccountSignIn.Parse("{\"Key\":\"" + verifier + "\",\"Name\":\"Zoe\",\"SignedInAt\":\"2026-10-28T12:00:00Z\"}")?.Name == "Zoe");
    Assert(AccountSignIn.Parse("{\"Key\":\"short\",\"Name\":\"Zoe\",\"SignedInAt\":\"2026-10-28T12:00:00Z\"}") == null && AccountSignIn.Parse("not json") == null);
    Assert(RelayConnectionFailure.Describe("wss://relay.example", new Exception("x"), "taken", 403).Contains("another Haven account"));
    Assert(RelayConnectionFailure.Describe("wss://relay.example", new Exception("x"), null, 401).Contains("Sign in with Discord"));
});
await Check("Account stamps match the Worker's and expire", () =>
{
    Assert(AccountStamp.Sign(new string('r', 40), 1234567890123, 1793000000000, "111111111111111111", "Zo\u00eb Chen")
        == "1234567890123.1793000000000.111111111111111111.Wm_DqyBDaGVu.dSmlmwXYruBar9rBO9gf0dPfN8dU5op7BzWNNf7n0cs");
    var key = new string('r', 40);
    var stamp = AccountStamp.Sign(key, 42, 1_000_060_000, "777", "Zoe Chen");
    Assert(AccountStamp.Read(stamp, key, 1_000_000_000) == new VerifiedAccount(42, "777", "Zoe Chen"));
    Assert(AccountStamp.Read(AccountStamp.Sign(key, 42, 1_000_060_000, "777", ""), key, 1_000_000_000) == new VerifiedAccount(42, "777", ""));
    Assert(AccountStamp.Read(stamp, key, 1_000_060_001) == null);            // expired
    Assert(AccountStamp.Read(stamp, "another-key-0123456789abcdefghijk", 1_000_000_000) == null);
    Assert(AccountStamp.Read(stamp.Replace("42.", "43."), key, 1_000_000_000) == null);
    Assert(AccountStamp.Read(stamp.Replace(".777.", ".778."), key, 1_000_000_000) == null);              // another account
    var named = stamp.Split('.');
    named[3] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("Eve Friend")).TrimEnd('=');
    Assert(AccountStamp.Read(string.Join('.', named), key, 1_000_000_000) == null);                      // another name
    Assert(AccountStamp.Read(AccountStamp.Sign(key, 42, 1_000_060_000, "x7", "Zoe"), key, 1_000_000_000) == null); // not an account id
    Assert(AccountStamp.Read(AccountStamp.Sign(key, 42, 9_000_000_000, "777", "Zoe"), key, 1_000_000_000) == null); // too far ahead
    Assert(AccountStamp.Read(null, key, 0) == null && AccountStamp.Read("x.y.z", key, 0) == null && AccountStamp.Read("1.2.3.!!.5", key, 0) == null);
});
await CheckAsync("With accounts on, the relay admits only the character the edge verified", async () =>
{
    const string relayKey = "account-test-recovery-key-0123456789";
    await using var accountRelay = RelayApp.Build(["--urls", "http://127.0.0.1:0", "--HAVEN_RECOVERY_KEY", relayKey, "--HAVEN_ACCOUNTS", "required"]);
    await accountRelay.StartAsync();
    var at = new Uri(accountRelay.Urls.Single().Replace("http://", "ws://") + "/");
    var soon = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 30_000;
    string Refusal(FrameHeader.Parsed frame) => Decode<ErrorPayload>(frame.Payload).Message;
    using (var unsigned = await Client.Connect(at, Join(781, "Unsigned", true) with { RoomPassword = "accounts-room-code" }))
        Assert(Refusal(await unsigned.Expect(WireKind.Error)).Contains("Sign in with Discord"));
    using (var impostor = await Client.Connect(at, Join(782, "Impostor", true) with { RoomPassword = "accounts-room-code" }, AccountStamp.Sign(relayKey, 783, soon, "9001", "Impostor")))
        Assert(Refusal(await impostor.Expect(WireKind.Error)).Contains("different character"));
    using (var forged = await Client.Connect(at, Join(784, "Forged", true) with { RoomPassword = "accounts-room-code" }, AccountStamp.Sign("not-the-relay-key-0123456789abcdef", 784, soon, "9001", "Forged")))
        Assert(Refusal(await forged.Expect(WireKind.Error)).Contains("Sign in with Discord"));
    using var signed = await Client.Connect(at, Join(785, "Signed", true) with { RoomPassword = "accounts-room-code" }, AccountStamp.Sign(relayKey, 785, soon, "9001", "Bound Name"));
    await signed.Expect(WireKind.RoomJoined);
    // Others see the name the character is bound to its account with, not the one its client claims.
    using (var friend = await Client.Connect(at, Join(786, "Signed", false) with { RoomPassword = "accounts-room-code", NearbyContentIds = [785] },
        AccountStamp.Sign(relayKey, 786, soon, "9002", "Friend Bound")))
    {
        var joined = Decode<PeerJoinedPayload>((await friend.Expect(WireKind.PeerJoined)).Payload);
        Assert(joined.ContentId == 785 && joined.CharacterName == "Bound Name");
        await friend.Send(WireKind.SessionEnd, []);
    }
    // Gatherings are limited per account, whichever character or address opens them: three, then a refusal.
    List<Client> more = [];
    try
    {
        for (var i = 0; i < 2; i++)
        {
            var other = await Client.Connect(at, Join(790UL + (ulong)i, "Alt", true) with { RoomPassword = $"accounts-room-{i}-code" }, AccountStamp.Sign(relayKey, 790UL + (ulong)i, soon, "9001", "Alt"));
            more.Add(other);
            await other.Expect(WireKind.RoomJoined);
        }
        using var fourth = await Client.Connect(at, Join(795, "Alt", true) with { RoomPassword = "accounts-room-9-code" }, AccountStamp.Sign(relayKey, 795, soon, "9001", "Alt"));
        Assert(Refusal(await fourth.Expect(WireKind.Error)).Contains("on your account"));
    }
    finally { foreach (var other in more) { await other.Send(WireKind.SessionEnd, []); other.Dispose(); } }
    await signed.Send(WireKind.SessionEnd, []);
});
await CheckAsync("Content tickets: owner-only uploads and ready-gated downloads for pictures the home uses", async () =>
{
    const string contentKey = "content-test-key-0123456789abcdefghij";
    await using var contentRelay = RelayApp.Build(["--urls", "http://127.0.0.1:0", "--HAVEN_CONTENT_KEY", contentKey]);
    await contentRelay.StartAsync();
    var contentEndpoint = new Uri(contentRelay.Urls.Single().Replace("http://", "ws://") + "/");
    var picture = new string('e', 64);
    var song = new string('5', 64);
    var furnishing = new string('7', 64);
    var framed = original with { Furnishings = [original.Furnishings[0] with { CustomPicture = picture }, original.Furnishings[0] with { Id = Guid.NewGuid(), CustomFurniture = furnishing }, .. original.Furnishings.Skip(1)],
        Music = original.Music with { Playlist = [new MusicTrack { CustomMusic = song, Title = "Rainy Day", Milliseconds = 183_000 }] } };
    var ownerSecret = ContentLocker.NewSecret();
    using var owner = await Client.Connect(contentEndpoint, Join(661, "PictureOwner", true) with { RoomPassword = "picture-room-code", ContentSecret = ownerSecret });
    await owner.Expect(WireKind.RoomJoined);
    using var guest = await Client.Connect(contentEndpoint, Join(662, "PictureGuest", false) with { RoomPassword = "picture-room-code", NearbyContentIds = [661] });
    await guest.Expect(WireKind.RoomJoined);
    await owner.Send(HousingWire.CompressedSnapshot, HousingSnapshotCodec.Encode(framed)); await owner.Ready(framed);
    await guest.Expect(HousingWire.Snapshot);
    async Task<ContentTicket> Ask(Client client, ContentTicketRequest request)
    { await client.Send(ContentWire.TicketRequest, ContentWire.Encode(request)); return ContentWire.DecodeTicket((await client.Expect(ContentWire.Ticket)).Payload); }
    // Uploads: the owner, a picture the home uses, an accepted agreement.
    var upload = await Ask(owner, new("upload", picture, 4096, ContentLocker.AgreementVersion));
    Assert(upload.Error == null && upload.Path!.StartsWith($"/content/{picture}?op=put&size=4096&owner={ContentLocker.OwnerKey(contentKey, 661, ownerSecret)}&agreement=1&exp="));
    Assert((await Ask(owner, new("upload", new string('f', 64), 4096, 1))).Error != null);
    Assert((await Ask(owner, new("upload", picture, 4096, 0))).Error != null);
    Assert((await Ask(guest, new("upload", picture, 4096, 1))).Error != null);
    // Downloads: only once the guest's scene is ready.
    Assert((await Ask(guest, new("download", picture))).Error != null);
    await guest.Ready(framed);
    var download = await Ask(guest, new("download", picture));
    var query = System.Web.HttpUtility.ParseQueryString(new Uri("https://relay.example" + download.Path).Query);
    Assert(download.Error == null && query["sig"] == ContentLocker.Sign(contentKey, ContentLocker.Canonical("get", picture, 0, "", 0, long.Parse(query["exp"]!))));
    // Songs: only those in the home's playlist, signed as songs; a picture's fingerprint is not a song, nor the reverse.
    var songUpload = await Ask(owner, new("upload", song, ContentLocker.MaxMusicBytes, 1, Media: ContentLocker.Music));
    Assert(songUpload.Error == null && songUpload.Path!.Contains("&media=music&sig="));
    Assert((await Ask(owner, new("upload", picture, 4096, 1, Media: ContentLocker.Music))).Error == ContentLocker.NotInHome);
    Assert((await Ask(owner, new("upload", song, 4096, 1))).Error == ContentLocker.NotInHome);
    // Furniture: only packages the home's furnishings use.
    Assert((await Ask(owner, new("upload", furnishing, 2_000_000, 1, Media: ContentLocker.FurnitureMedia))).Path!.Contains("&media=furniture&sig="));
    Assert((await Ask(owner, new("upload", song, 2_000_000, 1, Media: ContentLocker.FurnitureMedia))).Error == ContentLocker.NotInHome);
    Assert((await Ask(guest, new("download", furnishing, Media: ContentLocker.FurnitureMedia))).Error == null);
    var songDownload = await Ask(guest, new("download", song, Media: ContentLocker.Music));
    var songQuery = System.Web.HttpUtility.ParseQueryString(new Uri("https://relay.example" + songDownload.Path).Query);
    Assert(songDownload.Error == null && songQuery["media"] == "music"
        && songQuery["sig"] == ContentLocker.Sign(contentKey, ContentLocker.Canonical("get", song, 0, "", 0, long.Parse(songQuery["exp"]!), media: ContentLocker.Music)));
    // A relay without the key refuses every ticket.
    await using var keylessRelay = RelayApp.Build(["--urls", "http://127.0.0.1:0"]);
    await keylessRelay.StartAsync();
    using var keyless = await Client.Connect(new Uri(keylessRelay.Urls.Single().Replace("http://", "ws://") + "/"),
        Join(663, "KeylessOwner", true) with { RoomPassword = "keyless-picture-code", ContentSecret = ContentLocker.NewSecret() });
    await keyless.Expect(WireKind.RoomJoined);
    await keyless.Send(HousingWire.CompressedSnapshot, HousingSnapshotCodec.Encode(framed)); await keyless.Ready(framed);
    Assert((await Ask(keyless, new("upload", picture, 4096, 1))).Error != null);
    await keyless.Send(WireKind.SessionEnd, []);
    // In a gathering, sharing is refused: it goes through a content session.
    Assert((await Ask(owner, new("share", picture, Agreement: 1))).Error != null);
    Assert((await Ask(owner, new("delete", picture))).Error != null);
    await owner.Send(WireKind.SessionEnd, []);
    // A content session: no room, tickets for the session's own character only, any picture; downloads stay in
    // gatherings, and an older Haven is told to update.
    var sessionSecret = ContentLocker.NewSecret();
    async Task<System.Net.WebSockets.ClientWebSocket> OpenSession(ulong id, int revision, string? secret = null)
    {
        var socket = new System.Net.WebSockets.ClientWebSocket();
        await socket.ConnectAsync(contentEndpoint, CancellationToken.None);
        await socket.SendAsync(FrameHeader.BuildUpleg(ContentWire.Session, 0, ContentWire.Encode(new ContentSession(id, revision, secret ?? sessionSecret))), System.Net.WebSockets.WebSocketMessageType.Binary, true, CancellationToken.None);
        return socket;
    }
    async Task<ContentTicket> Reply(System.Net.WebSockets.ClientWebSocket socket)
    {
        var buffer = new byte[8192];
        var result = await socket.ReceiveAsync(buffer, new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        var frame = FrameHeader.ParseDownleg(buffer[..result.Count]);
        Assert(frame.Ok && frame.Kind == ContentWire.Ticket);
        return ContentWire.DecodeTicket(frame.Payload);
    }
    async Task<ContentTicket> AskSession(System.Net.WebSockets.ClientWebSocket socket, ContentTicketRequest request)
    {
        await socket.SendAsync(FrameHeader.BuildUpleg(ContentWire.TicketRequest, 0, ContentWire.Encode(request)), System.Net.WebSockets.WebSocketMessageType.Binary, true, CancellationToken.None);
        return await Reply(socket);
    }
    using (var session = await OpenSession(665, HousingWire.ProtocolRevision))
    {
        var anyPicture = new string('9', 64);
        var sessionUpload = await AskSession(session, new("upload", anyPicture, 2048, ContentLocker.AgreementVersion));
        Assert(sessionUpload.Error == null && sessionUpload.Path!.Contains($"?op=put&size=2048&owner={ContentLocker.OwnerKey(contentKey, 665, sessionSecret)}&agreement=1&"));
        // The community gallery closed in 0.38.0: an older Haven asking to share or unshare is told so, with no ticket.
        var share = await AskSession(session, new("share", anyPicture, Agreement: ContentLocker.AgreementVersion));
        Assert(share.Path == null && share.Error == ContentLocker.GalleryClosed);
        Assert((await AskSession(session, new("unshare", anyPicture))).Error == ContentLocker.GalleryClosed);
        Assert((await AskSession(session, new("delete", anyPicture))).Path!.Contains($"?op=delete&size=0&owner={ContentLocker.OwnerKey(contentKey, 665, sessionSecret)}&"));
        Assert((await AskSession(session, new("download", anyPicture))).Error != null);
        Assert((await AskSession(session, new("upload", anyPicture, 2048, 0))).Error != null);
        Assert((await AskSession(session, new("share", anyPicture))).Error != null);
    }
    // A session without a secret (an older Haven) gets no tickets: the content ID alone proves nothing.
    using (var secretless = await OpenSession(667, HousingWire.ProtocolRevision, ""))
        Assert((await AskSession(secretless, new("upload", new string('9', 64), 2048, ContentLocker.AgreementVersion))).Error!.Contains("Update Haven"));
    using (var outdated = await OpenSession(666, HousingWire.ProtocolRevision - 1))
        Assert((await Reply(outdated)).Error!.Contains($"revision {HousingWire.ProtocolRevision}"));
    if (hosted) await LiveContentCheck(endpoint, original);
});
await CheckAsync("Switch sides are ready-gated, limited to the home, stamped and replayed to late joiners", async () =>
{
    using var switchHost = await Client.Connect(endpoint, Join(641, "SwitchHost", true) with { RoomPassword = "switch-room-code" });
    await switchHost.Expect(WireKind.RoomJoined);
    using var switchGuest = await Client.Connect(endpoint, Join(642, "SwitchGuest", false) with { RoomPassword = "switch-room-code", NearbyContentIds = [641] });
    var switchGuestId = Decode<RoomJoinedPayload>((await switchGuest.Expect(WireKind.RoomJoined)).Payload).AssignedPeerId;
    await switchHost.Send(HousingWire.CompressedSnapshot, HousingSnapshotCodec.Encode(original)); await switchHost.Ready(original);
    await switchGuest.Expect(HousingWire.Snapshot);
    var lamp = original.Furnishings[0].Id;
    await switchGuest.Send(HousingWire.SwitchState, SwitchWire.Encode([new(lamp, false)]));
    await switchHost.Send(WireKind.Ping, []); await switchHost.ExpectWithout(WireKind.Pong, HousingWire.SwitchState);
    await switchGuest.Ready(original);
    await switchGuest.Send(HousingWire.SwitchState, SwitchWire.Encode([new(lamp, false), new(Guid.NewGuid(), true)]));
    var delivered = await switchHost.Expect(HousingWire.SwitchState);
    Assert(delivered.SenderId == switchGuestId && SwitchWire.Decode(delivered.Payload).SequenceEqual([new SwitchState(lamp, false)]));
    await switchGuest.Send(WireKind.Ping, []); await switchGuest.ExpectWithout(WireKind.Pong, HousingWire.SwitchState);
    using var switchLate = await Client.Connect(endpoint, Join(643, "SwitchLate", false) with { RoomPassword = "switch-room-code", NearbyContentIds = [641] });
    await switchLate.Expect(WireKind.RoomJoined); await switchLate.Expect(HousingWire.Snapshot); await switchLate.Ready(original);
    Assert(SwitchWire.Decode((await switchLate.Expect(HousingWire.SwitchState)).Payload).SequenceEqual([new SwitchState(lamp, false)]));
    // A guest's frame carries one switch (a click); only the owner's carries many (its saved sides).
    if (original.Furnishings.Length > 1)
    {
        await switchGuest.Send(HousingWire.SwitchState, SwitchWire.Encode([new(lamp, true), new(original.Furnishings[1].Id, true)]));
        Assert(SwitchWire.Decode((await switchHost.Expect(HousingWire.SwitchState)).Payload).SequenceEqual([new SwitchState(lamp, true)]));
    }
    await switchHost.Send(WireKind.SessionEnd, []);
});
Check("Relay notices are bounded, update notices skip clients already updated, and countdowns read as minutes", () =>
{
    var maintenance = new RelayNotice("m1", "maintenance", "Restarting soon.", 600_000);
    Assert(NoticeWire.Decode(NoticeWire.Encode(maintenance)) == maintenance);
    Assert(!NoticeWire.Valid(maintenance with { Kind = "shout" }) && !NoticeWire.Valid(maintenance with { Text = new string('x', 501) }) && !NoticeWire.Valid(maintenance with { EndsInMs = -1 })
        && !NoticeWire.Valid(maintenance with { Id = "bad id!" }) && !NoticeWire.Valid(maintenance with { Version = "0.45.0" }) && NoticeWire.Valid(new("c", "clear", "")));
    var update = new RelayNotice("u1", "update", "Update after you leave.", 0, "0.45.0");
    Assert(NoticeWire.Concerns(update, new(0, 44, 1)) && !NoticeWire.Concerns(update, new(0, 45, 0)) && NoticeWire.Concerns(maintenance, new(9, 9, 9)));
    Assert(NoticeWire.Countdown(TimeSpan.FromSeconds(245)) == "4:05" && NoticeWire.Countdown(TimeSpan.FromSeconds(3750)) == "1:02:30" && NoticeWire.Countdown(TimeSpan.Zero) == "now");
});
if (!hosted)
    await CheckAsync("Relay notices reach everyone connected and late arrivals, only through the edge's secret", async () =>
    {
        const string key = "notice-test-recovery-key-0123456789abcdef";
        await using var noticeRelay = RelayApp.Build(["--urls", "http://127.0.0.1:0", "--HAVEN_RECOVERY_KEY", key]);
        await noticeRelay.StartAsync();
        var address = noticeRelay.Urls.Single();
        var noticeEndpoint = new Uri(address.Replace("http://", "ws://") + "/");
        using var http = new HttpClient();
        using var noticeHost = await Client.Connect(noticeEndpoint, Join(691, "NoticeHost", true) with { RoomPassword = "notice-room-code" });
        await noticeHost.Expect(WireKind.RoomJoined);
        await noticeHost.Send(HousingWire.CompressedSnapshot, HousingSnapshotCodec.Encode(original)); await noticeHost.Ready(original);
        var posted = new RelayNotice("maint-1", "maintenance", "The relay restarts when the countdown ends.", 600_000);
        // Without the edge's secret the route doesn't exist.
        using (var denied = await http.PostAsync(address + "/internal/notice", new StringContent(System.Text.Json.JsonSerializer.Serialize(posted, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json")))
            Assert(denied.StatusCode == System.Net.HttpStatusCode.NotFound);
        HttpRequestMessage Edge(HttpMethod method, RelayNotice? body = null)
        {
            var request = new HttpRequestMessage(method, address + "/internal/notice");
            request.Headers.Add("X-Haven-Recovery", key);
            if (body != null) request.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json");
            return request;
        }
        using (var bad = await http.SendAsync(Edge(HttpMethod.Post, posted with { Kind = "shout" }))) Assert(bad.StatusCode == System.Net.HttpStatusCode.BadRequest);
        using (var ok = await http.SendAsync(Edge(HttpMethod.Post, posted))) Assert(ok.IsSuccessStatusCode);
        var received = NoticeWire.Decode((await noticeHost.Expect(HousingWire.Notice)).Payload);
        Assert(received.Id == "maint-1" && received.Kind == "maintenance" && received.EndsInMs is > 590_000 and <= 600_000);
        // Someone arriving later gets the standing notice once ready, its countdown measured then.
        using var noticeGuest = await Client.Connect(noticeEndpoint, Join(692, "NoticeGuest", false) with { RoomPassword = "notice-room-code", NearbyContentIds = [691] });
        await noticeGuest.Expect(WireKind.RoomJoined); await noticeGuest.Expect(HousingWire.Snapshot); await noticeGuest.Ready(original);
        Assert(NoticeWire.Decode((await noticeGuest.Expect(HousingWire.Notice)).Payload).Id == "maint-1");
        using (var standing = await http.SendAsync(Edge(HttpMethod.Get))) Assert((await standing.Content.ReadAsStringAsync()).Contains("maint-1"));
        // Withdrawn: everyone is told.
        using (var cleared = await http.SendAsync(Edge(HttpMethod.Delete))) Assert(cleared.IsSuccessStatusCode);
        Assert(NoticeWire.Decode((await noticeHost.Expect(HousingWire.Notice)).Payload).Kind == "clear");
        Assert(NoticeWire.Decode((await noticeGuest.Expect(HousingWire.Notice)).Payload).Kind == "clear");
    });
await CheckAsync("Pose entries are ready-gated, limited to the home's sets, stamped, refuse a held ball and reach late joiners", async () =>
{
    var pair = new PoseSet { Name = "Pair", Roles = [new PoseRole { Emote = 88 }, new PoseRole { Emote = 88 }] };
    var posed = original with { Furnishings = [original.Furnishings[0] with { Poses = [pair] }] };
    var bed = posed.Furnishings[0].Id;
    using var poseHost = await Client.Connect(endpoint, Join(681, "PoseHost", true) with { RoomPassword = "pose-room-code" });
    await poseHost.Expect(WireKind.RoomJoined);
    using var poseGuest = await Client.Connect(endpoint, Join(682, "PoseGuest", false) with { RoomPassword = "pose-room-code", NearbyContentIds = [681] });
    await poseGuest.Expect(WireKind.RoomJoined);
    await poseHost.Send(HousingWire.CompressedSnapshot, HousingSnapshotCodec.Encode(posed)); await poseHost.Ready(posed);
    await poseGuest.Expect(HousingWire.Snapshot);
    // Not ready yet: ignored.
    await poseGuest.Send(HousingWire.PoseState, PoseWire.Encode([new(682, bed, pair.Id, 1, 1)]));
    await poseGuest.Send(WireKind.Ping, []); await poseGuest.ExpectWithout(WireKind.Pong, HousingWire.PoseState);
    await poseGuest.Ready(posed);
    // The relay stamps the sender's own character, and echoes to the sender too.
    await poseHost.Send(HousingWire.PoseState, PoseWire.Encode([new(999, bed, pair.Id, 0, 1.2f)]));
    Assert(PoseWire.Decode((await poseGuest.Expect(HousingWire.PoseState)).Payload).Single() == new PoseEntry(681, bed, pair.Id, 0, 1.2f));
    Assert(PoseWire.Decode((await poseHost.Expect(HousingWire.PoseState)).Payload).Single() == new PoseEntry(681, bed, pair.Id, 0, 1.2f));
    // A held ball is refused: the claim becomes a viewer's entry, the claimant told so.
    await poseGuest.Send(HousingWire.PoseState, PoseWire.Encode([new(682, bed, pair.Id, 0, 1)]));
    Assert(PoseWire.Decode((await poseGuest.Expect(HousingWire.PoseState)).Payload).Single() == new PoseEntry(682, bed, pair.Id, PoseEntry.Viewing, 1));
    await poseHost.Expect(HousingWire.PoseState);
    // A set the home doesn't offer clears the entry.
    await poseGuest.Send(HousingWire.PoseState, PoseWire.Encode([new(682, bed, Guid.NewGuid(), 1, 1)]));
    Assert(PoseWire.Decode((await poseGuest.Expect(HousingWire.PoseState)).Payload).Single() == PoseEntry.None(682));
    await poseHost.Expect(HousingWire.PoseState);
    await poseGuest.Send(HousingWire.PoseState, PoseWire.Encode([new(682, bed, pair.Id, 1, .9f)]));
    await poseHost.Expect(HousingWire.PoseState); await poseGuest.Expect(HousingWire.PoseState);
    using var poseLate = await Client.Connect(endpoint, Join(683, "PoseLate", false) with { RoomPassword = "pose-room-code", NearbyContentIds = [681] });
    await poseLate.Expect(WireKind.RoomJoined); await poseLate.Expect(HousingWire.Snapshot); await poseLate.Ready(posed);
    var table = PoseWire.Decode((await poseLate.Expect(HousingWire.PoseState)).Payload);
    Assert(table.Length == 2 && PoseSessions.Playing(table, bed, pair) && table.Contains(new PoseEntry(682, bed, pair.Id, 1, .9f)));
    // A player on the set adjusts their partner: only the partner gets it, stamped with the sender's character.
    var adjust = new PoseAdjust(999, 682, bed, pair.Id, 1, new(.1f, -.3f, .2f), 15, 5, -5, 1.1f);
    await poseHost.Send(HousingWire.PoseAdjust, adjust.Encode());
    Assert(PoseAdjust.Decode((await poseGuest.Expect(HousingWire.PoseAdjust)).Payload) == adjust with { Sender = 681 });
    await poseLate.Send(WireKind.Ping, []); await poseLate.ExpectWithout(WireKind.Pong, HousingWire.PoseAdjust);
    // Someone not on a ball, or a ball the target doesn't hold, moves nobody.
    await poseLate.Send(HousingWire.PoseAdjust, adjust.Encode());
    await poseHost.Send(HousingWire.PoseAdjust, (adjust with { Role = 0 }).Encode());
    await poseGuest.Send(WireKind.Ping, []); await poseGuest.ExpectWithout(WireKind.Pong, HousingWire.PoseAdjust);
    // Someone neither decorating nor on the set can't hide its balls.
    await poseLate.Send(HousingWire.PoseClose, new PoseClose(bed, pair.Id).Encode());
    await poseLate.Send(WireKind.Ping, []); await poseLate.ExpectWithout(WireKind.Pong, HousingWire.PoseClose);
    // Someone on the set hides the balls for everyone: the close reaches every ready participant and the table forgets the set.
    await poseGuest.Send(HousingWire.PoseClose, new PoseClose(bed, pair.Id).Encode());
    Assert(PoseClose.Decode((await poseHost.Expect(HousingWire.PoseClose)).Payload) == new PoseClose(bed, pair.Id));
    await poseGuest.Expect(HousingWire.PoseClose); await poseLate.Expect(HousingWire.PoseClose);
    // Taken again afterwards, so the drop-out below still has a ball to free.
    await poseGuest.Send(HousingWire.PoseState, PoseWire.Encode([new(682, bed, pair.Id, 1, .9f)]));
    await poseHost.Expect(HousingWire.PoseState); await poseGuest.Expect(HousingWire.PoseState); await poseLate.Expect(HousingWire.PoseState);
    // Dropping out (the connection lost, a seat kept for a resume) frees the ball for everyone at once.
    poseGuest.Dispose();
    Assert(PoseWire.Decode((await poseHost.Expect(HousingWire.PoseState)).Payload).Single() == PoseEntry.None(682));
    await poseHost.Send(WireKind.SessionEnd, []);
});
await CheckAsync("Minigame messages are ready-gated, stamped with the sender and reach only their target in the room", async () =>
{
    using var gameHost = await Client.Connect(endpoint, Join(701, "GameHost", true) with { RoomPassword = "game-room-code" });
    await gameHost.Expect(WireKind.RoomJoined);
    using var gameGuest = await Client.Connect(endpoint, Join(702, "GameGuest", false) with { RoomPassword = "game-room-code", NearbyContentIds = [701] });
    await gameGuest.Expect(WireKind.RoomJoined);
    using var gameOther = await Client.Connect(endpoint, Join(703, "GameOther", false) with { RoomPassword = "game-room-code", NearbyContentIds = [701] });
    await gameOther.Expect(WireKind.RoomJoined);
    await gameHost.Send(HousingWire.CompressedSnapshot, HousingSnapshotCodec.Encode(original)); await gameHost.Ready(original);
    await gameGuest.Expect(HousingWire.Snapshot); await gameOther.Expect(HousingWire.Snapshot);
    var match = Guid.NewGuid();
    var invite = new GameMessage(999, 702, "triad", "invite", match, [(int)TriadRule.Same]);
    // Not ready yet: nothing goes.
    await gameHost.Send(HousingWire.MiniGame, invite.Encode());
    await gameGuest.Send(WireKind.Ping, []); await gameGuest.ExpectWithout(WireKind.Pong, HousingWire.MiniGame);
    await gameGuest.Ready(original); await gameOther.Ready(original);
    // Ready: the target gets it with the sender's own character; the third participant gets nothing.
    await gameHost.Send(HousingWire.MiniGame, invite.Encode());
    var received = GameMessage.Decode((await gameGuest.Expect(HousingWire.MiniGame)).Payload);
    Assert(received.Sender == 701 && received.Target == 702 && received.Match == match && received.Values!.SequenceEqual([(int)TriadRule.Same]));
    await gameOther.Send(WireKind.Ping, []); await gameOther.ExpectWithout(WireKind.Pong, HousingWire.MiniGame);
    // The answer goes back the same way; a message it can't read goes nowhere.
    await gameGuest.Send(HousingWire.MiniGame, new GameMessage(0, 701, "triad", "decline", match).Encode());
    Assert(GameMessage.Decode((await gameHost.Expect(HousingWire.MiniGame)).Payload) is { Sender: 702, Kind: "decline" });
    // A machine's result or an arcade player's swing goes to everyone else in the room, never back to the sender.
    var furnishing = Guid.NewGuid();
    await gameHost.Send(HousingWire.MiniGame, new GameMessage(0, GameMessage.Everyone, "machine", "play", furnishing, [13]).Encode());
    Assert(GameMessage.Decode((await gameGuest.Expect(HousingWire.MiniGame)).Payload) is { Sender: 701, Game: "machine", Kind: "play" } played && played.Match == furnishing);
    Assert(GameMessage.Decode((await gameOther.Expect(HousingWire.MiniGame)).Payload) is { Sender: 701, Game: "machine" });
    await gameHost.Send(WireKind.Ping, []); await gameHost.ExpectWithout(WireKind.Pong, HousingWire.MiniGame);
    await gameGuest.Send(HousingWire.MiniGame, System.Text.Encoding.UTF8.GetBytes("{\"Game\":\"chess\"}"));
    await gameHost.Send(WireKind.Ping, []); await gameHost.ExpectWithout(WireKind.Pong, HousingWire.MiniGame);
    await gameHost.Send(WireKind.SessionEnd, []);
});
await CheckAsync("Typing is ready-gated, stamped, isolated, ephemeral and cleared by chat", async () =>
{
    using var typingHost = await Client.Connect(endpoint, Join(621, "TypingHost", true) with { RoomPassword = "typing-room-code" });
    await typingHost.Expect(WireKind.RoomJoined);
    using var typingGuest = await Client.Connect(endpoint, Join(622, "TypingGuest", false) with { RoomPassword = "typing-room-code", NearbyContentIds = [621] });
    var guestId = Decode<RoomJoinedPayload>((await typingGuest.Expect(WireKind.RoomJoined)).Payload).AssignedPeerId;
    using var other = await Client.Connect(endpoint, Join(623, "TypingOther", true) with { RoomPassword = "other-typing-room-code" });
    await other.Expect(WireKind.RoomJoined);
    await other.Send(HousingWire.CompressedSnapshot, HousingSnapshotCodec.Encode(original)); await other.Ready(original);
    await typingHost.Send(HousingWire.CompressedSnapshot, HousingSnapshotCodec.Encode(original)); await typingHost.Ready(original);
    await typingGuest.Expect(HousingWire.Snapshot);
    await typingGuest.Send(HousingWire.Typing, [1]);
    await typingGuest.Send(WireKind.Ping, []); await typingGuest.Expect(WireKind.Pong);
    await typingHost.Send(WireKind.Ping, []); await typingHost.ExpectWithout(WireKind.Pong, HousingWire.Typing);
    await typingHost.Send(HousingWire.Typing, [1]);
    await typingHost.Send(WireKind.Ping, []); await typingHost.Expect(WireKind.Pong);
    await typingGuest.Ready(original);
    await typingGuest.Send(WireKind.Ping, []); await typingGuest.ExpectWithout(WireKind.Pong, HousingWire.Typing);
    foreach (var state in new byte[] { 1, 2, 0, 1 })
    {
        await typingGuest.Send(HousingWire.Typing, [state]);
        var delivered = await typingHost.Expect(HousingWire.Typing);
        Assert(delivered.SenderId == guestId && delivered.Payload.Length == 1 && delivered.Payload[0] == state);
    }
    await typingGuest.Send(WireKind.Ping, []); await typingGuest.ExpectWithout(WireKind.Pong, HousingWire.Typing);
    await other.Send(WireKind.Ping, []); await other.ExpectWithout(WireKind.Pong, HousingWire.Typing);
    using var newcomer = await Client.Connect(endpoint, Join(624, "TypingLate", false) with { RoomPassword = "typing-room-code", NearbyContentIds = [621] });
    await newcomer.Expect(WireKind.RoomJoined); await newcomer.Expect(HousingWire.Snapshot); await newcomer.Ready(original);
    await newcomer.Send(WireKind.Ping, []); await newcomer.ExpectWithout(WireKind.Pong, HousingWire.Typing);
    await typingGuest.Send(HousingWire.RoomChat, RoomChat.Encode(new(10, "finished typing")));
    Assert(TypingWire.Decode((await typingHost.Expect(HousingWire.Typing)).Payload) == TypingState.Idle);
    await typingHost.Expect(HousingWire.RoomChat);
    await typingHost.Send(WireKind.SessionEnd, []);
    await other.Send(WireKind.SessionEnd, []);
});
await CheckAsync("Relay rejects typing frames containing draft text or invalid states", async () =>
{
    foreach (var payload in new byte[][] { [3], [1, 2], Encoding.UTF8.GetBytes("private draft") })
    {
        using var bad = await Client.Connect(endpoint, Join(631, "BadTyping", true) with { RoomPassword = "invalid-typing-room-code" });
        await bad.Expect(WireKind.RoomJoined);
        await bad.Send(HousingWire.Typing, payload);
        await bad.ExpectClose();
    }
});
await CheckAsync("Malformed private chat is rejected by the relay", async () =>
{
    using var bad = await Client.Connect(endpoint, Join(611, "BadChat", true) with { RoomPassword = "invalid-chat-room-code" });
    await bad.Expect(WireKind.RoomJoined);
    await bad.Send(HousingWire.CompressedSnapshot, HousingSnapshotCodec.Encode(original)); await bad.Ready(original);
    await bad.Send(HousingWire.RoomChat, JsonSerializer.SerializeToUtf8Bytes(new RoomChatMessage(12, "World tells are not a house channel")));
    await bad.ExpectClose();
});

using var late = await Client.Connect(endpoint, Join(103, "Late", false));
await late.Expect(WireKind.RoomJoined);
await CheckAsync("An abandoned unpublished lobby cannot lock its former host out of a fresh gathering", async () =>
{
    using (var abandoned = await Client.Connect(endpoint, Join(751, "AbandonedHost", true) with { RoomPassword = "abandoned-unpublished-home" }))
        await abandoned.Expect(WireKind.RoomJoined);
    await Task.Delay(300); // Allow the transport close to be observed, without an explicit LeaveRoom.
    using var replacement = await Client.Connect(endpoint, Join(751, "AbandonedHost", true) with { RoomPassword = "abandoned-unpublished-home" });
    await replacement.Expect(WireKind.RoomJoined);
    await replacement.Send(WireKind.SessionEnd, []);
});
await CheckAsync("A published offline home keeps its authenticated recovery reservation", async () =>
{
    RoomJoinedPayload credentials;
    using (var saved = await Client.Connect(endpoint, Join(753, "SavedHost", true) with { RoomPassword = "published-preserved-home-clean" }))
    {
        credentials = Decode<RoomJoinedPayload>((await saved.Expect(WireKind.RoomJoined)).Payload);
        await saved.Send(HousingWire.CompressedSnapshot, HousingSnapshotCodec.Encode(original)); await saved.Ready(original);
    }
    await Task.Delay(300);
    using var replacement = await Client.Connect(endpoint, Join(753, "SavedHost", true) with { RoomPassword = "published-preserved-home-clean" });
    Assert(Decode<ErrorPayload>((await replacement.Expect(WireKind.Error)).Payload).Code == ErrCode.AlreadyHosting);
    // The legitimate owner can still resume and end it; keep the shared test relay clean.
    using var resumed = await Client.Connect(endpoint, Join(753, "SavedHost", false) with { RoomPassword = "published-preserved-home-clean",
        RoomId = credentials.RoomId, ResumeToken = credentials.ResumeToken });
    await resumed.Expect(WireKind.RoomJoined);
    await resumed.Send(WireKind.SessionEnd, []);
});
await CheckAsync("A participant who resumes after a dropped connection gets the room chat it missed", async () =>
{
    var password = "chat-replay-" + Guid.NewGuid().ToString("N")[..8];
    using var replayHost = await Client.Connect(endpoint, Join(761, "ReplayHost", true) with { RoomPassword = password });
    await replayHost.Expect(WireKind.RoomJoined);
    await replayHost.Send(HousingWire.CompressedSnapshot, HousingSnapshotCodec.Encode(original)); await replayHost.Ready(original);
    RoomJoinedPayload credentials;
    using (var guest = await Client.Connect(endpoint, Join(762, "ReplayGuest", false) with { RoomPassword = password, NearbyContentIds = [761] }))
    {
        credentials = Decode<RoomJoinedPayload>((await guest.Expect(WireKind.RoomJoined)).Payload);
        await guest.Ready(original);
    }
    await Task.Delay(300); // The relay notices the dropped connection and reserves the seat.
    await replayHost.Send(HousingWire.RoomChat, RoomChat.Encode(new RoomChatMessage(10, "Said while you were away")));
    await replayHost.Expect(HousingWire.RoomChat);
    using var back = await Client.Connect(endpoint, Join(762, "ReplayGuest", false) with { RoomPassword = password, NearbyContentIds = [761],
        RoomId = credentials.RoomId, ResumeToken = credentials.ResumeToken });
    await back.Expect(WireKind.RoomJoined);
    await back.Ready(original);
    var missed = RoomChat.Decode((await back.Expect(HousingWire.RoomChatReplay)).Payload);
    Assert(missed.Text == "Said while you were away" && missed.Name == "ReplayHost" && missed.Channel == 10);
    await replayHost.Send(WireKind.SessionEnd, []);
});
await CheckAsync("A batching client receives frames packed together, with only the newest movement per sender", async () =>
{
    var password = "batching-" + Guid.NewGuid().ToString("N")[..8];
    using var batchHost = await Client.Connect(endpoint, Join(771, "BatchHost", true) with { RoomPassword = password });
    await batchHost.Expect(WireKind.RoomJoined);
    await batchHost.Send(HousingWire.CompressedSnapshot, HousingSnapshotCodec.Encode(original)); await batchHost.Ready(original);
    using var batchGuest = await Client.Connect(endpoint, Join(772, "BatchGuest", false) with { RoomPassword = password, NearbyContentIds = [771], Batching = true });
    // Its own join answers may arrive packed too: read until ready, unpacking batches.
    var frames = new List<FrameHeader.Parsed>();
    async Task Collect(byte kind)
    {
        // One batch can carry several of the frames awaited here (the join answer and the house together).
        if (frames.Any(f => f.Kind == kind)) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var frame = await batchGuest.ReadNext(timeout.Token);
            var inner = frame.Kind == HousingWire.Batch ? FrameBatch.Unpack(frame.Payload).Select(FrameHeader.ParseDownleg).ToList() : [frame];
            frames.AddRange(inner);
            if (inner.Any(f => f.Kind == kind)) return;
        }
    }
    await Collect(WireKind.RoomJoined);
    await Collect(HousingWire.CompressedSnapshot);
    await batchGuest.Send(HousingWire.SceneReady, Encoding.ASCII.GetBytes(new PublishedHouseScene(original).Hash));
    await Collect(HousingWire.SceneReadyAck);
    frames.Clear();
    var batched = false;
    for (var i = 0; i < 6; i++) await batchHost.Send(WireKind.HotUpdate, Encode(new HotPayload { Protocol = 4, SenderContentId = 771, Seq = (uint)i }));
    await batchHost.Send(HousingWire.RoomChat, RoomChat.Encode(new RoomChatMessage(10, "Packed")));
    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        while (!frames.Any(f => f.Kind == HousingWire.RoomChat))
        {
            var frame = await batchGuest.ReadNext(timeout.Token);
            batched |= frame.Kind == HousingWire.Batch;
            frames.AddRange(frame.Kind == HousingWire.Batch ? FrameBatch.Unpack(frame.Payload).Select(FrameHeader.ParseDownleg) : [frame]);
        }
    var hot = frames.Where(f => f.Kind == WireKind.HotUpdate).ToArray();
    Assert(batched && hot.Length is >= 1 and < 6 && Decode<HotPayload>(hot[^1].Payload).Seq == 5);
    Assert(RoomChat.Decode(frames.Single(f => f.Kind == HousingWire.RoomChat).Payload).Text == "Packed");
    await batchHost.Send(WireKind.SessionEnd, []);
});
await CheckAsync("Late arrivals receive the latest housing snapshot", async () =>
{
    var saved = HouseFormat.Parse((await late.Expect(HousingWire.Snapshot)).Payload);
    Assert(saved.Id == original.Id && saved.Furnishings[0].Scale == 1.5f && saved.Furnishings[0].StainId == 12 && saved.Interior.MainHallCeilingLight == 65816 && saved.Music.Volume == .25f);
});
await CheckAsync("Loading visitors wait for scene readiness then receive current held character state", async () =>
{
    using var waiting = await Client.Connect(endpoint, Join(105, "Loading", false));
    await waiting.Expect(WireKind.RoomJoined);
    await waiting.Expect(HousingWire.Snapshot);
    await waiting.Send(WireKind.HotUpdate, Encode(new HotPayload { Protocol = 4, SenderContentId = 105, X = 999 }));
    await waiting.Send(WireKind.Ping, []); await waiting.Expect(WireKind.Pong);
    await host.Send(WireKind.Ping, []);
    await host.ExpectWithout(WireKind.Pong, WireKind.HotUpdate);
    await host.Send(WireKind.HotUpdate, Encode(new HotPayload { Protocol = 4, SenderContentId = 101, X = 22, Seq = 2 }));
    await host.Send(WireKind.WarmUpdate, Encode(new WarmPayload { EmoteId = 50, CPoseState = 3, StandupTimelineId = 644, StandupEpoch = 7 }));
    await host.Send(WireKind.ColdUpdate, Encode(new ColdPayload { MonikerName = "Held appearance" }));
    await host.Send(WireKind.Ping, []); await host.Expect(WireKind.Pong);
    await waiting.Send(WireKind.Ping, []);
    await waiting.ExpectWithout(WireKind.Pong, WireKind.HotUpdate, WireKind.WarmUpdate, WireKind.ColdUpdate);
    await waiting.Send(HousingWire.SceneReady, Encoding.ASCII.GetBytes(new string('0', 64)));
    await waiting.Expect(HousingWire.Snapshot);
    await waiting.Ready(original);
    Assert(Decode<HotPayload>((await waiting.Expect(WireKind.HotUpdate)).Payload).X == 22);
    var held = Decode<WarmPayload>((await waiting.Expect(WireKind.WarmUpdate)).Payload);
    Assert(held.CPoseState == 3 && held.StandupEpoch == 7 && held.StandupTimelineId == 0);
    Assert(Decode<ColdPayload>((await waiting.Expect(WireKind.ColdUpdate)).Payload).MonikerName == "Held appearance");
    await waiting.Ready(original); // Idempotent retry must not replay state/events again.
    await waiting.Send(WireKind.Ping, []);
    await waiting.ExpectWithout(WireKind.Pong, WireKind.HotUpdate, WireKind.WarmUpdate, WireKind.ColdUpdate);
});
await CheckAsync("Host departure transfers authority and the new host can close the session", async () =>
{
    await host.Send(WireKind.LeaveRoom, []);
    PeerLeftPayload departed;
    do { departed = Decode<PeerLeftPayload>((await guest.Expect(WireKind.PeerLeft)).Payload); }
    while (departed.PeerId != hostAck.AssignedPeerId);
    Assert(departed.NewHostId == guestAck.AssignedPeerId);
    await guest.Send(WireKind.SessionEnd, []);
    await late.Expect(WireKind.SessionEnd);
});
await CheckAsync("Ended rooms cannot be rejoined by cached identity", async () =>
{
    using var stale = await Client.Connect(endpoint, Join(104, "Stale", false) with { RoomId = hostAck.RoomId });
    Assert(Decode<ErrorPayload>((await stale.Expect(WireKind.Error)).Payload).Code == ErrCode.RoomNotFound);
});
await CheckAsync("Production client reconnects with stable identity and can cancel retries without leaving stale tasks", async () => await RecoveryIntegration.ClientResume(endpoint, Sample()));
await CheckAsync("Production client replaces a live room, shares overlapping disconnects and cancels pending admission", async () => await RecoveryIntegration.IntentionalReplacement(endpoint));
await CheckAsync("Production client holds its local session if recovery credentials are rejected", async () => await RecoveryIntegration.RejectedResume(endpoint, Sample()));
if (args.Length == 0)
{
    await CheckAsync("A promoted host cannot replace a missing checkpoint scene; only the approved fingerprint restores", EditingIntegration.RestoreAuthority);
    await CheckAsync("Entire gathering restores house and credentials after a real relay process restart", RecoveryIntegration.ProcessRestart);
    await CheckAsync("Both production clients automatically recover after group loss and missing snapshot checkpoint", RecoveryIntegration.GroupResume);
}
await Check("Recovery backoff is bounded and admission wire dumps redact credentials", () =>
{
    Assert(RecoveryPolicy.RetryDelay(1, 0).TotalSeconds >= .8);
    Assert(RecoveryPolicy.RetryDelay(100, 1).TotalSeconds <= 36);
    Assert(RecoveryPolicy.SilenceTimeout(0).TotalSeconds == 30 && RecoveryPolicy.SilenceTimeout(30000).TotalSeconds == 120);
    Assert(RecoveryPolicy.WatchdogTimeout(0).TotalSeconds == 20 && RecoveryPolicy.WatchdogTimeout(5000).TotalSeconds == 40 && RecoveryPolicy.WatchdogTimeout(60000).TotalSeconds == 60);
    Assert(RecoveryPolicy.SmoothRoundTrip(0, 100) == 100 && RecoveryPolicy.SmoothRoundTrip(100, 5100) == 1100 && RecoveryPolicy.SmoothRoundTrip(100, -5) == 80);
    var token = new string('A', 64);
    var dump = HavenNative.Sync.WireDumpDecoder.DecodePayload(WireKind.RoomJoined, Encode(new RoomJoinedPayload { ResumeToken = token }));
    Assert(!dump.Contains(token));
});
await CheckAsync("Relay rejects movement carrying another character's identity", async () =>
{
    using var attacker = await Client.Connect(endpoint, Join(201, "BadIdentity", true) with { RoomPassword = "second-test-code" });
    await attacker.Expect(WireKind.RoomJoined);
    await attacker.Send(WireKind.HotUpdate, Encode(new HotPayload { Protocol = 4, SenderContentId = 999 }));
    await attacker.ExpectClose();
});
await app.StopAsync();
Console.WriteLine($"PASS: {passed} checks (including real WebSocket integration against {endpoint.Host}){(quick ? ", quick: the Odyssey AI's strength checks left out" : "")}.");

async Task Check(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); await Task.CompletedTask; }
async Task CheckAsync(string name, Func<Task> test) { await test(); passed++; Console.WriteLine("PASS " + name); }
static void Assert(bool value) { if (!value) throw new Exception("Assertion failed."); }
static void Reject(Action action)
{
    try { action(); }
    catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException) { return; }
    throw new Exception("Expected invalid data to be rejected.");
}
static House Sample(string model = "bgcommon/hou/indoor/general/0001/asset.mdl") => WithStage(JsonSerializer.Serialize(new
{ FormatVersion = 2, Objects = new Dictionary<string, object> { ["chair"] = new { Type = "BgObject", ModelGamePath = model } } }));
static House WithStage(string stage) => new() { Name = "Test haven", TerritoryId = 282, Stage = JsonDocument.Parse(stage).RootElement.Clone() };
static byte[] Encode<T>(T value) => MessagePackSerializer.Serialize(value, WireFormat.Options);
static T Decode<T>(byte[] bytes) => MessagePackSerializer.Deserialize<T>(bytes, WireFormat.Options);
static TestJoin Join(ulong id, string name, bool host) => new() { ContentId = id, CharacterName = name, CreateIfMissing = host, RoomPassword = "haven-test-code", NearbyContentIds = [101, 102, 103] };

// Record used only for convenient immutable variations in tests, serialized through the actual wire type.
// Against a hosted relay: a real 1 KB test file goes through live tickets, the Worker and R2 and comes back intact.
// Each run uses a fresh character and room, so an earlier aborted run's reserved seats never block it.
// `--live-content <endpoint>` runs only this.
async Task LiveContentCheck(Uri endpoint, House original)
{
    async Task<ContentTicket> Ask(Client client, ContentTicketRequest request)
    { await client.Send(ContentWire.TicketRequest, ContentWire.Encode(request)); return ContentWire.DecodeTicket((await client.Expect(ContentWire.Ticket)).Payload); }
    var bytes = new byte[1024]; bytes[0] = 0xFF; bytes[1] = 0xD8; bytes[2] = 0xFF; bytes[3] = 0xE0;
    // A fresh file each run: a removed test file stays denied for good.
    System.Text.Encoding.ASCII.GetBytes("Haven content locker check " + Guid.NewGuid().ToString("N")).CopyTo(bytes, 4);
    var hash = ContentLocker.Fingerprint(bytes);
    var live = original with { Furnishings = [new() { ItemId = 6500, CustomPicture = hash }, .. original.Furnishings.Skip(1)] };
    using var liveOwner = await Client.Connect(endpoint, Join((ulong)Random.Shared.NextInt64(1_000_000, 2_000_000), "LivePictureOwner", true) with { RoomPassword = "live-picture-" + Guid.NewGuid().ToString("N")[..12], ContentSecret = ContentLocker.NewSecret() });
    await liveOwner.Expect(WireKind.RoomJoined);
    await liveOwner.Send(HousingWire.CompressedSnapshot, HousingSnapshotCodec.Encode(live)); await liveOwner.Ready(live);
    var origin = endpoint.GetLeftPart(UriPartial.Authority).Replace("wss://", "https://").Replace("ws://", "http://");
    using var http = new HttpClient();
    var put = await Ask(liveOwner, new("upload", hash, bytes.Length, ContentLocker.AgreementVersion));
    Assert(put.Error == null);
    var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, origin + put.Path));
    if (head.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        var stored = await http.PutAsync(origin + put.Path, new ByteArrayContent(bytes));
        if ((int)stored.StatusCode != 201) throw new Exception($"Live upload answered {(int)stored.StatusCode}: {await stored.Content.ReadAsStringAsync()}");
    }
    else if (!head.IsSuccessStatusCode) throw new Exception($"Live upload check answered {(int)head.StatusCode} for {put.Path!.Split("&sig=")[0]}.");
    var get = await Ask(liveOwner, new("download", hash));
    Assert(get.Error == null && (await http.GetByteArrayAsync(origin + get.Path)).SequenceEqual(bytes));
    Assert((int)(await http.GetAsync(origin + get.Path!.Replace("sig=", "sig=x"))).StatusCode == 404);
    await liveOwner.Send(WireKind.SessionEnd, []);
    // Sharing, through a content session: a fresh 1 KB file each run (labelled adult, so ordinary browsing never shows
    // it), found in the gallery, served publicly, then taken out again.
    var sessionId = (ulong)Random.Shared.NextInt64(2_000_000, 3_000_000);
    var shared = new byte[1024]; shared[0] = 0xFF; shared[1] = 0xD8; shared[2] = 0xFF; shared[3] = 0xE0;
    System.Text.Encoding.ASCII.GetBytes("Haven live share check " + Guid.NewGuid().ToString("N")).CopyTo(shared, 4);
    var sharedHash = ContentLocker.Fingerprint(shared);
    using var session = new System.Net.WebSockets.ClientWebSocket();
    await session.ConnectAsync(endpoint, CancellationToken.None);
    async Task SendFrame(byte kind, byte[] payload) =>
        await session.SendAsync(FrameHeader.BuildUpleg(kind, 0, payload), System.Net.WebSockets.WebSocketMessageType.Binary, true, CancellationToken.None);
    async Task<ContentTicket> AskSession(ContentTicketRequest request)
    {
        await SendFrame(ContentWire.TicketRequest, ContentWire.Encode(request));
        var buffer = new byte[8192];
        var result = await session.ReceiveAsync(buffer, new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        var frame = FrameHeader.ParseDownleg(buffer[..result.Count]);
        if (!frame.Ok || frame.Kind != ContentWire.Ticket) throw new Exception("Live content session: no ticket.");
        return ContentWire.DecodeTicket(frame.Payload);
    }
    await SendFrame(ContentWire.Session, ContentWire.Encode(new ContentSession(sessionId, HousingWire.ProtocolRevision, ContentLocker.NewSecret())));
    var sessionPut = await AskSession(new("upload", sharedHash, shared.Length, ContentLocker.AgreementVersion));
    if (sessionPut.Error != null) throw new Exception("Live session upload refused: " + sessionPut.Error);
    var sent = await http.PutAsync(origin + sessionPut.Path, new ByteArrayContent(shared));
    if ((int)sent.StatusCode != 201) throw new Exception($"Live session upload answered {(int)sent.StatusCode}: {await sent.Content.ReadAsStringAsync()}");
    Assert(sent.Headers.TryGetValues("X-Haven-Usage", out var usage) && usage.First().Split(',').Length == 6);
    // The community gallery is closed: sharing is refused; the uploader deletes at once, with the allowance back.
    Assert((await AskSession(new("share", sharedHash, Agreement: ContentLocker.AgreementVersion))).Error == ContentLocker.GalleryClosed);
    Assert((int)(await http.GetAsync(origin + "/content/gallery?portrait=0&adult=1")).StatusCode is 404 or 405);
    var delete = await AskSession(new("delete", sharedHash));
    var deleted = await http.PostAsync(origin + delete.Path, null);
    if (!deleted.IsSuccessStatusCode) throw new Exception($"Live delete answered {(int)deleted.StatusCode}: {await deleted.Content.ReadAsStringAsync()}");
    Assert(deleted.Headers.TryGetValues("X-Haven-Usage", out var after) && after.First().Split(',').Length == 6);
}

internal sealed record TestJoin
{
    public ulong ContentId { get; init; }
    public string CharacterName { get; init; } = "";
    public bool CreateIfMissing { get; init; }
    public string RoomPassword { get; init; } = "";
    public ulong[] NearbyContentIds { get; init; } = [];
    public bool Batching { get; init; }
    public string RoomId { get; init; } = "";
    public string ResumeToken { get; init; } = "";
    public int Revision { get; init; } = HousingWire.ProtocolRevision;
    public string ContentSecret { get; init; } = "";
    public JoinPayload Wire() => new() { ContentId = ContentId, CharacterName = CharacterName, CreateIfMissing = CreateIfMissing, RoomPassword = RoomPassword, NearbyContentIds = NearbyContentIds, RoomId = RoomId, HavenProtocolRevision = Revision, ResumeToken = ResumeToken, ContentSecret = ContentSecret, Batching = Batching };
}

internal sealed class Client : IDisposable
{
    public RecoveryCheckpoint? LastCheckpoint;
    private readonly ClientWebSocket socket = new();
    public static async Task<Client> Connect(Uri uri, TestJoin join, string? verified = null)
    {
        var client = new Client();
        if (verified != null) client.socket.Options.SetRequestHeader(AccountStamp.Header, verified);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.socket.ConnectAsync(uri, timeout.Token);
        await client.Send(WireKind.JoinRoom, MessagePackSerializer.Serialize(join.Wire(), WireFormat.Options));
        return client;
    }
    public async Task Send(byte kind, byte[] payload)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.SendAsync(FrameHeader.BuildUpleg(kind, 123, payload), WebSocketMessageType.Binary, true, timeout.Token);
    }
    public async Task Ready(House house)
    {
        var hash = new PublishedHouseScene(house).Hash;
        await Send(HousingWire.SceneReady, Encoding.ASCII.GetBytes(hash));
        AssertReady(await Expect(HousingWire.SceneReadyAck), hash);
    }
    private static void AssertReady(FrameHeader.Parsed ack, string hash)
    {
        if (Encoding.ASCII.GetString(ack.Payload) != hash) throw new InvalidDataException("Wrong ready acknowledgment.");
    }
    public async Task SendFragmented(byte kind, byte[] payload)
    {
        var frame = FrameHeader.BuildUpleg(kind, 123, payload);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.SendAsync(frame.AsMemory(0, 1000), WebSocketMessageType.Binary, false, timeout.Token);
        await socket.SendAsync(frame.AsMemory(1000), WebSocketMessageType.Binary, true, timeout.Token);
    }
    public async Task<FrameHeader.Parsed> Expect(byte kind)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var frame = await Read(timeout.Token);
            if (frame.Kind == WireKind.Error && kind != WireKind.Error)
                throw new InvalidDataException("Relay refused the operation: " + MessagePackSerializer.Deserialize<ErrorPayload>(frame.Payload, WireFormat.Options).Message);
            if (frame.Kind == kind) return frame;
        }
    }
    public async Task ExpectWithout(byte terminal, params byte[] forbidden)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (true)
        {
            var frame = await Read(timeout.Token);
            if (forbidden.Contains(frame.Kind)) throw new InvalidDataException("Actor state arrived before readiness or replayed twice.");
            if (frame.Kind == terminal) return;
        }
    }
    public async Task ExpectClose()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { while (true) await Read(timeout.Token); }
        catch (WebSocketException) { }
    }
    public Task<FrameHeader.Parsed> ReadNext(CancellationToken token) => Read(token);
    private async Task<FrameHeader.Parsed> Read(CancellationToken token)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[16 * 1024];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException($"Closed: {result.CloseStatus} {result.CloseStatusDescription}");
            bytes.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        var parsed = FrameHeader.ParseDownleg(bytes.ToArray());
        if (!parsed.Ok) throw new InvalidDataException("Invalid frame.");
        // Barriers (only a revision) hold a stream until the carrier's checkpoint is durable; they carry no state.
        if (parsed.Kind == HousingWire.RecoveryCheckpoint && JsonSerializer.Deserialize<RecoveryCheckpoint>(parsed.Payload) is { } checkpoint
            && (checkpoint.State != null || checkpoint.Removed)) LastCheckpoint = checkpoint;
        if (parsed.Kind == HousingWire.CompressedSnapshot)
            return FrameHeader.ParseDownleg(FrameHeader.BuildDownleg(HousingWire.Snapshot, parsed.SenderId, parsed.Timestamp,
                HouseFormat.Serialize(HousingSnapshotCodec.Decode(parsed.Payload))));
        return parsed;
    }
    public void Dispose() => socket.Dispose();
}

