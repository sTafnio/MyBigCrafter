using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using PoeModDataLib.Api;

namespace MyBigCrafter.Model;

/// <summary>
/// View helpers over PoeModDataLib's mod families, for the picker and condition editor. The crafter works
/// with a single representative base path (selections are homogeneous: one class/subtype, or one base) -
/// the library answers per-base, which is exactly what each picked base needs. Replaces ModDatabase/
/// ModLadder/ModTier; it holds no eligibility logic of its own (the library is the single source of truth).
/// </summary>
public static class ModFamilies
{
    // crafter AffixType -> RePoE generation_type (the spawn-weighted pools).
    public static string AffixToGen(string affix) => affix switch
    {
        "Prefix" => "prefix",
        "Suffix" => "suffix",
        "ExarchImplicit" => "searing_exarch_implicit",
        "EaterImplicit" => "eater_of_worlds_implicit",
        _ => null,
    };

    /// <summary>Eligible families on a base for a crafter affix (+ optional influence name). For cluster jewels,
    /// <paramref name="clusterTypeTags"/> are the selected passive-type tags (e.g.
    /// "affliction_trap_and_mine_damage") whose type-specific notables/grants should be surfaced.</summary>
    public static IReadOnlyList<ModFamily> For(string basePath, string affix, string influence = null,
        IReadOnlyList<string> clusterTypeTags = null)
    {
        var gen = AffixToGen(affix);
        if (gen == null || string.IsNullOrEmpty(basePath)) return Array.Empty<ModFamily>();

        var b = PoeModData.Instance.GetBase(basePath);

        // Map (Area-domain) mods are scoped by map tier (low/mid/top) plus a forced uber pool on Nightmare
        // maps; the picker surfaces those as four sections via ForMapTier. For() itself returns the WHOLE
        // Area pool, which is what requirement matching / value sliders need (they must find a picked mod
        // regardless of which tier section it came from). (Maps have no influence pools.)
        if (string.Equals(b?.Domain, "area", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrEmpty(influence) ? GroupToFamilies(DomainPool(gen, "area")) : Array.Empty<ModFamily>();

        // Cluster jewels (affliction_jewel domain): the size-generic "Added Small Passive Skills also grant"
        // mods gate on the base's size tag, but the NOTABLES ("1 Added Passive Skill is X") gate on the
        // cluster's passive-TYPE tag (affliction_<type>) - a per-item property the base doesn't carry. Augment
        // the base tags with the selected type tag(s) so the chosen type's notables (and grants) become
        // eligible and other types' don't. (Cluster jewels have no influence/essence pools.)
        if (string.Equals(b?.Domain, "affliction_jewel", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(influence)) return Array.Empty<ModFamily>();
            var tags = new HashSet<string>(b.Tags ?? Array.Empty<string>(), StringComparer.Ordinal);
            if (clusterTypeTags != null)
                foreach (var t in clusterTypeTags) if (!string.IsNullOrEmpty(t)) tags.Add(t);
            return GroupToFamilies(DomainPool(gen, "affliction_jewel").Where(m => RollsOn(m.SpawnWeights, tags)).ToList());
        }

        return PoeModData.Instance.ModFamiliesFor(basePath, gen, EmptyToNull(influence));
    }

    // The whole pool for a (generation type, domain), memoized (static game data). The picker rebuilds families
    // every frame (four map tier sections, the cluster type-filtered pool), so the library projection is
    // fetched once per (gen, domain).
    private static readonly Dictionary<string, IReadOnlyList<ModInfo>> DomainPoolCache = new();
    private static IReadOnlyList<ModInfo> DomainPool(string gen, string domain)
    {
        var key = domain + "|" + gen;
        if (!DomainPoolCache.TryGetValue(key, out var pool))
            DomainPoolCache[key] = pool = PoeModData.Instance.ModsOfGenerationType(gen, domain);
        return pool;
    }

    /// <summary>Map mod families for one tier section. low/mid/top resolve mods against that single tier tag;
    /// "uber" mirrors the Nightmare base (top + uber tier tags plus the per-affix forced slot tag), so it
    /// shows the high-tier pool plus the uber-exclusive mods. Lets the picker offer all four tiers as headers,
    /// independent of the selected base, so the user picks whichever applies to their craft.</summary>
    public static IReadOnlyList<ModFamily> ForMapTier(string basePath, string affix, string tierKey)
    {
        var gen = AffixToGen(affix);
        if (gen == null || string.IsNullOrEmpty(basePath)) return Array.Empty<ModFamily>();
        var b = PoeModData.Instance.GetBase(basePath);
        if (!string.Equals(b?.Domain, "area", StringComparison.OrdinalIgnoreCase)) return Array.Empty<ModFamily>();
        var tags = MapTierTagSet(tierKey, affix);
        return GroupToFamilies(DomainPool(gen, "area").Where(m => RollsOn(m.SpawnWeights, tags)).ToList());
    }

    // The synthetic base-tag set whose spawn-weight resolution yields one map tier's pool. (The prefix uber
    // slot tag carries a trailing space in the source data.)
    private static HashSet<string> MapTierTagSet(string tierKey, string affix) => tierKey == "uber"
        ? new(StringComparer.Ordinal)
            { "high_level_map", "top_tier_map", "uber_tier_map", "map_force_6_mods", "map",
              affix == "Prefix" ? "has_uber_map_prefix " : "has_uber_map_suffix" }
        : new(StringComparer.Ordinal) { tierKey };

    // RePoE spawn rule: the FIRST spawn-weight entry whose tag the base has (or "default") sets the weight;
    // weight 0 (e.g. a leading uber_tier_map:0 shadow) means the mod cannot roll on that base.
    private static bool RollsOn(IReadOnlyList<SpawnWeight> weights, HashSet<string> tags)
    {
        if (weights == null) return false;
        foreach (var w in weights)
            if (w.Tag == "default" || tags.Contains(w.Tag)) return w.Weight > 0;
        return false;
    }

    /// <summary>Enchant families on a base.</summary>
    public static IReadOnlyList<ModFamily> Enchants(string basePath) =>
        string.IsNullOrEmpty(basePath) ? Array.Empty<ModFamily>()
            : PoeModData.Instance.ModFamiliesFor(basePath, "enchantment");

    /// <summary>The base's implicit modifiers, grouped into families.</summary>
    public static IReadOnlyList<ModFamily> Implicits(string basePath) =>
        GroupToFamilies(PoeModData.Instance.ImplicitMods(basePath));

    /// <summary>Essence-exclusive families for an item class, split into prefix / suffix.</summary>
    public static (List<ModFamily> Prefix, List<ModFamily> Suffix) Essence(string itemClass)
    {
        var data = PoeModData.Instance;
        var pre = new List<ModFamily>();
        var suf = new List<ModFamily>();
        foreach (var g in data.EssenceOptionsFor(itemClass)
                     .Where(o => data.IsEssenceMod(o.ModId) && o.Mod != null)
                     .GroupBy(o => FamilyKey(o.Mod)))
        {
            var ordered = g.OrderByDescending(o => o.Mod.RequiredLevel).ToList();
            var tiers = ordered.Select((o, i) =>
                new ModTierRow(i + 1, o.ModId, o.Mod.Name, o.Mod.RequiredLevel, 0, 0, o.Mod.Text)).ToList();
            var fam = new ModFamily(ordered[0].Mod.Groups.Count > 0 ? ordered[0].Mod.Groups[0] : "",
                Strip(ordered[0].Mod.Text), tiers);
            (ordered[0].Mod.GenerationType == "suffix" ? suf : pre).Add(fam);
        }
        pre.Sort(ByDisplay);
        suf.Sort(ByDisplay);
        return (pre, suf);
    }

    private static List<ModFamily> GroupToFamilies(IReadOnlyList<ModInfo> mods) =>
        mods.Where(m => m != null).GroupBy(FamilyKey).Select(g =>
        {
            var ordered = g.OrderByDescending(m => m.RequiredLevel).ToList();
            var tiers = ordered.Select((m, i) =>
                new ModTierRow(i + 1, m.Id, m.Name, m.RequiredLevel, 0, 0, m.Text)).ToList();
            return new ModFamily(ordered[0].Groups.Count > 0 ? ordered[0].Groups[0] : "", DisplayText(ordered[0]), tiers);
        }).OrderBy(f => f.Display, StringComparer.OrdinalIgnoreCase).ToList();

    // The "reward tax" bolted onto every Area-domain (map) mod: increased item Quantity on normal maps, plus
    // Rarity and Pack size on the T17 / Map-tier mods. These lines repeat on nearly every mod and bury the
    // actual effect, so the picker hides them (see DisplayText). Magic/Rare-monster and Experience mods are
    // deliberately NOT here - those are genuine standalone identities, not a tax on a real effect.
    private static readonly HashSet<string> MapRewardStats = new(StringComparer.Ordinal)
    {
        "map_item_drop_quantity_+%",
        "map_item_drop_rarity_+%",
        "map_pack_size_+%",
    };

    // Mod text is static game data, but GroupToFamilies runs every frame the picker is open; memoize the
    // per-mod display (keyed by the unique mod id) so the per-map-mod re-translation happens once.
    private static readonly Dictionary<string, string> DisplayByModId = new(StringComparer.Ordinal);

    /// <summary>Picker display text for a mod. For map (Area-domain) mods, drops the reward-tax lines
    /// (item Quantity / Rarity / Pack size) when a real effect line remains, so "Deadly" reads
    /// "#% increased Monster Damage" instead of "#% increased Quantity… / #% increased Monster Damage".
    /// Non-map mods, and pure magic-find maps (whose whole identity IS the reward), keep their full text.</summary>
    private static string DisplayText(ModInfo m)
    {
        if (m == null) return "";
        if (DisplayByModId.TryGetValue(m.Id, out var cached)) return cached;

        string text;
        if (m.Stats.Count == 0 || !string.Equals(m.Domain, "area", StringComparison.OrdinalIgnoreCase))
            text = Strip(m.Text);
        else
        {
            var values = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var s in m.Stats) values[s.Id] = s.Max;
            // TranslateStats tags each rendered line with the stat ids that produced it, so a line is the
            // reward tax iff every one of its stats is a reward stat. If that strips everything, the reward
            // is the mod's whole identity - fall back to the full text.
            var kept = PoeModData.Instance.TranslateStats(values)
                .Where(l => l.StatIds.Any(id => !MapRewardStats.Contains(id)))
                .Select(l => l.Text)
                .ToList();
            text = Strip(kept.Count > 0 ? string.Join("\n", kept) : m.Text);
        }

        DisplayByModId[m.Id] = text;
        return text;
    }

    private static Comparison<ModFamily> ByDisplay =>
        (a, b) => string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase);

