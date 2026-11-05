namespace Haven.Core.Odyssey;

/// <summary>How rare a reward is: the rolled rewards' tiers, and the shop's prices.</summary>
public enum Rarity { Common, Uncommon, Rare, Legendary }

/// <summary>A Materia's colour, after Final Fantasy VII: green magic, yellow command, blue support, purple independent, red summon.</summary>
public enum MateriaColour { Green, Yellow, Blue, Purple, Red }

/// <summary>A Materia's effect outside the duel itself (its TriadEdge effect is Apply): the run-level Materia of §3.2.</summary>
public enum RunEffect { None, RoyalCharter, TreasureHunter, Mug, PhoenixPinion, MooglePurse, CardSharp, Haggler }

/// <summary>
/// A Materia: one effect on the player's TriadEdge (Apply), or one run-level effect. Key is the saved name; Icon is an
/// FFXIV item whose icon stands for it (the UI looks it up by name and falls back to a colour icon).
/// </summary>
public sealed record Materia(string Key, string Name, MateriaColour Colour, Rarity Rarity, string Description, Action<TriadEdge>? Apply, RunEffect Effect = RunEffect.None)
{
    /// <summary>An item to stand for it other than its colour's (the Master Materia).</summary>
    public string? OwnIcon { get; init; }
    public string Icon => OwnIcon ?? Colour switch
    {
        MateriaColour.Green => "Savage Aim Materia I", MateriaColour.Yellow => "Heavens' Eye Materia I", MateriaColour.Blue => "Quicktongue Materia I",
        MateriaColour.Purple => "Piety Materia I", _ => "Savage Might Materia I",
    };
    /// <summary>Whether this Materia changes the duel (an opponent can carry it); run-level Materia are the player's only.</summary>
    public bool InDuel => Apply != null;
}

/// <summary>
/// What an Enhancement does to a binder card: raise a side, a corner (two adjacent sides), all sides, set its type, give
/// it a perk (one scroll per card; a scroll may grant two traits), shift a side (+2 to it, -1 opposite) or swap a corner's
/// two numbers.
/// </summary>
public enum EnhancementKind { Side, Corner, AllSides, SetType, Perk, Shift, SwapCorner }

/// <summary>A permanent change to one binder card (§3.1). Icon is an FFXIV item name.</summary>
public sealed record Enhancement(string Key, string Name, Rarity Rarity, EnhancementKind Kind, string Description, string Icon, TriadPerk Perk = TriadPerk.None)
{
    /// <summary>Whether the Upgrade screen asks for a side (0-3), a corner (0-3) or a type (1-4) besides the card.</summary>
    public bool NeedsChoice => Kind is EnhancementKind.Side or EnhancementKind.Corner or EnhancementKind.SetType or EnhancementKind.Shift or EnhancementKind.SwapCorner;
    /// <summary>Whether the choice is a side (else a corner, or a type).</summary>
    public bool ChoosesSide => Kind is EnhancementKind.Side or EnhancementKind.Shift;
    public bool ChoosesCorner => Kind is EnhancementKind.Corner or EnhancementKind.SwapCorner;
}

/// <summary>
/// What an Item does for one duel (§3.3); Phoenix Down is held and used by itself on a loss. A Tonic changes either
/// side's TriadEdge for the duel (Item.Mine, Item.Theirs); the rest are wired through the director.
/// </summary>
public enum ItemKind { Potion, Ether, GysahlGreens, HiPotion, EchoScreen, Remedy, SmokeBomb, Elixir, PhoenixDown, Megalixir, Tonic, TreasureMap, PrismPowder, AddRule, Ribbon, Pandora }

/// <summary>A consumable (§3.3). Icon is an FFXIV item name.</summary>
public sealed record Item(string Key, string Name, Rarity Rarity, ItemKind Kind, string Description, string Icon)
{
    /// <summary>A tonic's change to the player's edge for the duel, and to the opponent's (made for them when they carry no Materia; a Remedy doesn't lift it).</summary>
    public Action<TriadEdge>? Mine { get; init; }
    public Action<TriadEdge>? Theirs { get; init; }
    /// <summary>Why a tonic can't be used against the rules in play (null: it can), as "They don't play Reverse."</summary>
    public Func<IReadOnlyList<TriadRule>, string?>? Refuse { get; init; }
    /// <summary>A Treasure Map's least rarity for the won duel's rolled choices.</summary>
    public Rarity MapMinimum { get; init; }
    /// <summary>Whether using it needs a target: a deck card (Potion), one of the opponent's rules (Echo Screen) or a rule to add (Queen of Cards' Favour).</summary>
    public bool NeedsCard => Kind == ItemKind.Potion;
    public bool NeedsRule => Kind is ItemKind.EchoScreen or ItemKind.AddRule;
    public bool AddsRule => Kind == ItemKind.AddRule;
    public bool Held => Kind == ItemKind.PhoenixDown;
}

/// <summary>A regular duelist's name and title, themed to the world.</summary>
public sealed record Duelist(string Name, string Title);

/// <summary>An elite of a world: harder, a better reward, and a themed signature card (3-4-star where the world has them) that can be claimed.</summary>
public sealed record Elite(string Name, string Title, uint SignatureCard);

/// <summary>
/// A world's boss: signature rules, signature Materia (keys), four fixed themed cards and a pool for the flexible fifth
/// slot (the world's other themed cards; a generic card when none fits the power target). The first fixed card is the
/// boss's signature, shown on the path and the duel's preview: the strongest card that fits the villain. The Endsinger's SecondForm is
/// the deck of her second duel at Crystal Rank 10.
/// </summary>
public sealed record Boss(string Name, string Title, TriadRule[] Rules, string[] Materia, uint[] Fixed, uint[] SecondForm)
{
    public string Line { get; init; } = "";
}

/// <summary>One of the fourteen worlds (§2).</summary>
public sealed record World(int Number, string Numeral, string Title, string Subtitle, string Flavour, uint[] ThemedCards, Boss Boss,
    Duelist[] Duelists, Elite[] Elites, uint DuelMusic, uint EliteMusic, uint BossMusic)
{
    /// <summary>The world's theme, on its screens outside duels (title card, path, deck, events, rewards).</summary>
    public uint MapMusic => OdysseyContent.MapMusic[Number - 1];
}

/// <summary>The events of §1.3, by kind; the director builds each one's choices from the run.</summary>
public enum EventKind { Merchant, Workshop, Wager, MooglePost, Stable, Shrine, Gilgamesh, GuestStar, FortuneTeller }

