using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using MyBigCrafter.Model;

namespace MyBigCrafter.UI;

/// <summary>
/// Focused, filter-row builder for a Check node's <see cref="Condition"/> tree. Opened in its own window
/// (double-click a Check, or the panel button). The top level reads as a sentence ("Item must match ALL of:")
/// and each condition is a row; "count" groups (at least N of...) are an indented sub-block. Leaves are split
/// by type - Property (a catalog field), Mod (a game-file affix), Set (count against a named mod set), or
/// Advanced (raw IFL). No IFL syntax required.
/// <see cref="Summary"/> renders a short human-readable description for the node/panel.
/// </summary>
public static class ConditionEditor
{
    private static readonly string[] GroupOps = { "all", "any", "count" };   // index maps to GroupOp
    private static readonly string[] CountOps = { ">", ">=", "<", "<=", "==", "!=" };
    // Implicit/Enchant leaves were retired: eldritch implicits and cluster mods live in the Mod picker.
    private static readonly string[] LeafTypes = { "Property", "Mod", "Set", "Advanced" };
    private static readonly string[] SetScopes = { "mods", "prefixes", "suffixes" };   // index maps to SetScope
    private static readonly string[] SetInOps = { "in", "not in" };
    // Field leaves listed alphabetically by label (the catalog's own order is grouped by kind, not sorted).
    private static readonly ItemField[] Fields = FieldCatalog.All.OrderBy(f => f.Label, StringComparer.OrdinalIgnoreCase).ToArray();
    private static readonly string[] FieldNames = Fields.Select(f => f.Label).ToArray();

    // The mod-name cell width, so a row's trailing controls (tier / min-vals) line up across stacked leaves;
    // the remove "x" is right-aligned to the content edge. Auto-sizes to the widest mod in the tree each frame.
    private static float _modColW = 250f;

    private static string _openKey = "";
    private static Condition _openRoot;
    private static string _openTitle = "";
    private static bool _openAllowCategory;
    private static bool _focusNext;   // bring the window to the front on the next draw (set by Open)

    /// <summary>
    /// Opens the editor window for a condition tree. <paramref name="key"/> is a stable id for the owner
    /// (e.g. "chk3" or "selfilter"). <paramref name="allowCategory"/> shows the Explicit/Fractured choice on
    /// mod leaves (matters for item selection; Check nodes ignore it and match both).
    /// </summary>
    public static void Open(string key, Condition root, string title, bool allowCategory = false)
    {
        _openKey = key;
        _openRoot = root;
        _openTitle = title;
        _openAllowCategory = allowCategory;
        _focusNext = true;   // re-opening an already-open editor raises it (it may sit behind the main window)
    }

    /// <summary>Closes the editor if it is currently editing <paramref name="root"/> (e.g. its node was deleted).</summary>
    public static void CloseIfEditing(Condition root)
    {
        if (ReferenceEquals(_openRoot, root)) _openKey = "";
    }

    /// <summary>Renders the open editor window, if any. Call once per frame from the crafts page (any sub-tab).
    /// Takes the whole plan: mod picking needs the representative base/class context, Set leaves need the
    /// plan's mod sets.</summary>
    public static void DrawWindow(CraftPlan plan, string basePath, string domain)
    {
        if (_openKey.Length == 0 || _openRoot == null) return;

        var ctx = new Ctx(new ModRequirementRow.Ctx(basePath, domain, plan.ItemClass, plan.ClusterTypes),
            _openAllowCategory, plan.ModSets);

        ImGui.SetNextWindowSize(new Vector2(700, 480), ImGuiCond.FirstUseEver);
        if (_focusNext) { ImGui.SetNextWindowFocus(); _focusNext = false; }
        var open = true;
        // "###" keeps one window identity across titles (item filter / Check), so size/pos save once.
        // NoDocking: avoid the docking resize-loop that flickers white and freezes these tool windows.
        if (ImGui.Begin($"{_openTitle}###condedit", ref open, ImGuiWindowFlags.NoDocking))
        {
            _modColW = MeasureModColumn(_openRoot, ImGui.GetContentRegionAvail().X);
            if (string.IsNullOrEmpty(basePath))
                ImGui.TextColored(UiColors.Warn, "Pick a class/base on the Item Selection tab to choose mods.");

            DrawGroup(_openRoot, ctx, _openKey, true, 0);
        }
        ImGui.End();

        if (!open) _openKey = "";
    }

