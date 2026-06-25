using System;
using System.Collections.Generic;
using System.Linq;
using PoeModDataLib.Api;

namespace MyBigCrafter.Data;

/// <summary>
/// Organises PoeModDataLib bases into the crafter's Category / Class taxonomy (with the cluster-jewel
/// split, which the library has no concept of). A light cache over the library's already-indexed bases,
/// built once it's ready. Replaces the old BaseItemRepository - no game-data of its own, just grouping.
/// </summary>
public static class BaseTaxonomy
{
    private static readonly HashSet<string> GearRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "Rings", "Amulets", "Belts", "Armours", "Weapons", "Quivers", "Flasks", "Jewels", "Trinkets", "Maps",
    };

    private const string ItemsPrefix = "Metadata/Items/";

    private static readonly string[] CategoryOrder =
        { "One-Handed Weapons", "Two-Handed Weapons", "Armour", "Off-hand", "Jewellery", "Jewels", "Flasks", "Maps", "Other" };

    /// <summary>Cluster jewels share the game class "Jewel" with regular jewels but have their own mod domain,
    /// bases and subtype - so the crafter treats them as their own class. Single source of that split.</summary>
    public static string EffectiveClassName(string className, string path) =>
        string.Equals(className, "Jewel", StringComparison.OrdinalIgnoreCase) &&
        path?.Contains("JewelPassiveTreeExpansion", StringComparison.OrdinalIgnoreCase) == true
            ? "Cluster Jewel"
            : className;

    private static bool _built;
    private static Dictionary<string, List<BaseInfo>> _byClass = new();
    private static Dictionary<string, string> _categoryByClass = new();
    private static List<string> _categories = new();
    private static Dictionary<string, BaseInfo> _byPath = new();

    /// <summary>True once the library is ready (the taxonomy builds lazily on first access after that).</summary>
    public static bool IsReady => PoeModData.Instance.IsReady;

    private static void EnsureBuilt()
    {
        if (_built) return;
        var data = PoeModData.Instance;
        if (!data.IsReady) return;

        var byClass = new Dictionary<string, List<BaseInfo>>(StringComparer.Ordinal);
        foreach (var cls in data.ItemClasses())
            foreach (var b in data.BasesOfClass(cls))
            {
                if (string.IsNullOrEmpty(b.Path) || !b.Path.StartsWith(ItemsPrefix, StringComparison.Ordinal)) continue;
                if (!IsGear(b.Path) || string.IsNullOrWhiteSpace(b.Name)) continue;
                var eff = EffectiveClassName(b.ItemClass, b.Path);
                if (string.IsNullOrWhiteSpace(eff)) continue;
                if (!byClass.TryGetValue(eff, out var list)) byClass[eff] = list = new List<BaseInfo>();
                list.Add(b);
            }

        foreach (var l in byClass.Values)
            l.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        _byClass = byClass;
        _categoryByClass = byClass.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Count > 0 ? CategoryFromPath(kv.Value[0].Path) : "Other",
            StringComparer.Ordinal);
        _categories = _categoryByClass.Values.Distinct()
            .OrderBy(c => { var i = Array.IndexOf(CategoryOrder, c); return i < 0 ? int.MaxValue : i; })
            .ToList();
        _byPath = new Dictionary<string, BaseInfo>(StringComparer.Ordinal);
        foreach (var l in byClass.Values)
            foreach (var b in l)
                _byPath[b.Path] = b;

        _built = true;
    }

    public static IReadOnlyList<string> Categories { get { EnsureBuilt(); return _categories; } }

    public static IEnumerable<string> ClassesInCategory(string category)
    {
        EnsureBuilt();
        return _byClass.Keys
            .Where(c => _categoryByClass.TryGetValue(c, out var cat) && cat == category)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase);
    }

    public static string CategoryOfClass(string effClass)
    {
        EnsureBuilt();
        return effClass != null && _categoryByClass.TryGetValue(effClass, out var cat) ? cat : "Other";
    }

    public static IReadOnlyList<BaseInfo> BasesOf(string effClass)
    {
        EnsureBuilt();
        return effClass != null && _byClass.TryGetValue(effClass, out var l) ? l : (IReadOnlyList<BaseInfo>)Array.Empty<BaseInfo>();
    }

    public static BaseInfo FindBase(string path)
    {
        EnsureBuilt();
        return path != null && _byPath.TryGetValue(path, out var b) ? b : null;
    }

    public static bool HasClass(string effClass)
    {
        EnsureBuilt();
        return effClass != null && _byClass.ContainsKey(effClass);
    }

    /// <summary>True when the class is the (synthetic) cluster jewel class - its bases carry size tags
    /// (expansion_jewel_*) and the meaningful pick is the passive type, not a base item.</summary>
    public static bool IsClusterClass(string itemClass) =>
        BasesOf(itemClass).Any(b => b.Tags != null && b.Tags.Any(ClusterJewelTypes.IsSizeTag));

    /// <summary>A single base that represents the craft's selection for per-base library queries: the first
    /// selected base, else the first base of the class+subtype. Valid because selections are homogeneous
    /// (one class/subtype, or specific bases that share eligibility-relevant tags).</summary>
    public static string RepresentativeBase(string itemClass, string subtype, IReadOnlyCollection<string> basePaths)
    {
        if (basePaths != null && basePaths.Count > 0) return basePaths.First();
        var filter = subtype ?? "";
        var bases = BasesOf(itemClass);
        foreach (var b in bases)
            if (filter.Length == 0 || b.Tags.Contains(filter)) return b.Path;
        return bases.Count > 0 ? bases[0].Path : null;
    }

    private static bool IsGear(string path)
    {
        var rest = path.AsSpan(ItemsPrefix.Length);
        var slash = rest.IndexOf('/');
        if (slash <= 0) return false;
        return GearRoots.Contains(rest[..slash].ToString());
    }

    private static string CategoryFromPath(string path)
    {
        var parts = path.Substring(ItemsPrefix.Length).Split('/');
        var root = parts.Length > 0 ? parts[0] : "";
        var sub = parts.Length > 1 ? parts[1] : "";
        return root switch
        {
            "Weapons" when sub == "OneHandWeapons" => "One-Handed Weapons",
            "Weapons" when sub == "TwoHandWeapons" => "Two-Handed Weapons",
            "Weapons" => "Weapons",
            "Armours" when sub == "Shields" => "Off-hand",
            "Armours" => "Armour",
            "Quivers" => "Off-hand",
            "Rings" or "Amulets" or "Belts" or "Trinkets" => "Jewellery",
            "Jewels" => "Jewels",
            "Flasks" => "Flasks",
            "Maps" => "Maps",
            _ => "Other",
        };
    }
}