/// <summary>An event's text. MinWorld and Rare gate it; OncePerRun for the secret duels.</summary>
public sealed record OdysseyEvent(EventKind Kind, string Title, string Text, int MinWorld = 1, bool Rare = false, bool OncePerRun = false);

/// <summary>A visitor from beyond the numbered games (the Guest Star event): their card and a line.</summary>
public sealed record GuestStar(string Name, string Title, uint Card, string Line);

/// <summary>
/// Everything the Odyssey is made of: the fourteen worlds with their themed cards, duelists, elites, bosses and music;
/// the Materia, Enhancements and Items; the events; Gilgamesh and the guest stars. Card ids are TripleTriadCard rows;
/// music is orchestrion rolls. Nothing here is random: the director rolls from these tables.
/// </summary>
public static class OdysseyContent
{
    // Music for the screens outside duels.
    /// <summary>
    /// Music is an orchestrion roll (row of Orchestrion, played from its own file), or Bgm plus a row of the game's BGM
    /// sheet for tracks that aren't rolls (the numbering the Orchestrion plugin shows). GameEffects.Track plays either.
    /// </summary>
    public const uint Bgm = 100_000;
    // Each world's theme on its screens outside duels, as chosen in 0.52.5: 84 The Seventh Sun, 468 Final Fantasy
    // Main Theme (Kainé), 41 Eternal Wind, 524 Welcome to Our Town! (Endwalker), 734 Exdeath's Castle, 198 Cyan's Theme,
    // BGM 592 The Chase/Crazy Motorcycle, 362 Blue Fields (Shadowbringers), 526 Festival of the Hunt (Endwalker), 867
    // Zanarkand (Modulation), 849 Main Theme from FINAL FANTASY XI, 389 Discord: Imperial (Zodiac Age Version), 458 Rise,
    // 33 Heroes. World IX had BGM 20042 Thy Warmth and the fanfare BGM 20237: the only two tracks here from Dawntrail's
    // music archive (sqpack ex5/0c0500), and a friend's game exited three times in World IX with c0500 "Could not locate
    // data files" (1-2 November 2026; the fanfare once sat loading for 25 s first). She plays on a free trial, which
    // since patch 7.5 runs through Shadowbringers: its install has no Endwalker or Dawntrail data at all. Festival of the Hunt was chosen in its
    // place, and the fanfare is now its orchestrion roll; World X's boss track, BGM 706 Locus (The Primals) from ex3, is
    // its roll 792. Rolls, Dawntrail's included, live in the base archive (music/ffxiv/Orchestrion), which every install
    // has; every Odyssey song is a roll. A BGM row from ex4 or ex5 wouldn't be heard on a trial
    // (HousingRuntime skips files the install lacks).
    public static readonly uint[] MapMusic = [84, 468, 41, 524, 734, 198, Bgm + 592, 362, 526, 867, 849, 389, 458, 33];
    // Title: 12 Prelude – Discoveries; shop: 856 Welcome to the Gold Saucer.
    /// <summary>
    /// The fanfare after a won duel and how long it plays before the world's theme takes over: FINAL FANTASY IX's for
    /// World IX (roll 808, 24.5 s, looping; the BGM row it had before, 20237, is the same fanfare in Dawntrail's archive:
    /// see MapMusic), FINAL FANTASY XII's Victory for World XII, FFXIV's own "Victory!" elsewhere (BGM rows; lengths from
    /// the Orchestrion plugin's song list). "Victory!" is 7.6 s without a loop, so it gives way after 7 s, before its
    /// end: the orchestrion player handles only looping files (HousingRuntime.RunsOutAfter).
    /// </summary>
    public static (uint Track, float Seconds) Fanfare(int world) => world switch { 9 => (808, 24), 12 => (Bgm + 560, 20), _ => (Bgm + 21, 7) };
    public const uint TitleMusic = 12, ShopMusic = 856, GameOverMusic = 760, VictoryMusic = 572, CreditsMusic = 498;

