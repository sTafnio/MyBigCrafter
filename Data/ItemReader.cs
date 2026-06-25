using System.Collections.Generic;
using System.Numerics;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ItemFilterLibrary;

namespace MyBigCrafter.Data;

public enum CraftSource { Inventory, VisibleStash, HarvestBench }

/// <summary>A click destination: a slot's screen center and size, so callers can click a random point
/// inside it (with an edge margin) rather than always the exact center.</summary>
public readonly record struct ClickTarget(Vector2 Center, Vector2 Size);

/// <summary>An item read from the game with its on-screen rect (for later clicking) and source.</summary>
public sealed class ReadItem
{
    public ItemData Data { get; init; }
    public Vector2 Center { get; init; }
    public Vector2 Size { get; init; }
    public CraftSource Source { get; init; }
    public ClickTarget Target => new(Center, Size);
}

/// <summary>
/// Reads items (as <see cref="ItemData"/>) from the player inventory or the currently visible stash tab,
/// together with a screen-space click center. Call from Tick (constructs ItemData — keep out of Render).
/// </summary>
public sealed class ItemReader
{
    private const float SlotTolerance = 25f; // squared px distance to re-find an item by its slot center

    private readonly GameController _gc;

    public ItemReader(GameController gc) => _gc = gc;

    /// <summary>
    /// The player inventory's server change counter: the server bumps it for every item event in this
    /// inventory (currency applied, stack consumed, item moved in/out). -1 when unreadable. Stash tab and
    /// bench inventories carry their own counter - same mechanism for later phases.
    /// </summary>
    public int ReadInventoryCounter()
    {
        try
        {
            var inventories = _gc?.IngameState?.Data?.ServerData?.PlayerInventories;
            if (inventories == null || inventories.Count == 0) return -1;
            return inventories[0]?.Inventory?.ServerRequestCounter ?? -1;
        }
        catch { return -1; }
    }

    /// <summary>The currently visible stash tab's server change counter (the currency tab while crafting in it). -1 when unreadable.</summary>
    public int ReadVisibleStashCounter()
    {
        try
        {
            return _gc?.IngameState?.IngameUi?.StashElement?.VisibleStash?.ServerInventory?.ServerRequestCounter ?? -1;
        }
        catch { return -1; }
    }

    /// <summary>The harvest bench item-slot's server change counter (bumps when a craft or currency is applied to the slotted item). -1 when unreadable.</summary>
    public int ReadHarvestBenchCounter()
    {
        try
        {
            return _gc?.IngameState?.IngameUi?.HorticraftingStationWindow?.CraftInventory?.ServerInventory?.ServerRequestCounter ?? -1;
        }
        catch { return -1; }
    }

    /// <summary>The change counter for the surface an item lives on (inventory, the currency tab, or the harvest bench slot).</summary>
    public int ReadCounter(CraftSource source) => source switch
    {
        CraftSource.VisibleStash => ReadVisibleStashCounter(),
        CraftSource.HarvestBench => ReadHarvestBenchCounter(),
        _ => ReadInventoryCounter(),
    };

    /// <summary>Re-reads just the item occupying the slot at <paramref name="center"/> on the given surface; only that slot's ItemData is built.</summary>
    public ReadItem ReadAt(CraftSource source, Vector2 center) => source switch
    {
        CraftSource.VisibleStash => ReadVisibleStashAt(center),
        CraftSource.HarvestBench => ReadHarvestBenchAt(center),
        _ => ReadInventoryAt(center),
    };

    /// <summary>
    /// Re-reads just the inventory item occupying the slot at <paramref name="center"/> (screen-space center
    /// from a prior read). Only the matching slot's ItemData is built.
    /// </summary>
    public ReadItem ReadInventoryAt(Vector2 center)
    {
        var inventories = _gc?.IngameState?.Data?.ServerData?.PlayerInventories;
        if (inventories == null || inventories.Count == 0) return null;

        var items = inventories[0]?.Inventory?.InventorySlotItems;
        if (items == null) return null;

        foreach (var slot in items)
        {
            if (slot?.Item == null || slot.Address == 0) continue;

            var rect = slot.GetClientRect();
            var c = new Vector2(rect.Center.X, rect.Center.Y);
            if (Vector2.DistanceSquared(c, center) > SlotTolerance) continue;

            try { return new ReadItem { Data = new ItemData(slot.Item, _gc), Center = c, Size = new Vector2(rect.Width, rect.Height), Source = CraftSource.Inventory }; }
            catch { return null; }
        }

        return null;
    }

