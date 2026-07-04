using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using MyBigCrafter.Data;
using MyBigCrafter.Model;
using PoeModDataLib.Api;

namespace MyBigCrafter.UI;

/// <summary>Item selection: aligned Category / Class / Subtype / Add base, then Bases / Advanced.</summary>
public static class ItemSelectionEditor
{
    private const float LabelW = 115f;
    private const float ComboW = 220f;
    private static string _category = "";

    public static void Draw(CraftPlan plan)
    {
        var cats = BaseTaxonomy.Categories;
        if (cats.Count == 0) { ImGui.TextDisabled("Loading bases..."); return; }

        var hasClass = !string.IsNullOrEmpty(plan.ItemClass) && BaseTaxonomy.HasClass(plan.ItemClass);
        var isCluster = hasClass && BaseTaxonomy.IsClusterClass(plan.ItemClass);

        // For cluster jewels the meaningful pick is the passive type, not a base item - so the list holds
        // passive types (empty = any type of the chosen size). Everything else lists real bases.
        ImGui.SeparatorText(isCluster ? "Passive types" : "Bases");
        if (!hasClass)
        {
            ImGui.TextDisabled("No item class chosen yet - click Add.");
        }
        else if (isCluster)
        {
            DrawClusterTypes(plan);
        }
        else
        {
            var scopeBases = ScopeBases(plan.ItemClass, plan);
            var scopeLabel = string.IsNullOrEmpty(plan.Subtype) ? plan.ItemClass : $"{Subtypes.Label(plan.Subtype)} {plan.ItemClass}";
            if (plan.BasePaths.Count == 0)
            {
                ImGui.TextDisabled($"All {scopeLabel} bases ({scopeBases.Count})");
            }
            else
            {
                string removePath = null;
                foreach (var path in plan.BasePaths)
                {
                    var b = BaseTaxonomy.FindBase(path);
                    if (RemovableRow(b?.Name ?? path, $"rm_{path}")) removePath = path;
                }
                if (removePath != null) plan.BasePaths.Remove(removePath);
            }
        }
        if (ImGui.Button("Add##addbase")) _addOpen = true;

        if (ClusterJewelTypes.DriftWarning != null)
            UiText.WrappedColored(UiColors.Warn, ClusterJewelTypes.DriftWarning);   // carries raw mod text ('%'!)

        // Advanced (which of those bases to actually craft) - same condition tree as Check nodes.
        ImGui.Spacing();
        ImGui.SeparatorText("Advanced");
        if (plan.Filter?.Children.Count > 0)
            UiText.Wrapped(ConditionEditor.Summary(plan.Filter));   // mod labels contain '%'
        if (ImGui.Button("Edit##filter"))
            ConditionEditor.Open("selfilter", plan.Filter, "Advanced filter", allowCategory: true);

        DrawAddWindow(plan);
    }

    /// <summary>The chosen cluster passive types (empty = any type of the selected size), each removable.</summary>
    private static void DrawClusterTypes(CraftPlan plan)
    {
        if (!ClusterJewelTypes.IsSizeTag(plan.Subtype))
        {
            ImGui.TextDisabled("Pick a size (Small / Medium / Large) via Add.");
            return;
        }
        if (plan.ClusterTypes.Count == 0)
        {
            var n = ClusterJewelTypes.OfSizeTag(plan.Subtype).Count();
            ImGui.TextDisabled($"All {Subtypes.Label(plan.Subtype)} passive types ({n})");
            return;
        }
        string remove = null;
        foreach (var tag in plan.ClusterTypes)
        {
            var ct = ClusterJewelTypes.ByTag(tag);
            if (RemovableRow(ct?.Name ?? tag, $"rmct_{tag}")) remove = tag;
        }
        if (remove != null) plan.ClusterTypes.Remove(remove);
    }

    /// <summary>A bulleted list row with its remove "x" right-aligned to the content edge (same column as the
    /// condition/mod-set rows), so long names never run underneath the button. True = remove clicked.</summary>
    private static bool RemovableRow(string name, string id)
    {
        UiText.Bullet(name);
        var bw = ImGui.GetFrameHeight();
        var xPos = ImGui.GetContentRegionMax().X - bw;
        ImGui.SameLine();
        if (ImGui.GetCursorPosX() < xPos) ImGui.SameLine(xPos);
        return ImGui.Button($"x##{id}", new Vector2(bw, 0));
    }