    public static readonly IReadOnlyList<World> Worlds =
    [
        new(1, "I", "FINAL FANTASY", "The Light of Cornelia",
            "Four crystals dim, and four warriors answer the prophecy. Your journey begins at the gates of Cornelia, with eight cards and a great deal of nerve.",
            [68, 151, 126, 92, 25, 18, 11, 6],
            new("Garland", "Knight of Cornelia", [TriadRule.AllOpen], [], [151, 126, 25, 11], []) { Line = "I, Garland, will knock you all down!" },
            [new("Cornelian Gate Guard", "Keeper of the Bridge"), new("Pravokan Pirate", "Bikke's Deckhand"), new("Elfheim Herbalist", "Tender of the Sleeping Prince"),
             new("Dwarf of Mount Duergar", "Canal Digger"), new("Crescent Lake Sage", "One of the Twelve"), new("Lefeinish Tinker", "Speaker of the Lost Tongue")],
            [new("Bikke", "the Pirate Captain", 126), new("Matoya", "the Blind Witch", 92), new("Astos", "the Dark Elf King", 25)],
            416, 416, 261),
        new(2, "II", "FINAL FANTASY II", "Rebellion's Rose",
            "The Empire's shadow falls over Fynn, and four orphans flee into the night. The rebels have a password. Whisper it to nobody: Wild Rose.",
            [69, 54, 58, 38, 91, 88, 9, 170],
            new("The Emperor", "Emperor of Palamecia", [TriadRule.AllOpen, TriadRule.FallenAce], ["berserk"], [54, 38, 170, 9], []) { Line = "Ungrateful wretches! You dare defy the Emperor?" },
            [new("Fynn Rebel", "Keeper of the Wild Rose"), new("Palamecian Dark Knight", "Sworn to the Emperor"), new("Altair Innkeeper", "Friend of the Rebellion"),
             new("Bafsk Dockhand", "Dreadnought Builder"), new("Deist Dragoon", "Last of the Wyvern Riders"), new("Mysidian Mage", "Scholar of the Ultima Tome")],
            [new("Princess Hilda", "the Rebel Leader", 91), new("Cid", "the Airship Captain", 58), new("Leon", "the Dark Knight", 38)],
            96, 96, 557),
        new(3, "III", "FINAL FANTASY III", "Warriors of the Crystal",
            "An earthquake opens the Altar Cave, and the Wind Crystal names four children its warriors. The darkness is flooding the world of light, and only balance can hold it back.",
            [70, 140, 135, 150, 42, 41, 61, 20],
            new("Cloud of Darkness", "The Void's Will", [TriadRule.AllOpen, TriadRule.Plus], ["steel-wall"], [140, 42, 41, 20], []) { Line = "All shall return to the Void. Even your cards." },
            [new("Ur Villager", "Topapa's Neighbour"), new("Kazus Geomancer", "Cursed by the Djinn"), new("Saronian Soldier", "Of the Divided City"),
             new("Amur Blacksmith", "Forger of Dragon Spears"), new("Dwarven Miner", "Of the Molten Cave"), new("Dark Warrior", "Keeper of the Balance")],
            [new("Doga and Unei", "the Ancients' Heirs", 150), new("Xande", "the Immortal", 135), new("The Wind Sage", "of Crystal Tower", 42)],
            602, 602, Bgm + 249),
        new(4, "IV", "FINAL FANTASY IV", "The Lunar Shadow",
            "A dark knight of Baron steals a crystal and cannot say why. The Red Wings fly on orders nobody understands, and the moon is watching. It has been watching for a very long time.",
            [71, 387, 404, 376, 365, 364, 375, 403, 234, 130, 23, 318],
            new("Zeromus", "The Hatred Made Flesh", [TriadRule.Reverse, TriadRule.Descension], ["toughness"], [404, 130, 23, 234], []) { Line = "Hatred... endless hatred... it is all that remains of me." },
            [new("Baron Soldier", "Of the King's Guard"), new("Red Wings Captain", "Pilot of the Enterprise"), new("Mysidian Elder", "Keeper of the Crystal of Water"),
             new("Dwarven Tank Commander", "Of the Underworld"), new("Lunarian Sage", "Sleeper of the Moon"), new("Eblan Ninja", "Of the Fallen Castle")],
            [new("Kain", "the Dragoon", 234), new("Rubicante", "of the Flame", 376), new("Barbariccia", "of the Wind", 365)],
            582, 538, 614),
        new(5, "V", "FINAL FANTASY V", "Wind, Water, Fire, Earth",
            "The Wind Crystal shatters, and a wanderer, a pirate, a princess and an old man with a lost memory ride a chocobo named Boko toward the end of the world. Behind them, a very large bridge.",
            [72, 39, 249, 240, 197, 152, 94, 21, 309, 316],
            new("Exdeath", "The Tree of the Void", [TriadRule.Order, TriadRule.FallenAce], ["final-attack"], [240, 197, 152, 309], []) { Line = "Turtle! I mean... fool! The Void shall swallow you!" },
            [new("Tycoon Knight", "Of the Wind Shrine"), new("Walse Water Mage", "Of the Water Tower"), new("Karnak Engineer", "Of the Fire-Powered Ship"),
             new("Hidden Cove Pirate", "Of Syldra's Crew"), new("Moogle Elder", "Of the Forest Village"), new("Crescent Bard", "Singer of the Wind")],
            [new("Galuf", "the Forgetful Hero", 94), new("Faris", "the Pirate Captain", 197), new("Lenna", "Princess of Tycoon", 309)],
            735, 72, 833),
        new(6, "VI", "FINAL FANTASY VI", "The World of Ruin",
            "Magitek armour crosses the snowfields of Narshe. A clown in a general's coat laughs at the statues of the gods. The world will burn, and the opera will still be sung.",
            [73, 51, 22, 311, 33, 456, 156, 160, 171, 175, 176, 43],
            new("Kefka Palazzo", "God of Magic", [TriadRule.Chaos, TriadRule.Swap], ["shiva"], [51, 456, 311, 176], []) { Line = "Why do people cling to their cards? Life... dreams... hope... destroy them all!" },
            [new("Narshe Guard", "Of the Mining Town"), new("Imperial Magitek Trooper", "Of Vector"), new("Figaro Chancellor", "Keeper of the Desert Castle"),
             new("Returner Scout", "Of the Hidden Base"), new("Zozo Thief", "Liar of the Rain City"), new("Jidoor Impresario", "Of the Opera House")],
            [new("Gogo", "the Mimic", 311), new("General Leo", "of the Empire", 176), new("The Conductor", "of the Phantom Train", 456)],
            256, 73, 862),
        new(7, "VII", "FINAL FANTASY VII", "The Planet's Cry",
            "The reactors drink the planet dry while the Gold Saucer's lights never go out. A mercenary with a very large sword takes a job for the money. Only for the money.",
            [74, 291, 312, 302, 324, 118, 173, 85, 13, 3],
            new("Sephiroth", "The One-Winged Angel", [TriadRule.Plus, TriadRule.Ascension], ["limit-break"], [291, 312, 324, 302], []) { Line = "Shall I give you despair? Your cards will do." },
            [new("Shinra Trooper", "Of Midgar's Security"), new("Turk Agent", "Of Administrative Research"), new("Gold Saucer Dealer", "Of the Wonder Square"),
             new("Wall Market Hustler", "Of Sector 6"), new("Corel Miner", "Of the Coal Town"), new("Costa del Sol Lifeguard", "Of the Beach")],
            [new("Rude & Reno", "of the Turks", 324), new("Dio", "Master of the Saucer", 118), new("Yuffie", "the Materia Thief", 302)],
            857, 857, 858),
        new(8, "VIII", "FINAL FANTASY VIII", "Garden of Fate",
            "A card game sweeps through Balamb Garden faster than the SeeD exam results. A sorceress waits at the end of time, and a lion with a scar refuses to talk about his feelings.",
            [75, 179, 456, 315, 52, 21, 2, 3],
            new("Ultimecia", "Sorceress from the Future", [TriadRule.Same, TriadRule.Plus], ["same-wall"], [179, 52, 456, 75], []) { Line = "Time shall kompress... all that has been, all that will be. Your deck first." },
            [new("Balamb SeeD Cadet", "Of the Garden"), new("Galbadian Soldier", "Of Deling City"), new("Dollet Mechanic", "Of the Communications Tower"),
             new("Timber Owl", "Of the Resistance"), new("Fishermans Horizon Engineer", "Of the Sunken Station"), new("CC Group Member", "Of the Card Club")],
            [new("Seifer", "the Sorceress's Knight", 315), new("The King", "of the CC Group", 52), new("Laguna", "the Dreamer", 456)],
            417, 327, 420),
        new(9, "IX", "FINAL FANTASY IX", "Memories of Gaia",
            "A theatre troupe of thieves kidnaps a princess who wanted to be kidnapped. A black mage wonders what he is. Somewhere above, a man with silver hair writes the final act.",
            [76, 448, 163, 470, 457, 91, 382, 20],
            new("Kuja", "The Angel of Death", [TriadRule.Same, TriadRule.ThreeOpen], ["carbuncle"], [448, 457, 76, 163], []) { Line = "How poetic. Your little binder, swept away by the wind of my design." },
            [new("Tantalus Thief", "Of the Theatre Ship"), new("Alexandrian Knight", "Of the Pluto Guard"), new("Lindblum Engineer", "Of the Grand Castle"),
             new("Burmecian Dragon Knight", "Of the Rain Kingdom"), new("Black Mage Villager", "Of the Hidden Village"), new("Treno Noble", "Of the Night City")],
            [new("Beatrix", "Captain of Alexandria", 163), new("Vivi", "and the Black Mages", 382), new("Queen Garnet", "of Alexandria", 470)],
            807, 806, 751),
        new(10, "X", "FINAL FANTASY X", "Spira's Sorrow",
            "A star player of Zanarkand wakes a thousand years from home. A summoner walks her pilgrimage knowing how it ends. Listen to the Hymn of the Fayth, and do not look away.",
            [77, 239, 339, 336, 40, 55, 61, 9, 2],
            new("Seymour Guado", "Maester of Yevon", [TriadRule.Reverse, TriadRule.FallenAce, TriadRule.ThreeOpen], ["fortify"], [339, 239, 336, 55], []) { Line = "Even death cannot save you from Yevon's embrace." },
            [new("Besaid Aurochs Blitzer", "Of the Island Team"), new("Yevon Warrior Monk", "Of Bevelle"), new("Al Bhed Salvager", "Of the Home"),
             new("Luca Goers Captain", "Of the Stadium"), new("Guado Guardsman", "Of Guadosalam"), new("Ronso Youth", "Of Mount Gagazet")],
            [new("Auron", "the Legendary Guardian", 336), new("Lulu", "of Besaid", 55), new("Wakka", "of the Aurochs", 40)],
            888, 888, 792),
        new(11, "XI", "FINAL FANTASY XI", "Chains of Vana'diel",
            "Three nations hold the line against the beastmen, and the Shadow Lord stirs in the north. The chains of Promathia bind the world; the Mithra of Windurst would like to know if you have any fish.",
            [78, 469, 434, 435, 472, 473, 446, 447, 427, 428, 429, 430, 431],
            new("The Shadow Lord", "Lord of the Beastmen", [TriadRule.Descension, TriadRule.Plus, TriadRule.ThreeOpen], ["star-power"], [435, 472, 473, 447], []) { Line = "Mortals... you have forgotten the darkness that bore you." },
            [new("San d'Orian Royal Knight", "Of the Temple Knights"), new("Bastokan Musketeer", "Of the Republic"), new("Windurstian Ranger", "Of the Mithra"),
             new("Jeunan Ducal Guard", "Of the Grand Duchy"), new("Norg Ninja", "Of the Tenshodo"), new("Galkan Miner", "Of the Zeruhn Mines")],
            [new("Prishe", "of the Distant Chains", 434), new("Kam'lanaut", "Archduke of Jeuno", 446), new("The Ark Angels", "the Five Who Came Before", 427)],
            716, 754, 711),
        new(12, "XII", "FINAL FANTASY XII", "The Skies of Ivalice",
            "Dalmasca kneels to Archadia, and a street urchin decides he will be a sky pirate. A judge magister follows orders he hates. Above the clouds, the Occuria pull the strings.",
            [79, 250, 229, 209, 299, 208, 276, 111],
            new("Vayne Solidor", "Emperor of Archadia", [TriadRule.Swap, TriadRule.Plus, TriadRule.ThreeOpen], ["plus-wall"], [250, 229, 79, 209], []) { Line = "History is written by the victor. Shall we see who writes this hand?" },
            [new("Dalmascan Street Urchin", "Of Lowtown"), new("Archadian Judge", "Of the Ninth Bureau"), new("Rabanastran Sky Pirate", "Of the Strahl"),
             new("Rozarrian Envoy", "Of the Western Empire"), new("Bhujerban Moogle Mechanic", "Of the Aerodrome"), new("Garif Elder", "Of Jahara")],
            [new("Balthier & Fran", "the Leading Man and His Partner", 208), new("Judge Gabranth", "of the Ninth Bureau", 209), new("Basch", "fon Ronsenburg", 299)],
            236, Bgm + 557, 391),
        new(13, "XIII", "FINAL FANTASY XIII & beyond", "Fabula Nova Crystallis",
            "Cocoon floats above Gran Pulse, a prince drives a very fine car across Lucis, and a Dominant burns for his brother in Valisthea. The crystals' legends have many tellings, and every one ends in fire.",
            [80, 252, 405, 52, 53, 54, 55, 41, 170, 38],
            new("Orphan", "Cradle of Cocoon", [TriadRule.Chaos, TriadRule.Same, TriadRule.Ascension], ["bahamut"], [80, 252, 405, 52], []) { Line = "Make the choice, l'Cie. Your focus ends here." },
            [new("PSICOM Soldier", "Of Cocoon's Sanctum"), new("Gran Pulse Hunter", "Of Oerba"), new("Kingsglaive", "Of Insomnia"),
             new("Hammerhead Mechanic", "Of the Outpost"), new("Rosarian Shield", "Of the Phoenix Gate"), new("Hideaway Cursebreaker", "Of the Bearers")],
            [new("Snow Villiers", "the Hero", 55), new("Gladiolus Amicitia", "the King's Shield", 41), new("Cidolfus Telamon", "the Outlaw", 53)],
            Bgm + 163, Bgm + 163, Bgm + 164),
        new(14, "XIV", "FINAL FANTASY XIV", "Hydaelyn's Champion",
            "Every star you crossed was a verse of one song. At the edge of the universe the Endsinger waits with the final chord, and the Warrior of Light has nothing left but a deck of cards and the people who believe in them.",
            [345, 344, 356, 377, 271, 201, 346, 293, 64, 304, 272, 99, 100, 153, 425, 424, 419],
            new("The Endsinger", "Herald of Oblivion", [TriadRule.Same, TriadRule.Plus, TriadRule.Roulette, TriadRule.ThreeOpen], ["fortify", "wall"], [356, 272, 425, 346], [344, 345, 377, 271])
                { Line = "Hear... feel... think. And then, despair. The song ends with you." },
            [new("Ul'dahn Duelist", "Of the Gold Saucer"), new("Ishgardian Knight", "Of the Holy See"), new("Sharlayan Scholar", "Of the Studium"),
             new("Garlean Centurion", "Of the Legion"), new("Limsan Yellowjacket", "Of the Maelstrom"), new("Doman Shinobi", "Of the Enclave"), new("Gridanian Wood Wailer", "Of the Twelveswood")],
            [new("Estinien", "the Azure Dragoon", 99), new("G'raha Tia", "the Crystal Exarch", 304), new("Emet-Selch", "the Architect", 271)],
            496, 775, 508),
    ];

