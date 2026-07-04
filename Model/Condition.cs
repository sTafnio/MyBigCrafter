using System.Collections.Generic;

namespace MyBigCrafter.Model;

/// <summary>
/// What a leaf tests. Group combines children; Field tests one catalog property (item level, prefix count,
/// rarity, identified, ...) via <see cref="FieldCatalog"/>; HasMod tests a picked affix; Set counts the item's
/// mods against a named per-craft <see cref="ModSet"/>; Raw is an IFL escape hatch.
/// </summary>
public enum CondKind { Group, Field, HasMod, Raw, Set }

/// <summary>How a group combines its children: All (AND), Any (OR), or Count (N of them, via CountOp/CountValue).</summary>
public enum GroupOp { All, Any, Count }

/// <summary>Which of the item's explicit mods a Set leaf counts: all of them, or one affix side only.</summary>
public enum SetScope { Mods, Prefixes, Suffixes }

/// <summary>
/// A node in a Check's condition tree. A Group combines child conditions (All / Any / Count-of); a leaf tests
/// one thing about the item. Scalar properties go through one <see cref="CondKind.Field"/> leaf driven by
/// <see cref="FieldCatalog"/> (so adding a property is a one-line catalog entry); mods use HasMod; Raw is the
/// IFL escape hatch. Any node can be negated. Mirrors the And/Or/Count structure of WheresMyCraftAt rules,
/// but built visually with mods read from the game files.
/// </summary>
public sealed class Condition
{
    public CondKind Kind { get; set; } = CondKind.Group;
    public bool Negate { get; set; }

    // Group
    public GroupOp Op { get; set; } = GroupOp.All;
    public string CountOp { get; set; } = ">=";   // > >= < <= == !=
    public int CountValue { get; set; } = 1;
    public List<Condition> Children { get; set; } = new();

    // Field leaf: a FieldCatalog key compared by Number (numeric/bool fields) or Value (enum fields).
    public string Field { get; set; } = "ItemLevel";
    public NumberCond Number { get; set; } = new();
    public string Value { get; set; } = "";        // enum value (e.g. Rarity name) OR the Raw query string

    // HasMod leaf
    public ModRequirement Mod { get; set; }         // MustHave forced true; negate via Negate

    // Set leaf: "count of [scope] [in|not in] set <Set>" compared by Number (default "any" = at least one).
    // SetInvert counts the mods that match NO entry of the set ("junk" counting for exalt/annul decisions);
    // Negate still negates the whole comparison - both are meaningful and independent.
    public string Set { get; set; } = "";
    public SetScope SetScope { get; set; } = SetScope.Mods;
    public bool SetInvert { get; set; }

    public static Condition NewGroup() => new() { Kind = CondKind.Group, Op = GroupOp.All };
    public static Condition NewCountGroup() => new() { Kind = CondKind.Group, Op = GroupOp.Count, CountOp = ">=", CountValue = 2 };
    public static Condition NewLeaf() => new() { Kind = CondKind.Field, Field = "ItemLevel" };
    public static Condition NewMod() => new() { Kind = CondKind.HasMod, Mod = new ModRequirement { Category = ModCategory.Explicit } };
    public static Condition NewSet(string setName = "") => new() { Kind = CondKind.Set, Set = setName };
}
