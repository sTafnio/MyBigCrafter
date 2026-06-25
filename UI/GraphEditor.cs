using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using Newtonsoft.Json;
using MyBigCrafter.Data;
using MyBigCrafter.Model;

namespace MyBigCrafter.UI;

/// <summary>
/// Custom ImGui node editor for the crafting flow chart. Drag nodes; click an output pin then a node's
/// input pin to link; middle-drag to pan; double-click a node (or right-click -> Edit) to edit it
/// (Apply -> currency window, Check -> condition window); right-click a node for an Edit / Copy / Delete
/// menu. Palette + navigation legend sit below the canvas.
/// </summary>
public static class GraphEditor
{
    private const float MinW = 170f, MaxW = 560f, PinR = 5f, PinHit = 15f;
    private const float PadX = 8f, PadTop = 6f, TitleH = 22f, LineH = 17f, PadBot = 8f;

    private static Vector2 _scroll = Vector2.Zero;
    private static int _selected = -1;
    private static int _linkFrom = -1;  // node id of the armed output pin
    private static int _linkPin;        // 0 = Next, 1 = OnTrue, 2 = OnFalse
    private static int _applyEdit = -1; // Apply node whose currency window is open (-1 = none)
    private static int _harvestEdit = -1; // Harvest node whose craft-picker window is open (-1 = none)
    private static Vector2 _addAtPos;   // graph-space position for the empty-canvas "add node" menu
    private static string _curFilter = "";
    private static string _harvestFilter = "";

    public static void Draw(CraftPlan plan, CurrencyCatalog currencies, HarvestCraftCatalog harvest)
    {
        var graph = plan.Graph;
        graph.EnsureDefaults();

        DrawCanvas(graph);

        // Palette + view controls below the canvas.
        if (ImGui.Button("+ Apply")) AddNode(graph, NodeType.Apply);
        ImGui.SameLine(); if (ImGui.Button("+ Check")) AddNode(graph, NodeType.Check);
        ImGui.SameLine(); if (ImGui.Button("+ Harvest")) AddNode(graph, NodeType.Harvest);
        ImGui.SameLine(); if (ImGui.Button("+ Finish")) AddNode(graph, NodeType.Finish);
        ImGui.SameLine(); if (ImGui.Button("Reset view")) _scroll = Vector2.Zero;

        ImGui.PushStyleColor(ImGuiCol.Text, UiColors.Muted);
        ImGui.TextUnformatted("Drag a node to move it.");
        ImGui.TextUnformatted("Double-click a node to edit it.");
        ImGui.TextUnformatted("Right-click a node for edit / copy / delete, or right-click empty space to add a node.");
        ImGui.TextUnformatted("Click an output pin then an input pin to link them.");
        ImGui.TextUnformatted("Click an output pin and drop on empty space to disconnect it.");
        ImGui.TextUnformatted("Middle-drag to pan.");
        ImGui.PopStyleColor();

        DrawApplyWindow(graph, currencies);
        DrawHarvestWindow(graph, harvest);
        // The Check condition editor window is rendered by the crafts page (so it shows on any sub-tab).
    }

    /// <summary>Double-click editor for an Apply node: pick one or more currencies (used in priority order) and
    /// toggle the Shift-hold option.</summary>
    private static void DrawApplyWindow(CraftGraph graph, CurrencyCatalog currencies)
    {
        if (_applyEdit < 0) return;
        var node = graph.Get(_applyEdit);
        if (node == null || node.Type != NodeType.Apply) { _applyEdit = -1; return; }

        ImGui.SetNextWindowSize(new Vector2(320, 0), ImGuiCond.FirstUseEver);
        var open = true;
        if (ImGui.Begin("Apply step##applyedit", ref open))
        {
            // Ordered list: each row drags vertically to reorder, with an "x" to remove it (same idiom as the
            // craft queue on the Main page).
            var methods = node.Methods;
            int moveFrom = -1, moveTo = -1, removeAt = -1;
            for (var i = 0; i < methods.Count; i++)
            {
                ImGui.PushID(i);
                if (ImGui.Button("x")) removeAt = i;
                ImGui.SameLine();
                ImGui.Selectable(methods[i]);

                if (ImGui.IsItemActive() && !ImGui.IsItemHovered())
                {
                    var dir = ImGui.GetMouseDragDelta(ImGuiMouseButton.Left).Y < 0f ? -1 : 1;
                    var j = i + dir;
                    if (j >= 0 && j < methods.Count) { moveFrom = i; moveTo = j; ImGui.ResetMouseDragDelta(); }
                }
                ImGui.PopID();
            }
            if (moveFrom >= 0 && moveTo >= 0) (methods[moveFrom], methods[moveTo]) = (methods[moveTo], methods[moveFrom]);
            if (removeAt >= 0) methods.RemoveAt(removeAt);
            if (methods.Count == 0) ImGui.TextColored(UiColors.Warn, "No currency selected yet.");

            var picked = CurrencyAddCombo("##applyadd", currencies.Names);
            if (picked != null && !methods.Any(m => string.Equals(m, picked, StringComparison.OrdinalIgnoreCase)))
                methods.Add(picked);

            ImGui.Spacing();
            var shift = node.UseShift;
            if (ImGui.Checkbox("Shift (keep selected across uses)", ref shift)) node.UseShift = shift;
        }
        ImGui.End();

        if (!open) _applyEdit = -1;
    }