    /// <summary>Renders the Category / Class / Subtype combos, editing the plan.</summary>
    private static void DrawHierarchy(CraftPlan plan)
    {
        if (BaseTaxonomy.HasClass(plan.ItemClass) && string.IsNullOrEmpty(_category))
            _category = BaseTaxonomy.CategoryOfClass(plan.ItemClass);

        var cats = BaseTaxonomy.Categories.ToArray();
        if (cats.Length == 0) return;

        // Category
        var ci = Math.Max(0, Array.IndexOf(cats, _category));
        _category = cats[ci];
        Label("Category");
        ImGui.SetNextItemWidth(ComboW);
        if (ImGui.Combo("##cat", ref ci, cats, cats.Length))
        {
            _category = cats[ci];
            if (BaseTaxonomy.HasClass(plan.ItemClass) && BaseTaxonomy.CategoryOfClass(plan.ItemClass) != _category)
            {
                plan.ItemClass = ""; plan.Subtype = ""; plan.ClusterTypes.Clear(); plan.BasePaths.Clear();
            }
        }

        // Item class
        var classes = BaseTaxonomy.ClassesInCategory(_category).ToArray();
        Label("Item class");
        var cls = Array.IndexOf(classes, plan.ItemClass);
        ImGui.SetNextItemWidth(ComboW);
        if (ImGui.Combo("##cls", ref cls, classes, classes.Length) && cls >= 0)
        {
            plan.ItemClass = classes[cls]; plan.Subtype = ""; plan.ClusterTypes.Clear(); plan.BasePaths.Clear();
        }

        // Subtype (only for classes that have any: armour attributes, cluster jewel sizes)
        var subs = !string.IsNullOrEmpty(plan.ItemClass) ? Subtypes.For(plan.ItemClass) : new List<(string Tag, string Label)>();
        if (subs.Count > 0)
        {
            Label("Subtype");
            var subLabels = new[] { "Any" }.Concat(subs.Select(s => s.Label)).ToArray();
            var subTags = new[] { "" }.Concat(subs.Select(s => s.Tag)).ToArray();
            var si = Math.Max(0, Array.IndexOf(subTags, plan.Subtype));
            ImGui.SetNextItemWidth(ComboW);
            if (ImGui.Combo("##sub", ref si, subLabels, subLabels.Length))
            {
                plan.Subtype = subTags[si];
                plan.ClusterTypes.Clear();   // sizes' type pools differ
                plan.BasePaths.Clear();
            }
        }
        else if (!string.IsNullOrEmpty(plan.Subtype))
        {
            plan.Subtype = "";
        }
    }

    /// <summary>The bases of the selected class, narrowed to the chosen subtype (an armour attribute or cluster size tag).</summary>
    private static List<BaseInfo> ScopeBases(string itemClass, CraftPlan plan)
    {
        var filter = plan.Subtype ?? "";
        return BaseTaxonomy.BasesOf(itemClass)
            .Where(b => filter.Length == 0 || b.Tags.Contains(filter))
            .ToList();
    }

    private static bool _addOpen;
    private static string _addSearch = "";

    /// <summary>The base picker window opened by the Bases "Add" button.</summary>
    private static void DrawAddWindow(CraftPlan plan)
    {
        if (!_addOpen) return;

        ImGui.SetNextWindowSize(new Vector2(380, 470), ImGuiCond.FirstUseEver);
        var open = true;
        // NoDocking: avoid the docking resize-loop that flickers white and freezes these tool windows.
        if (ImGui.Begin("Add bases##addbases", ref open, ImGuiWindowFlags.NoDocking))
        {
            DrawHierarchy(plan);
            var hasClass = BaseTaxonomy.HasClass(plan.ItemClass);
            var isCluster = hasClass && BaseTaxonomy.IsClusterClass(plan.ItemClass);

            ImGui.Spacing();
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##bsearch", ref _addSearch, 64);
            var q = _addSearch.Trim();

            if (ImGui.BeginChild("##blist", new Vector2(0, 0), ImGuiChildFlags.Border))
            {
                if (!hasClass)
                {
                    ImGui.TextDisabled("Pick an item class above.");
                }
                else if (isCluster)
                {
                    // The cluster "bases" are the size's passive types (the real base is just the size).
                    if (!ClusterJewelTypes.IsSizeTag(plan.Subtype))
                        ImGui.TextDisabled("Pick a size in Subtype above.");
                    else
                        foreach (var ct in ClusterJewelTypes.OfSizeTag(plan.Subtype))
                        {
                            if (plan.ClusterTypes.Contains(ct.Tag)) continue;
                            if (q.Length > 0 && !ct.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
                            if (ImGui.Selectable($"{ct.Name}##{ct.Tag}")) plan.ClusterTypes.Add(ct.Tag);
                        }
                }
                else
                {
                    foreach (var b in ScopeBases(plan.ItemClass, plan))
                    {
                        if (plan.BasePaths.Contains(b.Path)) continue;
                        if (q.Length > 0 && !b.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
                        if (ImGui.Selectable($"{b.Name}##{b.Path}")) plan.BasePaths.Add(b.Path);
                    }
                }
            }
            ImGui.EndChild();
        }
        ImGui.End();

        if (!open) _addOpen = false;
    }

    private static void Label(string text)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(text);
        ImGui.SameLine(LabelW);
    }
}
