using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using ExileCore;
using ExileCore.Shared;
using ExileCore.Shared.Enums;
using ImGuiNET;
using MyBigCrafter.Data;
using MyBigCrafter.Model;
using MyBigCrafter.UI;
using PoeModDataLib.Api;

namespace MyBigCrafter;

public class MyBigCrafter : BaseSettingsPlugin<MyBigCrafterSettings>
{
    private readonly CurrencyCatalog _currencies = new();
    private readonly HarvestCraftCatalog _harvest = new();

    private CraftPlan _plan = new();
    private RunConfig _run = new();
    private CraftStorage _storage;
    private StatsStore _stats;
    private CurrencyPricer _pricer;
    private bool _statsUseCurrentPrices;   // Stats page: false = at-craft-time cost basis, true = today's prices
    private ItemReader _reader;
    private CraftExecutor _exec;

    private SyncTask<bool> _runTask;
    private CancellationTokenSource _cts;

    private static readonly string[] NoCrafts = { "(no saved crafts)" };

    private string[] _craftFiles = Array.Empty<string>();
    private string[] _stashTabs = Array.Empty<string>();      // Normal/Premium/Quad -- input/output roles
    private string[] _currencyTabs = Array.Empty<string>();   // Currency tabs -- currency role only
    private int _loadIdx = -1;   // -1 = nothing selected in the Open dropdown
    private bool _queueAddOpen;
    private bool _queueAddFocus;   // raise the add-craft window on its next draw (set on open/re-open)
    private string _queueAddSearch = "";
    private string _status = "";
    private string _loadedName = "";   // file the current craft was loaded/saved as ("" = unsaved)

    public override bool Initialise() => true;

    public override Job Tick()
    {
        _reader ??= new ItemReader(GameController);

        // The bench drops its selected craft when its window closes - mirror that in our tracker so the next
        // harvest run re-selects, while a continuously-open bench keeps its selection across runs.
        if (_exec != null && GameController?.IngameState?.IngameUi?.HorticraftingStationWindow?.IsVisible != true)
            _exec.ResetHarvestSelection();

        if (Settings.StartStopHotkey.PressedOnce())
        {
            if (_runTask != null) StopRun();
            else if (_exec != null) StartQueue();
        }
        if (_runTask != null) TaskUtils.RunOrRestart(ref _runTask, () => null);

        return null;
    }

