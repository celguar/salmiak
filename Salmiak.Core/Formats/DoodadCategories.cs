using System;
using System.Collections.Generic;
using System.Text;

namespace Salmiak.Core.Formats;

public static class DoodadCategories
{
    public sealed class Category
    {
        public string Name = "";
        public string[] Aliases = Array.Empty<string>();
        public string[] Keywords = Array.Empty<string>();
    }

    private static Category Cat(string name, string[] keywords, params string[] aliases) =>
        new() { Name = name, Keywords = keywords, Aliases = aliases };

    public static readonly Category[] All =
    {
        Cat("camp", new[] { "tent", "campfire", "bonfire", "fishingpole", "fishingbox", "fishing", "cauldron", "brazier", "bedroll", "firepit", "cookpot", "pavillion", "vendortent" }, "camping"),
        Cat("fire", new[] { "campfire", "bonfire", "brazier", "torch", "fire", "flame", "ember", "firepit", "forge", "hearth", "lava" }),
        Cat("light", new[] { "lamp", "lantern", "candle", "chandelier", "sconce", "torch", "brazier", "light", "streetlamp", "lightpost", "glow" }, "lights", "lighting", "lamps"),
        Cat("furniture", new[] { "chair", "table", "bench", "stool", "desk", "throne", "bookshelf", "shelf", "bed", "cabinet", "wardrobe", "dresser", "couch", "cushion", "rug", "carpet", "bedroll", "podium", "pulpit" }, "furnishings"),
        Cat("container", new[] { "barrel", "crate", "box", "chest", "sack", "basket", "keg", "urn", "bucket", "cask", "vase", "amphora", "jug", "jar" }, "containers", "storage"),
        Cat("nature", new[] { "tree", "bush", "plant", "flower", "fern", "vine", "mushroom", "shroom", "shrub", "fungus", "ivy", "stump", "branch", "kelp", "lilypad", "lilly", "cactus", "reed", "moss", "leaf", "leaves", "sapling", "hedge", "grass", "foliage" }, "plants", "flora", "vegetation"),
        Cat("tree", new[] { "tree", "stump", "branch", "sapling", "palm", "pine", "oak" }, "trees"),
        Cat("flower", new[] { "flower", "lilly", "lilypad", "blossom", "rose", "mushroom", "fern", "bloom" }, "flowers"),
        Cat("rock", new[] { "rock", "stone", "boulder", "pebble", "cliff", "gravel", "rubble", "geyser" }, "rocks", "stones"),
        Cat("crystal", new[] { "crystal", "shard", "geode", "quartz" }, "crystals", "gem", "gems"),
        Cat("sign", new[] { "sign", "signpost", "marker", "milepost", "billboard", "placard", "waypost" }, "signs"),
        Cat("building", new[] { "wall", "fence", "gate", "door", "pillar", "column", "arch", "roof", "tower", "bridge", "ruins", "ruin", "rubble", "archway", "stairs", "staircase", "balcony", "chimney", "window", "scaffold" }, "buildings", "structure"),
        Cat("door", new[] { "door", "gate", "portcullis", "archway", "hatch", "trapdoor" }, "doors"),
        Cat("fence", new[] { "fence", "wall", "palisade", "hedge", "railing", "rampart" }, "fences"),
        Cat("banner", new[] { "banner", "flag", "pennant", "bunting" }, "banners", "flags"),
        Cat("statue", new[] { "statue", "monument", "obelisk", "idol", "gargoyle", "pedestal", "effigy" }, "statues", "monument"),
        Cat("totem", new[] { "totem" }, "totems"),
        Cat("bones", new[] { "bone", "skeleton", "skull", "corpse", "carcass", "ribcage" }, "skeleton", "skeletons"),
        Cat("grave", new[] { "grave", "tomb", "coffin", "sarcophagus", "crypt", "headstone", "mausoleum", "gravestone", "tombstone" }, "graves", "tombs"),
        Cat("water", new[] { "dock", "pier", "boat", "ship", "anchor", "buoy", "raft", "wharf", "well", "fountain", "canoe", "fishing", "waterwheel" }, "harbor", "naval"),
        Cat("dock", new[] { "dock", "pier", "wharf", "anchor", "boat", "buoy", "raft", "canoe" }, "docks"),
        Cat("food", new[] { "meat", "bread", "fruit", "apple", "cheese", "wine", "keg", "cookpot", "foodbarrel", "cauldron", "pumpkin" }, "foods"),
        Cat("book", new[] { "book", "scroll", "tome", "parchment", "bookshelf" }, "books"),
        Cat("forge", new[] { "forge", "anvil", "smelt", "furnace", "bellows", "grindstone", "smith" }, "smith", "blacksmith"),
        Cat("mine", new[] { "mine", "minecart", "oredeposit", "pickaxe", "excavation", "scaffold", "oredebris" }, "mining"),
        Cat("farm", new[] { "haystack", "scarecrow", "plow", "wheelbarrow", "windmill", "pumpkin", "trough", "wagon", "cart" }, "farming"),
        Cat("cage", new[] { "cage", "prison", "shackle", "manacle", "gibbet", "stockade", "stocks" }, "cages", "prison"),
        Cat("decoration", new[] { "rug", "carpet", "tapestry", "painting", "vase", "candelabra", "ornament", "drape", "curtain", "flowerpot" }, "decor"),
        Cat("holiday", new[] { "christmas", "holiday", "carnival", "snowman", "present", "lunar", "valentine", "festival", "fireworks", "wintersveil" }, "festival"),
        Cat("weapon", new[] { "sword", "axe", "mace", "hammer", "spear", "dagger", "shield", "halberd", "polearm", "glaive", "lance", "blade", "knife", "club", "rifle", "crossbow", "wand", "staff", "weaponrack" }, "weapons"),
        Cat("armor", new[] { "helm", "shoulder", "breastplate", "robe", "glove", "gauntlet", "cloak", "bracer", "pauldron", "circlet", "leather" }, "armour"),
        Cat("spell", new[] { "spell", "missile", "impact", "precast", "aura", "explosion", "emitter", "portal", "nova" }, "spells", "visual", "effect"),
        Cat("portal", new[] { "portal", "teleport", "runeportal" }, "portals"),
    };

