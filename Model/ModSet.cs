using System.Collections.Generic;

namespace MyBigCrafter.Model;

/// <summary>
/// A named, per-craft list of mod requirements ("Wanted Prefixes", "Good Suffixes", ...) that Set condition
/// leaves count against. Defined once on the Mod Sets tab and referenced by name from any number of Check
/// nodes / the item filter, so a big craft's target list is edited in ONE place. Entries are explicit-pool
/// requirements (regular, influence or essence affixes); implicit/enchant/eldritch one-offs stay as plain
/// Mod leaves in the condition tree.
/// </summary>
public sealed class ModSet
{
    public string Name { get; set; } = "";
    public List<ModRequirement> Mods { get; set; } = new();
}