    private readonly struct Ctx
    {
        public readonly ModRequirementRow.Ctx Row;      // picker/library context, shared with the mod-row widget
        public readonly bool AllowCategory;
        public readonly IReadOnlyList<ModSet> Sets;     // the plan's mod sets (Set leaves pick from these)
        public Ctx(ModRequirementRow.Ctx row, bool allowCategory, IReadOnlyList<ModSet> sets)
        { Row = row; AllowCategory = allowCategory; Sets = sets; }
    }

    /// <returns>true if the parent should remove this node.</returns>
    private static bool DrawNode(Condition c, Ctx ctx, string id, int depth)
    {
        ImGui.PushID(id);
        var remove = c.Kind == CondKind.Group ? DrawGroup(c, ctx, id, false, depth) : DrawLeaf(c, ctx, id);
        ImGui.PopID();
        return remove;
    }

    private static bool DrawGroup(Condition c, Ctx ctx, string id, bool isRoot, int depth)
    {
        var remove = false;
        var boxed = !isRoot;

        if (boxed)
        {
            ImGui.PushStyleColor(ImGuiCol.ChildBg, GroupTint(depth));
            ImGui.BeginChild($"box{id}", new Vector2(0, 0), ImGuiChildFlags.Border | ImGuiChildFlags.AutoResizeY);
        }

        // --- sentence header ---
        ImGui.AlignTextToFramePadding();
        if (isRoot)
        {
            ImGui.TextUnformatted("Item must match");
            ImGui.SameLine();
        }
        else
        {
            NotToggle(c);
            ImGui.SameLine();
        }

        var op = (int)c.Op;
        ImGui.SetNextItemWidth(86);
        if (ImGui.Combo("##op", ref op, GroupOps, GroupOps.Length)) c.Op = (GroupOp)op;

        if (c.Op == GroupOp.Count)
        {
            ImGui.SameLine();
            var co = Math.Max(0, Array.IndexOf(CountOps, c.CountOp));
            ImGui.SetNextItemWidth(56);
            if (ImGui.Combo("##cop", ref co, CountOps, CountOps.Length)) c.CountOp = CountOps[co];
            ImGui.SameLine();
            ImGui.SetNextItemWidth(52);
            var cv = c.CountValue;
            if (ImGui.InputInt("##cv", ref cv, 0)) c.CountValue = cv;
        }

        ImGui.SameLine();
        ImGui.TextUnformatted("of:");
        if (boxed)
        {
            ImGui.SameLine();
            if (ImGui.Button("x##rmg")) remove = true;
        }

        // --- rows ---
        ImGui.Indent(10);
        for (var i = 0; i < c.Children.Count; i++)
        {
            if (DrawNode(c.Children[i], ctx, $"{id}_{i}", depth + 1))
            {
                c.Children.RemoveAt(i);
                i--;
            }
        }
        AddBar(c, ctx, id);
        ImGui.Unindent(10);

        if (boxed) { ImGui.EndChild(); ImGui.PopStyleColor(); }
        return remove;
    }

    private static void AddBar(Condition group, Ctx ctx, string id)
    {
        ImGui.Spacing();
        if (ImGui.Button("+ property")) group.Children.Add(Condition.NewLeaf());
        ImGui.SameLine();
        if (ImGui.Button("+ mod"))
        {
            group.Children.Add(Condition.NewMod());
            ModAffixPicker.Show($"{id}_{group.Children.Count - 1}_mod");
        }
        ImGui.SameLine();
        if (ImGui.Button("+ set")) group.Children.Add(Condition.NewSet(FirstSetName(ctx)));
        ImGui.SameLine();
        if (ImGui.Button("+ group")) group.Children.Add(Condition.NewCountGroup());
    }

    private static string FirstSetName(Ctx ctx) => ctx.Sets is { Count: > 0 } ? ctx.Sets[0].Name : "";

