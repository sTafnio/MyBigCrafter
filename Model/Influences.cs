using System;
using System.Collections.Generic;
using System.Linq;

namespace MyBigCrafter.Model;

/// <summary>
/// The six item influences and their internal spawn-tag suffix, verified live against
/// <c>GameController.Files.Mods</c>. An influence modifier spawn-gates on a <c>{slotTag}_{Suffix}</c> tag the
/// base gains when influenced (e.g. <c>body_armour_shaper</c>, <c>amulet_crusader</c>, <c>quiver_basilisk</c>),
/// listed first in the mod's spawn weights ahead of <c>default:0</c>. The four conqueror influences use
/// codenames in the data: Hunter = basilisk, Redeemer = eyrie, Warlord = adjudicator.
/// </summary>
public static class Influences
{
    public sealed record Influence(string Name, string Suffix);

    /// <summary>In the order the user asked for (Shaper, Elder, Hunter, Crusader, Warlord, Redeemer).</summary>
    public static readonly IReadOnlyList<Influence> All = new[]
    {
        new Influence("Shaper", "shaper"),
        new Influence("Elder", "elder"),
        new Influence("Hunter", "basilisk"),
        new Influence("Crusader", "crusader"),
        new Influence("Warlord", "adjudicator"),
        new Influence("Redeemer", "eyrie"),
    };

    private static readonly Dictionary<string, string> NameBySuffix =
        All.ToDictionary(i => i.Suffix, i => i.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Display name for a tag suffix (e.g. "basilisk" -> "Hunter"); the suffix itself if unknown.</summary>
    public static string NameOf(string suffix) =>
        suffix != null && NameBySuffix.TryGetValue(suffix, out var n) ? n : suffix ?? "";
}