    /// <summary>Positional re-read of one item in the visible stash tab (the currency tab during stash crafting).</summary>
    public ReadItem ReadVisibleStashAt(Vector2 center)
    {
        var visible = _gc?.IngameState?.IngameUi?.StashElement is { IsVisible: true } s ? s.VisibleStash : null;
        if (visible == null) return null;

        var items = visible.VisibleInventoryItems;
        if (items != null)
            foreach (var slot in items)
            {
                if (slot?.Item == null || slot.Address == 0) continue;
                var rect = slot.GetClientRect();
                var c = new Vector2(rect.Center.X, rect.Center.Y);
                if (Vector2.DistanceSquared(c, center) > SlotTolerance) continue;
                try { return new ReadItem { Data = new ItemData(slot.Item, _gc), Center = c, Size = new Vector2(rect.Width, rect.Height), Source = CraftSource.VisibleStash }; }
                catch { return null; }
            }

        // Special tabs (Currency, ...) keep the craft item in a generic slot that isn't rendered in
        // VisibleInventoryItems - it only shows in ServerInventory. Match it there (the one with mods).
        var server = visible.ServerInventory?.InventorySlotItems;
        if (server != null)
            foreach (var slot in server)
            {
                if (slot?.Item == null || slot.Address == 0 || !slot.Item.HasComponent<Mods>()) continue;
                var rect = slot.GetClientRect();
                var c = new Vector2(rect.Center.X, rect.Center.Y);
                if (Vector2.DistanceSquared(c, center) > SlotTolerance) continue;
                try { return new ReadItem { Data = new ItemData(slot.Item, _gc), Center = c, Size = new Vector2(rect.Width, rect.Height), Source = CraftSource.VisibleStash }; }
                catch { return null; }
            }

        return null;
    }

    public List<ReadItem> Read(CraftSource source) => source switch
    {
        CraftSource.VisibleStash => ReadVisibleStash(),
        CraftSource.HarvestBench => ReadHarvestBench(),
        _ => ReadInventory(),
    };

    public List<ReadItem> ReadInventory()
    {
        var result = new List<ReadItem>();
        var inventories = _gc?.IngameState?.Data?.ServerData?.PlayerInventories;
        if (inventories == null || inventories.Count == 0) return result;

        var items = inventories[0]?.Inventory?.InventorySlotItems;
        if (items == null) return result;

        foreach (var slot in items)
        {
            if (slot?.Item == null || slot.Address == 0) continue;
            ItemData data;
            try { data = new ItemData(slot.Item, _gc); } catch { continue; }

            var rect = slot.GetClientRect();
            result.Add(new ReadItem
            {
                Data = data,
                Center = new Vector2(rect.Center.X, rect.Center.Y),
                Size = new Vector2(rect.Width, rect.Height),
                Source = CraftSource.Inventory,
            });
        }

        return result;
    }

    public List<ReadItem> ReadVisibleStash()
    {
        var result = new List<ReadItem>();
        var visible = _gc?.IngameState?.IngameUi?.StashElement is { IsVisible: true } s ? s.VisibleStash : null;
        if (visible == null) return result;

        var seen = new HashSet<long>();

        // Normal tabs render every item here. Special tabs (Currency, Essence, ...) only list their dedicated
        // slots - the single generic slot that holds an arbitrary craft item is NOT in this list.
        var items = visible.VisibleInventoryItems;
        if (items != null)
            foreach (var slot in items)
            {
                if (slot?.Item == null || slot.Address == 0) continue;
                ItemData data;
                try { data = new ItemData(slot.Item, _gc); } catch { continue; }
                seen.Add(slot.Item.Address);
                var rect = slot.GetClientRect();
                result.Add(new ReadItem
                {
                    Data = data,
                    Center = new Vector2(rect.Center.X, rect.Center.Y),
                    Size = new Vector2(rect.Width, rect.Height),
                    Source = CraftSource.VisibleStash,
                });
            }

        // Recover the generic-slot craft item from ServerInventory (where special tabs keep it). It's the one
        // item with a Mods component - currency, cards and fragments have none - and it carries a click rect.
        var server = visible.ServerInventory?.InventorySlotItems;
        if (server != null)
            foreach (var slot in server)
            {
                if (slot?.Item == null || slot.Address == 0) continue;
                if (!seen.Add(slot.Item.Address)) continue;       // already taken from the rendered list
                if (!slot.Item.HasComponent<Mods>()) continue;    // skip currency / cards / fragments
                ItemData data;
                try { data = new ItemData(slot.Item, _gc); } catch { continue; }
                var rect = slot.GetClientRect();
                result.Add(new ReadItem
                {
                    Data = data,
                    Center = new Vector2(rect.Center.X, rect.Center.Y),
                    Size = new Vector2(rect.Width, rect.Height),
                    Source = CraftSource.VisibleStash,
                });
            }

        return result;
    }