    private static bool DrawLeaf(Condition c, Ctx ctx, string id)
    {
        var remove = false;

        NotToggle(c);
        ImGui.SameLine();

        var ti = LeafTypeIndex(c);
        ImGui.SetNextItemWidth(110);
        if (ImGui.Combo("##lt", ref ti, LeafTypes, LeafTypes.Length)) ApplyLeafType(c, ti, ctx);
        ImGui.SameLine();

        switch (c.Kind)
        {
            case CondKind.Field: DrawField(c, id); break;
            case CondKind.HasMod: DrawHasMod(c, ctx, id); break;
            case CondKind.Set: DrawSet(c, ctx, id); break;
            default: DrawRaw(c); break;
        }

        // Right-align the remove button so every row's "x" lines up in a column on the right.
        var bw = ImGui.GetFrameHeight();
        var xPos = ImGui.GetContentRegionMax().X - bw;
        ImGui.SameLine();
        if (ImGui.GetCursorPosX() < xPos) ImGui.SameLine(xPos);
        if (ImGui.Button("x##rml", new Vector2(bw, 0))) remove = true;
        return remove;
    }

    private static void DrawField(Condition c, string id)
    {
        var fi = Math.Max(0, Array.FindIndex(Fields, f => f.Key == c.Field));
        ImGui.SetNextItemWidth(170);
        if (ImGui.Combo($"##fld_{id}", ref fi, FieldNames, FieldNames.Length))
        {
            var nf = Fields[fi];
            if (c.Field != nf.Key)
            {
                c.Field = nf.Key;
                c.Number = new NumberCond();
                c.Value = nf.Type == FieldType.Enum && nf.Options.Length > 0 ? nf.Options[0] : "";
            }
        }

        var f = Fields[fi];
        ImGui.SameLine();
        switch (f.Type)
        {
            case FieldType.Number:
                NumberCondUI.DrawInline(c.Number, $"f_{id}");
                break;
            case FieldType.Enum:
                var oi = Math.Max(0, Array.IndexOf(f.Options, c.Value));
                ImGui.SetNextItemWidth(150);
                if (ImGui.Combo($"##ev_{id}", ref oi, f.Options, f.Options.Length)) c.Value = f.Options[oi];
                break;
            case FieldType.Bool:
                ImGui.TextColored(UiColors.Muted, c.Negate ? "is false" : "is true");
                break;
        }
    }

    /// <summary>Widest mod label in the tree (so the mod cell fits the longest name), clamped to leave room
    /// in the current window for the left controls and the trailing T1+/min-val/x columns.</summary>
    private static float MeasureModColumn(Condition root, float availWidth)
    {
        var labels = new List<string>();
        void Walk(Condition c)
        {
            if (c == null) return;
            if (c.Kind == CondKind.HasMod && !string.IsNullOrEmpty(c.Mod?.Group))
                labels.Add(c.Mod.Label);
            if (c.Children != null)
                foreach (var ch in c.Children) Walk(ch);
        }
        Walk(root);

        const float leftReserve = 175f;   // not + type combo
        const float rightReserve = 175f;  // T1+ slot + min-val + x
        return ModRequirementRow.MeasureLabelColumn(labels, availWidth, leftReserve, rightReserve);
    }

    private static void DrawHasMod(Condition c, Ctx ctx, string id)
    {
        c.Mod ??= new ModRequirement { Category = ModCategory.Explicit };
        var replaced = ModRequirementRow.Draw($"{id}_mod", c.Mod, ctx.Row, _modColW, allowFractured: ctx.AllowCategory);
        if (replaced != null) c.Mod = replaced;
    }

