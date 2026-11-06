using Haven.Core;
using Haven.Core.World;

/// <summary>The world's ambience catalogue: which effect and sound paths a home may hold, and the table.</summary>
internal static class AmbientTests
{
    public static async Task Run(Func<string, Action, Task> check)
    {
        await check("Placeable world effects and sounds are the game's own ambience files, nothing else", () =>
        {
            Assert(WorldAmbient.IsEffectPath("bg/ffxiv/fst_f1/common/vfx/eff/f1dfire03_y.avfx"));
            Assert(WorldAmbient.IsEffectPath("bg/ex5/02_ykt_y6/common/vfx/eff/y6t1_watr01y.avfx"));
            Assert(WorldAmbient.IsEffectPath("bgcommon/world/vfx_world/vfx/eff/w0watr002_1y.avfx") && WorldAmbient.IsEffectPath("bgcommon/vfx/eff/w_fire001b1y.avfx"));
            Assert(!WorldAmbient.IsEffectPath("vfx/action/ab_cnj009/eff/ab_cnj009c0f.avfx") && !WorldAmbient.IsEffectPath("bg/ffxiv/fst_f1/common/vfx/eff/../x.avfx"));
            Assert(!WorldAmbient.IsEffectPath("bg/ffxiv/fst_f1/common/bgparts/x.mdl") && !WorldAmbient.IsEffectPath("bg/ffxiv/fst_f1/common/vfx/eff/Fire.avfx"));
            Assert(WorldAmbient.IsSoundPath("bgcommon/sound/spot_wtr/spot_wtr_River_2ch.scd") && WorldAmbient.IsSoundPath("bgcommon/env/sound/fst/fst_bg_fst_d_rainstorm_narrow_loop.scd"));
            Assert(WorldAmbient.IsSoundPath("bgcommon/sound/fst/fst_spot_fall-thin.scd"));
            Assert(!WorldAmbient.IsSoundPath("sound/battle/mon/1234.scd") && !WorldAmbient.IsSoundPath("music/ffxiv/BGM_Field_01.scd") && !WorldAmbient.IsSoundPath("bgcommon/sound/../x.scd"));
        });

        await check("A home may hold world effects and sounds as building pieces, sounds and world pieces with a volume", () =>
        {
            Furnishing Piece(string path, float? volume = null) => new() { BuildingPart = path, Position = [1, 0, 1], SoundVolume = volume };
            static bool Valid(params Furnishing[] pieces)
            {
                try { SavedGroups.Validate(new SavedGroup { Name = "Test", Pieces = pieces }); return true; }   // a saved group follows a home's rules
                catch (InvalidDataException) { return false; }
            }
            const string fire = "bg/ffxiv/fst_f1/common/vfx/eff/f1dfire03_y.avfx", river = "bgcommon/sound/spot_wtr/spot_wtr_River_2ch.scd";
            const string set = "bg/ex2/02_est_e3/shared/for_bg/sgbg_e3t1_a1_lnt01.sgb", wall = "bgcommon/hou/indoor/general/0013/asset/fun_b0_m0013.sgb";
            Assert(Valid(Piece(fire), Piece(river), Piece(river, .4f), Piece(set, .5f)));
            Assert(Haven.Core.BuildingParts.IsAmbient(fire) && Haven.Core.BuildingParts.IsAmbient(river) && !Haven.Core.BuildingParts.IsAmbient(set));
            // A volume only on what plays a sound; never on an effect or a housing part; never outside 0-1.
            Assert(!Valid(Piece(fire, .5f)) && !Valid(Piece(wall, .5f)) && !Valid(Piece(river, 1.5f)) && !Valid(Piece(river, float.NaN)));
            // A spell's effect or a battle sound is no piece.
            Assert(!Valid(Piece("vfx/action/ab_cnj009/eff/ab_cnj009c0f.avfx")) && !Valid(Piece("sound/battle/mon/1234.scd")));
        });

        await check("The ambience table keeps valid entries, cleans names and drops what a home can't hold", () =>
        {
            var table = WorldAmbientTable.Parse("""
                [{ "path": "bg/ffxiv/fst_f1/common/vfx/eff/f1dfire03_y.avfx", "kind": "Effect", "name": "  Brazier fire ", "category": "Fire & embers", "zone": "Haukke Manor", "uses": 4, "size": 1.5, "colour": [1, 0.8, 0.6, 1] },
                 { "path": "bgcommon/sound/spot_wtr/spot_wtr_River_2ch.scd", "kind": "Sound", "name": "Flowing river", "category": "", "zone": "Gridania", "uses": 75, "repeat": "Loop" },
                 { "path": "vfx/action/ab_cnj009/eff/ab_cnj009c0f.avfx", "kind": "Effect", "name": "Cure", "category": "Magic", "zone": "", "uses": 1 },
                 { "path": "bgcommon/sound/spot_wtr/spot_wtr_Drop.scd", "kind": "Sound", "name": "", "category": "Water", "zone": "", "uses": 1 },
                 { "path": "bgcommon/sound/spot_wtr/spot_wtr_River_2ch.scd", "kind": "Sound", "name": "Duplicate", "category": "Water", "zone": "", "uses": 1 }]
                """);
            Assert(table.Count == 2);
            Assert(table.Find("bg/ffxiv/fst_f1/common/vfx/eff/f1dfire03_y.avfx") is { Name: "Brazier fire", Kind: AmbientKind.Effect, Size: 1.5f, Colour.Length: 4 });
            Assert(table.Find("bgcommon/sound/spot_wtr/spot_wtr_River_2ch.scd") is { Name: "Flowing river", Category: "Other", Repeat: AmbientRepeat.Loop, Uses: 75 });
            // An effect file is written as a sound and back alike; what doesn't read is no table at all.
            Assert(WorldAmbientTable.Parse(WorldAmbientTable.Write(table.Entries)).Count == 2 && WorldAmbientTable.Parse("not json").Count == 0);
            Assert(WorldAmbientTable.Embedded().Count >= 0);
        });
    }

    private static void Assert(bool value) { if (!value) throw new Exception("Ambience assertion failed."); }
}
