using ImGuiNET;
using MyBigCrafter.Model;

namespace MyBigCrafter.UI;

/// <summary>Shared editor for a <see cref="NumberCond"/> (any / min / max / range).</summary>
public static class NumberCondUI
{
    // "any" used to mean "always pass" (0 satisfied it); it now means present (value >= 1). Labelled to match.
    private static readonly string[] Modes = { ">=1", "min", "max", "range" };

    public static void DrawInline(NumberCond c, string id)
    {
        var m = (int)c.Mode;
        ImGui.SetNextItemWidth(80);
        if (ImGui.Combo($"##nm_{id}", ref m, Modes, Modes.Length)) c.Mode = (NumberMode)m;

        if (c.Mode is NumberMode.AtLeast or NumberMode.AtMost)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            var v = c.Value;
            if (ImGui.InputInt($"##nv_{id}", ref v)) c.Value = v;
        }
        else if (c.Mode == NumberMode.Between)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(70);
            var mn = c.Min;
            if (ImGui.InputInt($"##nmin_{id}", ref mn)) c.Min = mn;
            ImGui.SameLine();
            ImGui.TextUnformatted("to");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(70);
            var mx = c.Max;
            if (ImGui.InputInt($"##nmax_{id}", ref mx)) c.Max = mx;
        }
    }

    public static void DrawLabeled(string label, NumberCond c, string id, float labelW = 120f)
    {
        ImGui.TextUnformatted(label);
        ImGui.SameLine(labelW);
        DrawInline(c, id);
    }
}