    public static World WorldAt(int number) => Worlds[Math.Clamp(number, 1, Worlds.Count) - 1];

    // Materia effects, keyed by the saved name (§3.2).
    public static readonly IReadOnlyList<Materia> AllMateria =
    [
        new("protect", "Protect", MateriaColour.Green, Rarity.Common, "Your corner cards +1.", e => e.Corner += 1),
        new("shell", "Shell", MateriaColour.Green, Rarity.Common, "Your edge-middle cards +1.", e => e.Side += 1),
        new("gravity", "Gravity", MateriaColour.Green, Rarity.Common, "Your centre card +2.", e => e.Centre += 2),
        new("berserk", "Berserk", MateriaColour.Green, Rarity.Common, "+1 when attacking.", e => e.Attack += 1),
        new("steel-wall", "Steel Wall", MateriaColour.Green, Rarity.Common, "+1 when defending.", e => e.Defend += 1),
        new("haste", "Haste", MateriaColour.Yellow, Rarity.Common, "Your first card each duel +2.", e => e.First += 2),
        new("final-attack", "Final Attack", MateriaColour.Yellow, Rarity.Common, "Your last card each duel +2.", e => e.Last += 2),
        new("libra", "Libra", MateriaColour.Yellow, Rarity.Common, "Scan: see each opponent's whole hand before you choose them, and in duels even under Three Open.", e => e.Reveal = true),
        new("limit-break", "Limit Break", MateriaColour.Yellow, Rarity.Uncommon, "A card you place while behind +2.", e => e.Behind += 2),
        new("counter", "Counter", MateriaColour.Yellow, Rarity.Uncommon, "After a placement that takes two or more, your next card +1.", e => e.Momentum += 1),
        new("toughness", "Toughness", MateriaColour.Green, Rarity.Uncommon, "Your sides below 3 count as 3.", e => e.Floor = Math.Max(e.Floor, 3)),
        new("moogle-charm", "Moogle Charm", MateriaColour.Blue, Rarity.Uncommon, "Your one- and two-star cards +1.", e => e.Common += 1),
        new("eikon-attunement", "Eikon Attunement", MateriaColour.Blue, Rarity.Uncommon, "Your Primal cards +2.", e => e.Type[1] += 2),
        new("scions-resolve", "Scion's Resolve", MateriaColour.Blue, Rarity.Uncommon, "Your Scion cards +2.", e => e.Type[2] += 2),
        new("beast-tribe-pact", "Beast Tribe Pact", MateriaColour.Blue, Rarity.Uncommon, "Your Society cards +2.", e => e.Type[3] += 2),
        new("magitek-armor", "Magitek Armor", MateriaColour.Blue, Rarity.Uncommon, "Your Garlean cards +2.", e => e.Type[4] += 2),
        new("ascension", "Ascension", MateriaColour.Blue, Rarity.Uncommon, "Your typed cards ascend; Descension never touches them.", e => e.Ascend = true),
        new("unity", "Unity", MateriaColour.Blue, Rarity.Uncommon, "A card placed beside your own of its type: both +1.", e => e.Bond = true),
        new("same", "Same", MateriaColour.Yellow, Rarity.Uncommon, "Same works for your placements.", e => e.Same = true),
        new("plus", "Plus", MateriaColour.Yellow, Rarity.Uncommon, "Plus works for your placements.", e => e.Plus = true),
        new("esuna", "Esuna", MateriaColour.Yellow, Rarity.Uncommon, "Reverse doesn't apply to your captures.", e => e.IgnoreReverse = true),
        new("free-will", "Free Will", MateriaColour.Yellow, Rarity.Uncommon, "Order and Chaos don't bind you.", e => e.FreeChoice = true),
        new("star-power", "Star Power", MateriaColour.Blue, Rarity.Rare, "Your four- and five-star cards +1.", e => e.Rare += 1),
        new("fortify", "Fortify", MateriaColour.Green, Rarity.Rare, "Your sides below 4 count as 4.", e => e.Floor = Math.Max(e.Floor, 4)),   // floor 5 was in 99 % of simulated wins
        new("same-wall", "Same Wall", MateriaColour.Yellow, Rarity.Rare, "The walls count as A for your Same.", e => e.SameWall = true),
        new("plus-wall", "Plus Wall", MateriaColour.Yellow, Rarity.Rare, "The walls count as A for your Plus.", e => e.PlusWall = true),
        new("carbuncle", "Carbuncle", MateriaColour.Red, Rarity.Rare, "The first capture of your cards each duel is undone.", e => e.Reflect += 1),
        new("shiva", "Shiva", MateriaColour.Red, Rarity.Rare, "Enemy cards beside each card you place lose 1, for good.", e => e.Frost = true),
        new("bahamut", "Bahamut", MateriaColour.Red, Rarity.Rare, "Your last card +3.", e => e.Last += 3),
        new("royal-charter", "Royal Charter", MateriaColour.Purple, Rarity.Rare, "Deck limits +1: two 5-star cards, three of 4-star or more.", null, RunEffect.RoyalCharter),
        new("treasure-hunter", "Treasure Hunter", MateriaColour.Purple, Rarity.Rare, "One more reward choice after every duel.", null, RunEffect.TreasureHunter),
        new("mug", "Mug", MateriaColour.Purple, Rarity.Rare, "After a win, also take a random card of the loser's.", null, RunEffect.Mug),
        new("phoenix-pinion", "Phoenix Pinion", MateriaColour.Purple, Rarity.Rare, "Once per world, a lost duel is dealt again.", null, RunEffect.PhoenixPinion),
        new("moogles-purse", "Moogle's Purse", MateriaColour.Purple, Rarity.Common, "+50 % gil.", null, RunEffect.MooglePurse),
        new("card-sharp", "Card Sharp", MateriaColour.Purple, Rarity.Uncommon, "One free reward reroll per world.", null, RunEffect.CardSharp),
        new("haggler", "Haggler", MateriaColour.Purple, Rarity.Uncommon, "Shop prices -25 %.", null, RunEffect.Haggler),
        new("knights-of-the-round", "Knights of the Round", MateriaColour.Red, Rarity.Legendary, "Your cards +1 on every side.", e => e.All += 1),
        new("odin", "Odin", MateriaColour.Red, Rarity.Legendary, "Ties take, for your placements.", e => e.TiesWin = true),
        new("alexander", "Alexander", MateriaColour.Red, Rarity.Legendary, "Your corner cards can't be taken.", e => e.CornerGuard = true),
        new("wall", "Wall", MateriaColour.Red, Rarity.Legendary, "The first two captures of your cards each duel are undone.", e => e.Reflect += 2),
    ];

