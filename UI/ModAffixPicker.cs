using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using MyBigCrafter.Model;
using PoeModDataLib.Api;

namespace MyBigCrafter.UI;

/// <summary>What pool the picker shows.</summary>
public enum PickMode { Explicit, Implicit, Enchant }

/// <summary>A mod chosen in the picker: the family, a tier bound (0 = any), the section's affix, and the
/// sub-type it came from (an influence codename, or essence).</summary>
public readonly record struct PickResult(ModFamily Family, int Tier, string Affix, string Influence, bool Essence);

/// <summary>
/// A reusable "pick one mod" window over PoeModDataLib families. Explicit mode is a poedb-style two-column
/// layout (Prefixes left, Suffixes right) with collapsing sub-sections - Regular, the six influences, then
/// Essence - all scoped to the selected (representative) base. Eldritch implicits sit full-width below.
/// Implicit/Enchant modes show their pool as a single list.
/// </summary>
public static class ModAffixPicker
{
    private const ImGuiTableFlags TableFlags =
        ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingFixedFit |
        ImGuiTableFlags.Resizable;

    private static readonly Dictionary<string, string> Search = new();
    private static string _openId;
    private static readonly HashSet<string> Expanded = new();

    // Frameless tier-expand arrow: invisible background, faint highlight on hover/press.
    private static readonly Vector4 Transparent = new(0, 0, 0, 0);
    private static readonly Vector4 ArrowHover = new(1f, 1f, 1f, 0.12f);

    // Eldritch currency can only target these four armour slots.
    private static readonly HashSet<string> EldritchSlots = new(StringComparer.OrdinalIgnoreCase)
        { "helmet", "body_armour", "gloves", "boots" };

    // Map (Area-domain) mod sections, shown top-to-bottom in place of the gear "Regular" section. Each key is
    // the tier ModFamilies.ForMapTier resolves against (low/mid/top_tier_map, or "uber" for the Nightmare pool).
    private static readonly (string Key, string Name)[] MapTiers =
        { ("uber", "Uber"), ("top_tier_map", "High"), ("mid_tier_map", "Mid"), ("low_tier_map", "Low") };

    public static void Show(string id) => _openId = id;

    public static PickResult? Draw(string id, string basePath, string domain, string itemClass,
        PickMode mode = PickMode.Explicit, IReadOnlyList<string> clusterTypeTags = null)
    {
        if (_openId != id) return null;

        PickResult? picked = null;

        ImGui.SetNextWindowSize(new Vector2(720, 520), ImGuiCond.FirstUseEver);
        var open = true;
        // NoDocking: avoid the docking resize-loop that flickers white and freezes these tool windows.
        if (ImGui.Begin("Pick a mod###modpick", ref open, ImGuiWindowFlags.NoDocking))
        {
            if (string.IsNullOrEmpty(basePath))
            {
                ImGui.TextDisabled("Pick a class/base on the Item Selection tab first.");
            }
            else
            {
                var s = Search.TryGetValue(id, out var sv) ? sv : "";
                ImGui.SetNextItemWidth(-1);
                ImGui.InputText($"##s_{id}", ref s, 64);
                Search[id] = s;
                var q = s.Trim();
                var fo = q.Length > 0;

                switch (mode)
                {
                    case PickMode.Implicit:
                        picked = RenderSection(MakeSec("Implicits", "implicit", ModFamilies.Implicits(basePath),
                            "Unique", "", false, domain, true, true), q, fo);
                        break;
                    case PickMode.Enchant:
                        picked = RenderSection(MakeSec("Enchants", "enchant", ModFamilies.Enchants(basePath),
                            "Unique", "", false, domain, true, true), q, fo);
                        break;
                    default:
                        picked = ExplicitColumns(basePath, itemClass, domain, q, fo, clusterTypeTags);

                        if (picked == null && EligibleEldritch(basePath))
                            picked = RenderSection(MakeSec("Searing Exarch implicits", "exarch",
                                         ModFamilies.For(basePath, "ExarchImplicit"), "ExarchImplicit", "", false, domain, false, true), q, fo)
                                     ?? RenderSection(MakeSec("Eater of Worlds implicits", "eater",
                                         ModFamilies.For(basePath, "EaterImplicit"), "EaterImplicit", "", false, domain, false, true), q, fo);
                        break;
                }
            }
        }
        ImGui.End();

        if (picked != null) open = false;
        if (!open) _openId = null;
        return picked;
    }

    private static bool EligibleEldritch(string basePath)
    {
        var b = PoeModData.Instance.GetBase(basePath);
        return b?.Tags != null && b.Tags.Any(EldritchSlots.Contains);
    }