    /// <summary>The single craftable item in the visible (currency) tab: the one with a Mods component
    /// (currency / cards / fragments have none) - the reliable way to spot a base, unlike StackInfo. Preferred
    /// from the rendered list (real NormalInventoryItem rect); the ServerInventory generic slot is the
    /// fallback. Null when the tab holds no craft item.</summary>
    public ReadItem ReadStashCraftable()
    {
        var visible = _gc?.IngameState?.IngameUi?.StashElement is { IsVisible: true } s ? s.VisibleStash : null;
        if (visible == null) return null;

        var rendered = visible.VisibleInventoryItems;
        if (rendered != null)
            foreach (var slot in rendered)
                if (slot?.Item != null && slot.Address != 0 && slot.Item.HasComponent<Mods>())
                {
                    var rect = slot.GetClientRect();
                    try { return new ReadItem { Data = new ItemData(slot.Item, _gc), Center = new Vector2(rect.Center.X, rect.Center.Y), Size = new Vector2(rect.Width, rect.Height), Source = CraftSource.VisibleStash }; }
                    catch { return null; }
                }

        var server = visible.ServerInventory?.InventorySlotItems;
        if (server != null)
            foreach (var slot in server)
                if (slot?.Item != null && slot.Address != 0 && slot.Item.HasComponent<Mods>())
                {
                    var rect = slot.GetClientRect();
                    try { return new ReadItem { Data = new ItemData(slot.Item, _gc), Center = new Vector2(rect.Center.X, rect.Center.Y), Size = new Vector2(rect.Width, rect.Height), Source = CraftSource.VisibleStash }; }
                    catch { return null; }
                }

        return null;
    }

    // ---------------- harvest bench (Horticrafting Station) ----------------

    private ExileCore.PoEMemory.MemoryObjects.VendorInventory BenchInventory()
    {
        var w = _gc?.IngameState?.IngameUi?.HorticraftingStationWindow;
        return w is { IsVisible: true } ? w.CraftInventory : null;
    }

    /// <summary>The single craft item placed in the harvest bench slot, or null when empty/closed. Identified by
    /// its Mods component (the slot only ever holds a craftable gear/map/jewel item here).</summary>
    public ReadItem ReadHarvestBenchCraftable()
    {
        var items = BenchInventory()?.VisibleInventoryItems;
        if (items == null) return null;
        foreach (var slot in items)
        {
            if (slot?.Item == null || slot.Address == 0 || !slot.Item.HasComponent<Mods>()) continue;
            var rect = slot.GetClientRect();
            try { return new ReadItem { Data = new ItemData(slot.Item, _gc), Center = new Vector2(rect.Center.X, rect.Center.Y), Size = new Vector2(rect.Width, rect.Height), Source = CraftSource.HarvestBench }; }
            catch { return null; }
        }
        return null;
    }

    public List<ReadItem> ReadHarvestBench()
    {
        var result = new List<ReadItem>();
        var craftable = ReadHarvestBenchCraftable();
        if (craftable != null) result.Add(craftable);
        return result;
    }

    /// <summary>Positional re-read of the harvest bench slot item (used to re-resolve the item between graph steps).</summary>
    public ReadItem ReadHarvestBenchAt(Vector2 center)
    {
        var items = BenchInventory()?.VisibleInventoryItems;
        if (items == null) return null;
        foreach (var slot in items)
        {
            if (slot?.Item == null || slot.Address == 0) continue;
            var rect = slot.GetClientRect();
            var c = new Vector2(rect.Center.X, rect.Center.Y);
            if (Vector2.DistanceSquared(c, center) > SlotTolerance) continue;
            try { return new ReadItem { Data = new ItemData(slot.Item, _gc), Center = c, Size = new Vector2(rect.Width, rect.Height), Source = CraftSource.HarvestBench }; }
            catch { return null; }
        }
        return null;
    }
}