    /// <summary>A Materia Slot is offered as a reward like a Materia (Rare), adding a slot up to six; it is never slotted itself.</summary>
    public const string MateriaSlotKey = "materia-slot";
    public static readonly Materia MateriaSlot = new(MateriaSlotKey, "Materia Slot", MateriaColour.Purple, Rarity.Rare, "One more Materia slot (up to six).", null);

    /// <summary>
    /// The Master Materia (FF VII's mastered Materia, born again as a new one): a Legendary reward, found at most once a
    /// run, that lets one more Legendary Materia be slotted. Like the Materia Slot it is never slotted itself.
    /// </summary>
    public const string MasterMateriaKey = "master-materia";
    public static readonly Materia MasterMateria = new(MasterMateriaKey, "Master Materia", MateriaColour.Red, Rarity.Legendary,
        "One more Legendary Materia may be slotted at once. Found once a run at most.", null) { OwnIcon = "Savage Might Materia X" };

    public static Materia? MateriaOf(string key) => key == MateriaSlotKey ? MateriaSlot : key == MasterMateriaKey ? MasterMateria : AllMateria.FirstOrDefault(m => m.Key == key);

    /// <summary>A TriadEdge from a set of Materia keys (unknown keys and run-level Materia leave it unchanged).</summary>
    public static TriadEdge Edge(IEnumerable<string> keys)
    {
        var edge = new TriadEdge();
        foreach (var key in keys) MateriaOf(key)?.Apply?.Invoke(edge);
        return edge;
    }

