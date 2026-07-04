using System.Numerics;
using ImGuiNET;

namespace MyBigCrafter.UI;

/// <summary>
/// printf-safe text rendering. ImGui's Text/TextColored/TextWrapped/BulletText/SetTooltip treat the string
/// as a FORMAT string, so any dynamic text containing '%' (mod lines like "12-16% increased ...") gets its
/// specifiers replaced with stack garbage. Every dynamic string must go through these TextUnformatted-based
/// helpers instead; plain literals without '%' may keep using the ImGui calls directly.
/// </summary>
public static class UiText
{
    public static void Colored(Vector4 color, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted(text ?? "");
        ImGui.PopStyleColor();
    }

    public static void Wrapped(string text)
    {
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(text ?? "");
        ImGui.PopTextWrapPos();
    }

    public static void WrappedColored(Vector4 color, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        Wrapped(text);
        ImGui.PopStyleColor();
    }

    /// <summary>BulletText without the printf trap.</summary>
    public static void Bullet(string text)
    {
        ImGui.Bullet();
        ImGui.SameLine();
        ImGui.TextUnformatted(text ?? "");
    }

    /// <summary>SetTooltip without the printf trap. Call only when the owning item is hovered.</summary>
    public static void Tooltip(string text)
    {
        ImGui.BeginTooltip();
        ImGui.TextUnformatted(text ?? "");
        ImGui.EndTooltip();
    }
}