    /// <summary>Double-click editor for a Harvest node: pick one craft from the full Horticrafting list (searchable).</summary>
    private static void DrawHarvestWindow(CraftGraph graph, HarvestCraftCatalog harvest)
    {
        if (_harvestEdit < 0) return;
        var node = graph.Get(_harvestEdit);
        if (node == null || node.Type != NodeType.Harvest) { _harvestEdit = -1; return; }

        ImGui.SetNextWindowSize(new Vector2(470, 430), ImGuiCond.FirstUseEver);
        var open = true;
        if (ImGui.Begin("Harvest craft##harvestedit", ref open, ImGuiWindowFlags.NoDocking))
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Craft:");
            ImGui.SameLine();
            var has = !string.IsNullOrEmpty(node.HarvestCraft);
            ImGui.TextColored(has ? UiColors.Match : UiColors.Warn, has ? node.HarvestCraft : "(none selected)");

            if (!harvest.IsBuilt) ImGui.TextColored(UiColors.Warn, "Couldn't load the harvest craft list.");

            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##hsearch", "Search crafts...", ref _harvestFilter, 128);

            if (ImGui.BeginChild("##hlist", new Vector2(0, 0), ImGuiChildFlags.Border))
            {
                var q = _harvestFilter.Trim();
                foreach (var c in harvest.All)
                {
                    if (q.Length > 0 && c.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var selected = string.Equals(c.Name, node.HarvestCraft, StringComparison.Ordinal);
                    if (ImGui.Selectable(c.Name, selected)) node.HarvestCraft = c.Name;
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(c.Name);
                }
            }
            ImGui.EndChild();
        }
        ImGui.End();

        if (!open) _harvestEdit = -1;
    }

    private static void OpenCheck(CraftNode n) => ConditionEditor.Open($"chk{n.Id}", n.Check, "Edit Check");

    /// <summary>Opens the node's editor: Apply -> currency window, Check -> condition window. No-op for Start/Finish.</summary>
    private static void EditNode(CraftNode n)
    {
        if (n.Type == NodeType.Check) OpenCheck(n);
        else if (n.Type == NodeType.Apply) _applyEdit = n.Id;
        else if (n.Type == NodeType.Harvest) { _harvestEdit = n.Id; _harvestFilter = ""; }
    }

    /// <summary>Duplicates a node next to itself (deep-cloning its Check tree so they don't alias), unlinked.</summary>
    private static void CopyNode(CraftGraph graph, CraftNode n)
    {
        var copy = graph.Add(n.Type, n.X + 24, n.Y + 24);
        copy.Methods = new List<string>(n.Methods);
        copy.UseShift = n.UseShift;
        copy.HarvestCraft = n.HarvestCraft;
        copy.Check = JsonConvert.DeserializeObject<Condition>(JsonConvert.SerializeObject(n.Check)) ?? new Condition();
        _selected = copy.Id;
    }

    private static void DeleteNode(CraftGraph graph, CraftNode n)
    {
        ConditionEditor.CloseIfEditing(n.Check);
        graph.Remove(n.Id);
        if (_selected == n.Id) _selected = -1;
        if (_applyEdit == n.Id) _applyEdit = -1;
        if (_harvestEdit == n.Id) _harvestEdit = -1;
    }