    public static readonly IReadOnlyList<Enhancement> Enhancements =
    [
        new("dark-matter-1", "Grade 1 Dark Matter", Rarity.Common, EnhancementKind.Side, "+1 to one side (choose).", "Grade 1 Dark Matter"),
        new("dark-matter-5", "Grade 5 Dark Matter", Rarity.Uncommon, EnhancementKind.Corner, "+1 to two adjacent sides (choose the corner).", "Grade 5 Dark Matter"),
        new("dark-matter-superior", "Superior Dark Matter", Rarity.Rare, EnhancementKind.AllSides, "+1 to all four sides.", "Grade 8 Dark Matter"),
        new("glamour-prism", "Glamour Prism", Rarity.Uncommon, EnhancementKind.SetType, "Set the card's type: Primal, Scion, Society or Garlean.", "Glamour Prism"),
        new("scroll-steadfast", "Scroll of the Steadfast", Rarity.Rare, EnhancementKind.Perk, "One of your cards becomes Steadfast: a higher number can never take it (Same, Plus and combos still can).", "Iron Ingot", TriadPerk.Steadfast),
        new("scroll-aegis", "Scroll of the Aegis", Rarity.Uncommon, EnhancementKind.Perk, "One of your cards gains Aegis: Same, Plus and combos can never take it (a higher number still can).", "Mythril Ingot", TriadPerk.Aegis),
        new("scroll-ace", "Scroll of the Ace", Rarity.Uncommon, EnhancementKind.Perk, "One of your cards becomes an Ace Killer: its 1s take an A, as under Fallen Ace, in every duel.", "Dark Matter Cluster", TriadPerk.AceKiller),
        new("scroll-avalanche", "Scroll of the Avalanche", Rarity.Rare, EnhancementKind.Perk, "One of your cards gains Avalanche: every card it takes goes on to take its weaker neighbours, like a combo.", "Earth Crystal", TriadPerk.Avalanche),
        new("scroll-fury", "Scroll of Fury", Rarity.Common, EnhancementKind.Perk, "One of your cards becomes Reckless: +2 on every side when you play it, but -1 while it sits on the board.", "Fire Crystal", TriadPerk.Reckless),
        new("scroll-warding", "Scroll of Warding", Rarity.Common, EnhancementKind.Perk, "One of your cards becomes a Guardian: +2 on every side while it sits on the board, but -1 when you play it.", "Ice Crystal", TriadPerk.Guardian),
        new("scroll-cornerstone", "Scroll of the Cornerstone", Rarity.Uncommon, EnhancementKind.Perk, "One of your cards becomes a Cornerstone: +2 on every side while it is in a corner.", "Electrum Ingot", TriadPerk.Cornerstone),
        new("scroll-crown", "Scroll of the Crown", Rarity.Legendary, EnhancementKind.Perk, "One of your cards gains the Crown: while you own it at the end of a duel, it counts as two cards.", "Gold Ingot", TriadPerk.Crown),
        new("scroll-chameleon", "Scroll of the Chameleon", Rarity.Uncommon, EnhancementKind.Perk, "One of your cards becomes Wild: it counts as Primal, Scion, Society and Garlean at once, for every rule and Materia that cares.", "Wind Crystal", TriadPerk.Wild),
        // Reshaping, timing and position: trade-offs and conditions rather than more flat power (the flat ones dominated).
        new("whetstone", "Mythril Whetstone", Rarity.Common, EnhancementKind.Shift, "+2 to one side and -1 to the side opposite (choose).", "Grade 3 Dark Matter"),
        new("fantasia", "Fantasia", Rarity.Common, EnhancementKind.SwapCorner, "Swap the numbers of two adjacent sides (choose the corner).", "Fantasia"),
        new("scroll-vanguard", "Scroll of the Vanguard", Rarity.Common, EnhancementKind.Perk, "One of your cards becomes a Vanguard: +2 on every side if it is the first card you play in a duel, for the rest of the duel.", "Lightning Shard", TriadPerk.Vanguard),
        new("scroll-desperation", "Scroll of Desperation", Rarity.Common, EnhancementKind.Perk, "One of your cards becomes Desperate: +2 on every side if you play it while behind, for the rest of the duel.", "Fire Shard", TriadPerk.Desperate),
        new("scroll-keystone", "Scroll of the Keystone", Rarity.Uncommon, EnhancementKind.Perk, "One of your cards becomes a Keystone: +2 on every side while it is in the centre.", "Cobalt Ingot", TriadPerk.Keystone),
        new("scroll-blizzard", "Scroll of the Blizzard", Rarity.Uncommon, EnhancementKind.Perk, "One of your cards gains Frostbite: when you play it, enemy cards beside it lose 1 on every side, for good.", "Ice Cluster", TriadPerk.Frostbite),
        new("scroll-banner", "Scroll of the Banner", Rarity.Uncommon, EnhancementKind.Perk, "One of your cards gains Rally: when you play it, your cards beside it gain +1 on every side, for good.", "Water Crystal", TriadPerk.Rally),
        new("scroll-drain", "Scroll of Drain", Rarity.Uncommon, EnhancementKind.Perk, "One of your cards gains Drain: when you play it, it gains +1 on every side for each card that placement takes, for good.", "Darksteel Ingot", TriadPerk.Drain),
        new("scroll-looking-glass", "Scroll of the Looking Glass", Rarity.Rare, EnhancementKind.Perk, "One of your cards becomes Contrary: it takes with lower numbers instead of higher (higher under Reverse). It defends as normal.", "Lightning Cluster", TriadPerk.Contrary),
        new("scroll-bastion", "Scroll of the Bastion", Rarity.Rare, EnhancementKind.Perk, "One of your cards becomes a Bastion: +2 on every side while it is in a corner, and Same, Plus and combos can never take it.", "Titanium Ingot", TriadPerk.Aegis | TriadPerk.Cornerstone),
        new("scroll-rampage", "Scroll of the Rampage", Rarity.Rare, EnhancementKind.Perk, "One of your cards goes on a Rampage: +2 on every side when you play it and -1 while it sits on the board, and every card it takes goes on to take its weaker neighbours, like a combo.", "Fire Cluster", TriadPerk.Reckless | TriadPerk.Avalanche),
        new("scroll-masquerade", "Scroll of the Masquerade", Rarity.Rare, EnhancementKind.Perk, "One of your cards wears a mask: it counts as one star lower for the deck limits, but plays -1 on every side.", "Glamour Dispeller", TriadPerk.Masked),
        new("scroll-adamant", "Scroll of Adamant", Rarity.Legendary, EnhancementKind.Perk, "One of your cards becomes Adamant: nothing can take it, not a higher number, not Same, Plus or a combo.", "Adamantite Ingot", TriadPerk.Steadfast | TriadPerk.Aegis),
        new("scroll-mimic", "Scroll of the Mimic", Rarity.Legendary, EnhancementKind.Perk, "One of your cards becomes a Mimic: when you play it, each of its sides that faces a card copies that card's number if it is higher, for the rest of the duel.", "Aurum Regis Ingot", TriadPerk.Mimic),
    ];
    public static Enhancement? EnhancementOf(string key) => Enhancements.FirstOrDefault(e => e.Key == key);

