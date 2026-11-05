using System.Text.Json;
using System.Text.RegularExpressions;

namespace Haven.Core.World;

/// <summary>A world piece's name and category, as the drawer shows it.</summary>
public sealed record WorldName(string Name, string Category);

/// <summary>
/// Names for world pieces. The game names none of them, so each gets a name from its path: the
/// zone placing it ("Kugane"), the part code in its file name humanised ("e3t1_a1_lnt01a": lnt, "lantern"), and a number
/// among that zone's pieces of the same kind ("Kugane lantern 3"). A names table (WorldNameTable) can give better ones.
/// The codes are the game artists' own, English or romanised Japanese (iwa, rock; saku, fence); the table below covers
/// the most frequent in the installed game's world index, and a code it doesn't know reads as a "piece".
/// </summary>
public static partial class WorldNames
{
    /// <summary>
    /// A file's part code: the letters starting its last name segment that begins with two or more letters
    /// ("s1t1_a1_lmp01" → "lmp"; zone and area codes such as s1t1, a1 begin with one). Empty when there is none.
    /// </summary>
    public static string Code(string path)
    {
        var file = Path.GetFileNameWithoutExtension(path);
        var segments = file.Split('_');
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            var letters = 0;
            while (letters < segments[i].Length && segments[i][letters] is >= 'a' and <= 'z') letters++;
            // A group's name starts with its kind (sgbg_…), which says nothing of what it is.
            if (letters >= 2 && !(i == 0 && segments.Length > 1 && segments[i] is "sgbg" or "sgev" or "sgpl" or "sgvf" or "sgho")) return segments[i][..letters];
        }
        return "";
    }

    /// <summary>
    /// The family a piece's variants share: its folder and file name without the variant at the end ("lnt01a", "lnt02"
    /// → "lnt"; "w_emp_007_01d" → "w_emp_007_"), models and groups apart.
    /// </summary>
    public static string Family(string path)
    {
        var slash = path.LastIndexOf('/');
        var file = Path.GetFileNameWithoutExtension(path);
        return path[..(slash + 1)] + Variant().Replace(file, "") + Path.GetExtension(path);
    }

    [GeneratedRegex(@"[0-9]+[a-z]{0,2}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Variant();

    /// <summary>A part code as a word ("lnt": "lantern"); "piece" for a code the table doesn't know.</summary>
    public static string Word(string code) => Words.GetValueOrDefault(code) ?? "piece";

    /// <summary>The drawer's category for a word: Nature, Architecture, Lighting, Furnishings or Other.</summary>
    public static string Category(string word) => Categories.GetValueOrDefault(word) ?? Other;

    public const string Other = "Other";

    /// <summary>A group's name, saying it's a set of parts ("Brass airship (set)").</summary>
    public static WorldName AsSet(WorldName name) => name.Name.EndsWith(" (set)", StringComparison.Ordinal) ? name : name with { Name = name.Name + " (set)" };

    /// <summary>
    /// Every piece's name and category: a names table's where it has one, for the piece or for its family (the table
    /// names each family once, from its contact-sheet picture; its variants are numbered in path order), else the
    /// fallback from its path, numbered among the zone's pieces of the same word in path order (a word only one piece
    /// of the zone has gets no number).
    /// </summary>
    public static Dictionary<string, WorldName> All(WorldIndex index, WorldNameTable? table = null)
    {
        var result = new Dictionary<string, WorldName>(index.Pieces.Count, StringComparer.Ordinal);
        if (table != null)
            foreach (var family in index.Pieces.Where(p => table.Find(p.Path) == null).GroupBy(p => Family(p.Path)))
            {
                if (table.Find(family.Key) is not { } named) continue;
                var pieces = family.OrderBy(p => p.Path, StringComparer.Ordinal).ToArray();
                for (var i = 0; i < pieces.Length; i++)
                    result[pieces[i].Path] = pieces.Length > 1 && i > 0 ? named with { Name = $"{named.Name} {i + 1}" } : named;
            }
        foreach (var group in index.Pieces.Where(p => !result.ContainsKey(p.Path) && table?.Find(p.Path) == null).GroupBy(p => (p.Zone, Word: Word(Code(p.Path)))))
        {
            var pieces = group.OrderBy(p => p.Path, StringComparer.Ordinal).ToArray();
            var zone = index.Zones[group.Key.Zone].Name;
            for (var i = 0; i < pieces.Length; i++)
                result[pieces[i].Path] = new($"{zone} {group.Key.Word}{(pieces.Length > 1 ? " " + (i + 1) : "")}", Category(group.Key.Word));
        }
        if (table != null)
            foreach (var piece in index.Pieces)
                if (table.Find(piece.Path) is { } named) result[piece.Path] = named;
        return result;
    }

    private const string Nature = "Nature", Architecture = "Architecture", Lighting = "Lighting", Furnishings = "Furnishings";

    // The artists' part codes, from the most frequent in the world index (3 November 2026: 104,186 pieces, 4,915 codes;
    // the 300 commonest cover most of them). Unclear ones are left out rather than guessed.
    private static readonly Dictionary<string, string> Words = new(StringComparer.Ordinal)
    {
        ["stc"] = "structure", ["arf"] = "fixture", ["wal"] = "wall", ["wall"] = "wall", ["kabe"] = "wall", ["kab"] = "wall", ["wl"] = "wall",
        ["tre"] = "tree", ["tree"] = "tree", ["yasi"] = "palm tree", ["gmc"] = "mechanism", ["obj"] = "object", ["itm"] = "item",
        ["rck"] = "rock", ["rock"] = "rock", ["roc"] = "rock", ["rk"] = "rock", ["iwa"] = "rock", ["rok"] = "rock", ["ro"] = "rock", ["stn"] = "stone",
        ["ston"] = "stone", ["stns"] = "stones", ["clf"] = "cliff", ["yama"] = "mountain", ["yam"] = "mountain",
        ["flo"] = "floor", ["flr"] = "floor", ["flor"] = "floor", ["floor"] = "floor", ["yuka"] = "floor", ["fl"] = "floor", ["til"] = "tiles",
        ["nat"] = "natural feature", ["plr"] = "pillar", ["pil"] = "pillar", ["pill"] = "pillar", ["pilr"] = "pillar", ["pila"] = "pillar", ["col"] = "column",
        ["lmp"] = "lamp", ["lamp"] = "lamp", ["lig"] = "light", ["lit"] = "light", ["ligh"] = "light", ["ligt"] = "light", ["lgt"] = "light", ["turo"] = "stone lantern",
        ["rub"] = "rubble", ["grk"] = "rubble", ["debr"] = "debris", ["deb"] = "debris", ["dbr"] = "debris", ["debu"] = "debris", ["rui"] = "ruins", ["frag"] = "fragment",
        ["lsf"] = "landscape", ["ter"] = "terrain", ["land"] = "land", ["cry"] = "crystal", ["crys"] = "crystal", ["crst"] = "crystal",
        ["dor"] = "door", ["door"] = "door", ["gate"] = "gate", ["gat"] = "gate", ["ent"] = "entrance", ["grl"] = "railing", ["rail"] = "railing",
        ["fen"] = "fence", ["fenc"] = "fence", ["fnc"] = "fence", ["saku"] = "fence", ["hou"] = "house", ["hous"] = "house", ["koya"] = "hut",
        ["box"] = "box", ["wat"] = "water", ["wate"] = "water", ["sea"] = "sea", ["wep"] = "weapon", ["flw"] = "flowers", ["flow"] = "flowers",
        ["tow"] = "tower", ["twr"] = "tower", ["towe"] = "tower", ["towr"] = "tower", ["rom"] = "room", ["room"] = "room", ["emp"] = "imperial piece",
        ["bas"] = "base", ["base"] = "base", ["flg"] = "flag", ["flag"] = "flag", ["stg"] = "stage", ["stag"] = "stage", ["win"] = "window", ["wid"] = "window",
        ["wind"] = "window", ["aet"] = "aetheryte", ["wod"] = "wood", ["wood"] = "wood", ["log"] = "log", ["dec"] = "decoration", ["deco"] = "decoration",
        ["str"] = "stairs", ["step"] = "steps", ["stp"] = "steps", ["stir"] = "stairs", ["slp"] = "slope", ["slop"] = "slope", ["kaid"] = "stairs", ["rmp"] = "ramp",
        ["rot"] = "roots", ["root"] = "roots", ["boss"] = "arena", ["bos"] = "arena", ["bil"] = "building", ["bui"] = "building", ["buil"] = "building",
        ["bild"] = "building", ["bld"] = "building", ["pol"] = "pole", ["pole"] = "pole", ["enk"] = "backdrop", ["pip"] = "pipe", ["pipe"] = "pipe",
        ["fsh"] = "fish", ["fish"] = "fish", ["fun"] = "furnishing", ["bri"] = "bridge", ["brg"] = "bridge", ["brid"] = "bridge", ["brig"] = "bridge",
        ["sip"] = "ship", ["shp"] = "ship", ["ship"] = "ship", ["air"] = "airship", ["chr"] = "chair", ["isu"] = "chair", ["char"] = "chair",
        ["dsk"] = "desk", ["desk"] = "desk", ["rof"] = "roof", ["roof"] = "roof", ["yane"] = "roof", ["tent"] = "tent", ["tnt"] = "tent", ["ita"] = "boards",
        ["hal"] = "hall", ["fod"] = "food", ["food"] = "food", ["frut"] = "fruit", ["veg"] = "vegetables", ["pot"] = "pot", ["pic"] = "picture",
        ["vfg"] = "fog", ["vfog"] = "fog", ["fog"] = "fog", ["vf"] = "fog", ["clt"] = "cloth", ["bed"] = "bed", ["tbl"] = "table", ["tabl"] = "table",
        ["blk"] = "block", ["dai"] = "stand", ["cel"] = "ceiling", ["ceil"] = "ceiling", ["cei"] = "ceiling", ["arc"] = "arch", ["arch"] = "arch",
        ["xma"] = "Starlight decoration", ["plan"] = "plant", ["pla"] = "plant", ["plnt"] = "plant", ["uek"] = "plant", ["nae"] = "seedling",
        ["roa"] = "road", ["road"] = "road", ["way"] = "path", ["dom"] = "dome", ["toge"] = "thorns", ["ivy"] = "ivy", ["lvy"] = "ivy",
        ["gea"] = "gear", ["gear"] = "gear", ["bush"] = "bush", ["bsh"] = "bush", ["gra"] = "grass", ["grs"] = "grass", ["kusa"] = "grass",
        ["rope"] = "rope", ["rop"] = "rope", ["net"] = "net", ["lift"] = "lift", ["part"] = "part", ["bok"] = "books", ["book"] = "books",
        ["chn"] = "chain", ["zou"] = "statue", ["ice"] = "ice", ["snw"] = "snow", ["bal"] = "balcony", ["egg"] = "egg", ["bag"] = "bag", ["mat"] = "mat",
        ["cpt"] = "carpet", ["komo"] = "straw mat", ["shaf"] = "shaft", ["shft"] = "shaft", ["ring"] = "ring", ["ball"] = "ball", ["hol"] = "hole",
        ["hole"] = "hole", ["mcn"] = "machine", ["tana"] = "shelf", ["shf"] = "shelf", ["slf"] = "shelf", ["cag"] = "cage", ["tank"] = "tank",
        ["car"] = "cart", ["mush"] = "mushroom", ["warp"] = "warp", ["pier"] = "pier", ["cav"] = "cave", ["cave"] = "cave", ["dsh"] = "dish",
        ["fire"] = "fire", ["fan"] = "fan", ["beam"] = "beam", ["bot"] = "bottle", ["gls"] = "glass", ["swit"] = "switch", ["sign"] = "sign",
        ["lav"] = "lava", ["ppr"] = "papers", ["foun"] = "fountain", ["fou"] = "fountain", ["fram"] = "frame", ["sky"] = "sky", ["cov"] = "cover",
        ["whel"] = "wheel", ["cup"] = "cup", ["tool"] = "tools", ["cou"] = "counter", ["snd"] = "sand", ["ixl"] = "Ixal piece",
        ["barl"] = "barrel", ["taru"] = "barrel", ["cnd"] = "candle", ["lnt"] = "lantern", ["lant"] = "lantern", ["chst"] = "chest",
        ["sof"] = "sofa", ["cur"] = "curtain", ["ctn"] = "curtain", ["mir"] = "mirror", ["clk"] = "clock", ["bsk"] = "basket",
        ["plt"] = "plant", ["reed"] = "reeds", ["sta"] = "statue", ["mon"] = "monument", ["kai"] = "stairs", ["oba"] = "shrub",
    };

    private static readonly Dictionary<string, string> Categories = new(StringComparer.Ordinal)
    {
        ["tree"] = Nature, ["palm tree"] = Nature, ["rock"] = Nature, ["stone"] = Nature, ["stones"] = Nature, ["cliff"] = Nature, ["mountain"] = Nature,
        ["natural feature"] = Nature, ["landscape"] = Nature, ["terrain"] = Nature, ["land"] = Nature, ["crystal"] = Nature, ["water"] = Nature, ["sea"] = Nature,
        ["flowers"] = Nature, ["roots"] = Nature, ["plant"] = Nature, ["seedling"] = Nature, ["thorns"] = Nature, ["ivy"] = Nature, ["bush"] = Nature,
        ["grass"] = Nature, ["ice"] = Nature, ["snow"] = Nature, ["mushroom"] = Nature, ["cave"] = Nature, ["lava"] = Nature, ["sky"] = Nature,
        ["sand"] = Nature, ["log"] = Nature, ["wood"] = Nature, ["fog"] = Nature, ["backdrop"] = Nature, ["shrub"] = Nature, ["reeds"] = Nature,
        ["structure"] = Architecture, ["architecture"] = Architecture, ["wall"] = Architecture, ["floor"] = Architecture, ["tiles"] = Architecture,
        ["pillar"] = Architecture, ["column"] = Architecture, ["door"] = Architecture, ["gate"] = Architecture, ["entrance"] = Architecture,
        ["railing"] = Architecture, ["fence"] = Architecture, ["house"] = Architecture, ["hut"] = Architecture, ["tower"] = Architecture, ["room"] = Architecture,
        ["base"] = Architecture, ["stage"] = Architecture, ["window"] = Architecture, ["stairs"] = Architecture, ["steps"] = Architecture, ["slope"] = Architecture,
        ["ramp"] = Architecture, ["arena"] = Architecture, ["building"] = Architecture, ["bridge"] = Architecture, ["roof"] = Architecture, ["hall"] = Architecture,
        ["block"] = Architecture, ["ceiling"] = Architecture, ["arch"] = Architecture, ["road"] = Architecture, ["path"] = Architecture, ["dome"] = Architecture,
        ["balcony"] = Architecture, ["shaft"] = Architecture, ["pier"] = Architecture, ["beam"] = Architecture, ["fixture"] = Architecture, ["ruins"] = Architecture, ["rubble"] = Architecture, ["debris"] = Architecture, ["fragment"] = Architecture,
        ["pole"] = Architecture, ["pipe"] = Architecture, ["boards"] = Architecture, ["monument"] = Architecture, ["statue"] = Architecture, ["fountain"] = Architecture,
        ["lamp"] = Lighting, ["light"] = Lighting, ["stone lantern"] = Lighting, ["lantern"] = Lighting, ["candle"] = Lighting, ["fire"] = Lighting,
        ["box"] = Furnishings, ["chair"] = Furnishings, ["desk"] = Furnishings, ["table"] = Furnishings, ["bed"] = Furnishings, ["pot"] = Furnishings,
        ["picture"] = Furnishings, ["cloth"] = Furnishings, ["stand"] = Furnishings, ["books"] = Furnishings, ["mat"] = Furnishings, ["carpet"] = Furnishings,
        ["straw mat"] = Furnishings, ["shelf"] = Furnishings, ["food"] = Furnishings, ["fruit"] = Furnishings, ["vegetables"] = Furnishings, ["dish"] = Furnishings,
        ["bottle"] = Furnishings, ["cup"] = Furnishings, ["barrel"] = Furnishings, ["chest"] = Furnishings, ["sofa"] = Furnishings, ["curtain"] = Furnishings,
        ["mirror"] = Furnishings, ["clock"] = Furnishings, ["basket"] = Furnishings, ["bag"] = Furnishings, ["papers"] = Furnishings, ["tools"] = Furnishings,
        ["counter"] = Furnishings, ["furnishing"] = Furnishings, ["frame"] = Furnishings, ["sign"] = Furnishings, ["flag"] = Furnishings, ["decoration"] = Furnishings,
        ["Starlight decoration"] = Furnishings, ["item"] = Furnishings, ["glass"] = Furnishings, ["cage"] = Furnishings, ["cart"] = Furnishings,
    };
}

/// <summary>
/// The names table: better names, and a category, for world pieces by path, embedded in Haven
/// (World/world-names.json). Empty for now; filled later from the offline tool's contact sheets.
/// </summary>
public sealed class WorldNameTable
{
    private readonly Dictionary<string, WorldName> names;
    public int Count => names.Count;
    public WorldNameTable(IEnumerable<(string Path, WorldName Name)> entries)
    {
        names = new(StringComparer.Ordinal);
        foreach (var (path, name) in entries)
            if (BuildingParts.IsWorldAsset(path) && SafeText.Clean(name.Name, 80) is { Length: > 0 } clean)
                names[path] = new(clean, SafeText.Clean(name.Category, 40) is { Length: > 0 } category ? category : WorldNames.Other);
    }

    public WorldName? Find(string path) => names.GetValueOrDefault(path);

    private sealed record Entry(string Path, string Name, string? Category);

    /// <summary>A table from its JSON: an array of { "path", "name", "category" }; entries that don't read are skipped.</summary>
    public static WorldNameTable Parse(string json)
    {
        try
        {
            var entries = JsonSerializer.Deserialize<Entry[]>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            return new(entries.Where(e => e?.Path != null && e.Name != null).Select(e => (e.Path, new WorldName(e.Name, e.Category ?? ""))));
        }
        catch (JsonException) { return new([]); }
    }

    /// <summary>The table embedded in Haven.Core.</summary>
    public static WorldNameTable Embedded()
    {
        using var stream = typeof(WorldNameTable).Assembly.GetManifestResourceStream("Haven.Core.World.world-names.json");
        if (stream == null) return new([]);
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
}

/// <summary>
/// World pieces the drawer doesn't offer, embedded in Haven (World/world-hidden.txt, one path
/// a line, written by the offline tool's survey): models with nothing to see (only a light shaft, a fog volume or a
/// shadow caster) and groups the building reader can't make (no supported model, or more parts than a piece may be).
/// A home that already holds one still loads; it just isn't listed.
/// </summary>
public static class WorldHidden
{
    public static HashSet<string> Embedded()
    {
        using var stream = typeof(WorldHidden).Assembly.GetManifestResourceStream("Haven.Core.World.world-hidden.txt");
        if (stream == null) return new(StringComparer.Ordinal);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
    }
}