    // "count of [mods|prefixes|suffixes] [in|not in] <set> [>=1|min|max|range]" - the set itself is edited
    // once on the Mod Sets tab; this leaf only counts against it.
    private static void DrawSet(Condition c, Ctx ctx, string id)
    {
        var scope = (int)c.SetScope;
        ImGui.SetNextItemWidth(84);
        if (ImGui.Combo("##sscope", ref scope, SetScopes, SetScopes.Length)) c.SetScope = (SetScope)scope;
        ImGui.SameLine();

        var inv = c.SetInvert ? 1 : 0;
        ImGui.SetNextItemWidth(66);
        if (ImGui.Combo("##sinv", ref inv, SetInOps, SetInOps.Length)) c.SetInvert = inv == 1;
        ImGui.SameLine();

        if (ctx.Sets is not { Count: > 0 })
        {
            ImGui.TextColored(UiColors.Warn, "(no mod sets - define them on the Mod Sets tab)");
            return;
        }

        var names = new string[ctx.Sets.Count];
        for (var i = 0; i < names.Length; i++) names[i] = ctx.Sets[i].Name;
        var idx = Array.FindIndex(names, n => string.Equals(n, c.Set, StringComparison.OrdinalIgnoreCase));
        ImGui.SetNextItemWidth(170);
        if (ImGui.Combo("##sname", ref idx, names, names.Length) && idx >= 0) c.Set = names[idx];
        ImGui.SameLine();

        NumberCondUI.DrawInline(c.Number, $"s_{id}");
    }

    private static void DrawRaw(Condition c)
    {
        var v = c.Value ?? "";
        ImGui.SetNextItemWidth(300);
        if (ImGui.InputTextWithHint("##raw", "IFL query, e.g. Rarity == ItemRarity.Magic", ref v, 256)) c.Value = v;
    }

    private static void NotToggle(Condition c)
    {
        var not = c.Negate;
        if (ImGui.Checkbox("not", ref not)) c.Negate = not;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Negate (NOT) this condition");
    }

    // 0 Property | 1 Mod | 2 Set | 3 Advanced (legacy Implicit/Enchant leaves still render as Mod)
    private static int LeafTypeIndex(Condition c)
    {
        if (c.Kind == CondKind.Field) return 0;
        if (c.Kind == CondKind.Set) return 2;
        if (c.Kind == CondKind.Raw) return 3;
        return 1;
    }

    private static void ApplyLeafType(Condition c, int ti, Ctx ctx)
    {
        switch (ti)
        {
            case 0: // Property
                if (c.Kind != CondKind.Field)
                {
                    c.Kind = CondKind.Field;
                    c.Field = "ItemLevel";
                    c.Number = new NumberCond();
                    c.Value = "";
                }
                break;
            case 1: SetModLeaf(c, ModCategory.Explicit); break;
            case 2: // Set
                if (c.Kind != CondKind.Set)
                {
                    c.Kind = CondKind.Set;
                    c.Set = FirstSetName(ctx);
                    c.SetScope = SetScope.Mods;
                    c.SetInvert = false;
                    c.Number = new NumberCond();
                }
                break;
            default: // Advanced
                if (c.Kind != CondKind.Raw) c.Value = "";
                c.Kind = CondKind.Raw;
                break;
        }
    }

    private static void SetModLeaf(Condition c, ModCategory cat)
    {
        // Explicit and Fractured share the explicit pool; Implicit and Enchant are their own. Keep the picked
        // mod only when staying in the same pool, otherwise reset to an unpicked placeholder of the new category.
        static bool Explicitish(ModCategory x) => x is ModCategory.Explicit or ModCategory.Fractured;
        var samePool = c.Kind == CondKind.HasMod && c.Mod != null &&
                       (Explicitish(cat) ? Explicitish(c.Mod.Category) : c.Mod.Category == cat);
        c.Kind = CondKind.HasMod;
        if (!samePool) c.Mod = new ModRequirement { Category = cat };
    }

    private static Vector4 GroupTint(int depth) =>
        (depth & 1) == 1 ? new Vector4(0.55f, 0.78f, 1f, 0.05f) : new Vector4(1f, 1f, 1f, 0.04f);

    // ---- human-readable summary (node label + panel) ----

    public static string Summary(Condition c)
    {
        var s = DescribeTop(c);
        return string.IsNullOrEmpty(s) ? "(no conditions)" : s;
    }

    /// <summary>One-line description of a single condition (used for one-row-per-condition node bodies).</summary>
    public static string Line(Condition c)
    {
        var s = DescribeTop(c);
        return string.IsNullOrEmpty(s) ? "(empty)" : s;
    }