    private static (string raw, string[] tokens) Forms(string path)
    {
        string p = path.Replace('/', '\\');
        int dot = p.LastIndexOf('.');
        if (dot > 0) p = p.Substring(0, dot);
        var segs = p.Split('\\');
        string name = segs.Length >= 2 ? segs[^2] + "\\" + segs[^1] : p;

        var toks = new List<string>();
        var cur = new StringBuilder();
        char prev = '\0';
        void Flush() { if (cur.Length > 0) { toks.Add(cur.ToString()); cur.Clear(); } }
        foreach (char c in name)
        {
            bool sep = !char.IsLetterOrDigit(c);
            bool camel = char.IsUpper(c) && (char.IsLower(prev) || char.IsDigit(prev));
            bool letterAfterDigit = char.IsLetter(c) && char.IsDigit(prev);
            bool digitAfterLetter = char.IsDigit(c) && char.IsLetter(prev);
            if (sep || camel || letterAfterDigit || digitAfterLetter) Flush();
            if (!sep) cur.Append(char.ToLowerInvariant(c));
            prev = c;
        }
        Flush();
        return (name.ToLowerInvariant(), toks.ToArray());
    }

    private static bool Hit(string raw, string[] tokens, string k)
    {
        if (k.Length >= 6) return raw.Contains(k);
        foreach (var t in tokens)
            if (t.Length >= k.Length && t.Length <= k.Length + 2 && t.StartsWith(k, StringComparison.Ordinal)) return true;
        return false;
    }

    public static List<string> Tags(string path)
    {
        var (raw, tokens) = Forms(path);
        var tags = new List<string>();
        foreach (var c in All)
            foreach (var k in c.Keywords)
                if (Hit(raw, tokens, k)) { tags.Add(c.Name); break; }
        return tags;
    }

    public static int Rank(string path, string queryLower)
    {
        bool sub = path.IndexOf(queryLower, StringComparison.OrdinalIgnoreCase) >= 0;
        if (queryLower.Length < 3) return sub ? 1 : 0;

        bool anyInvoked = false;
        foreach (var c in All) if (Invoked(c, queryLower)) { anyInvoked = true; break; }
        if (!anyInvoked) return sub ? 1 : 0;

        var (raw, tokens) = Forms(path);
        foreach (var c in All)
        {
            if (!Invoked(c, queryLower)) continue;
            foreach (var k in c.Keywords) if (Hit(raw, tokens, k)) return 2;
        }
        return sub ? 1 : 0;
    }

    public static bool Match(string path, string queryLower) => Rank(path, queryLower) > 0;

    private static bool Invoked(Category c, string q)
    {
        if (c.Name.StartsWith(q, StringComparison.Ordinal)) return true;
        foreach (var a in c.Aliases) if (a.StartsWith(q, StringComparison.Ordinal)) return true;
        return false;
    }

    public static string[] Names()
    {
        var n = new string[All.Length];
        for (int i = 0; i < All.Length; i++) n[i] = All[i].Name;
        return n;
    }
}
