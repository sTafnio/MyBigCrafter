using System;
using System.Collections.Generic;
using System.Linq;
using MyBigCrafter.Data;

namespace MyBigCrafter.Model;

/// <summary>
/// Attribute subtypes for armour classes (Gloves(int), Body Armours(str_dex), …), derived from base
/// spawn tags. Data-driven - the only curated bit is the tag-&gt;label mapping for the 7 attribute combos.
/// </summary>
public static class Subtypes
{
    public static readonly (string Tag, string Label)[] Armour =
    {
        ("str_armour", "Strength"),
        ("dex_armour", "Dexterity"),
        ("int_armour", "Intelligence"),
        ("str_dex_armour", "Str / Dex"),
        ("str_int_armour", "Str / Int"),
        ("dex_int_armour", "Dex / Int"),
        ("str_dex_int_armour", "Str / Dex / Int"),
    };

    /// <summary>The subtypes actually present among a class's bases: armour attribute combos, plus cluster
    /// jewel passive types ("Large: Fire Damage", ...) when the class contains cluster bases.</summary>
    public static List<(string Tag, string Label)> For(string itemClass)
    {
        var bases = BaseTaxonomy.BasesOf(itemClass);
        if (bases.Count == 0) return new List<(string, string)>();

        var present = new HashSet<string>();
        foreach (var b in bases)
            foreach (var t in b.Tags)
                present.Add(t);

        var result = Armour.Where(a => present.Contains(a.Tag)).ToList();

        // Cluster jewels: the subtype is the SIZE (the real base). The passive type is picked separately
        // (it's a rolled property, not a base). Only the sizes actually present among the class's bases.
        foreach (var (tag, label) in ClusterSizes)
            if (present.Contains(tag))
                result.Add((tag, label));

        return result;
    }

    private static readonly (string Tag, string Label)[] ClusterSizes =
    {
        ("expansion_jewel_small", "Small"),
        ("expansion_jewel_medium", "Medium"),
        ("expansion_jewel_large", "Large"),
    };

    public static string Label(string tag)
    {
        foreach (var (t, l) in Armour)
            if (t == tag) return l;
        foreach (var (t, l) in ClusterSizes)
            if (t == tag) return l;
        var ct = ClusterJewelTypes.ByTag(tag);
        return ct != null ? ct.Label : tag;
    }
}