    public static readonly IReadOnlyList<Item> Items =
    [
        new("potion", "Potion", Rarity.Common, ItemKind.Potion, "One chosen card +1 on every side this duel.", "Potion"),
        new("ether", "Ether", Rarity.Common, ItemKind.Ether, "You see the opponent's hand this duel.", "Ether"),
        new("gysahl-greens", "Gysahl Greens", Rarity.Common, ItemKind.GysahlGreens, "You move first this duel.", "Gysahl Greens"),
        new("hi-potion", "Hi-Potion", Rarity.Uncommon, ItemKind.HiPotion, "Every card +1 this duel.", "Hi-Potion"),
        new("echo-screen", "Echo Screen", Rarity.Uncommon, ItemKind.EchoScreen, "Cancel one of the opponent's rules (not Sudden Death).", "Echo Drops"),
        new("remedy", "Remedy", Rarity.Uncommon, ItemKind.Remedy, "Cancel the opponent's Materia this duel.", "Hi-Elixir"),
        new("smoke-bomb", "Smoke Bomb", Rarity.Uncommon, ItemKind.SmokeBomb, "Skip a regular duel (no reward, no gil). Not for elites or bosses.", "Black Bombard Ash"),
        new("elixir", "Elixir", Rarity.Rare, ItemKind.Elixir, "Every card +2 this duel.", "Elixir"),
        new("phoenix-down", "Phoenix Down", Rarity.Rare, ItemKind.PhoenixDown, "Held: when you lose, it's used up and the duel is dealt again.", "Phoenix Down"),
        new("megalixir", "Megalixir", Rarity.Legendary, ItemKind.Megalixir, "Every card +2, and the first capture of yours is undone.", "Mega-Potion"),
        // Answers to one threat, the opponent's side, the reward and the rules: conditional rather than "+N to my cards".
        new("holy-water", "Holy Water", Rarity.Common, ItemKind.Tonic, "Reverse doesn't apply to your captures this duel.", "Antidote")
            { Mine = e => e.IgnoreReverse = true, Refuse = rules => rules.Contains(TriadRule.Reverse) ? null : "They don't play Reverse." },
        new("tranquilizer", "Tranquilizer", Rarity.Common, ItemKind.Tonic, "Order and Chaos don't bind you this duel.", "Hi-Ether")
            { Mine = e => e.FreeChoice = true, Refuse = rules => rules.Contains(TriadRule.Order) || rules.Contains(TriadRule.Chaos) ? null : "They play neither Order nor Chaos." },
        new("bomb-fragment", "Bomb Fragment", Rarity.Common, ItemKind.Tonic, "Their first card this duel -2 on every side, for good.", "Bomb Ash") { Theirs = e => e.First -= 2 },
        new("treasure-map", "Treasure Map", Rarity.Common, ItemKind.TreasureMap, "Win this duel and its reward offers one more choice. On a boss, the crystal offers one more Materia.", "Timeworn Leather Map"),
        new("prism-powder", "Prism Powder", Rarity.Uncommon, ItemKind.PrismPowder, "Your hand is hidden this duel: no Open rule shows them your cards.", "Crystal Sand"),
        new("arctic-wind", "Arctic Wind", Rarity.Uncommon, ItemKind.Tonic, "Enemy cards beside each card you place lose 1, for good, this duel.", "Ice Shard") { Mine = e => e.Frost = true },
        new("shell-stone", "Shell Stone", Rarity.Uncommon, ItemKind.Tonic, "Same, Plus and combos can't take your cards this duel.", "Water Cluster") { Mine = e => e.Shielded = true },
        new("hero-drink", "Hero Drink", Rarity.Uncommon, ItemKind.Tonic, "A card you place while behind +3 this duel.", "X-Potion") { Mine = e => e.Behind += 3 },
        new("protect-stone", "Protect Stone", Rarity.Rare, ItemKind.Tonic, "Your corner cards can't be taken this duel.", "Earth Cluster") { Mine = e => e.CornerGuard = true },
        new("card-club-rulebook", "Card Club Rulebook", Rarity.Rare, ItemKind.Tonic, "Same and Plus work for your placements this duel.", "Gold Triad Card")
            { Mine = e => { e.Same = true; e.Plus = true; }, Refuse = rules => rules.Contains(TriadRule.Same) && rules.Contains(TriadRule.Plus) ? "Same and Plus are in play already." : null },
        new("queens-favour", "Queen of Cards' Favour", Rarity.Rare, ItemKind.AddRule, "Choose a rule to add to this duel: Same, Plus, Fallen Ace, Reverse, Ascension, Descension or Order (one that fits with theirs).", "Imperial Triad Card"),
        new("ancient-map", "Ancient Treasure Map", Rarity.Rare, ItemKind.TreasureMap, "Win this duel and its reward offers one more choice, and every rolled choice is Rare or better. On a boss, the crystal offers one more Materia.", "Timeworn Dragonskin Map")
            { MapMinimum = Rarity.Rare },
        new("ribbon", "Ribbon", Rarity.Legendary, ItemKind.Ribbon, "This duel, the opponent plays only the Open rule and Sudden Death, and their Materia is cancelled.", "X-Ether"),
        new("pandoras-box", "Pandora's Box", Rarity.Legendary, ItemKind.Pandora, "Open the box: one Legendary Materia you don't hold is yours for this duel, chosen at random and shown at once.", "Allagan Tomestone of Poetics"),
    ];
    public static Item? ItemOf(string key) => Items.FirstOrDefault(i => i.Key == key);
    /// <summary>The rules Queen of Cards' Favour may add (the early pool: nothing of chance, nothing that clashes).</summary>
    public static readonly TriadRule[] AddableRules = [TriadRule.Same, TriadRule.Plus, TriadRule.FallenAce, TriadRule.Reverse, TriadRule.Ascension, TriadRule.Descension, TriadRule.Order];

