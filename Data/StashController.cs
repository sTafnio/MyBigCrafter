using System;
using System.Threading;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory.Elements;
using ExileCore.Shared;
using ExileCore.Shared.Enums;
using InputHumanizer.Input;

namespace MyBigCrafter.Data;

/// <summary>
/// Drives premium-stash tab navigation for stash-mode crafting: resolves a role tab name to its visible
/// (tab-bar) index, switches tabs with arrow keys (robust against tab-bar scrolling, the way Stashie does
/// it), and waits for a freshly shown tab to load. Every wait is a frame-checked condition, not a sleep.
/// </summary>
public sealed class StashController
{
    private const int SwitchTimeoutMs = 2000;
    private const int LoadTimeoutMs = 2000;

    private readonly GameController _gc;

    public StashController(GameController gc) => _gc = gc;

    private StashElement Stash => _gc?.IngameState?.IngameUi?.StashElement;

    public int CurrentIndex => Stash?.IndexVisibleStash ?? -1;

    /// <summary>Visible (tab-bar) index of the tab named <paramref name="name"/>, or -1 if there's no such tab.</summary>
    public int ResolveIndex(string name)
    {
        if (string.IsNullOrEmpty(name)) return -1;
        var tabs = _gc?.IngameState?.Data?.ServerData?.PlayerStashTabs;
        if (tabs == null) return -1;
        foreach (var t in tabs)
        {
            if (t == null || !string.Equals(t.Name, name, StringComparison.Ordinal)) continue;

            if (t.TabType is InventoryTabType.Normal or InventoryTabType.Premium
                or InventoryTabType.Quad or InventoryTabType.Currency)
                return t.VisibleIndex;
        }
        return -1;
    }

    /// <summary>True when the tab at <paramref name="visibleIndex"/> has its inventory loaded in memory.</summary>
    public bool IsLoaded(int visibleIndex)
    {
        try
        {
            var invs = Stash?.Inventories;
            return invs != null && visibleIndex >= 0 && visibleIndex < invs.Count && invs[visibleIndex] != null;
        }
        catch { return false; }
    }

    /// <summary>Switches the visible stash to <paramref name="target"/> via arrow keys, then waits for it to load.</summary>
    public async SyncTask<bool> SwitchToTab(int target, IInputController input, CancellationToken ct)
    {
        for (var tries = 0; tries < 3; tries++)
        {
            var current = CurrentIndex;
            if (current < 0) return false;
            if (current == target) return await WaitLoaded(ct);

            var key = target < current ? Keys.Left : Keys.Right;
            var steps = Math.Abs(target - current);
            for (var i = 0; i < steps; i++)
            {
                if (!await input.KeyDown(key, ct)) return false;
                if (!await input.KeyUp(key, false, ct)) return false;
            }

            if (await WaitFor(() => CurrentIndex == target, SwitchTimeoutMs, ct))
                return await WaitLoaded(ct);
        }
        return CurrentIndex == target && await WaitLoaded(ct);
    }

    private async SyncTask<bool> WaitLoaded(CancellationToken ct) =>
        await WaitFor(() => IsLoaded(CurrentIndex) &&
            Stash?.VisibleStash?.InvType is { } t && t != InventoryType.InvalidInventory,
            LoadTimeoutMs, ct);

    private static async SyncTask<bool> WaitFor(Func<bool> condition, int timeoutMs, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        var ok = await TaskUtils.CheckEveryFrame(condition, cts.Token);
        ct.ThrowIfCancellationRequested();
        return ok;
    }
}
