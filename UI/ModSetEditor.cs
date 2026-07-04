using System;
using System.Numerics;
using ImGuiNET;
using MyBigCrafter.Model;

namespace MyBigCrafter.UI;

/// <summary>
/// The "Mod Sets" tab: named per-craft mod lists ("Wanted Prefixes", ...) that Set condition leaves count
/// against, so a big craft's target mods are edited in ONE place instead of inside every Check node.
/// Left: the craft's sets; right: the selected set's mods as shared requirement rows. Renaming a set rewrites
/// every referencing leaf in the same edit; deleting is blocked while references exist - dangling set names
/// can't be produced from the UI.
/// </summary>
public static class ModSetEditor
{
    private static int _selected;
    private static string _nameEdit = "";
    private static ModSet _nameEditFor;   // the set instance the name buffer belongs to
    private static string _status = "";

    public static void Draw(CraftPlan plan, string basePath, string domain)
    {
        var sets = plan.ModSets;
        if (_selected >= sets.Count) _selected = sets.Count - 1;

        // --- left: the craft's sets ---
        if (ImGui.BeginChild("##msetlist", new Vector2(230, 0), ImGuiChildFlags.Border))
        {
            for (var i = 0; i < sets.Count; i++)
            {
                ImGui.PushID(i);
                if (ImGui.Selectable($"{sets[i].Name}  ({sets[i].Mods.Count})", i == _selected)) _selected = i;
                ImGui.PopID();
            }
            if (sets.Count == 0) ImGui.TextDisabled("No sets yet.");

            ImGui.Spacing();
            if (ImGui.Button("+ add set"))
            {
                sets.Add(new ModSet { Name = FreshName(plan) });
                _selected = sets.Count - 1;
                _status = "";
            }
        }
        ImGui.EndChild();

        ImGui.SameLine();

        // --- right: the selected set ---
        if (ImGui.BeginChild("##msetedit", new Vector2(0, 0), ImGuiChildFlags.Border))
        {
            if (_selected < 0 || _selected >= sets.Count)
                ImGui.TextDisabled("Add a set on the left to start.");
            else
                DrawSet(plan, sets[_selected], basePath, domain);

            if (!string.IsNullOrEmpty(_status))
            {
                ImGui.Spacing();
                ImGui.TextColored(UiColors.Warn, _status);
            }
        }
        ImGui.EndChild();
    }

    private static void DrawSet(CraftPlan plan, ModSet set, string basePath, string domain)
    {
        // Name: committed when the field loses focus (per-keystroke renames would churn every reference).
        if (!ReferenceEquals(_nameEditFor, set)) { _nameEdit = set.Name; _nameEditFor = set; }
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Name");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(220);
        ImGui.InputText("##msname", ref _nameEdit, 64);
        if (ImGui.IsItemDeactivatedAfterEdit()) CommitRename(plan, set);

        // Delete: blocked while referenced, so Set leaves can never point at a missing set.
        var refs = plan.CountSetReferences(set.Name);
        ImGui.SameLine();
        ImGui.BeginDisabled(refs > 0);
        var delete = ImGui.Button("Delete set");
        ImGui.EndDisabled();
        if (refs > 0 && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip($"Used by {refs} condition{(refs == 1 ? "" : "s")} - remove those Set leaves first.");
        if (delete)
        {
            plan.ModSets.Remove(set);
            _nameEditFor = null;
            _status = "";
            return;
        }

        if (refs > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"used by {refs} condition{(refs == 1 ? "" : "s")}");
        }

        ImGui.Separator();

        if (string.IsNullOrEmpty(basePath))
            ImGui.TextColored(UiColors.Warn, "Pick a class/base on the Item Selection tab to choose mods.");

        // --- the set's mods, as shared requirement rows with a right-aligned remove column ---
        var ctx = new ModRequirementRow.Ctx(basePath, domain, plan.ItemClass, plan.ClusterTypes);
        var labelW = MeasureLabels(set);
        var removeAt = -1;

        for (var i = 0; i < set.Mods.Count; i++)
        {
            ImGui.PushID(i);
            var replaced = ModRequirementRow.Draw(PickId(i), set.Mods[i], ctx, labelW, includeEldritch: false);
            if (replaced != null) set.Mods[i] = replaced;

            var bw = ImGui.GetFrameHeight();
            var xPos = ImGui.GetContentRegionMax().X - bw;
            ImGui.SameLine();
            if (ImGui.GetCursorPosX() < xPos) ImGui.SameLine(xPos);
            if (ImGui.Button("x##rm", new Vector2(bw, 0))) removeAt = i;
            ImGui.PopID();
        }
        if (removeAt >= 0) set.Mods.RemoveAt(removeAt);
        if (set.Mods.Count == 0) ImGui.TextDisabled("No mods in this set yet.");

        ImGui.Spacing();
        if (ImGui.Button("+ mod"))
        {
            set.Mods.Add(new ModRequirement());
            ModAffixPicker.Show(PickId(set.Mods.Count - 1));
        }
    }

    // Picker ids are keyed by set + row position; the set index keeps two sets' same-position rows distinct.
    private static string PickId(int row) => $"mset_{_selected}_{row}";

    private static float MeasureLabels(ModSet set)
    {
        var labels = new string[set.Mods.Count];
        for (var i = 0; i < labels.Length; i++) labels[i] = set.Mods[i].Label;
        const float rightReserve = 175f;   // T1+ slot + min-val + x
        return ModRequirementRow.MeasureLabelColumn(labels, ImGui.GetContentRegionAvail().X, 0f, rightReserve);
    }

    private static void CommitRename(CraftPlan plan, ModSet set)
    {
        var name = (_nameEdit ?? "").Trim();
        if (name.Length == 0 || string.Equals(name, set.Name, StringComparison.Ordinal))
        {
            _nameEdit = set.Name;
            return;
        }
        var clash = plan.ModSets.Exists(s =>
            !ReferenceEquals(s, set) && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (clash)
        {
            _status = $"A set named '{name}' already exists.";
            _nameEdit = set.Name;
            return;
        }
        plan.RenameSet(set.Name, name);   // renames the set AND every referencing leaf together
        _nameEdit = name;
        _status = "";
    }

    private static string FreshName(CraftPlan plan)
    {
        for (var n = 1; ; n++)
        {
            var name = n == 1 ? "New Set" : $"New Set {n}";
            if (plan.FindSet(name) == null) return name;
        }
    }
}