    /// <summary>One pickable sub-section: a list of families for one affix, plus its section context.</summary>
    private readonly struct Sec
    {
        public readonly string Display;
        public readonly string Key;
        public readonly IReadOnlyList<ModFamily> Families;
        public readonly string Affix;
        public readonly string Influence;
        public readonly bool Essence;
        public readonly string Domain;
        public readonly bool DefaultOpen;
        public readonly bool AllowTierPick;
        public Sec(string display, string key, IReadOnlyList<ModFamily> families, string affix, string influence,
                   bool essence, string domain, bool defaultOpen, bool allowTierPick)
        { Display = display; Key = key; Families = families; Affix = affix; Influence = influence; Essence = essence; Domain = domain; DefaultOpen = defaultOpen; AllowTierPick = allowTierPick; }
    }

    private static Sec MakeSec(string display, string key, IReadOnlyList<ModFamily> families, string affix,
        string influence, bool essence, string domain, bool defaultOpen, bool allowTierPick) =>
        new(display, key, families, affix, influence, essence, domain, defaultOpen, allowTierPick);

    private static PickResult? ExplicitColumns(string basePath, string itemClass, string domain, string q, bool fo,
        IReadOnlyList<string> clusterTypeTags)
    {
        // Maps: one combined collapsing header per tier (full width) so a single click opens that tier's
        // prefixes (left) and suffixes (right) together.
        if (string.Equals(domain, "Area", StringComparison.OrdinalIgnoreCase))
            return MapColumns(basePath, domain, q, fo);

        PickResult? picked = null;

        // Regular: for cluster jewels the selected passive-type tag(s) bring in that type's notables.
        picked = CombinedHeaderRow(picked, "Regular", "regular",
            MakeSec("Regular", "Prefix_regular", ModFamilies.For(basePath, "Prefix", null, clusterTypeTags), "Prefix", "", false, domain, true, true),
            MakeSec("Regular", "Suffix_regular", ModFamilies.For(basePath, "Suffix", null, clusterTypeTags), "Suffix", "", false, domain, true, true), q, fo);

        // Influence sections (gear). The library already returns only the influence-unlocked families.
        foreach (var inf in Influences.All)
            picked = CombinedHeaderRow(picked, inf.Name, inf.Suffix,
                MakeSec(inf.Name, $"Prefix_{inf.Suffix}", ModFamilies.For(basePath, "Prefix", inf.Suffix), "Prefix", inf.Suffix, false, domain, false, true),
                MakeSec(inf.Name, $"Suffix_{inf.Suffix}", ModFamilies.For(basePath, "Suffix", inf.Suffix), "Suffix", inf.Suffix, false, domain, false, true), q, fo);

        var (essPre, essSuf) = ModFamilies.Essence(itemClass);
        picked = CombinedHeaderRow(picked, "Essence", "essence",
            MakeSec("Essence", "Prefix_Essence", essPre, "Prefix", "", true, domain, false, false),
            MakeSec("Essence", "Suffix_Essence", essSuf, "Suffix", "", true, domain, false, false), q, fo);

        return picked;
    }

    // Maps: a combined collapsing header per tier (Uber/High/Mid/Low) - same one-header-per-section layout as
    // the gear sections. No tier is auto-opened.
    private static PickResult? MapColumns(string basePath, string domain, string q, bool fo)
    {
        PickResult? picked = null;
        foreach (var (key, name) in MapTiers)
            picked = CombinedHeaderRow(picked, name, key,
                MakeSec(name, $"Prefix_{key}", ModFamilies.ForMapTier(basePath, "Prefix", key), "Prefix", "", false, domain, false, false),
                MakeSec(name, $"Suffix_{key}", ModFamilies.ForMapTier(basePath, "Suffix", key), "Suffix", "", false, domain, false, false), q, fo);
        return picked;
    }

