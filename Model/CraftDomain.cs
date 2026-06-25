using System;

namespace MyBigCrafter.Model;

/// <summary>Maps a craft's item class/category to the mod domain its affixes live in.</summary>
public static class CraftDomain
{
    public static string For(string itemClass, string category)
    {
        if (string.Equals(itemClass, "Tincture", StringComparison.OrdinalIgnoreCase)) return "Tincture";
        if (category == "Maps") return "Area";       // map mods live in the Area mod domain
        if (category == "Flasks") return "Flask";
        if (category == "Jewels")
        {
            if (itemClass != null && itemClass.IndexOf("Abyss", StringComparison.OrdinalIgnoreCase) >= 0)
                return "AbyssJewel";
            // "Cluster Jewel" is the repository's synthetic class (game ClassName is "Jewel").
            if (string.Equals(itemClass, "Cluster Jewel", StringComparison.OrdinalIgnoreCase))
                return "ClusterJewel";
            return "BaseJewel";   // verified live: regular jewel affixes are ModDomain "BaseJewel" (RePoE "misc")
        }
        return "Item";
    }
}