    // Top-level: an un-negated outermost And/Or group doesn't need wrapping parens - the panel/node label
    // already bounds it, so "(Rarity Magic and (mod))" reads as "Rarity Magic and (mod)". A negated or
    // Count group keeps its brackets so its scope stays unambiguous.
    private static string DescribeTop(Condition c)
    {
        if (c is { Kind: CondKind.Group, Negate: false } && c.Op != GroupOp.Count)
        {
            var parts = c.Children.Select(Describe).Where(x => x.Length > 0).ToList();
            if (parts.Count > 0)
                return string.Join(c.Op == GroupOp.All ? " and " : " or ", parts);
        }
        return Describe(c);
    }

    private static string Describe(Condition c)
    {
        if (c == null) return "";
        string r;
        switch (c.Kind)
        {
            case CondKind.Group:
                var parts = c.Children.Select(Describe).Where(x => x.Length > 0).ToList();
                if (parts.Count == 0) r = "(empty)";
                else if (c.Op == GroupOp.Count) r = $"{c.CountOp}{c.CountValue} of [{string.Join(", ", parts)}]";
                else r = "(" + string.Join(c.Op == GroupOp.All ? " and " : " or ", parts) + ")";
                break;
            case CondKind.HasMod:
                var m = c.Mod;
                string pfx;
                if (m?.AffixType == "ExarchImplicit") pfx = "Exarch ";
                else if (m?.AffixType == "EaterImplicit") pfx = "Eater ";
                else if (m != null && !string.IsNullOrEmpty(m.Influence)) pfx = Influences.NameOf(m.Influence) + " ";
                else if (m?.Essence == true) pfx = "essence ";
                else pfx = m?.Category switch
                {
                    ModCategory.Implicit => "implicit ",
                    ModCategory.Enchant => "enchant ",
                    ModCategory.Fractured => "fractured ",
                    _ => "",
                };
                r = pfx + (string.IsNullOrEmpty(m?.Label) ? "(mod)" : m.Label);
                if (m != null && m.Tier > 0) r += $" T{m.Tier}+";
                if (m?.MinValues is { } mv && mv.Exists(v => v.HasValue))
                {
                    // bounds in value order, "-" = unbounded, trailing unbounded trimmed
                    var last = mv.FindLastIndex(v => v.HasValue);
                    r += " >=" + string.Join("/", mv.Take(last + 1).Select(v => v.HasValue ? v.Value.ToString() : "-"));
                }
                break;
            case CondKind.Set:
                var scope = c.SetScope switch
                {
                    SetScope.Prefixes => "prefixes",
                    SetScope.Suffixes => "suffixes",
                    _ => "mods",
                };
                var bound = c.Number.Mode switch
                {
                    NumberMode.AtLeast => $" >={c.Number.Value}",
                    NumberMode.AtMost => $" <={c.Number.Value}",
                    NumberMode.Between => $" {c.Number.Min}-{c.Number.Max}",
                    _ => " >=1",   // the default mode means "at least one" - say so on the canvas
                };
                r = $"{scope} {(c.SetInvert ? "not in" : "in")} [{(string.IsNullOrEmpty(c.Set) ? "set" : c.Set)}]{bound}";
                break;
            case CondKind.Raw:
                r = string.IsNullOrWhiteSpace(c.Value) ? "query" : "{" + c.Value + "}";
                break;
            default:
                var f = FieldCatalog.Get(c.Field);
                r = f == null ? c.Field : $"{f.Label} {DescribeField(c, f)}".TrimEnd();
                break;
        }
        return c.Negate ? "not " + r : r;
    }

    private static string DescribeField(Condition c, ItemField f) => f.Type switch
    {
        FieldType.Enum => c.Value,
        FieldType.Bool => "",
        _ => c.Number.Mode switch
        {
            NumberMode.AtLeast => $">={c.Number.Value}",
            NumberMode.AtMost => $"<={c.Number.Value}",
            NumberMode.Between => $"{c.Number.Min}-{c.Number.Max}",
            _ => ">=1",   // the default mode means "at least one", not "anything goes"
        },
    };
}