    private static void DrawCanvas(CraftGraph graph)
    {
        if (!ImGui.BeginChild("##canvas", new Vector2(0, 360), ImGuiChildFlags.Border, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.EndChild();
            return;
        }

        var dl = ImGui.GetWindowDrawList();
        var canvasP0 = ImGui.GetCursorScreenPos();
        var canvasSz = ImGui.GetContentRegionAvail();
        var origin = canvasP0 + _scroll;
        _lastOrigin = origin;

        // Background: pan + click-to-deselect/cancel. AllowOverlap so nodes/pins on top still get input.
        ImGui.SetNextItemAllowOverlap();
        ImGui.InvisibleButton("##bg", canvasSz,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Middle))
            _scroll += ImGui.GetIO().MouseDelta;
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left)) { _linkFrom = -1; _selected = -1; }

        // Right-click empty canvas: add a node where the cursor is. IsItemClicked respects overlap, so this
        // only fires on empty space (a right-click over a node goes to that node's own menu instead).
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            _addAtPos = ImGui.GetMousePos() - origin;
            ImGui.OpenPopup("##bgadd");
        }
        if (ImGui.BeginPopup("##bgadd"))
        {
            if (ImGui.Selectable("Add Apply")) AddNodeAt(graph, NodeType.Apply);
            if (ImGui.Selectable("Add Check")) AddNodeAt(graph, NodeType.Check);
            if (ImGui.Selectable("Add Harvest")) AddNodeAt(graph, NodeType.Harvest);
            if (ImGui.Selectable("Add Finish")) AddNodeAt(graph, NodeType.Finish);
            ImGui.EndPopup();
        }

        // Links (under nodes)
        foreach (var n in graph.Nodes)
        {
            if (n.Type is NodeType.Start or NodeType.Apply or NodeType.Harvest)
                DrawLink(dl, graph, OutPin(n, origin, 0), n.Next, UiColors.Muted);
            else if (n.Type == NodeType.Check)
            {
                DrawLink(dl, graph, OutPin(n, origin, 1), n.OnTrue, UiColors.Match);
                DrawLink(dl, graph, OutPin(n, origin, 2), n.OnFalse, new Vector4(1f, 0.45f, 0.45f, 1f));
            }
        }
        if (_linkFrom >= 0)
        {
            var from = graph.Get(_linkFrom);
            if (from != null)
                Bezier(dl, OutPin(from, origin, _linkPin), ImGui.GetMousePos(), ImGui.GetColorU32(UiColors.Accent));
        }

        // Nodes
        foreach (var n in graph.Nodes.ToList())
            DrawNode(dl, graph, n, origin);

        ImGui.EndChild();
    }

    private static void DrawNode(ImDrawListPtr dl, CraftGraph graph, CraftNode n, Vector2 origin)
    {
        var p = origin + new Vector2(n.X, n.Y);
        var lines = NodeLines(n);
        var size = NodeSize(n, lines);

        // AllowOverlap so the pin buttons (submitted after, overlapping the edges) still get input.
        ImGui.SetNextItemAllowOverlap();
        ImGui.SetCursorScreenPos(p);
        ImGui.InvisibleButton($"node{n.Id}", size,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            n.X += ImGui.GetIO().MouseDelta.X;
            n.Y += ImGui.GetIO().MouseDelta.Y;
        }
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left)) _selected = n.Id;
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) _selected = n.Id; // right-click selects too, then opens the menu
        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) EditNode(n);

        // Right-click context menu: Edit / Copy / Delete (Start has none of these).
        if (n.Type != NodeType.Start && ImGui.BeginPopupContextItem($"ctx{n.Id}"))
        {
            if ((n.Type is NodeType.Apply or NodeType.Check or NodeType.Harvest) && ImGui.Selectable("Edit")) EditNode(n);
            if (ImGui.Selectable("Copy")) CopyNode(graph, n);
            if (ImGui.Selectable("Delete"))
            {
                DeleteNode(graph, n);
                ImGui.EndPopup();
                return;
            }
            ImGui.EndPopup();
        }

        var bg = NodeColor(n.Type);
        var selected = _selected == n.Id;
        dl.AddRectFilled(p, p + size, ImGui.GetColorU32(bg), 5f);
        dl.AddRect(p, p + size, ImGui.GetColorU32(selected ? UiColors.Accent : new Vector4(0, 0, 0, 0.6f)), 5f, ImDrawFlags.None, selected ? 2.5f : 1.2f);

        dl.AddText(p + new Vector2(PadX, PadTop), ImGui.GetColorU32(new Vector4(1, 1, 1, 1)), NodeTitle(n.Type));
        var lineCol = ImGui.GetColorU32(new Vector4(0.85f, 0.85f, 0.85f, 1));
        var innerW = size.X - PadX * 2;
        for (var i = 0; i < lines.Count; i++)
            dl.AddText(p + new Vector2(PadX, PadTop + TitleH + i * LineH), lineCol, TrimToWidth(lines[i], innerW));

        // Input pin
        if (n.Type != NodeType.Start)
        {
            var inC = p + new Vector2(0, size.Y / 2);
            if (PinButton($"in_{n.Id}", inC) && _linkFrom >= 0 && _linkFrom != n.Id)
            {
                Connect(graph, _linkFrom, _linkPin, n.Id);
                _linkFrom = -1;
            }
            dl.AddCircleFilled(inC, PinR, ImGui.GetColorU32(new Vector4(0.8f, 0.8f, 0.8f, 1)));
        }

        // Output pins. Clicking an output grabs its wire (clearing the existing link): drop it on an input
        // to re-link, or on empty space to leave it disconnected. A filled centre dot marks a connected pin.
        if (n.Type is NodeType.Start or NodeType.Apply or NodeType.Harvest)
        {
            var c = OutPin(n, origin, 0);
            if (PinButton($"out_{n.Id}", c)) { _linkFrom = n.Id; _linkPin = 0; n.Next = -1; }
            DrawOutPin(dl, c, UiColors.Muted, n.Next >= 0);
        }
        else if (n.Type == NodeType.Check)
        {
            var ct = OutPin(n, origin, 1);
            if (PinButton($"outT_{n.Id}", ct)) { _linkFrom = n.Id; _linkPin = 1; n.OnTrue = -1; }
            DrawOutPin(dl, ct, UiColors.Match, n.OnTrue >= 0);

            var cf = OutPin(n, origin, 2);
            if (PinButton($"outF_{n.Id}", cf)) { _linkFrom = n.Id; _linkPin = 2; n.OnFalse = -1; }
            DrawOutPin(dl, cf, new Vector4(1f, 0.45f, 0.45f, 1f), n.OnFalse >= 0);
        }
    }

    /// <summary>An output pin: a ring in its branch colour, with a filled centre when it has a link.</summary>
    private static void DrawOutPin(ImDrawListPtr dl, Vector2 center, Vector4 color, bool connected)
    {
        var col = ImGui.GetColorU32(color);
        if (connected) dl.AddCircleFilled(center, PinR, col);
        else dl.AddCircle(center, PinR, col, 0, 1.5f);
    }

    // ---- geometry / helpers ----

    private static Vector2 OutPin(CraftNode n, Vector2 origin, int pin)
    {
        var p = origin + new Vector2(n.X, n.Y);
        var size = NodeSize(n, NodeLines(n));
        return pin switch
        {
            1 => p + new Vector2(size.X, size.Y * 0.32f),
            2 => p + new Vector2(size.X, size.Y * 0.68f),
            _ => p + new Vector2(size.X, size.Y / 2),
        };
    }

    private static bool PinButton(string id, Vector2 center)
    {
        ImGui.SetCursorScreenPos(center - new Vector2(PinHit / 2));
        ImGui.InvisibleButton(id, new Vector2(PinHit, PinHit));
        return ImGui.IsItemClicked(ImGuiMouseButton.Left);
    }

    private static Vector2 _lastOrigin;

    private static void DrawLink(ImDrawListPtr dl, CraftGraph graph, Vector2 from, int toId, Vector4 color)
    {
        var to = graph.Get(toId);
        if (to == null) return;
        var target = _lastOrigin + new Vector2(to.X, to.Y) + new Vector2(0, NodeSize(to, NodeLines(to)).Y / 2);
        Bezier(dl, from, target, ImGui.GetColorU32(color));
    }

    private static void Bezier(ImDrawListPtr dl, Vector2 a, Vector2 b, uint col)
    {
        var d = MathF.Min(80f, MathF.Abs(b.X - a.X) * 0.6f + 20f);
        dl.AddBezierCubic(a, a + new Vector2(d, 0), b - new Vector2(d, 0), b, col, 2.5f);
    }

    private static void Connect(CraftGraph graph, int fromId, int pin, int toId)
    {
        var from = graph.Get(fromId);
        if (from == null || fromId == toId) return;
        if (pin == 1) from.OnTrue = toId;
        else if (pin == 2) from.OnFalse = toId;
        else from.Next = toId;
    }

    private static void AddNode(CraftGraph graph, NodeType type)
    {
        var node = graph.Add(type, 60 - _scroll.X + 120, 60 - _scroll.Y + 40 * graph.Nodes.Count % 200);
        _selected = node.Id;
    }

    /// <summary>Adds a node at the empty-canvas right-click position (graph-space, captured on open).</summary>
    private static void AddNodeAt(CraftGraph graph, NodeType type)
    {
        var node = graph.Add(type, _addAtPos.X, _addAtPos.Y);
        _selected = node.Id;
    }

    private static Vector4 NodeColor(NodeType t) => t switch
    {
        NodeType.Start => new Vector4(0.30f, 0.55f, 0.32f, 0.95f),
        NodeType.Apply => new Vector4(0.25f, 0.40f, 0.65f, 0.95f),
        NodeType.Check => new Vector4(0.60f, 0.45f, 0.20f, 0.95f),
        NodeType.Harvest => new Vector4(0.45f, 0.30f, 0.58f, 0.95f),
        NodeType.Finish => new Vector4(0.20f, 0.52f, 0.55f, 0.95f),
        _ => new Vector4(0.4f, 0.4f, 0.4f, 0.95f),
    };

    private static string NodeTitle(NodeType t) => t.ToString().ToUpperInvariant();

    /// <summary>The body text of a node, one entry per line. Check nodes list one condition per line.</summary>
    private static string ApplyLabel(CraftNode n)
    {
        var ms = n.Methods;
        var shift = n.UseShift ? " (shift)" : "";
        if (ms.Count == 0) return "(no currency)";
        return (ms.Count == 1 ? ms[0] : $"{ms[0]} +{ms.Count - 1} more") + shift;
    }

    private static List<string> NodeLines(CraftNode n) => n.Type switch
    {
        NodeType.Apply => new() { ApplyLabel(n) },
        NodeType.Check => CheckLines(n.Check),
        NodeType.Harvest => new() { string.IsNullOrEmpty(n.HarvestCraft) ? "(no craft)" : n.HarvestCraft },
        NodeType.Finish => new() { "item done" },
        _ => new() { "entry" },
    };

    private static List<string> CheckLines(Condition root)
    {
        if (root == null || root.Children.Count == 0) return new() { "(no conditions)" };

        var lines = new List<string>();
        if (root.Op == GroupOp.Any) lines.Add("any of:");
        else if (root.Op == GroupOp.Count) lines.Add($"{root.CountOp}{root.CountValue} of:");
        foreach (var ch in root.Children) lines.Add(ConditionEditor.Line(ch));
        return lines;
    }

    // Width fits the widest line (measured), clamped to [MinW, MaxW]; height grows by line count.
    private static Vector2 NodeSize(CraftNode n, List<string> lines)
    {
        var w = MathF.Max(MinW, ImGui.CalcTextSize(NodeTitle(n.Type)).X + PadX * 2);
        foreach (var l in lines) w = MathF.Max(w, ImGui.CalcTextSize(l).X + PadX * 2);
        w = MathF.Min(w, MaxW);
        var h = PadTop + TitleH + lines.Count * LineH + PadBot;
        return new Vector2(w, h);
    }

    /// <summary>Trims a line with an ellipsis only if it would overflow the given pixel width (rare, past MaxW).</summary>
    private static string TrimToWidth(string s, float maxPx)
    {
        if (string.IsNullOrEmpty(s) || ImGui.CalcTextSize(s).X <= maxPx) return s;
        var n = s.Length;
        while (n > 0 && ImGui.CalcTextSize(s[..n] + "...").X > maxPx) n--;
        return n <= 0 ? "..." : s[..n] + "...";
    }

    /// <summary>Searchable combo that returns the chosen currency name (or null), for adding to an Apply node's list.</summary>
    private static string CurrencyAddCombo(string id, List<string> names)
    {
        ImGui.SetNextItemWidth(260);
        if (!ImGui.BeginCombo(id, "+ add currency")) return null;

        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##curfilter", ref _curFilter, 64);

        string picked = null;
        var filter = _curFilter;
        var shown = 0;
        foreach (var nm in names)
        {
            if (filter.Length > 0 && !nm.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            if (ImGui.Selectable(nm)) picked = nm;
            if (++shown >= 200) break;
        }
        ImGui.EndCombo();
        return picked;
    }
}
