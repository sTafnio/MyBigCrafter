using System;
using System.Linq;
using ItemFilterLibrary;

namespace MyBigCrafter.Model;

public enum FieldType { Number, Enum, Bool }

/// <summary>One scalar item property a Field leaf can test: how to read it and which widget edits it.</summary>
public sealed class ItemField
{
    public string Key { get; init; }
    public string Label { get; init; }
    public FieldType Type { get; init; }
    public string[] Options { get; init; }              // Enum: allowed values
    public Func<ItemData, int> Num { get; init; }       // Number / Bool (bool reported as 0|1)
    public Func<ItemData, string> Text { get; init; }   // Enum: current value
}

/// <summary>
/// The set of scalar item properties a Check's Field leaf can test. Adding a craftable property is a single
/// entry here - no model, evaluator, or editor changes needed. Mods (HasMod) and raw IFL queries are handled
/// separately because they are not simple property-compare leaves.
/// </summary>
public static class FieldCatalog
{
    public static readonly ItemField[] All =
    {
        // --- numbers ---
        Num ("ItemLevel",   "Item level",     i => i.ItemLevel),
        Num ("Quality",     "Quality",        i => i.ItemQuality),
        Num ("PrefixCount", "Prefix count",   i => i.ModsInfo?.Prefixes.Count ?? 0),
        Num ("SuffixCount", "Suffix count",   i => i.ModsInfo?.Suffixes.Count ?? 0),
        Num ("AffixCount",  "Total affixes",  i => (i.ModsInfo?.Prefixes.Count ?? 0) + (i.ModsInfo?.Suffixes.Count ?? 0)),
        Num ("OpenPrefix",  "Open prefixes",  i => Math.Max(0, i.ModsInfo?.OpenPrefixCount ?? 0)),
        Num ("OpenSuffix",  "Open suffixes",  i => Math.Max(0, i.ModsInfo?.OpenSuffixCount ?? 0)),
        Num ("Sockets",     "Sockets",        i => i.SocketInfo?.SocketNumber ?? 0),
        Num ("Links",       "Links",          i => i.SocketInfo?.LargestLinkSize ?? 0),
        Num ("ImplicitCount", "Implicit count", i => i.ModsInfo?.ImplicitMods?.Count ?? 0),
        Num ("FracturedCount", "Fractured count", i => i.FracturedModCount),
        Num ("VeiledCount", "Veiled count",   i => i.VeiledModCount),
        Num ("MapTier",     "Map tier",       i => i.MapInfo?.Tier ?? 0),
        // Aggregated map reward totals (the tooltip values, already summed across all mods by the game).
        Num ("MapQuantity", "Map item quantity",  i => i.MapInfo?.Quantity ?? 0),
        Num ("MapRarity",   "Map item rarity",    i => i.MapInfo?.Rarity ?? 0),
        Num ("MapPackSize", "Map pack size",      i => i.MapInfo?.PackSize ?? 0),
        Num ("MapMoreMaps", "Map more maps",      i => i.MapInfo?.MoreMaps ?? 0),
        Num ("MapMoreScarabs",  "Map more scarabs",  i => i.MapInfo?.MoreScarabs ?? 0),
        Num ("MapMoreCurrency", "Map more currency", i => i.MapInfo?.MoreCurrency ?? 0),

        // --- enum ---
        Enum("Rarity",      "Rarity", new[] { "Normal", "Magic", "Rare", "Unique" }, i => i.Rarity.ToString()),

        // --- flags ---
        Bool("Identified",  "Identified",     i => i.IsIdentified),
        Bool("Corrupted",   "Corrupted",      i => i.IsCorrupted),
        Bool("Mirrored",    "Mirrored",       i => i.IsMirrored),
        Bool("Synthesised", "Synthesised",    i => i.IsSynthesised),
        Bool("Fractured",   "Fractured",      i => i.FracturedModCount > 0),
        Bool("Enchanted",   "Enchanted",      i => i.Enchanted),
        Bool("Influenced",  "Influenced (any)", i => i.IsInfluenced),
        Bool("Shaper",      "Shaper",         i => i.IsShaper),
        Bool("Elder",       "Elder",          i => i.IsElder),
        Bool("Crusader",    "Crusader",       i => i.IsCrusader),
        Bool("Redeemer",    "Redeemer",       i => i.IsRedeemer),
        Bool("Hunter",      "Hunter",         i => i.IsHunter),
        Bool("Warlord",     "Warlord",        i => i.IsWarlord),
    };

    public static ItemField Get(string key) => All.FirstOrDefault(f => f.Key == key);

    private static ItemField Num(string key, string label, Func<ItemData, int> num)
        => new() { Key = key, Label = label, Type = FieldType.Number, Num = num };

    private static ItemField Bool(string key, string label, Func<ItemData, bool> b)
        => new() { Key = key, Label = label, Type = FieldType.Bool, Num = i => b(i) ? 1 : 0 };

    private static ItemField Enum(string key, string label, string[] options, Func<ItemData, string> text)
        => new() { Key = key, Label = label, Type = FieldType.Enum, Options = options, Text = text };
}