    private static string FamilyKey(ModInfo m)
    {
        var c = PoeModData.Instance.ClassifyMod(m.Id);
        return (c?.Group ?? "") + "|" + CoreStatSig(c?.StatSig ?? "");
    }

    public static string StatSig(ModFamily f) =>
        f.Tiers.Count > 0 ? CoreStatSig(PoeModData.Instance.ClassifyMod(f.Tiers[0].ModId)?.StatSig ?? "") : "";

    /// <summary>A mod's stat signature with the map reward-tax stats (item Quantity / Rarity / Pack size)
    /// dropped. Those scale with map tier, so one effect ships as several reward-bundle variants (low / mid /
    /// top tier, plus the uber pool) that share a group but carry a different full signature - which split the
    /// same effect into duplicate picker rows and made cross-tier matching miss. Stripping them gives every
    /// variant of an effect ONE identity. The reward stat ids only ever occur on Area (map) mods, so this is a
    /// no-op for gear/jewel/flask signatures and can be applied uniformly.</summary>
    public static string CoreStatSig(string statSig) =>
        string.IsNullOrEmpty(statSig)
            ? statSig
            : string.Join("|", statSig.Split('|').Where(seg => !MapRewardStats.Contains(StatIdOfSeg(seg))));

    // A signature segment is "<statId>:<L|G>"; stat ids never contain ':', so the last ':' splits off the flag.
    private static string StatIdOfSeg(string seg)
    {
        var i = seg.LastIndexOf(':');
        return i < 0 ? seg : seg.Substring(0, i);
    }