    public override void DrawSettings()
    {
        _currencies.EnsureBuilt();
        _harvest.EnsureBuilt();
        _reader ??= new ItemReader(GameController);
        _exec ??= new CraftExecutor(GameController, Name, _reader);
        if (_storage == null)
        {
            _storage = new CraftStorage(ConfigDirectory);
            _stats = new StatsStore(ConfigDirectory);
            _pricer = new CurrencyPricer(GameController);
            _run = _storage.LoadRunConfig();
            RefreshCraftFiles();
        }

        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
        ImGui.PushStyleVar(ImGuiStyleVar.TabRounding, 4f);

        if (ImGui.BeginTabBar("##mbc_top", ImGuiTabBarFlags.None))
        {
            if (ImGui.BeginTabItem("Main"))
            {
                DrawMainPage();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Crafts"))
            {
                DrawCraftsPage();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Stats"))
            {
                DrawStatsPage();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Settings"))
            {
                ImGui.Spacing();
                base.DrawSettings();
                ImGui.Spacing();
                DrawStashTabsConfig();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }

        ImGui.PopStyleVar(2);
    }

    // ---------------- Main page (run dashboard) ----------------

    private void DrawMainPage()
    {
        ImGui.Spacing();
        ImGui.SeparatorText("Mode");
        var mode = (int)_run.Mode;
        if (ImGui.RadioButton("Inventory", ref mode, 0)) { _run.Mode = RunMode.Inventory; SaveRun(); }
        ImGui.SameLine();
        if (ImGui.RadioButton("Stash", ref mode, 1)) { _run.Mode = RunMode.Stash; SaveRun(); }
        ImGui.SameLine();
        if (ImGui.RadioButton("Harvest", ref mode, 2)) { _run.Mode = RunMode.Harvest; SaveRun(); }

        ImGui.Spacing();
        ImGui.SeparatorText("Craft queue");
        DrawQueueEditor();

        ImGui.Spacing();
        ImGui.SeparatorText("Run");
        DrawRunControls();
    }

    // ---------------- Stash tabs (Settings tab) ----------------

    private void DrawStashTabsConfig()
    {
        ImGui.SeparatorText("Stash tabs");
        DrawStashTabConfig();
    }

    private void DrawStashTabConfig()
    {
        RefreshStashTabs();
        if (_stashTabs.Length == 0 && _currencyTabs.Length == 0)
        {
            ImGui.TextColored(UiColors.Warn, "Open your stash once in-game to load tab names.");
            return;
        }
        TabCombo("Input tab", _stashTabs, () => _run.InputTab, v => _run.InputTab = v);
        TabCombo("Currency tab", _currencyTabs, () => _run.CurrencyTab, v => _run.CurrencyTab = v);
        TabCombo("Output tab", _stashTabs, () => _run.OutputTab, v => _run.OutputTab = v);
    }

    private void TabCombo(string label, string[] options, Func<string> get, Action<string> set)
    {
        ImGui.TextUnformatted(label);
        ImGui.SameLine(140);
        if (options.Length == 0)
        {
            ImGui.TextDisabled("none found");
            return;
        }
        var cur = Array.IndexOf(options, get());
        ImGui.SetNextItemWidth(200);
        if (ImGui.Combo($"##tab_{label}", ref cur, options, options.Length) && cur >= 0)
        {
            set(options[cur]);
            SaveRun();
        }
    }

    private void DrawQueueEditor()
    {
        if (_run.Queue.Count == 0)
            ImGui.TextDisabled("Queue is empty.");
        else
            DrawQueueList();

        if (ImGui.Button("Add craft")) { _queueAddOpen = true; _queueAddFocus = true; _queueAddSearch = ""; }

        DrawQueueAddWindow();
    }

    /// <summary>The ordered queue: each row drags vertically to reorder, with an "x" to remove it.</summary>
    private void DrawQueueList()
    {
        int moveFrom = -1, moveTo = -1;
        string remove = null;

        for (var i = 0; i < _run.Queue.Count; i++)
        {
            ImGui.PushID(i);
            // A leading bullet matches the Bases list dots; the label fills the row up to the trailing X, so a
            // long craft name uses all the available width (and a tooltip shows the full name if it still clips).
            ImGui.Bullet();
            ImGui.SameLine();
            var style = ImGui.GetStyle();
            var xW = ImGui.CalcTextSize("X").X + style.FramePadding.X * 2;
            var labelW = Math.Max(60f, ImGui.GetContentRegionAvail().X - xW - style.ItemSpacing.X);
            ImGui.Selectable(_run.Queue[i], false, ImGuiSelectableFlags.None, new Vector2(labelW, 0));
            if (ImGui.IsItemHovered()) UiText.Tooltip(_run.Queue[i]);

            // Drag a row off itself to swap with the neighbour in the drag direction (ImGui reorder idiom).
            if (ImGui.IsItemActive() && !ImGui.IsItemHovered())
            {
                var dir = ImGui.GetMouseDragDelta(ImGuiMouseButton.Left).Y < 0f ? -1 : 1;
                var j = i + dir;
                if (j >= 0 && j < _run.Queue.Count) { moveFrom = i; moveTo = j; ImGui.ResetMouseDragDelta(); }
            }

            ImGui.SameLine();
            if (ImGui.Button($"X##rmq_{i}")) remove = _run.Queue[i];
            ImGui.PopID();
        }

        var changed = false;
        if (moveFrom >= 0 && moveTo >= 0) { (_run.Queue[moveFrom], _run.Queue[moveTo]) = (_run.Queue[moveTo], _run.Queue[moveFrom]); changed = true; }
        if (remove != null) { _run.Queue.Remove(remove); changed = true; }
        if (changed) SaveRun();
    }

    /// <summary>Search popup to add a craft to the queue; already-queued crafts are hidden (each can be added once).</summary>
    private void DrawQueueAddWindow()
    {
        if (!_queueAddOpen) return;

        ImGui.SetNextWindowSize(new Vector2(320, 380), ImGuiCond.FirstUseEver);
        if (_queueAddFocus) { ImGui.SetNextWindowFocus(); _queueAddFocus = false; }
        var open = true;
        // NoDocking: these tool windows must not be dockable - docking them together triggers an ImGui
        // resize feedback loop that flickers white and freezes (forced a manual imgui.ini delete to recover).
        if (ImGui.Begin("Add craft##queueadd", ref open, ImGuiWindowFlags.NoDocking))
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##qsearch", ref _queueAddSearch, 64);
            var q = _queueAddSearch.Trim();

            if (ImGui.BeginChild("##qlist", new Vector2(0, 0), ImGuiChildFlags.Border))
            {
                var any = false;
                foreach (var name in _craftFiles)
                {
                    if (name.Contains("template", StringComparison.OrdinalIgnoreCase)) continue; // starting points, never run directly
                    if (_run.Queue.Contains(name)) continue; // each craft only once
                    if (q.Length > 0 && !name.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
                    any = true;
                    if (ImGui.Selectable(name)) { _run.Queue.Add(name); SaveRun(); }
                }
                if (!any) ImGui.TextDisabled(_craftFiles.Length == 0 ? "No saved crafts." : "All crafts are already queued.");
            }
            ImGui.EndChild();
        }
        ImGui.End();

        if (!open) _queueAddOpen = false;
    }

    private void DrawRunControls()
    {
        // Starting is hotkey-only (StartStopHotkey toggle); the button only stops.
        var running = _runTask != null;
        if (running)
        {
            if (ImGui.Button("Stop", new Vector2(90, 0))) StopRun();
            ImGui.SameLine();
            ImGui.TextColored(UiColors.Match, $"running... {_exec.CurrentItem}  ({_exec.Done}/{_exec.Processed})");
        }

        if (ImGui.BeginChild("##exec_log", new Vector2(0, 180), ImGuiChildFlags.Border))
        {
            foreach (var line in _exec.LogLines)
                ImGui.TextUnformatted(line);
            if (running) ImGui.SetScrollHereY(1f);
        }
        ImGui.EndChild();
    }

    // ---------------- Crafts page (library + editor) ----------------

    private void DrawCraftsPage()
    {
        DrawToolbar();

        if (!BaseTaxonomy.IsReady)
        {
            ImGui.Spacing();
            ImGui.TextWrapped("Loading item/mod data from PoeModDataLib... if it stays here, enable the PoE Mod Data Lib plugin.");
            return;
        }

        // The representative base + domain drive mod picking on the Mod Sets tab and in the condition editor.
        var domain = CraftDomain.For(_plan.ItemClass, BaseTaxonomy.CategoryOfClass(_plan.ItemClass));
        var repBase = BaseTaxonomy.RepresentativeBase(_plan.ItemClass, _plan.Subtype, _plan.BasePaths);

        if (ImGui.BeginTabBar("##mbc_craft", ImGuiTabBarFlags.None))
        {
            if (ImGui.BeginTabItem("Item Selection"))
            {
                ImGui.Spacing();
                ItemSelectionEditor.Draw(_plan);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Mod Sets"))
            {
                ImGui.Spacing();
                ModSetEditor.Draw(_plan, repBase, domain);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Crafting Steps"))
            {
                ImGui.Spacing();
                GraphEditor.Draw(_plan, _currencies, _harvest);
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }

        // Shared condition editor window (item filter + Check nodes) - rendered here so it shows on any sub-tab.
        ConditionEditor.DrawWindow(_plan, repBase, domain);
    }

    private void DrawToolbar()
    {
        var hasFiles = _craftFiles.Length > 0;
        const float labelW = 64f;

        // The craft currently in the editor: its name, save it, start fresh, or branch a copy.
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(UiColors.Accent, "Craft");
        ImGui.SameLine(labelW);
        ImGui.SetNextItemWidth(RowFieldWidth("Save", "New", "Duplicate"));
        var name = _plan.Name;
        if (ImGui.InputTextWithHint("##craftname", "Name this craft to save it...", ref name, 64)) _plan.Name = name;
        var canSave = !string.IsNullOrWhiteSpace(name);
        ImGui.SameLine();
        ImGui.BeginDisabled(!canSave);
        if (ImGui.Button("Save"))
        {
            if (_storage.Save(_plan, out var err)) { _loadedName = _plan.Name; _status = $"Saved '{_plan.Name}'"; RefreshCraftFiles(); }
            else _status = "Save failed: " + err;
        }
        ImGui.EndDisabled();
        Tip("Save this craft under the name above.");
        ImGui.SameLine();
        if (ImGui.Button("New")) { _plan = new CraftPlan(); _loadedName = ""; _status = "New craft"; }
        Tip("Start a blank new craft.");
        ImGui.SameLine();
        if (ImGui.Button("Duplicate"))
        {
            var clone = _storage.Import(_storage.Export(_plan), out _);
            if (clone != null) { clone.Name = _plan.Name + " copy"; _plan = clone; _loadedName = ""; _status = "Duplicated - save it to keep it"; }
        }
        Tip("Copy this craft under a new name (the original is untouched).");

        // Open / manage a saved craft.
        var items = hasFiles ? _craftFiles : NoCrafts;
        var idx = Math.Clamp(_loadIdx, -1, items.Length - 1);   // -1 shows an empty combo (no craft selected)
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(UiColors.Accent, "Open");
        ImGui.SameLine(labelW);
        ImGui.BeginDisabled(!hasFiles);
        ImGui.SetNextItemWidth(RowFieldWidth("Load", "Rename", "Delete"));
        if (ImGui.Combo("##savedlist", ref idx, items, items.Length)) _loadIdx = idx;
        ImGui.SameLine();
        ImGui.BeginDisabled(idx < 0);
        if (ImGui.Button("Load"))
        {
            var loaded = _storage.Load(_craftFiles[idx]);
            if (loaded != null) { _plan = loaded; _loadedName = _craftFiles[idx]; _status = $"Loaded '{loaded.Name}'"; }
        }
        ImGui.EndDisabled();
        Tip("Load the selected craft into the editor.");
        ImGui.SameLine();
        ImGui.BeginDisabled(!canSave || string.IsNullOrEmpty(_loadedName) || _loadedName == _plan.Name);
        if (ImGui.Button("Rename"))
        {
            _storage.Delete(_loadedName);
            if (_storage.Save(_plan, out var err)) { _status = $"Renamed '{_loadedName}' to '{_plan.Name}'"; _loadedName = _plan.Name; RefreshCraftFiles(); }
            else _status = "Rename failed: " + err;
        }
        ImGui.EndDisabled();
        Tip("Rename the loaded craft to the current name above.");
        ImGui.SameLine();
        ImGui.BeginDisabled(idx < 0);
        if (ImGui.Button("Delete")) { _storage.Delete(_craftFiles[idx]); _status = $"Deleted '{_craftFiles[idx]}'"; RefreshCraftFiles(); }
        ImGui.EndDisabled();
        Tip("Delete the selected craft's file.");
        ImGui.EndDisabled();

        // Share via the clipboard. Talks to the OS clipboard directly (ClipboardUtil) - ImGui's clipboard
        // depends on backend wiring and silently broke share strings.
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(UiColors.Accent, "Share");
        ImGui.SameLine(labelW);
        if (ImGui.Button("Import from clipboard"))
        {
            var imported = _storage.Import(ClipboardUtil.GetText(), out var importError);
            if (imported == null)
            {
                _status = "Import failed: " + importError;
            }
            else
            {
                _plan = imported;
                _loadedName = "";
                _status = string.IsNullOrWhiteSpace(imported.Name)
                    ? "Imported an unnamed craft - name it and Save to keep it"
                    : $"Imported '{imported.Name}' - Save to keep it";
            }
        }
        Tip("Create a craft from a share string on the clipboard.");
        ImGui.SameLine();
        // Exporting requires a name for the same reason Save does - it exports the craft IN THE EDITOR, and
        // an unnamed (usually blank) editor exports an empty craft that then "imports as nothing" elsewhere.
        ImGui.BeginDisabled(!canSave);
        if (ImGui.Button("Export to clipboard"))
        {
            _status = ClipboardUtil.SetText(_storage.Export(_plan))
                ? "Copied share string to clipboard"
                : "Clipboard is in use by another app - try exporting again";
        }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))   // plain Tip() never shows on a disabled button
            ImGui.SetTooltip(canSave
                ? "Copy this craft as a share string to the clipboard."
                : "Name the craft first - Export shares the craft currently in the editor.");

        if (!string.IsNullOrEmpty(_status))
        {
            ImGui.Spacing();
            UiText.Colored(UiColors.Match, _status);   // may carry craft names / import-error snippets ('%'!)
        }
        ImGui.Separator();
    }

    private static void Tip(string text)
    {
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(text);
    }

    // Width for the field on a "label + field + trailing buttons" row so the field stretches to fill the row
    // (long craft names fit) while leaving room for the named buttons. Call right after SameLine(labelW).
    private static float RowFieldWidth(params string[] buttonLabels)
    {
        var style = ImGui.GetStyle();
        var buttons = 0f;
        foreach (var b in buttonLabels)
            buttons += ImGui.CalcTextSize(b).X + style.FramePadding.X * 2 + style.ItemSpacing.X;
        return Math.Max(160f, ImGui.GetContentRegionAvail().X - buttons);
    }

    // ---------------- Stats ----------------

    /// <summary>Stats tab: an Overall summary (totals + currency consumed across every craft) and a per-craft
    /// table - items finished, currency used (expand for the per-currency breakdown), and chaos cost. Cost is
    /// the at-craft-time basis by default; the bottom "Use current prices" toggle switches to today's prices
    /// (Ninja Price bridge). Pricing / reset controls sit at the bottom.</summary>
    private void DrawStatsPage()
    {
        if (_stats == null) { ImGui.TextDisabled("Stats are not ready yet."); return; }

        var all = _stats.All;
        if (all.Count == 0)
        {
            ImGui.TextDisabled("No crafts have run yet - stats accumulate as the queue runs.");
            return;
        }

        var useCurrent = _statsUseCurrentPrices;

        // Aggregate across every craft: counts + cost basis per currency, plus grand totals.
        long totFinished = 0, totUsed = 0;
        var overall = new Dictionary<string, CurrencyTally>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in all)
        {
            totFinished += s.Finished;
            foreach (var kv in s.Currency)
            {
                totUsed += kv.Value.Count;
                if (!overall.TryGetValue(kv.Key, out var ot)) overall[kv.Key] = ot = new CurrencyTally();
                ot.Count += kv.Value.Count;
                ot.ChaosSpent += kv.Value.ChaosSpent;
            }
        }
        double totCost = 0; var anyCost = false; var anyPartial = false;
        foreach (var kv in overall)
        {
            var v = CurrencyValue(kv.Key, kv.Value, useCurrent);
            if (v.HasValue) { totCost += v.Value; anyCost = true; }
            else if (kv.Value.Count > 0) anyPartial = true;
        }

        ImGui.SeparatorText("Overall");
        ImGui.TextColored(UiColors.Accent,
            $"Crafts {all.Count}     Finished {totFinished}     Currency used {totUsed}"
            + (anyCost ? $"     {(useCurrent ? "Worth" : "Spent")} ~{Chaos(totCost)}{(anyPartial ? "+" : "")}c" : ""));
        DrawCurrencyTable("##overallcur", overall, useCurrent);

        ImGui.SeparatorText("Per craft");
        string toReset = null;
        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                                      ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingFixedFit;
        if (ImGui.BeginTable("##craftstats", 7, flags))
        {
            ImGui.TableSetupColumn("Craft", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Finished", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("Used", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("Used / item", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn(useCurrent ? "Worth (c)" : "Cost (c)", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("c / item", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("##rm", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableHeadersRow();

            foreach (var s in all)
            {
                ImGui.PushID(s.Name);
                ImGui.TableNextRow();

                long used = 0;
                foreach (var t in s.Currency.Values) used += t.Count;
                var cost = CraftCost(s, useCurrent, out var partial);
                var hasCur = s.Currency.Count > 0;

                // Craft name as a tree node - expand to list this craft's currency under the matching columns.
                ImGui.TableSetColumnIndex(0);
                var nodeFlags = ImGuiTreeNodeFlags.SpanAvailWidth;
                if (!hasCur) nodeFlags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;
                var open = ImGui.TreeNodeEx(s.Name, nodeFlags);

                ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(s.Finished.ToString());
                ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(used.ToString());
                ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(s.Finished > 0 ? (used / (double)s.Finished).ToString("0.#") : "-");
                ImGui.TableSetColumnIndex(4); ImGui.TextUnformatted(cost.HasValue ? Chaos(cost.Value) + (partial ? "+" : "") : "-");
                ImGui.TableSetColumnIndex(5); ImGui.TextUnformatted(cost.HasValue && s.Finished > 0 ? Chaos(cost.Value / s.Finished) : "-");
                ImGui.TableSetColumnIndex(6); if (ImGui.SmallButton("Reset")) toReset = s.Name;

                if (open && hasCur)
                {
                    foreach (var kv in s.Currency.OrderByDescending(c => CurrencyValue(c.Key, c.Value, useCurrent) ?? -1d).ThenByDescending(c => c.Value.Count))
                    {
                        ImGui.TableNextRow();
                        ImGui.TableSetColumnIndex(0); ImGui.Indent(); ImGui.TextDisabled(kv.Key); ImGui.Unindent();
                        ImGui.TableSetColumnIndex(2); ImGui.TextDisabled(kv.Value.Count.ToString());
                        ImGui.TableSetColumnIndex(3); ImGui.TextDisabled(s.Finished > 0 ? (kv.Value.Count / (double)s.Finished).ToString("0.#") : "-");
                        var v = CurrencyValue(kv.Key, kv.Value, useCurrent);
                        ImGui.TableSetColumnIndex(4); ImGui.TextDisabled(v.HasValue ? Chaos(v.Value) : "-");
                    }
                    ImGui.TreePop();
                }
                ImGui.PopID();
            }
            ImGui.EndTable();
        }

        if (toReset != null) _stats.Reset(toReset);

        // Bottom controls: price-mode toggle, refresh (current mode only), reset-all.
        ImGui.Separator();
        ImGui.Checkbox("Use current prices", ref _statsUseCurrentPrices);
        Tip("Off: what each craft cost at the time it ran (frozen).  On: value at today's prices.");
        if (_statsUseCurrentPrices)
        {
            ImGui.SameLine();
            if (_pricer != null && _pricer.Available)
            {
                if (ImGui.Button("Refresh prices")) _pricer.Refresh();
                Tip("Re-query Ninja Price (prices change over time).");
            }
            else
            {
                ImGui.TextColored(UiColors.Warn, "Ninja Price plugin not detected - current prices unavailable.");
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Reset all")) ImGui.OpenPopup("##statsresetall");
        if (ImGui.BeginPopup("##statsresetall"))
        {
            ImGui.Text("Clear stats for ALL crafts?");
            if (ImGui.Button("Yes, clear everything")) { _stats.ResetAll(); ImGui.CloseCurrentPopup(); }
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }

    // A compact "Currency | Used | Cost/Worth(c)" table, most-valuable first - the Overall currency-consumed view.
    private void DrawCurrencyTable(string id, IReadOnlyDictionary<string, CurrencyTally> currency, bool useCurrent)
    {
        if (currency.Count == 0) { ImGui.TextDisabled("(no currency used yet)"); return; }
        const ImGuiTableFlags f = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingFixedFit;
        if (ImGui.BeginTable(id, 3, f))
        {
            ImGui.TableSetupColumn("Currency", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Used", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn(useCurrent ? "Worth (c)" : "Cost (c)", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableHeadersRow();
            foreach (var kv in currency.OrderByDescending(c => CurrencyValue(c.Key, c.Value, useCurrent) ?? -1d).ThenByDescending(c => c.Value.Count))
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(kv.Key);
                ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(kv.Value.Count.ToString());
                var v = CurrencyValue(kv.Key, kv.Value, useCurrent);
                ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(v.HasValue ? Chaos(v.Value) : "-");
            }
            ImGui.EndTable();
        }
    }

    // Chaos value of a currency tally in the active mode: current = Count x today's per-unit price; historical
    // = the recorded cost basis (ChaosSpent). Null when unknown (unpriced now, or no basis recorded).
    private double? CurrencyValue(string name, CurrencyTally t, bool useCurrent)
    {
        if (useCurrent)
        {
            var per = _pricer?.PerUnit(name);
            return per.HasValue ? t.Count * per.Value : null;
        }
        return t.ChaosSpent > 0 ? t.ChaosSpent : null;
    }

    // Total chaos cost/value of a craft's currency in the active mode; partial = some currency had no value
    // (unpriced now, or no recorded basis), so the shown number is a lower bound.
    private double? CraftCost(CraftStat s, bool useCurrent, out bool partial)
    {
        partial = false;
        double sum = 0; var any = false;
        foreach (var kv in s.Currency)
        {
            var v = CurrencyValue(kv.Key, kv.Value, useCurrent);
            if (v.HasValue) { sum += v.Value; any = true; }
            else if (kv.Value.Count > 0) partial = true;
        }
        return any ? sum : null;
    }

    private static string Chaos(double v) =>
        v >= 1000 ? (v / 1000d).ToString("0.0") + "k" : v >= 10 ? v.ToString("0") : v.ToString("0.0");

    // ---------------- Run / queue ----------------

    private void StartQueue()
    {
        if (_runTask != null) return;

        _exec.ClearLog();
        if (_run.Queue.Count == 0)
        {
            _exec.Log("Queue is empty - add crafts above.");
            return;
        }
        if (GameController?.IngameState?.IngameUi?.InventoryPanel?.IsVisible != true)
        {
            _exec.Log("Open your inventory (default I) before starting.");
            return;
        }
        if (_run.Mode == RunMode.Stash)
        {
            if (GameController?.IngameState?.IngameUi?.StashElement?.IsVisible != true)
            {
                _exec.Log("Open your stash before starting a stash run.");
                return;
            }
            if (string.IsNullOrEmpty(_run.InputTab) || string.IsNullOrEmpty(_run.CurrencyTab) || string.IsNullOrEmpty(_run.OutputTab))
            {
                _exec.Log("Set the In / $ / Out tabs on the Main page before a stash run.");
                return;
            }
        }
        if (_run.Mode == RunMode.Harvest)
        {
            if (GameController?.IngameState?.IngameUi?.HorticraftingStationWindow?.IsVisible != true)
            {
                _exec.Log("Open the Horticrafting bench before starting a harvest run.");
                return;
            }
        }

        _exec.AltAugShortcut = Settings.AltAugShortcut.Value;
        _exec.ChanceScourShortcut = Settings.ChanceScourShortcut.Value;
        _exec.AlchScourShortcut = Settings.AlchScourShortcut.Value;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _runTask = RunQueue(_cts.Token);
    }

    private async SyncTask<bool> RunQueue(CancellationToken ct)
    {
        foreach (var craftName in _run.Queue.ToList())
        {
            ct.ThrowIfCancellationRequested();
            var plan = _storage.Load(craftName);
            if (plan == null) { _exec.Log($"Craft '{craftName}' not found - skipping."); continue; }

            // Harvest-craft graphs need the bench, so they can only run in Harvest mode.
            if (_run.Mode != RunMode.Harvest && plan.Graph is { } g && g.HasHarvestNode)
            {
                _exec.Log($"{plan.Name}: needs Harvest mode - skipping.");
                continue;
            }

            _exec.Log($"=== {plan.Name} ===");
            if (_run.Mode == RunMode.Stash)
                await _exec.RunStash(plan, _run.InputTab, _run.CurrencyTab, _run.OutputTab, ct);
            else if (_run.Mode == RunMode.Harvest)
                await _exec.RunHarvest(plan, ct);
            else
                await _exec.Run(plan, ct);
            _stats?.Record(plan.Name, _exec.Done, _exec.Spent, ChaosForSpend(_exec.Spent));   // fold run into lifetime stats
        }
        _exec.Log("Queue complete.");
        return true;
    }

    // count x per-unit chaos for each priced currency in this run's spend - the cost basis captured now. Uses a
    // FRESH bridge query (PerUnitFresh) so the stored basis is the price at this craft's finish, not a stale one.
    private Dictionary<string, double> ChaosForSpend(IReadOnlyDictionary<string, int> spent)
    {
        var d = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (_pricer != null)
            foreach (var kv in spent)
            {
                var per = _pricer.PerUnitFresh(kv.Key);
                if (per.HasValue) d[kv.Key] = kv.Value * per.Value;
            }
        return d;
    }

    private void StopRun()
    {
        // Only cancel - do NOT null _runTask here. The run is a coroutine pumped by RunOrRestart in Tick;
        // it must keep being pumped after cancellation so it can unwind through its finally blocks (release
        // Shift/Ctrl/Alt, drop the cursor, free the input lock). RunOrRestart nulls _runTask once it finishes.
        if (_cts != null && !_cts.IsCancellationRequested) _cts.Cancel();
    }

    // ---------------- helpers ----------------

    private void RefreshCraftFiles()
    {
        _craftFiles = _storage.List().ToArray();
        if (_loadIdx >= _craftFiles.Length) _loadIdx = -1;
    }

    private void RefreshStashTabs()
    {
        try
        {
            var tabs = GameController?.IngameState?.Data?.ServerData?.PlayerStashTabs;
            if (tabs is not { Count: > 0 }) return;

            // PlayerStashTabs is in server order, not the order shown in-game - sort by VisibleIndex
            // (verified live: list position 0 was the 8th visible tab). It also lists entries that
            // are no real tabs at all: the map stash's internal sub-stashes (one per section, ALL
            // named "1", type Map), folders and remove-only leftovers. Offer only what a role can
            // use - Normal/Premium/Quad for input/output, Currency tabs for the currency role.
            var normal = new List<(string Name, int Visible)>(tabs.Count);
            var currency = new List<(string Name, int Visible)>();
            foreach (var t in tabs)
            {
                var n = t?.Name;
                if (string.IsNullOrEmpty(n) || t.Flags.HasFlag(InventoryTabFlags.RemoveOnly)) continue;
                if (t.TabType is InventoryTabType.Normal or InventoryTabType.Premium or InventoryTabType.Quad)
                    normal.Add((n, t.VisibleIndex));
                else if (t.TabType == InventoryTabType.Currency)
                    currency.Add((n, t.VisibleIndex));
            }
            if (normal.Count > 0)
                _stashTabs = normal.OrderBy(x => x.Visible).Select(x => x.Name).ToArray();
            if (currency.Count > 0)
                _currencyTabs = currency.OrderBy(x => x.Visible).Select(x => x.Name).ToArray();
        }
        catch { /* stash not open */ }
    }

    private void SaveRun() => _storage?.SaveRunConfig(_run);
}
