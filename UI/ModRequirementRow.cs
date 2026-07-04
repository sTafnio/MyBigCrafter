using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using ImGuiNET;
using MyBigCrafter.Model;

namespace MyBigCrafter.UI;

/// <summary>
/// The shared "one mod requirement" row: a colored label that opens the <see cref="ModAffixPicker"/>, a tier
/// badge, an optional fractured toggle, and the min-roll popup. Used by the condition editor's Mod leaves and
/// by the Mod Sets tab, so requirements render (and edit) identically everywhere.
/// </summary>
public static class ModRequirementRow
{
    public const float TierColW = 36f;
    public const float ColGap = 6f;

    /// <summary>Picker/library context: the representative base plus its domain/class (and cluster type tags).</summary>
    public readonly struct Ctx
    {
        public readonly string BasePath;
        public readonly string Domain;
        public readonly string ItemClass;
        public readonly IReadOnlyList<string> ClusterTypes;
        public Ctx(string basePath, string domain, string itemClass, IReadOnlyList<string> clusterTypes)
        { BasePath = basePath; Domain = domain; ItemClass = itemClass; ClusterTypes = clusterTypes; }
    }

    /// <summary>
    /// Draws the row at the current cursor. The label cell is <paramref name="labelW"/> wide so the trailing
    /// tier/min-roll columns line up across stacked rows. Returns a replacement requirement when the user picks
    /// a different mod (the caller stores it; the pick keeps <paramref name="req"/>'s category so implicit/
    /// enchant/fractured leaves stay in their pool), or null when nothing changed.
    /// </summary>
    public static ModRequirement Draw(string pickId, ModRequirement req, in Ctx ctx, float labelW,
        bool allowFractured = false, bool includeEldritch = true)
    {
        var hasMod = !string.IsNullOrEmpty(req.Group);
        var label = hasMod ? req.Label : "(click to pick a mod)";
        var color = hasMod ? UiColors.ForAffix(req.AffixType) : UiColors.Muted;

        var mode = req.Category switch
        {
            ModCategory.Implicit => PickMode.Implicit,
            ModCategory.Enchant => PickMode.Enchant,
            _ => PickMode.Explicit,
        };

        // Fixed-width mod cell (auto-sized to the widest mod) so the "T1+" / min-val columns that follow line up.
        var modX = ImGui.GetCursorPosX();
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        if (ImGui.Selectable(label, false, ImGuiSelectableFlags.None, new Vector2(labelW, 0)))
            ModAffixPicker.Show(pickId);
        ImGui.PopStyleColor();
        // On a too-narrow window a long name can still clip, so the tooltip carries the full label + change hint.
        if (hasMod && ImGui.IsItemHovered()) ImGui.SetTooltip(label + "\nClick to change this mod");

        if (hasMod && req.Tier > 0)
        {
            ImGui.SameLine(modX + labelW + ColGap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled($"T{req.Tier}+");
        }

        // Fractured choice is only for explicit affixes in item selection (checks match both lists).
        var showFractured = hasMod && allowFractured && req.Category is ModCategory.Explicit or ModCategory.Fractured;
        if (showFractured)
        {
            ImGui.SameLine();
            var frac = req.Category == ModCategory.Fractured;
            if (ImGui.Checkbox("fractured", ref frac))
                req.Category = frac ? ModCategory.Fractured : ModCategory.Explicit;
        }

        // Optional minimum rolled value(s): a summary button that opens a per-value slider popup. Sits in a
        // fixed column (after the tier slot) so it aligns too - unless fractured pushed the row wider.
        if (hasMod)
        {
            if (showFractured) ImGui.SameLine();
            else ImGui.SameLine(modX + labelW + ColGap + TierColW);
            DrawMinRolls(req, ctx, pickId);
        }

        var picked = ModAffixPicker.Draw(pickId, ctx.BasePath, ctx.Domain, ctx.ItemClass, mode, ctx.ClusterTypes, includeEldritch);
        if (picked == null) return null;

        var cat = req.Category;   // preserve the row's pool (implicit/enchant/explicit/fractured)
        var next = ModAffixPicker.ToRequirement(picked.Value, ctx.Domain);   // carries influence/essence sub-type
        next.Category = cat;
        return next;
    }

    /// <summary>Widest label among <paramref name="labels"/> (at least the placeholder's width), clamped so the
    /// row's leading controls and trailing columns still fit in <paramref name="availWidth"/>.</summary>
    public static float MeasureLabelColumn(IEnumerable<string> labels, float availWidth, float leftReserve, float rightReserve)
    {
        var max = ImGui.CalcTextSize("(click to pick a mod)").X;
        foreach (var l in labels)
            if (!string.IsNullOrEmpty(l))
                max = Math.Max(max, ImGui.CalcTextSize(l).X);
        var maxCol = Math.Max(140f, availWidth - leftReserve - rightReserve);
        return Math.Clamp(max + 14f, 140f, maxCol);
    }

    /// <summary>
    /// The minimum-roll control: a compact summary button (">= 33" / "min val") that opens a popup with one
    /// range-clamped slider per rolled value. Hybrid mods (e.g. Nautilus's +armour/+life) get a labeled
    /// slider per value; stats that never vary (eldritch presence flags, unused 0-0 slots) are skipped.
    /// A slider at the stat's floor means "any" and stores null.
    /// </summary>
    private static void DrawMinRolls(ModRequirement req, in Ctx ctx, string id)
    {
        // The attainable roll range depends on the tier bound: "T2 and better" can only ever roll what the
        // top two tiers roll, so the sliders span those tiers only (TierPool applies the bound). Influence
        // and essence pools are resolved by ModFamilies.
        var pool = ModFamilies.TierPool(req, ctx.BasePath, ctx.ItemClass);
        var valueCount = ModFamilies.ValueCount(pool);
        if (valueCount == 0) return;
        var axes = ModFamilies.Axes(pool);
        if (axes.Count == 0) return;   // nothing rollable to bound

        req.MinValues ??= new List<int?>();
        while (req.MinValues.Count < valueCount) req.MinValues.Add(null);

        var set = req.MinValues.Where(v => v.HasValue).Select(v => $">={v.Value}").ToList();
        var label = set.Count > 0 ? string.Join(" / ", set) : axes.Count > 1 ? "min values" : "min val";

        // Caller positions the cursor (fixed column); just draw the button here.
        if (set.Count > 0) ImGui.PushStyleColor(ImGuiCol.Text, UiColors.Accent);
        var clicked = ImGui.Button($"{label}##minv");
        if (set.Count > 0) ImGui.PopStyleColor();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(RollTooltip(req, axes));
        if (clicked) ImGui.OpenPopup($"minvals_{id}");

        if (!ImGui.BeginPopup($"minvals_{id}")) return;

        ImGui.TextDisabled(req.Tier > 0
            ? $"Required minimum rolls within T{req.Tier}+ (far left = any, ctrl+click to type):"
            : "Required minimum rolls (far left = any, ctrl+click to type):");
        if (ImGui.BeginTable($"##mvt_{id}", 3, ImGuiTableFlags.SizingFixedFit))
        {
            foreach (var ax in axes)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(ax.Label);
                ImGui.TableSetColumnIndex(1);
                ImGui.SetNextItemWidth(170);
                var cur = req.MinValues[ax.Index] ?? ax.Min;
                var fmt = cur <= ax.Min ? "any" : ">= %d";
                if (ImGui.SliderInt($"##mv{ax.Index}", ref cur, ax.Min, ax.Max, fmt, ImGuiSliderFlags.AlwaysClamp))
                    req.MinValues[ax.Index] = cur > ax.Min ? cur : (int?)null;
                ImGui.TableSetColumnIndex(2);
                ImGui.TextDisabled($"{ax.Min}-{ax.Max}");
            }
            ImGui.EndTable();
        }
        if (ImGui.Button("clear (any roll)"))
            for (var i = 0; i < req.MinValues.Count; i++) req.MinValues[i] = null;
        ImGui.EndPopup();
    }

    private static string RollTooltip(ModRequirement req, List<ModFamilies.ValueAxis> axes)
    {
        var sb = new StringBuilder("Minimum rolled values - click to edit.");
        foreach (var ax in axes)
        {
            var b = ax.Index < req.MinValues.Count ? req.MinValues[ax.Index] : null;
            sb.Append('\n').Append(ax.Label).Append(": ")
              .Append(b.HasValue ? $">={b.Value}" : "any")
              .Append(req.Tier > 0 ? $"  (T{req.Tier}+ rolls {ax.Min}-{ax.Max})" : $"  (rolls {ax.Min}-{ax.Max})");
        }
        return sb.ToString();
    }
}