    /// <summary>Value range(s) of a mod for display (e.g. "175-189 / 16-17"); variable stats joined with " / ".</summary>
    public static string RangeOf(string modId)
    {
        var m = PoeModData.Instance.GetMod(modId);
        if (m == null || m.Stats.Count == 0) return "";
        var shown = m.Stats.Where(s => s.Min != s.Max || s.Max != 0)
            .Select(s => s.Min == s.Max ? s.Max.ToString() : $"{s.Min}-{s.Max}").ToList();
        if (shown.Count == 0) shown.Add(m.Stats[0].Max.ToString());
        return string.Join(" / ", shown);
    }

    /// <summary>Best-tier value range for the picker's "Best T1" column.</summary>
    public static string BestRange(ModFamily f) => f.Tiers.Count > 0 ? RangeOf(f.Tiers[0].ModId) : "";

    /// <summary>Build a requirement from a picked family + tier + section context (tier 0 = any).</summary>
    public static ModRequirement ToRequirement(ModFamily f, int tier, string affix, ModCategory category,
        string influence, bool essence, string domain) => new()
    {
        Group = f.Group,
        AffixType = affix,
        StatSig = StatSig(f),
        Domain = domain ?? "Item",
        Label = f.Display,
        Category = category,
        MustHave = true,
        Tier = tier,
        Influence = influence ?? "",
        Essence = essence,
    };

