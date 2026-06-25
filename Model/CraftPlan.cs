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

    public CraftGraph Graph { get; set; } = new();
}