    // One section as a single full-width collapsing header that, when open, shows its prefixes (left) and
    // suffixes (right) side by side - one click reveals both. Used for EVERY explicit section: Regular, the six
    // influences, Essence, and the map tiers. DefaultOpen comes from the section (Regular opens by default).
    private static PickResult? CombinedHeaderRow(PickResult? picked, string name, string key, Sec pre, Sec suf, string q, bool fo)
    {
        if (pre.Families.Count == 0 && suf.Families.Count == 0) return picked;
        if (fo) ImGui.SetNextItemOpen(true, ImGuiCond.Always);
        var flags = pre.DefaultOpen ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
        if (!ImGui.CollapsingHeader($"{name}###sec_{key}", flags)) return picked;
        if (ImGui.BeginTable($"##seccols_{key}", 2,
                ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableSetupColumn("Prefixes");
            ImGui.TableSetupColumn("Suffixes");
            ImGui.TableHeadersRow();
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            var p0 = RenderModTable(pre, q);
            ImGui.TableSetColumnIndex(1);
            var p1 = RenderModTable(suf, q);
            ImGui.EndTable();
            picked = picked ?? p0 ?? p1;
        }
        return picked;
    }

    private static PickResult? RenderSection(Sec sec, string q, bool forceOpen)
    {
        if (sec.Families.Count == 0) return null;

        if (forceOpen) ImGui.SetNextItemOpen(true, ImGuiCond.Always);
        var hdrFlags = sec.DefaultOpen ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
        if (!ImGui.CollapsingHeader($"{sec.Display}###hdr_{sec.Key}", hdrFlags))
            return null;
        return RenderModTable(sec, q);
    }

    // The 4-column mod table for one section, without the collapsing header - the caller owns that, so the map
    // tiers can share ONE header across their prefix + suffix tables.
    private static PickResult? RenderModTable(Sec sec, string q)
    {
        if (sec.Families.Count == 0) return null;
        if (!ImGui.BeginTable($"##tbl_{sec.Key}", 4, TableFlags))
            return null;

        ImGui.TableSetupColumn("Mod (click to pick any tier)", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Tiers", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("Best T1", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("ilvl", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableHeadersRow();

        PickResult? picked = null;
        var color = UiColors.ForAffix(sec.Affix);

        ImGui.PushID(sec.Key);
        foreach (var fam in sec.Families.OrderBy(f => f.Display, StringComparer.OrdinalIgnoreCase))
        {
            if (q.Length > 0 && !fam.Display.Contains(q, StringComparison.OrdinalIgnoreCase))
                continue;
            if (fam.Tiers.Count == 0) continue;
            var best = fam.Tiers[0];

            ImGui.PushID(fam.Group + "|" + best.ModId);
            var expKey = $"{sec.Key}:{fam.Group}:{best.ModId}";
            var expanded = sec.AllowTierPick && Expanded.Contains(expKey);

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            if (sec.AllowTierPick)
            {
                // A tree-node style triangle (vector-drawn, so it renders in the default font) with a
                // transparent button background - no orange Header bar like a real TreeNode would draw.
                ImGui.PushStyleColor(ImGuiCol.Button, Transparent);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ArrowHover);
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, ArrowHover);
                ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(2, 0));
                var toggled = ImGui.ArrowButton("##exp", expanded ? ImGuiDir.Down : ImGuiDir.Right);
                ImGui.PopStyleVar();
                ImGui.PopStyleColor(3);
                if (toggled) { if (expanded) Expanded.Remove(expKey); else Expanded.Add(expKey); }
                ImGui.SameLine();
            }
            ImGui.PushStyleColor(ImGuiCol.Text, color);
            if (ImGui.Selectable($"{fam.Display}##pick", false))
                picked = new PickResult(fam, 0, sec.Affix, sec.Influence, sec.Essence);
            ImGui.PopStyleColor();

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(fam.Tiers.Count.ToString());
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(ModFamilies.BestRange(fam));
            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(best.RequiredLevel.ToString());

            if (expanded)
            {
                for (var i = 0; i < fam.Tiers.Count; i++)
                {
                    var t = fam.Tiers[i];
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    // "T1" for the top tier (T1-and-better is just T1); "T2+", "T3+", ... below it.
                    var tierLabel = i == 0 ? "T1" : $"T{i + 1}+";
                    if (ImGui.Selectable($"      {tierLabel}##t{i}", false, ImGuiSelectableFlags.SpanAllColumns))
                        picked = new PickResult(fam, i + 1, sec.Affix, sec.Influence, sec.Essence);
                    ImGui.TableSetColumnIndex(2);
                    ImGui.TextDisabled(ModFamilies.RangeOf(t.ModId));
                    ImGui.TableSetColumnIndex(3);
                    ImGui.TextDisabled(t.RequiredLevel.ToString());
                }
            }

            ImGui.PopID();
        }
        ImGui.PopID();
        ImGui.EndTable();

        return picked;
    }

    public static ModRequirement ToRequirement(PickResult p, string domain)
    {
        var category = p.Affix switch
        {
            "Unique" => ModCategory.Implicit,   // implicit/enchant pools (refined by the leaf's own category)
            _ => ModCategory.Explicit,
        };
        return ModFamilies.ToRequirement(p.Family, p.Tier, p.Affix, category, p.Influence, p.Essence, domain);
    }
}
