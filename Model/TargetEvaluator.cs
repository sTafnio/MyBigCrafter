using System;
using System.Collections;
using System.Collections.Generic;
using ItemFilterLibrary;
using PoeModDataLib.Api;

namespace MyBigCrafter.Model;

/// <summary>Evaluates a <see cref="ModRequirement"/> (Yes/No presence + tier/value bound) against a live item,
/// classifying the item's mods through PoeModDataLib.</summary>
public static class TargetEvaluator
{
    private static bool IsEldritch(ModRequirement req) =>
        req.AffixType is "ExarchImplicit" or "EaterImplicit";

    /// <summary>True if the item has the required mod. Used by CHECK nodes: explicit mods search BOTH explicit
    /// and fractured; implicit/enchant search their own list.</summary>
    public static bool SatisfiedForCheck(ModRequirement req, ItemData item)
    {
        if (req == null || item?.ModsInfo == null) return false;
        var m = item.ModsInfo;
        var present = IsEldritch(req)
            ? AnyMatch(req, m.ImplicitMods, item)
            : req.Category switch
            {
                ModCategory.Implicit => AnyMatch(req, m.ImplicitMods, item),
                ModCategory.Enchant => AnyMatch(req, m.EnchantedMods, item),
                _ => AnyMatch(req, m.ExplicitMods, item) || AnyMatch(req, m.FracturedMods, item),
            };
        return req.MustHave ? present : !present;
    }

    // The element type of these mod lists lives in a referenced DLL; iterate as a plain IEnumerable and read
    // the mod's key + values via dynamic, so no DLL type name needs to be spelled out here.
    private static bool AnyMatch(ModRequirement req, IEnumerable modList, ItemData item)
    {
        if (modList == null) return false;
        foreach (dynamic mod in modList)
        {
            string key = mod.ModRecord?.Key;
            var values = mod.Values;
            var n = values != null ? (int)values.Count : 0;
            var vals = new int[n];
            for (var i = 0; i < n; i++) vals[i] = (int)values[i];
            if (MatchesKey(req, key, vals, item.Path)) return true;
        }
        return false;
    }

    private static bool MatchesKey(ModRequirement req, string key, int[] values, string basePath)
    {
        if (string.IsNullOrEmpty(key)) return false;

        var data = PoeModData.Instance;
        var cls = data.ClassifyMod(key);
        if (cls == null) return false;
        if (!string.Equals(cls.Group, req.Group, StringComparison.OrdinalIgnoreCase)) return false;
        // Affix guard for the spawn-weighted pools (a stat can be both a prefix and a suffix). Implicit/enchant
        // ("Unique") are already pinned by the Category routing, so group+statSig is enough there.
        if (req.AffixType is "Prefix" or "Suffix" or "ExarchImplicit" or "EaterImplicit"
            && !AffixMatches(cls.GenerationType, req.AffixType)) return false;
        // Compare on the CORE signature (map reward-tax stats stripped) so a map-mod requirement matches the
        // effect on any map tier - the live mod's reward bundle (low/mid/top/uber) differs from the picked
        // one's but the effect is the same. No-op for gear, whose signatures carry no reward stats.
        if (!string.IsNullOrEmpty(req.StatSig) &&
            ModFamilies.CoreStatSig(cls.StatSig) != ModFamilies.CoreStatSig(req.StatSig)) return false;

        // Influence/essence mods share a family with their normally-rolled cousins, so require the matched
        // record to actually be that influence / essence-only mod - not just any mod in the family.
        if (req.Essence && !data.IsEssenceMod(key)) return false;
        if (!string.IsNullOrEmpty(req.Influence) && !InfluenceMatches(cls.Influence, req.Influence)) return false;

        // Essence is presence-only (the picker can't set a tier). TierOf is influence-aware in the library.
        if (req.Tier > 0 && !req.Essence)
        {
            var ti = data.TierOf(basePath, key);
            if (ti == null || ti.Tier > req.Tier) return false;   // not at the required tier (or better)
        }

        // per-value minimum bounds (hybrid mods roll several values, in the family's stat order)
        var bounds = req.MinValues;
        if (bounds != null)
            for (var i = 0; i < bounds.Count; i++)
            {
                if (!bounds[i].HasValue) continue;
                if (i >= values.Length || values[i] < bounds[i].Value) return false;
            }

        return true;
    }

    // crafter AffixType <-> RePoE generation_type.
    private static bool AffixMatches(string repoeGen, string affix) => affix switch
    {
        "Prefix" => repoeGen == "prefix",
        "Suffix" => repoeGen == "suffix",
        "ExarchImplicit" => repoeGen == "searing_exarch_implicit",
        "EaterImplicit" => repoeGen == "eater_of_worlds_implicit",
        _ => true,
    };

    // req.Influence is the spawn-tag codename (shaper/basilisk/…); ClassifyMod.Influence is the display name.
    private static bool InfluenceMatches(string clsInfluence, string reqInfluence)
    {
        if (string.IsNullOrEmpty(clsInfluence)) return false;
        return string.Equals(clsInfluence, reqInfluence, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(NameToCodename(clsInfluence), reqInfluence, StringComparison.OrdinalIgnoreCase);
    }

    private static string NameToCodename(string name) => name switch
    {
        "Shaper" => "shaper", "Elder" => "elder", "Hunter" => "basilisk",
        "Crusader" => "crusader", "Warlord" => "adjudicator", "Redeemer" => "eyrie",
        _ => name,
    };
}
