using System;
using System.Linq;
using ItemFilterLibrary;
using MyBigCrafter.Data;

namespace MyBigCrafter.Model;

/// <summary>Decides whether an item is one this craft should operate on: class + subtype + bases + filter.</summary>
public static class ItemMatcher
{
    /// <summary>Full match: the base selection plus the advanced filter. Used to pick input items to craft.</summary>
    public static bool Matches(CraftPlan plan, ItemData item) =>
        MatchesBase(plan, item) && CheckEvaluator.Passes(plan.Filter, item);

    /// <summary>Base selection only (class + subtype + cluster type + bases), ignoring the advanced filter.
    /// Used to recognise an item already in the currency tab, which may be mid-craft and no longer pass it.</summary>
    public static bool MatchesBase(CraftPlan plan, ItemData item)
    {
        if (plan == null || item == null) return false;

        // Compare against the repository's effective class so the synthetic "Cluster Jewel" class
        // matches (and plain "Jewel" no longer swallows cluster jewels).
        if (!string.IsNullOrEmpty(plan.ItemClass) &&
            !string.Equals(BaseTaxonomy.EffectiveClassName(item.ClassName, item.Path), plan.ItemClass,
                StringComparison.OrdinalIgnoreCase))
            return false;

        // Subtype is an armour attribute tag or a cluster size tag - both live in the item's base tags.
        if (!string.IsNullOrEmpty(plan.Subtype) && (item.Tags == null || !item.Tags.Contains(plan.Subtype)))
            return false;

        if (plan.ClusterTypes is { Count: > 0 })
        {
            // Cluster passive type lives in item data (LocalStats node index), not in base tags. Empty list
            // = any type of the chosen size; otherwise the item's type must be one of the selected ones.
            var ct = ClusterJewelTypes.TypeOf(item);
            if (ct == null || !plan.ClusterTypes.Contains(ct.Tag, StringComparer.OrdinalIgnoreCase))
                return false;
        }

        if (plan.BasePaths is { Count: > 0 } && !plan.BasePaths.Contains(item.Path))
            return false;

        return true;
    }
}
