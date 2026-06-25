using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Windows.Forms;
using ExileCore;
using ExileCore.Shared;
using ExileCore.Shared.Enums;
using ImGuiNET;
using InputHumanizer.Input;
using ItemFilterLibrary;
using MyBigCrafter.Data;

namespace MyBigCrafter.Model;

/// <summary>
/// Auto-crafter for inventory mode (<see cref="Run"/>) and stash mode (<see cref="RunStash"/>). For each
/// matching item it walks the craft's flow chart from Start, applying currency at Apply nodes and branching
/// at Check nodes, until it reaches Finish, runs out of currency, or is aborted. Every apply is verified
/// against the surface's ServerRequestCounter (the server bumps it for every item event); an unacknowledged
/// apply aborts the run as desynced rather than crafting blind. Apply nodes can hold Shift to keep the
/// currency selected across uses, and enabled Alt-swap shortcuts (alteration->augmentation,
/// chance/alchemy->scouring) apply their partner currency without re-selecting.
///
/// Stash mode crafts the item IN the currency tab: the item is moved In tab -> inventory -> currency tab
/// (ctrl+click hops, since the game has no direct tab-to-tab move), crafted there alongside the currency,
/// then moved out to the Out tab. The same per-item walk runs against the visible stash instead of the
/// inventory - it just reads its item, currency and change-counter from a different surface.
/// </summary>
public sealed class CraftExecutor
{
    private const int VerifyTimeoutMs = 1000; // max wait for the server to acknowledge an apply or a move
    private const int ArmTimeoutMs = 400;     // max wait for use-mode after right-clicking the currency
    private const int EscapeTimeoutMs = 300;  // max wait for the cursor to free up after Escape
    private const int SearchTimeoutMs = 1500; // max wait for the harvest list to filter after a search
    private const float ClickMarginPx = 4f;   // keep clicks this far inside a slot's edge to avoid misclicks

    private static readonly Random Rng = new();

    private enum ApplyResult { InputFailed, NotArmed, Clicked }
    private enum CraftOutcome { Finished, Abandoned, Stop }

    // The Alt-swap pairs the game offers while a currency is selected: target method -> required selected method.
    private const string Alteration = "Orb of Alteration", Augmentation = "Orb of Augmentation";
    private const string Chance = "Orb of Chance", Alchemy = "Orb of Alchemy", Scouring = "Orb of Scouring";

    private readonly GameController _gc;
    private readonly string _pluginName;
    private readonly ItemReader _reader;
    private readonly HarvestController _harvestController;
    private readonly List<string> _log = new();
    private readonly Dictionary<string, int> _spent = new(StringComparer.OrdinalIgnoreCase);
    private uint _areaHash;
    private string _armedMethod;   // currency currently selected in use-mode ("" = none)
    private bool _shiftHeld;       // we are holding Shift to keep _armedMethod selected across uses
    private Vector2 _itemClickPos; // cached jittered click point on the current item - kept fixed across
                                   // consecutive applies (no mouse jiggle); re-rolled only when we re-arm
    private string _selectedHarvestCraft; // harvest craft tracked as selected in the bench - persists across runs;
                                          // cleared only by searching a new craft or the bench closing (ResetHarvestSelection)
    private Vector2 _harvestClickPos;     // cached click point on the Craft button, reused across applies (no mouse jiggle)
    private bool _harvestPosSet;

    public CraftExecutor(GameController gc, string pluginName, ItemReader reader)
    {
        _gc = gc;
        _pluginName = pluginName;
        _reader = reader;
        _harvestController = new HarvestController(gc);
    }

    public bool Running { get; private set; }
    public string CurrentItem { get; private set; } = "";
    public int Processed { get; private set; }
    public int Done { get; private set; }

    /// <summary>Currency consumed during the most recent run (base name -> count). Read after a run to record
    /// stats; reset at the start of each Run/RunStash.</summary>
    public IReadOnlyDictionary<string, int> Spent => _spent;

    // In-game Alt-swap shortcuts the user has enabled (Settings).
    public bool AltAugShortcut { get; set; }
    public bool ChanceScourShortcut { get; set; }
    public bool AlchScourShortcut { get; set; }

    public IReadOnlyList<string> LogLines => _log;
    public void ClearLog() => _log.Clear();

    public void Log(string msg)
    {
        _log.Add(msg);
        if (_log.Count > 300) _log.RemoveAt(0);
    }

