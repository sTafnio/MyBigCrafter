using System;
using System.Numerics;
using ExileCore;
using ExileCore.PoEMemory.Elements;

namespace MyBigCrafter.Data;

/// <summary>
/// Read access to the Horticrafting Station window for harvest-mode crafting: whether it's open, the screen
/// rect of the Craft button, and locating a craft row by its display name. The crafts list is long and
/// scrolled (off-screen rows have valid-but-unclickable rects), so a row is only returned when it's actually
/// on-screen (<c>IsVisible</c>) - the executor brings the wanted craft on-screen by searching for it first.
/// </summary>
public sealed class HarvestController
{
    private readonly GameController _gc;

    public HarvestController(GameController gc) => _gc = gc;

    private HarvestWindow Window => _gc?.IngameState?.IngameUi?.HorticraftingStationWindow;

    public bool IsVisible => Window is { IsVisible: true };

    /// <summary>The Craft (confirm) button's click target, or null when the window/button isn't shown.</summary>
    public ClickTarget? CraftButtonTarget()
    {
        try
        {
            var b = Window?.CraftButton;
            if (b == null || !b.IsVisible) return null;
            var r = b.GetClientRect();
            return new ClickTarget(new Vector2(r.Center.X, r.Center.Y), new Vector2(r.Width, r.Height));
        }
        catch { return null; }
    }

    /// <summary>Click target of the on-screen craft row whose display name equals <paramref name="displayName"/>,
    /// or null if no matching craft is currently visible (e.g. scrolled off, filtered out, or unavailable).</summary>
    public ClickTarget? FindVisibleCraft(string displayName)
    {
        if (string.IsNullOrEmpty(displayName)) return null;
        try
        {
            var crafts = Window?.Crafts;
            if (crafts == null) return null;
            foreach (var c in crafts)
            {
                if (c == null || !c.IsVisible) continue;
                string name;
                try { name = c.CraftDisplayName; } catch { continue; }
                if (!string.Equals(name, displayName, StringComparison.Ordinal)) continue;
                var r = c.GetClientRect();
                return new ClickTarget(new Vector2(r.Center.X, r.Center.Y), new Vector2(r.Width, r.Height));
            }
        }
        catch { /* window torn down mid-read */ }
        return null;
    }
}