    public static readonly IReadOnlyList<OdysseyEvent> Events =
    [
        new(EventKind.Merchant, "Wandering Merchant",
            "A hooded figure sits at the roadside with a blanket of wares and a chocobo that is clearly stolen. \"Kupo? No, no, I'm no moogle. Just a humble merchant. Have a look, friend, everything's genuine. Mostly.\""),
        new(EventKind.Workshop, "Cid's Workshop",
            "Steam, grease and the smell of burnt tea. A man in goggles looks up from an engine that should not be able to fly and squints at your binder. \"Hand one over. I'll tune it up. The good job costs; the quick one's on the house.\""),
        new(EventKind.Wager, "Tonberry's Wager",
            "A small green figure with a lantern and a very sharp knife is laying out cards on a barrel. It does not say anything. It never says anything. It slides a stake of 30 gil across the barrel and waits. The odds, scratched into the wood, are honest: 45 % to treble it."),
        new(EventKind.MooglePost, "Moogle Post",
            "\"Kupo! Delivery for... you, I think! The address just says 'the one with the cards'.\" The moogle digs through a bag twice its size. \"There's a parcel, or there's the cash-on-delivery refund. Pick quick, kupo, I've got a schedule.\""),
        new(EventKind.Stable, "Fat Chocobo's Stable",
            "The stable smells of greens and ambition. A chocobo the size of a haywain eyes your binder with interest. The stablehand shrugs. \"He likes to trade. Give him a card and he'll bring up... something better. Probably. You'll see it before you say yes.\""),
        new(EventKind.Shrine, "Crystal Shrine",
            "A crystal hums in a cave nobody built. The light runs over your cards like water. You feel that you could let one go, and the crystal would keep it; or that you could ask it to remake one in the image of the Primals, the Scions, the Societies or the Empire."),
        new(EventKind.Gilgamesh, "Gilgamesh!",
            "The road narrows to a bridge, and the bridge is occupied. \"Enough expository banter! Now we fight like men! And ladies! And ladies who dress like men! For Gilgamesh... IT IS MORPHING TIME!\" Enkidu barks. He has brought cards. He has, as ever, brought the wrong ones, but he'll trade blows all the same.",
            MinWorld: 5, Rare: true, OncePerRun: true),
        new(EventKind.GuestStar, "A Visitor from Beyond",
            "The sky flickers, as if a different story were trying to load. Someone who does not belong to any of the numbered worlds steps out of the glitch and sizes you up.",
            MinWorld: 8, Rare: true, OncePerRun: true),
        new(EventKind.FortuneTeller, "Fortune Teller",
            "Beads, incense and a crystal ball that is quite obviously a snow globe. \"Cross my palm with nothing, dear, this one's free. I see... the next world. I see its master. I see the rules they will set, and the cards in their hand.\"",
            MinWorld: 1),
    ];
    public static OdysseyEvent EventOf(EventKind kind) => Events.First(e => e.Kind == kind);

    /// <summary>Gilgamesh, the secret duel of §1.3: an elite with Swap, two cards to claim and a Legendary reward.</summary>
    public static readonly Elite Gilgamesh = new("Gilgamesh", "of the Big Bridge", 249);
    public static readonly uint[] GilgameshCards = [39, 249];
    public const uint GilgameshMusic = 72;

    public static readonly IReadOnlyList<GuestStar> GuestStars =
    [
        new("2B", "YoRHa No. 2 Type B", 326, "\"Emotions are prohibited. Card games, apparently, are not.\""),
        new("9S", "YoRHa No. 9 Type S", 282, "\"I've scanned your deck. I'd rather not say what I found.\""),
        new("2P", "YoRHa No. 2 Type P", 303, "She says nothing at all. The cards are already dealt."),
        new("Clive Rosfield", "the First Shield of Rosaria", 405, "\"I've faced Eikons. I can face a deck of cards.\""),
    ];
    public const uint GuestStarMusic = 888;

    /// <summary>
    /// The worlds whose boss signature (its first fixed card) each card is. A run shows such a card nowhere before it
    /// reaches that boss's world: not in another opponent's deck (so it can't be claimed), a pack, the starting binder
    /// or an event, and no elite of that world or before carries it.
    /// </summary>
    public static readonly IReadOnlyDictionary<uint, int> SignatureWorld =
        Worlds.Where(w => w.Boss.Fixed.Length > 0).GroupBy(w => w.Boss.Fixed[0]).ToDictionary(g => g.Key, g => g.Max(w => w.Number));

    /// <summary>Every themed card id in any world, plus the secret duels'.</summary>
    public static IEnumerable<uint> AllThemedIds => Worlds.SelectMany(w => w.ThemedCards.Concat(w.Boss.Fixed).Concat(w.Boss.SecondForm).Concat(w.Elites.Select(e => e.SignatureCard)))
        .Concat(GilgameshCards).Concat(GuestStars.Select(g => g.Card)).Distinct();

    /// <summary>The Moogle's Verdict (§7): a one-line fairness note for the game-over screen.</summary>
    public static string Verdict(double powerGap, int margin, bool luck) =>
        powerGap > .12 ? "Your deck was outmatched by a wide margin, kupo. Draft harder!"
        : margin == 1 && luck ? "The cards just weren't with you, kupo."
        : margin == 1 ? "So close! One card decided it."
        : "A fair fight, kupo. Next time!";
}
