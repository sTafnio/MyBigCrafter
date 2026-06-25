using System.Numerics;

namespace MyBigCrafter.UI;

/// <summary>Shared UI colors for consistent theming.</summary>
public static class UiColors
{
    public static readonly Vector4 Accent = new(0.55f, 0.78f, 1f, 1f);
    public static readonly Vector4 Done = new(0.40f, 0.85f, 1.0f, 1f);
    public static readonly Vector4 Match = new(0.45f, 1.0f, 0.50f, 1f);
    public static readonly Vector4 Muted = new(0.55f, 0.55f, 0.55f, 1f);
    public static readonly Vector4 Warn = new(1.0f, 0.70f, 0.30f, 1f);

    // Affix coloring (roughly matches in-game prefix/suffix feel).
    public static readonly Vector4 Prefix = new(0.55f, 0.75f, 1.0f, 1f);  // blue
    public static readonly Vector4 Suffix = new(0.55f, 1.0f, 0.65f, 1f);  // green
    public static readonly Vector4 Eldritch = new(0.80f, 0.55f, 1.0f, 1f);  // purple (eldritch implicits)

    public static Vector4 ForAffix(string affixType) => affixType switch
    {
        "Prefix" => Prefix,
        "Suffix" => Suffix,
        "ExarchImplicit" or "EaterImplicit" => Eldritch,
        "Unique" => Accent,   // base implicits / enchants
        _ => Suffix,
    };
}