    public async SyncTask<bool> Run(CraftPlan plan, CancellationToken ct)
    {
        Running = true;
        Processed = 0;
        Done = 0;
        CurrentItem = "";
        _spent.Clear();
        _armedMethod = null;
        _shiftHeld = false;

        try
        {
            var start = plan.Graph?.Start;
            if (start == null || start.Next < 0)
            {
                Log("This craft's graph has no Start -> first node. Add a flow on the Graph tab.");
                return false;
            }

            var input = AcquireInput();
            if (input == null) return false;

            using (input)
            {
                try
                {
                    _areaHash = _gc.Area?.CurrentArea?.Hash ?? 0;

                    var matching = _reader.ReadInventory().Where(i => ItemMatcher.Matches(plan, i.Data)).ToList();
                    var items = matching.Where(i => GraphSimulator.WillCraft(plan.Graph, i.Data)).ToList();
                    LogWorkCount(items.Count, matching.Count - items.Count, "in inventory");

                    foreach (var item in items)
                    {
                        ct.ThrowIfCancellationRequested();
                        var outcome = await CraftItem(plan, item, input, ct);
                        Processed++;
                        if (outcome == CraftOutcome.Stop) { Log("Stopping run."); break; }
                    }

                    LogRunSummary();
                }
                finally
                {
                    // Never leave Shift held or a currency armed on the cursor, whatever path got us here.
                    // The run's token may already be cancelled, so recovery runs un-cancellable, best-effort.
                    try
                    {
                        await ReleaseShiftAndCursor(input, CancellationToken.None);
                        await ReleaseHeldModifiers(input);
                    }
                    catch (Exception e) { Log("Cursor recovery failed: " + e.Message); }
                }
            }

            return true;
        }
        catch (OperationCanceledException) { Log("Aborted."); return false; }
        catch (Exception e) { Log("Error: " + e.Message); return false; }
        finally
        {
            Running = false;
            CurrentItem = "";
        }
    }

    /// <summary>
    /// Stash-mode run: crafts each matching item from the Input tab inside the Currency tab, then deposits
    /// finished items in the Output tab. One item at a time. Stops (leaving the in-progress item in the
    /// currency tab) on out-of-currency, a guard trip, a desync, or a craft that ends without Finish.
    /// </summary>
    public async SyncTask<bool> RunStash(CraftPlan plan, string inTab, string curTab, string outTab, CancellationToken ct)
    {
        Running = true;
        Processed = 0;
        Done = 0;
        CurrentItem = "";
        _spent.Clear();
        _armedMethod = null;
        _shiftHeld = false;

        try
        {
            var start = plan.Graph?.Start;
            if (start == null || start.Next < 0)
            {
                Log("This craft's graph has no Start -> first node. Add a flow on the Graph tab.");
                return false;
            }

            var input = AcquireInput();
            if (input == null) return false;

            using (input)
            {
                try
                {
                    _areaHash = _gc.Area?.CurrentArea?.Hash ?? 0;
                    var stash = new StashController(_gc);

                    int inIdx = stash.ResolveIndex(inTab), curIdx = stash.ResolveIndex(curTab), outIdx = stash.ResolveIndex(outTab);
                    if (inIdx < 0 || curIdx < 0 || outIdx < 0)
                    {
                        Log($"Can't find the stash tabs (In='{inTab}', $='{curTab}', Out='{outTab}'). Open the stash and set them on the Main page.");
                        return false;
                    }

                    // No preloading: every SwitchToTab below waits for its tab to load (WaitLoaded), and we
                    // never read a tab without switching to it first, so an unviewed tab loads on first use.
                    await StashLoop(plan, stash, inIdx, curIdx, outIdx, input, ct);
                    LogRunSummary();
                }
                finally
                {
                    try
                    {
                        await ReleaseShiftAndCursor(input, CancellationToken.None);
                        await ReleaseHeldModifiers(input);
                    }
                    catch (Exception e) { Log("Cursor recovery failed: " + e.Message); }
                }
            }

            return true;
        }
        catch (OperationCanceledException) { Log("Aborted."); return false; }
        catch (Exception e) { Log("Error: " + e.Message); return false; }
        finally
        {
            Running = false;
            CurrentItem = "";
        }
    }

    /// <summary>
    /// Harvest-mode run: each matching inventory item is placed into the Horticrafting bench slot and crafted
    /// there (harvest crafts via the bench; currency orbs applied to the slotted item straight from the
    /// inventory), then the finished item is moved back to the inventory. A pre-existing bench occupant is
    /// resumed (if it matches) or cleared out first. Stops - leaving the in-progress item in the bench - on a
    /// non-finish. Items are taken from a one-time snapshot, so a finished item that still matches isn't recrafted.
    /// </summary>
    public async SyncTask<bool> RunHarvest(CraftPlan plan, CancellationToken ct)
    {
        Running = true;
        Processed = 0;
        Done = 0;
        CurrentItem = "";
        _spent.Clear();
        _armedMethod = null;
        _shiftHeld = false;

        try
        {
            var start = plan.Graph?.Start;
            if (start == null || start.Next < 0)
            {
                Log("This craft's graph has no Start -> first node. Add a flow on the Graph tab.");
                return false;
            }

            var input = AcquireInput();
            if (input == null) return false;

            using (input)
            {
                try
                {
                    _areaHash = _gc.Area?.CurrentArea?.Hash ?? 0;
                    if (!GuardsOk(CraftSource.HarvestBench, out var reason)) { Log($"{reason} - aborting run."); return false; }

                    // Resume or clear whatever is already sitting in the bench slot (same rule as the currency tab).
                    var occupant = _reader.ReadHarvestBenchCraftable();
                    if (occupant != null)
                    {
                        if (ItemMatcher.MatchesBase(plan, occupant.Data) && GraphSimulator.WillCraft(plan.Graph, occupant.Data))
                        {
                            Log($"Resuming the item already in the bench ('{occupant.Data.BaseName}').");
                            if (!await CraftAndDeposit(plan, occupant, input, ct)) return true; // stopped; item stays in the bench
                        }
                        else
                        {
                            // Doesn't fit this craft, or is already finished - move it out to free the slot.
                            Log($"Bench holds an item we won't craft ('{occupant.Data.BaseName}') - moving it to the inventory.");
                            if (!await MoveItem(input, occupant.Target, ct)) { Log("Couldn't clear the bench - stopping."); return false; }
                        }
                    }

                    // Snapshot the matching, not-yet-finished inventory items ONCE - finished items return to the
                    // inventory but are not reprocessed (the snapshot is fixed), and untouched items keep their cells.
                    var matching = _reader.ReadInventory().Where(i => ItemMatcher.Matches(plan, i.Data)).ToList();
                    var items = matching.Where(i => GraphSimulator.WillCraft(plan.Graph, i.Data)).ToList();
                    LogWorkCount(items.Count, matching.Count - items.Count, "in inventory");

                    foreach (var snap in items)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!GuardsOk(CraftSource.HarvestBench, out reason)) { Log($"{reason} - aborting run."); break; }

                        var live = _reader.ReadInventoryAt(snap.Center);
                        if (live == null) continue; // already moved/consumed

                        if (_reader.ReadHarvestBenchCraftable() != null) { Log("Bench slot still occupied - stopping."); break; }

                        if (!await MoveItem(input, live.Target, ct)) { Log("Couldn't place the item into the bench - stopping."); break; }

                        var work = await WaitForBenchItem(ct);
                        if (work == null) { Log("Item didn't arrive in the bench - stopping."); break; }

                        if (!await CraftAndDeposit(plan, work, input, ct)) break; // stopped; item stays in the bench
                    }

                    LogRunSummary();
                }
                finally
                {
                    try
                    {
                        await ReleaseShiftAndCursor(input, CancellationToken.None);
                        await ReleaseHeldModifiers(input);
                    }
                    catch (Exception e) { Log("Cursor recovery failed: " + e.Message); }
                }
            }

