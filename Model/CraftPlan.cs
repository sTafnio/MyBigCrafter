using System;
using System.Collections.Generic;

namespace MyBigCrafter.Model;

public enum ModCategory { Explicit, Fractured, Implicit, Enchant }

/// <summary>A mod requirement: the item must have (or, if MustHave is false, must NOT have) a mod from this
/// ladder, matched against the item's mods of the given <see cref="Category"/> (explicit / fractured /
/// implicit / enchant). Tier is fixed when chosen from the picker (0 = any; else the item's mod must be that
/// tier or better). Minimum rolled values are optional per-value bounds (e.g. implicit at max via Blessed Orb,
/// or just the life half of a hybrid +armour/+life prefix).</summary>
public sealed class ModRequirement
{
    public string Group { get; set; } = "";
    public string AffixType { get; set; } = "";   // "Prefix" / "Suffix"
    public string StatSig { get; set; } = "";      // identifies the exact ladder (stat signature)
    public string Domain { get; set; } = "Item";   // mod domain (Item/Flask/Jewel/…)
    public string Label { get; set; } = "";        // display text

    public ModCategory Category { get; set; } = ModCategory.Explicit;
    public bool MustHave { get; set; } = true;     // false = "Not" (must not have)
    public int Tier { get; set; }                   // 0 = any tier; else require tier <= Tier

    /// <summary>Influence tag suffix (shaper/elder/basilisk=Hunter/crusader/adjudicator=Warlord/eyrie=Redeemer)
    /// when this is an influence modifier; "" otherwise. Influence mods land in ExplicitMods so Category stays
    /// Explicit - this is a display + match-precision hint (the matched mod must actually be that influence).</summary>
    public string Influence { get; set; } = "";

    /// <summary>True when this is an essence-only modifier (one essences guarantee that never rolls normally on
    /// the base). Lands in ExplicitMods so Category stays Explicit - a display + match-precision hint (the matched
    /// mod must be an essence-only record).</summary>
    public bool Essence { get; set; }

    /// <summary>Per-value minimum bounds aligned to the ladder's stat order - hybrid mods roll several
    /// values (e.g. Nautilus's = +armour and +life). null = no bound for that value.</summary>
    public List<int?> MinValues { get; set; } = new();
}

/// <summary>
/// The shareable craft definition: one item class + bases + an advanced filter (which items it applies to),
/// and a flow-chart graph (how to craft them). Serialized to JSON for save/load/share.
/// </summary>
public sealed class CraftPlan
{
    public string Name { get; set; } = "";

    public string ItemClass { get; set; } = "";
    public string Subtype { get; set; } = "";   // attribute tag (e.g. "int_armour") or cluster size tag (expansion_jewel_*); empty = any
    public List<string> ClusterTypes { get; set; } = new();   // cluster jewel passive-type tags; empty = any type of the chosen size
    public List<string> BasePaths { get; set; } = new();

    /// <summary>Which items this craft applies to (on top of class+bases): a condition tree (empty = all).</summary>
    public Condition Filter { get; set; } = new();

    /// <summary>Named mod lists (defined on the Mod Sets tab) that Set condition leaves count against.</summary>
    public List<ModSet> ModSets { get; set; } = new();

    public CraftGraph Graph { get; set; } = new();

    public ModSet FindSet(string name) =>
        string.IsNullOrEmpty(name) ? null
            : ModSets.Find(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>How many Set leaves (item filter + every Check node) reference the named set.</summary>
    public int CountSetReferences(string name)
    {
        var count = 0;
        VisitConditions(c =>
        {
            if (c.Kind == CondKind.Set && string.Equals(c.Set, name, StringComparison.OrdinalIgnoreCase)) count++;
        });
        return count;
    }

    /// <summary>Renames a set AND every leaf referencing it in one step, so dangling names can't exist.</summary>
    public void RenameSet(string oldName, string newName)
    {
        var set = FindSet(oldName);
        if (set != null) set.Name = newName;
        VisitConditions(c =>
        {
            if (c.Kind == CondKind.Set && string.Equals(c.Set, oldName, StringComparison.OrdinalIgnoreCase)) c.Set = newName;
        });
    }

    private void VisitConditions(Action<Condition> fn)
    {
        Visit(Filter, fn);
        foreach (var n in Graph.Nodes) Visit(n.Check, fn);
    }

    private static void Visit(Condition c, Action<Condition> fn)
    {
        if (c == null) return;
        fn(c);
        if (c.Children == null) return;
        foreach (var ch in c.Children) Visit(ch, fn);
    }
}