    // --- value sliders (condition editor) ---

    /// <summary>The ordered mod-id pool a requirement can roll within (tier-bounded), for value-range sliders.</summary>
    public static List<string> TierPool(ModRequirement req, string basePath, string itemClass)
    {
        List<string> ids;
        if (req.Essence)
        {
            var (pre, suf) = Essence(itemClass);
            var fam = pre.Concat(suf).FirstOrDefault(f => Match(f, req));
            ids = fam?.Tiers.Select(t => t.ModId).ToList() ?? new List<string>();
        }
        else
        {
            var fam = For(basePath, req.AffixType, req.Influence).FirstOrDefault(f => Match(f, req));
            ids = fam?.Tiers.Select(t => t.ModId).ToList() ?? new List<string>();
        }
        if (req.Tier > 0 && req.Tier < ids.Count) ids = ids.GetRange(0, req.Tier);
        return ids;
    }

    private static bool Match(ModFamily f, ModRequirement req) =>
        string.Equals(f.Group, req.Group, StringComparison.OrdinalIgnoreCase) &&
        (string.IsNullOrEmpty(req.StatSig) || StatSig(f) == CoreStatSig(req.StatSig));

    public sealed record ValueAxis(int Index, string Label, int Min, int Max);

    public static int ValueCount(IReadOnlyList<string> poolModIds) =>
        poolModIds.Count == 0 ? 0 : PoeModData.Instance.GetMod(poolModIds[0])?.Stats.Count ?? 0;

    /// <summary>Per-VARIABLE-value axes (label + span across the pool) for the sliders.</summary>
    public static List<ValueAxis> Axes(IReadOnlyList<string> poolModIds)
    {
        var data = PoeModData.Instance;
        var mods = poolModIds.Select(data.GetMod).Where(m => m != null).ToList();
        var axes = new List<ValueAxis>();
        if (mods.Count == 0) return axes;

        var n = mods[0].Stats.Count;
        for (var i = 0; i < n; i++)
        {
            int lo = int.MaxValue, hi = int.MinValue;
            var variable = false;
            int? seen = null;
            foreach (var m in mods)
            {
                if (i >= m.Stats.Count) continue;
                var s = m.Stats[i];
                lo = Math.Min(lo, s.Min);
                hi = Math.Max(hi, s.Max);
                if (s.Min != s.Max) variable = true;
                else if (seen == null) seen = s.Max;
                else if (seen != s.Max) variable = true;
            }
            if (lo == int.MaxValue) { lo = 0; hi = 0; }
            if (!variable) continue;   // flag / unused values aren't worth a bound
            var id = mods[0].Stats[i].Id;
            var label = Strip(data.TranslateStat(id, hi));
            axes.Add(new ValueAxis(i, string.IsNullOrEmpty(label) ? Humanize(id) : label, lo, hi));
        }
        return axes;
    }

    private static readonly Regex RollRun =
        new(@"\(?[-+]?\d+(?:\.\d+)?(?:-[-+]?\d+(?:\.\d+)?)?\)?", RegexOptions.Compiled);

    private static string Strip(string s) =>
        string.IsNullOrEmpty(s) ? "" : RollRun.Replace(s, "#").Replace("\n", " / ").Trim();

    private static string EmptyToNull(string s) => string.IsNullOrEmpty(s) ? null : s;

    private static string Humanize(string statKey)
    {
        if (string.IsNullOrEmpty(statKey)) return "(stat)";
        var s = statKey.Replace("_", " ").Replace("+", "").Trim();
        s = Regex.Replace(s, @"\s+", " ");
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s);
    }
}
