using System;
using System.Collections.Generic;
using PoeModDataLib.Api;

namespace MyBigCrafter.Data;

/// <summary>
/// Currency base names usable for crafting, for the Apply node picker. Sourced from RePoE (PoeModDataLib),
/// classified by each base's usage <c>directions</c>: a craft currency is right-clicked then left-clicked
/// onto an item "to apply it" / "to corrupt it" / "(the item) you wish to modify". That naturally separates
/// the apply-to-item orbs/scrolls/essences/catalysts (incl. Scroll of Wisdom) from use-on-self scrolls
/// (Portal), trade items (coins), shards, and itemising orbs (Beasts / Corpses / Voidstones) - no live tag
/// data needed, and no hand-maintained allow/deny lists.
/// </summary>
public sealed class CurrencyCatalog
{
    // RePoE item_class that holds the stacked currencies (orbs, scrolls, essences, catalysts, delirium, …).
    private const string CurrencyClass = "StackableCurrency";

    public List<string> Names { get; } = new();
    public bool IsBuilt { get; private set; }

    public void EnsureBuilt()
    {
        if (IsBuilt) return;

        var data = PoeModData.Instance;
        if (data == null || !data.IsReady) return;

        var bases = data.BasesOfClass(CurrencyClass);
        if (bases == null || bases.Count == 0) return;

        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in bases)
            if (!string.IsNullOrWhiteSpace(b.Name) && IsAppliedToItem(b.Properties?.Directions))
                set.Add(b.Name);

        Names.AddRange(set);
        IsBuilt = Names.Count > 0;
    }

    // A craft currency's directions read "Right click this item then left click <target> to apply it" (or
    // "to corrupt it" / "the item you wish to modify"). Require the left-click-a-target form and an
    // item-modifying goal, then drop the targets that aren't a held item: itemising orbs (Beast / Corpse) and
    // atlas currencies (Voidstone sextants, Atlas map seals).
    private static bool IsAppliedToItem(string directions)
    {
        if (string.IsNullOrEmpty(directions)) return false;
        if (directions.IndexOf("left click", StringComparison.OrdinalIgnoreCase) < 0) return false;
        if (ContainsAny(directions, "itemise", "Beast", "Corpse", "Voidstone", "Atlas")) return false;
        return ContainsAny(directions, "to apply it", "to corrupt it", "you wish to modify");
    }

    private static bool ContainsAny(string s, params string[] needles)
    {
        foreach (var n in needles)
            if (s.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }
}
