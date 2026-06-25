using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ExileCore.PoEMemory.Components;
using ExileCore.Shared.Enums;
using ItemFilterLibrary;
using PoeModDataLib.Api;

namespace MyBigCrafter.Data;

/// <summary>One cluster jewel passive type ("Added Small Passive Skills grant: ...").</summary>
public sealed record ClusterType(int Index, string Size, string Name, string Tag, string StatText)
{
    public string Label => $"{Size}: {Name}";
}

/// <summary>
/// Cluster jewel passive types. The type is NOT a mod on the item - the game stores it as
/// LocalStats stat <c>LocalJewelExpansionPassiveNodeIndex</c>, a 1-based row index into the
/// passive-type table (Large rows 1-17, Medium 18-38, Small 39-55; verified live on three jewels).
/// Each type's <see cref="ClusterType.Tag"/> is the spawn-weight tag the rollable notables/mods are
/// gated on, and the base item contributes its size tag (expansion_jewel_small/medium/large).
/// The table is sourced LIVE from PoeModDataLib's cluster_jewels.json (<see cref="All"/>), so it tracks
/// the dataset automatically - no hand-maintained copy. The two "old_do_not_use_*" tags are real: live
/// mods still gate on them.
/// </summary>
public static class ClusterJewelTypes
{
    private static ClusterType[] _all;

    /// <summary>The cluster passive types in game index order, built once from PoeModDataLib
    /// (<c>ClusterJewelPassives</c>): Large 1-17, Medium 18-38, Small 39-55, the 1-based position matching
    /// the live item's <c>LocalJewelExpansionPassiveNodeIndex</c>. Empty until the library is ready.</summary>
    public static ClusterType[] All
    {
        get
        {
            if (_all != null) return _all;
            var passives = PoeModData.Instance?.ClusterJewelPassives();
            if (passives == null || passives.Count == 0) return Array.Empty<ClusterType>();
            var arr = new ClusterType[passives.Count];
            for (var i = 0; i < passives.Count; i++)
            {
                var p = passives[i];
                arr[i] = new ClusterType(i + 1, p.Size, p.Name, p.Tag,
                    string.Join("\n", p.StatText ?? Array.Empty<string>()));
            }
            return _all = arr;
        }
    }

    private static Dictionary<string, ClusterType> _byTag;

    public static ClusterType ByIndex(int oneBased)
    {
        var all = All;
        return oneBased >= 1 && oneBased <= all.Length ? all[oneBased - 1] : null;
    }

    /// <summary>First type with this tag (the two block types share "affliction_chance_to_block" -
    /// same mod pool, so first-wins is fine). Rebuilt while the table is still empty (library not ready).</summary>
    public static ClusterType ByTag(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return null;
        if (_byTag == null || _byTag.Count == 0)
        {
            var byTag = new Dictionary<string, ClusterType>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in All) byTag.TryAdd(t.Tag, t);
            _byTag = byTag;
        }
        return _byTag.GetValueOrDefault(tag);
    }

    /// <summary>True for a cluster jewel size tag (expansion_jewel_small/medium/large).</summary>
    public static bool IsSizeTag(string tag) =>
        tag != null && tag.StartsWith("expansion_jewel_", StringComparison.OrdinalIgnoreCase);

    /// <summary>The distinct passive types available for a size tag (the two Small block types share a tag).</summary>
    public static IEnumerable<ClusterType> OfSizeTag(string sizeTag)
    {
        if (string.IsNullOrEmpty(sizeTag)) yield break;
        var size = sizeTag.Replace("expansion_jewel_", "", StringComparison.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ct in All)
            if (string.Equals(ct.Size, size, StringComparison.OrdinalIgnoreCase) && seen.Add(ct.Tag))
                yield return ct;
    }

    private const string GrantPrefix = "Added Small Passive Skills grant: ";
    private static readonly Regex Digits = new(@"\d+", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Set when a live jewel disagrees with the RePoE table (the cluster passive table changed in a
    /// patch the cached dataset hasn't caught up to) - shown in the UI as an "update the RePoE data" hint.</summary>
    public static string DriftWarning { get; private set; }

    /// <summary>
    /// Reads a live item's cluster passive type (null when not a cluster jewel). The LocalStats node
    /// index is the primary key; the rendered "Added Small Passive Skills grant" enchant line
    /// cross-checks it (digit-insensitive, so balance-only patches still pass). On a mismatch -
    /// e.g. a patch inserted table rows and shifted the indexes - the text match wins and a drift
    /// warning is raised so the table gets regenerated.
    /// </summary>
    public static ClusterType TypeOf(ItemData item)
    {
        try
        {
            var stats = item?.Entity?.GetComponent<LocalStats>()?.StatDictionary;
            if (stats == null || !stats.TryGetValue(GameStat.LocalJewelExpansionPassiveNodeIndex, out var idx))
                return null;
            var byIndex = ByIndex(idx);

            var grant = item.Entity.GetComponent<Mods>()?.EnchantedStats?
                .FirstOrDefault(s => s != null && s.StartsWith(GrantPrefix, StringComparison.Ordinal));
            if (grant == null) return byIndex;   // no rendered text available - trust the index

            var text = Normalize(grant.Substring(GrantPrefix.Length));
            if (byIndex != null && Normalize(byIndex.StatText) == text) return byIndex;

            var byText = All.FirstOrDefault(t => Normalize(t.StatText) == text);
            if (byText != null)
            {
                if (byText != byIndex)
                    DriftWarning = $"RePoE cluster table looks a patch behind (index {idx} resolved by text to '{byText.Label}') - update the RePoE dataset.";
                return byText;
            }

            DriftWarning = $"Unknown cluster passive type (index {idx}, '{grant}') - the RePoE dataset may be a patch behind.";
            return byIndex;
        }
        catch
        {
            // component layout changed or entity gone - treat as unknown
            return null;
        }
    }

    private static string Normalize(string s) =>
        Spaces.Replace(Digits.Replace(s, "#"), " ").Trim().ToLowerInvariant();
}