            return true;
        }
        catch (OperationCanceledException) { Log("Aborted."); return false; }
        catch (Exception e) { Log("Error: " + e.Message); return false; }
        finally
        {
            Running = false;
            CurrentItem = "";
        }
    }

    /// <summary>Crafts the item currently in the bench; on Finish moves it back to the inventory. Returns false to stop the run.</summary>
    private async SyncTask<bool> CraftAndDeposit(CraftPlan plan, ReadItem work, IInputController input, CancellationToken ct)
    {
        var outcome = await CraftItem(plan, work, input, ct);
        Processed++;
        if (outcome != CraftOutcome.Finished) { Log("Stopping run; the current item stays in the bench."); return false; }

        // The item never moved during crafting, so its slot rect is still valid for the move out to the inventory.
        if (!await MoveItem(input, work.Target, ct)) { Log("Crafted item couldn't leave the bench - stopping."); return false; }
        return true;
    }

    private async SyncTask<ReadItem> WaitForBenchItem(CancellationToken ct)
    {
        ReadItem found = null;
        await WaitFor(() => (found = _reader.ReadHarvestBenchCraftable()) != null, VerifyTimeoutMs, ct);
        return found;
    }

    /// <summary>Clears the tracked harvest-craft selection. The game keeps a craft selected until the bench
    /// window closes (or a new craft is searched), so this is called from Tick whenever the bench isn't open -
    /// then the next harvest apply re-selects from scratch, while an uninterrupted session keeps its selection.</summary>
    public void ResetHarvestSelection()
    {
        _selectedHarvestCraft = null;
        _harvestPosSet = false;
    }

    /// <summary>
    /// Applies the selected harvest craft to the item in the bench, verified by the bench's ServerRequestCounter
    /// moving. Re-selects the craft only when our tracked selection doesn't already match it (selection persists
    /// across uses, item moves, and run restarts - it resets only on a new search or the window closing), then
    /// clicks the Craft button, reusing the same button click-point across applies so the mouse doesn't jiggle.
    /// </summary>
    private async SyncTask<bool> ApplyHarvestVerified(IInputController input, string craft, string name, CraftSource source, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var before = _reader.ReadCounter(source);
            if (before < 0) { Log("Can't read the bench state - aborting run."); return false; }

            var reselected = false;
            if (!string.Equals(_selectedHarvestCraft, craft, StringComparison.Ordinal))
            {
                if (!await SelectHarvestCraft(input, craft, ct)) return false;
                _selectedHarvestCraft = craft;
                reselected = true;
            }

            var btn = _harvestController.CraftButtonTarget();
            if (btn == null) { Log("Horticrafting Craft button not found - aborting run."); return false; }

            // Reuse the same Craft-button click point across applies (no jiggle, like held-currency spam); re-roll
            // only when we just moved the cursor away to (re)select, or the point hasn't been set yet this session.
            if (reselected || !_harvestPosSet) { _harvestClickPos = ClickPos(btn.Value); _harvestPosSet = true; }

            if (!await input.Click(MouseButtons.Left, _harvestClickPos, ct)) { Log("Craft-button click failed."); return false; }

            if (await WaitFor(() => { var n = _reader.ReadCounter(source); return n >= 0 && n != before; }, VerifyTimeoutMs, ct))
            {
                _spent[craft] = _spent.GetValueOrDefault(craft) + 1;
                return true;
            }

            Log($"{name}: '{craft}' didn't apply{(attempt == 1 ? " - retrying once." : " (out of lifeforce or unavailable).")}");
            _selectedHarvestCraft = null; // force a fresh search+select on the retry
            _harvestPosSet = false;
        }

        Log($"{name}: harvest craft could not be applied - stopping.");
        return false;
    }

    /// <summary>
    /// Brings <paramref name="craft"/> on-screen and selects it: copy its text to the clipboard, focus the
    /// search box (Ctrl+F, which selects any existing text) and paste over it (Ctrl+V) so only that craft
    /// shows, restore the previous clipboard, then click the now-visible row. Searching by the full display
    /// text sidesteps the list's scrolling - off-screen rows can't be clicked.
    /// </summary>
    private async SyncTask<bool> SelectHarvestCraft(IInputController input, string craft, CancellationToken ct)
    {
        if (!_harvestController.IsVisible) { Log("Horticrafting bench window is not open - stopping."); return false; }

        string saved = "";
        try { saved = ImGui.GetClipboardText() ?? ""; } catch { /* ignore */ }

        try
        {
            try { ImGui.SetClipboardText(craft); }
            catch (Exception e) { Log("Couldn't set the clipboard for the harvest search: " + e.Message); return false; }

            if (!await KeyChord(input, Keys.LControlKey, Keys.F, ct)) return false;
            if (!await KeyChord(input, Keys.LControlKey, Keys.V, ct)) return false;

            if (!await WaitFor(() => _harvestController.FindVisibleCraft(craft) != null, SearchTimeoutMs, ct))
            {
                Log($"Harvest craft not found after search: '{craft}'.");
                return false;
            }
        }
        finally
        {
            try { ImGui.SetClipboardText(saved); } catch { /* ignore */ }
        }

        var target = _harvestController.FindVisibleCraft(craft);
        if (target == null) { Log($"Harvest craft vanished before it could be selected: '{craft}'."); return false; }
        if (!await input.Click(MouseButtons.Left, ClickPos(target.Value), ct)) { Log("Craft-row click failed."); return false; }

        return true;
    }

    /// <summary>Sends a modifier+key chord (e.g. Ctrl+F / Ctrl+V), releasing the modifier even if the key press fails.</summary>
    private static async SyncTask<bool> KeyChord(IInputController input, Keys modifier, Keys key, CancellationToken ct)
    {
        if (!await input.KeyDown(modifier, ct)) return false;
        try
        {
            if (!await input.KeyDown(key, ct)) return false;
            await input.KeyUp(key, false, ct);
        }
        finally
        {
            await input.KeyUp(modifier, false, CancellationToken.None);
        }
        return true;
    }

    private async SyncTask<bool> StashLoop(CraftPlan plan, StashController stash,
        int inIdx, int curIdx, int outIdx, IInputController input, CancellationToken ct)
    {
        // Count the input-tab work up front (matching items that aren't already finished) so we can log a total
        // and stop without a final empty trip back to the input tab once they're all done.
        if (!await stash.SwitchToTab(inIdx, input, ct)) { Log("Couldn't switch to the input tab - stopping."); return false; }
        var inItems = _reader.ReadVisibleStash().Where(i => ItemMatcher.Matches(plan, i.Data)).ToList();
        var remaining = inItems.Count(i => GraphSimulator.WillCraft(plan.Graph, i.Data));
        LogWorkCount(remaining, inItems.Count - remaining, "in the input tab");

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (!GuardsOk(CraftSource.VisibleStash, out var reason)) { Log($"{reason} - aborting run."); return false; }

            // The currency tab is where crafting happens; make it visible and check whether a craftable
            // item is already sitting in it (left from a previous run, or one we just moved in).
            if (!await stash.SwitchToTab(curIdx, input, ct)) { Log("Couldn't switch to the currency tab - stopping."); return false; }

            var work = FirstMatch(plan, CraftSource.VisibleStash);
            if (work == null)
            {
                // The currency tab's item slot may already be occupied. If it holds the right base (e.g. a
                // mid-craft item left by a previous run) resume crafting it; if it's something else, clear it
                // out to the inventory first - otherwise the next item can't be placed in that slot.
                var occupant = FirstStashOccupant();
                if (occupant != null && ItemMatcher.MatchesBase(plan, occupant.Data))
                {
                    work = occupant;
                }
                else if (occupant != null)
                {
                    Log($"Currency tab holds a non-matching item ('{occupant.Data.BaseName}') - moving it to the inventory.");
                    if (!await MoveItem(input, occupant.Target, ct)) { Log("Couldn't clear the currency tab - stopping."); return false; }
                    continue; // slot is free now; re-evaluate from the top
                }
                else
                {
                    if (remaining <= 0) return true; // every counted input item is done - don't revisit the input tab
                    var (stop, pulled) = await PullNextItem(plan, stash, inIdx, curIdx, input, ct);
                    if (stop) return false;
                    if (pulled == null) return true; // input emptied earlier than counted - clean finish
                    remaining--;
                    work = pulled;
                }
            }

            // The base name is stable across the craft (currency never changes the base), so it identifies
            // the in-transit item for the output move even when crafting made it stop matching the filter.
            var baseName = work.Data.BaseName;
            var outcome = await CraftItem(plan, work, input, ct);
            Processed++;
            if (outcome != CraftOutcome.Finished)
            {
                Log("Stopping run; the current item stays in the currency tab.");
                return false;
            }

            // Finished -> currency tab -> inventory -> Out tab.
            if (!await MoveItem(input, work.Target, ct)) { Log("Crafted item couldn't leave the currency tab - stopping."); return false; }
            if (!await stash.SwitchToTab(outIdx, input, ct)) { Log("Couldn't switch to the output tab - stopping."); return false; }
            var deposited = FirstInventoryByBase(baseName);
            if (deposited == null) { Log("Finished item not found in inventory for the output move - stopping."); return false; }
            if (!await MoveItem(input, deposited.Target, ct, ignoreAffinity: true)) { Log("Crafted item couldn't move to the output tab - stopping."); return false; }
        }
    }

    /// <summary>
    /// Pulls the next matching Input-tab item into the currency tab (via the inventory hop). Returns
    /// (stop, item): stop=true means abort the run; item=null with stop=false means the input tab has no
    /// more matches (a clean finish); otherwise item is the work item now sitting in the currency tab.
    /// </summary>
    private async SyncTask<(bool stop, ReadItem item)> PullNextItem(CraftPlan plan, StashController stash,
        int inIdx, int curIdx, IInputController input, CancellationToken ct)
    {
        if (!await stash.SwitchToTab(inIdx, input, ct)) { Log("Couldn't switch to the input tab - stopping."); return (true, null); }
        var src = FirstMatchToCraft(plan, CraftSource.VisibleStash); // skip already-finished items (leave them in place)
        if (src == null) return (false, null); // no more items to craft - clean finish

        if (!await MoveItem(input, src.Target, ct)) { Log("Failed to move an item out of the input tab - stopping."); return (true, null); }

        if (!await stash.SwitchToTab(curIdx, input, ct)) { Log("Couldn't switch to the currency tab - stopping."); return (true, null); }
        var inInventory = FirstMatch(plan, CraftSource.Inventory);
        if (inInventory == null) { Log("Pulled item didn't reach the inventory - stopping."); return (true, null); }
        if (!await MoveItem(input, inInventory.Target, ct, ignoreAffinity: true)) { Log("Failed to move the item into the currency tab - stopping."); return (true, null); }

        var item = FirstMatch(plan, CraftSource.VisibleStash);
        if (item == null) { Log("Item didn't arrive in the currency tab - stopping."); return (true, null); }
        return (false, item);
    }

    private IInputController AcquireInput()
    {
        var getter = _gc.PluginBridge.GetMethod<Func<string, IInputController>>("InputHumanizer.TryGetInputController");
        if (getter == null) { Log("InputHumanizer not available - is it enabled?"); return null; }
        var input = getter(_pluginName);
        if (input == null) { Log("Couldn't acquire input lock (another plugin is using it)."); return null; }
        return input;
    }

    private ReadItem FirstMatch(CraftPlan plan, CraftSource source) =>
        _reader.Read(source).FirstOrDefault(i => ItemMatcher.Matches(plan, i.Data));

    /// <summary>First item on the surface that both matches the plan AND would actually be crafted - i.e. the
    /// graph wouldn't immediately finish it without any work. Used when pulling input items, so already-finished
    /// items are left in place (they may be a base for a later craft) instead of being shuttled around for nothing.</summary>
    private ReadItem FirstMatchToCraft(CraftPlan plan, CraftSource source) =>
        _reader.Read(source).FirstOrDefault(i => ItemMatcher.Matches(plan, i.Data) && GraphSimulator.WillCraft(plan.Graph, i.Data));

    /// <summary>Finds an inventory item by exact base name - used to re-locate the in-transit crafted item, whose mods/rarity may no longer match the plan filter.</summary>
    private ReadItem FirstInventoryByBase(string baseName) =>
        _reader.ReadInventory().FirstOrDefault(i => i.Data?.BaseName == baseName);

    /// <summary>The craftable item sitting in the visible (currency) tab - identified by its Mods component
    /// (currency / cards have none), which is reliable where StackInfo is not. Null when the slot is empty.</summary>
    private ReadItem FirstStashOccupant() => _reader.ReadStashCraftable();

    /// <summary>Logs the work set for a craft: how many items will be crafted, and how many matching items were
    /// skipped because the graph would finish them with no work (left in place for a possible later craft).</summary>
    private void LogWorkCount(int toCraft, int skipped, string where) =>
        Log(skipped > 0
            ? $"{toCraft} item(s) to craft {where} ({skipped} already finished, skipped)."
            : $"{toCraft} item(s) to craft {where}.");

    private void LogRunSummary()
    {
        Log($"Run complete. {Done}/{Processed} finished.");
        if (_spent.Count > 0)
            Log("Used: " + string.Join(", ", _spent.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value}x {kv.Key}")));
    }

    /// <summary>
    /// Ctrl+click an item to move it between the visible stash tab and the inventory, verified by the
    /// inventory's counter moving (the item enters or leaves it either way). When <paramref name="ignoreAffinity"/>
    /// is set, Shift is held too so the move ignores stash affinities - used for moves INTO a target tab
    /// (currency / output) so the item lands in the tab we're viewing instead of an affinity-matched one.
    /// </summary>
    private async SyncTask<bool> MoveItem(IInputController input, ClickTarget target, CancellationToken ct, bool ignoreAffinity = false)
    {
        // A craft can reach Finish with Shift still held (a held-currency sequence) or a currency armed.
        // Clear that first - a Shift+ctrl+click or right-click-while-armed is not a clean move. This also
        // leaves a clean cursor for the arrow-key tab switch that follows.
        await ReleaseShiftAndCursor(input, ct);

        var before = _reader.ReadInventoryCounter();
        if (before < 0) { Log("Can't read the inventory state for a move."); return false; }

        var pos = ClickPos(target);
        if (ignoreAffinity && !await input.KeyDown(Keys.ShiftKey, ct)) return false;
        try
        {
            if (!await input.KeyDown(Keys.LControlKey, ct)) return false;
            try
            {
                if (!await input.Click(MouseButtons.Left, pos, ct)) { Log("Ctrl+click (move) failed."); return false; }
            }
            finally
            {
                await input.KeyUp(Keys.LControlKey, false, CancellationToken.None);
            }
        }
        finally
        {
            if (ignoreAffinity) await input.KeyUp(Keys.ShiftKey, false, CancellationToken.None);
        }

        return await WaitFor(() => { var n = _reader.ReadInventoryCounter(); return n >= 0 && n != before; }, VerifyTimeoutMs, ct);
    }

    /// <summary>Walks the graph for one item on its own surface (inventory, or the visible currency tab).</summary>
    private async SyncTask<CraftOutcome> CraftItem(CraftPlan plan, ReadItem item, IInputController input, CancellationToken ct)
    {
        var graph = plan.Graph;
        var center = item.Center;
        var source = item.Source;
        var name = item.Data.BaseName;
        CurrentItem = name;

        // Always re-read fresh by slot position: entity references can go stale once the server rewrites
        // the item, and a stale read would defeat the change-verification this executor relies on.
        ItemData Resolve()
        {
            try { return _reader.ReadAt(source, center)?.Data; }
            catch { return null; }
        }

        var current = graph.Get(graph.Start.Next);

        while (current != null)
        {
            ct.ThrowIfCancellationRequested();

            // One node transition per frame: keeps a Check->Check cycle from freezing the HUD thread
            // (Apply nodes await clicks anyway) while leaving step counts unbounded.
            await TaskUtils.NextFrame();

            switch (current.Type)
            {
                case NodeType.Finish:
                    Log($"{name}: finished.");
                    Done++;
                    return CraftOutcome.Finished;

                case NodeType.Check:
                {
                    var data = Resolve();
                    if (data == null) { Log($"{name}: item no longer present."); return CraftOutcome.Stop; }
                    var pass = CheckEvaluator.Passes(current.Check, data);
                    current = graph.Get(pass ? current.OnTrue : current.OnFalse);
                    break;
                }

                case NodeType.Apply:
                {
                    var methods = current.Methods;
                    if (methods.Count == 0) { current = graph.Get(current.Next); break; }

                    if (!GuardsOk(source, out var reason)) { Log($"{reason} - aborting run."); return CraftOutcome.Stop; }

                    // Use the first selected currency that still has stock; only "out of currency" (stop) when
                    // every one of them is empty. Lets the user list several essences cheapest-first and spam.
                    var avail = FirstAvailable(methods, CurrencySourceFor(source));
                    if (avail == null)
                    {
                        Log($"Out of {DescribeMethods(methods)} - skipping the rest of this craft.");
                        return CraftOutcome.Stop;
                    }

                    if (Resolve() == null) { Log($"{name}: item no longer present."); return CraftOutcome.Stop; }

                    if (!await ApplyVerified(input, avail.Value.Stack.Target, item.Target, current, avail.Value.Method, name, source, ct))
                        return CraftOutcome.Stop;

                    current = graph.Get(current.Next);
                    break;
                }

                case NodeType.Harvest:
                {
                    var craft = current.HarvestCraft;
                    if (string.IsNullOrEmpty(craft)) { current = graph.Get(current.Next); break; }

                    if (!GuardsOk(source, out var reason)) { Log($"{reason} - aborting run."); return CraftOutcome.Stop; }
                    if (Resolve() == null) { Log($"{name}: item no longer present."); return CraftOutcome.Stop; }

                    if (!await ApplyHarvestVerified(input, craft, name, source, ct))
                        return CraftOutcome.Stop;

                    current = graph.Get(current.Next);
                    break;
                }

                default: // Start
                    current = graph.Get(current.Next);
                    break;
            }
        }

        Log($"{name}: flow ended without reaching Finish - abandoning this item.");
        return CraftOutcome.Abandoned;
    }

    // ---------------- apply + verification ----------------

    /// <summary>
    /// One verified currency application, then wait for the inventory's ServerRequestCounter to move (the
    /// server bumps it for every item event - mod rewrite, stack consumed). Three ways to apply: continue a
    /// held-Shift sequence with a bare left-click, ride an enabled Alt-swap shortcut off the selected
    /// currency, or arm fresh with a right-click (holding Shift afterwards if the node asks for it).
    /// A missed click or unacknowledged apply gets the state cleared and one retry; failing that the run
    /// aborts - the on-screen state can no longer be trusted.
    /// </summary>
    private async SyncTask<bool> ApplyVerified(IInputController input, ClickTarget currency, ClickTarget item,
        CraftNode node, string method, string name, CraftSource source, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            // Snapshot per attempt: if a first apply lands after its timeout, the retry's baseline owns it
            // and the retry's own apply is still verified against the newer value.
            var before = _reader.ReadCounter(source);
            if (before < 0) { Log("Can't read the item's surface state - aborting run."); return false; }

            // When a held-Shift currency runs out and we fall through to the next one, _armedMethod no longer
            // matches and ArmAndApply re-arms (releasing the old Shift) - so switching essences just works.
            ApplyResult result;
            if (_shiftHeld && CursorOccupied() && _armedMethod == method)
                result = await ApplyClick(input, item, altSwap: false, ct);
            else if (_shiftHeld && CursorOccupied() && ShortcutActive(method))
                result = await ApplyClick(input, item, altSwap: true, ct);
            else
                result = await ArmAndApply(input, currency, item, node, method, ct);

            if (result == ApplyResult.InputFailed) return false;

            if (result == ApplyResult.Clicked)
            {
                _spent[method] = _spent.GetValueOrDefault(method) + 1;

                if (await WaitFor(() =>
                    {
                        var now = _reader.ReadCounter(source);
                        return now >= 0 && now != before;
                    }, VerifyTimeoutMs, ct)) return true;
                Log($"{name}: server never acknowledged '{method}'{(attempt == 1 ? " - retrying once." : ".")}");
            }
            else
            {
                Log($"{name}: use-mode never armed after right-clicking '{method}'{(attempt == 1 ? " - retrying once." : ".")}");
            }

            // Reset to a clean slate (Shift up, cursor free) so the retry re-arms from scratch.
            await ReleaseShiftAndCursor(input, ct);
        }

        Log($"{name}: apply could not be verified - aborting run as desynced.");
        return false;
    }

    /// <summary>True when the game's Alt-swap can produce <paramref name="target"/> from the selected currency.</summary>
    private bool ShortcutActive(string target) =>
        (target == Augmentation && _armedMethod == Alteration && AltAugShortcut) ||
        (target == Scouring && _armedMethod == Chance && ChanceScourShortcut) ||
        (target == Scouring && _armedMethod == Alchemy && AlchScourShortcut);

    /// <summary>One application within an active use-mode: bare left-click, or left-Alt+click for a swap shortcut.</summary>
    private async SyncTask<ApplyResult> ApplyClick(IInputController input, ClickTarget item, bool altSwap, CancellationToken ct)
    {
        // Held-shift spam: keep hitting the SAME point on the item (the cursor hasn't moved off it), so we
        // don't jiggle the mouse on every apply. _itemClickPos was set when this currency was armed.
        var pos = _itemClickPos;

        // Explicitly left Alt (LMenu) wrapped around a plain click - InputHumanizer's MouseModifiers.Alt
        // would send the neutral VK_MENU, which the game may not map to its swap shortcut.
        if (altSwap && !await input.KeyDown(Keys.LMenu, ct)) return ApplyResult.InputFailed;
        try
        {
            if (!await input.Click(MouseButtons.Left, pos, ct))
            {
                Log("Left-click (apply to item) failed.");
                return ApplyResult.InputFailed;
            }
        }
        finally
        {
            // Un-cancellable: Alt must come back up even when the click throws on a mid-run abort.
            if (altSwap) await input.KeyUp(Keys.LMenu, false, CancellationToken.None);
        }

        return ApplyResult.Clicked;
    }

    /// <summary>Selects <paramref name="method"/> with a right-click (clearing any leftover state first) and applies once.</summary>
    private async SyncTask<ApplyResult> ArmAndApply(IInputController input, ClickTarget currency, ClickTarget item,
        CraftNode node, string method, CancellationToken ct)
    {
        // Leftover Shift sequence for another method, or a stray armed cursor (user leftover, earlier
        // failure) that would swallow the right-click as a cancel.
        await ReleaseShiftAndCursor(input, ct);

        if (!await input.Click(MouseButtons.Right, ClickPos(currency), ct))
        {
            Log("Right-click (pick currency) failed.");
            return ApplyResult.InputFailed;
        }

        // Wait for the game to confirm the currency is armed before clicking the item; spend is only
        // counted once the apply click actually goes out. Click pacing itself comes from InputHumanizer's
        // built-in humanized delays - no extra sleeps here.
        if (!await WaitFor(CursorOccupied, ArmTimeoutMs, ct)) return ApplyResult.NotArmed;
        _armedMethod = method;

        if (node.UseShift)
        {
            // Shift goes down after the right-click (a Shift+right-click could stack-split instead) and
            // stays down so the currency survives each use until ReleaseShiftAndCursor.
            if (!await input.KeyDown(Keys.ShiftKey, ct)) return ApplyResult.InputFailed;
            _shiftHeld = true;
        }

        // Fresh point on the item now that the cursor has come back from the currency; held applies reuse it.
        _itemClickPos = ClickPos(item);
        if (!await input.Click(MouseButtons.Left, _itemClickPos, ct))
        {
            Log("Left-click (apply to item) failed.");
            return ApplyResult.InputFailed;
        }

        if (!node.UseShift) _armedMethod = null; // single use-mode is consumed by the click
        return ApplyResult.Clicked;
    }

    /// <summary>
    /// Returns input state to neutral: releases a held Shift (the game then auto-deselects the currency)
    /// and falls back to Escape for anything still on the cursor.
    /// </summary>
    private async SyncTask<bool> ReleaseShiftAndCursor(IInputController input, CancellationToken ct)
    {
        _armedMethod = null;

        if (_shiftHeld)
        {
            _shiftHeld = false;
            await input.KeyUp(Keys.ShiftKey, false, ct);
            await WaitFor(() => !CursorOccupied(), EscapeTimeoutMs, ct);
        }

        return await DropUseMode(input, ct); // no-op when the cursor is already free
    }

    /// <summary>
    /// Run-end backstop: unconditionally lift every key the crafter can hold (Shift, left Ctrl, left Alt),
    /// independent of the _shiftHeld flag, so nothing stays physically down after a run ends or aborts.
    /// releaseImmediately skips the hold-delay; a KeyUp on a key that isn't down is harmless.
    /// </summary>
    private static async SyncTask<bool> ReleaseHeldModifiers(IInputController input)
    {
        await input.KeyUp(Keys.ShiftKey, true, CancellationToken.None);
        await input.KeyUp(Keys.LControlKey, true, CancellationToken.None);
        await input.KeyUp(Keys.LMenu, true, CancellationToken.None);
        return true;
    }

    /// <summary>Frame-checked wait (TaskUtils.CheckEveryFrame): false on timeout, throws on run abort.</summary>
    private static async SyncTask<bool> WaitFor(Func<bool> condition, int timeoutMs, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        var ok = await TaskUtils.CheckEveryFrame(condition, cts.Token);
        ct.ThrowIfCancellationRequested();
        return ok;
    }

    // ---------------- guards + cursor recovery ----------------

    private bool GuardsOk(CraftSource source, out string reason)
    {
        if (!_gc.IsForeGroundCache) { reason = "Game window lost focus"; return false; }
        // Items hop through the inventory in stash mode too, so it must stay open in both modes.
        if (_gc.IngameState?.IngameUi?.InventoryPanel?.IsVisible != true) { reason = "Inventory panel closed"; return false; }
        if (source == CraftSource.VisibleStash && _gc.IngameState?.IngameUi?.StashElement?.IsVisible != true) { reason = "Stash closed"; return false; }
        if (source == CraftSource.HarvestBench && _gc.IngameState?.IngameUi?.HorticraftingStationWindow?.IsVisible != true) { reason = "Horticrafting bench closed"; return false; }
        if ((_gc.Area?.CurrentArea?.Hash ?? 0) != _areaHash) { reason = "Area changed"; return false; }
        reason = null;
        return true;
    }

    /// <summary>True when the game cursor holds or is using an item (use-mode armed).</summary>
    private bool CursorOccupied()
    {
        try
        {
            var action = _gc.IngameState?.IngameUi?.Cursor?.Action;
            return action.HasValue && action.Value != MouseActionType.Free;
        }
        catch { return false; }
    }

    /// <summary>Presses Escape to drop an armed use-mode. No-op when the cursor is already free.</summary>
    private async SyncTask<bool> DropUseMode(IInputController input, CancellationToken ct)
    {
        if (!CursorOccupied()) return true;

        await input.KeyDown(Keys.Escape, ct);
        await input.KeyUp(Keys.Escape, false, ct);

        if (!await WaitFor(() => !CursorOccupied(), EscapeTimeoutMs, ct))
        {
            Log("Cursor still holds something after Escape - check the game.");
            return false;
        }
        return true;
    }

    // ---------------- helpers ----------------

    /// <summary>Where to read apply currency for an item on the given surface: harvest crafting reads orbs from
    /// the inventory while the item sits in the bench; otherwise currency shares the item's surface.</summary>
    private static CraftSource CurrencySourceFor(CraftSource itemSource) =>
        itemSource == CraftSource.HarvestBench ? CraftSource.Inventory : itemSource;

    /// <summary>The first of <paramref name="methods"/> (in priority order) that still has a stack with stock,
    /// paired with that stack. Null when none of them have any left (= out of currency for this node).</summary>
    private (string Method, CurrencyStack Stack)? FirstAvailable(IReadOnlyList<string> methods, CraftSource source)
    {
        var map = CurrencyIndex.Build(_reader.Read(source));
        foreach (var m in methods)
            if (!string.IsNullOrEmpty(m) && map.TryGetValue(m, out var stack) && stack.Count > 0)
                return (m, stack);
        return null;
    }

    private static string DescribeMethods(IReadOnlyList<string> methods) =>
        methods.Count == 1 ? $"'{methods[0]}'" : "all selected currencies (" + string.Join(", ", methods) + ")";

    private Vector2 WindowOffset()
    {
        var rect = _gc.Window.GetWindowRectangle();
        return new Vector2(rect.TopLeft.X, rect.TopLeft.Y);
    }

    /// <summary>A randomised screen-space click point inside the target's rect, kept <see cref="ClickMarginPx"/>
    /// clear of the edges so repeated applies don't always strike the same pixel, then offset to the window.</summary>
    private Vector2 ClickPos(ClickTarget target)
    {
        var hx = Math.Max(0f, target.Size.X / 2f - ClickMarginPx);
        var hy = Math.Max(0f, target.Size.Y / 2f - ClickMarginPx);
        var jx = (float)(Rng.NextDouble() * 2 - 1) * hx;
        var jy = (float)(Rng.NextDouble() * 2 - 1) * hy;
        return target.Center + new Vector2(jx, jy) + WindowOffset();
    }
}
