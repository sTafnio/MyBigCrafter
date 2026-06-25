using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using ImGuiNET;
using MyBigCrafter.Model;

namespace MyBigCrafter.UI;

/// <summary>
/// Focused, filter-row builder for a Check node's <see cref="Condition"/> tree. Opened in its own window
/// (double-click a Check, or the panel button). The top level reads as a sentence ("Item must match ALL of:")
/// and each condition is a row; "count" groups (at least N of...) are an indented sub-block. Leaves are split
/// by type - Property (a catalog field), Mod (a game-file affix), or Advanced (raw IFL). No IFL syntax required.
/// <see cref="Summary"/> renders a short human-readable description for the node/panel.
/// </summary>
public static class ConditionEditor
{
    private static readonly string[] GroupOps = { "all", "any", "count" };   // index maps to GroupOp
    private static readonly string[] CountOps = { ">", ">=", "<", "<=", "==", "!=" };
    // Implicit/Enchant leaves were retired: eldritch implicits and cluster mods live in the Mod picker.
    private static readonly string[] LeafTypes = { "Property", "Mod", "Advanced" };
    // Field leaves listed alphabetically by label (the catalog's own order is grouped by kind, not sorted).
    private static readonly ItemField[] Fields = FieldCatalog.All.OrderBy(f => f.Label, StringComparer.OrdinalIgnoreCase).ToArray();
    private static readonly string[] FieldNames = Fields.Select(f => f.Label).ToArray();

    // Column widths so a row's trailing controls line up across stacked leaves: the mod-name cell, then the
    // "T1+" slot, then min-vals; the remove "x" is right-aligned to the content edge. The mod cell auto-sizes
    // to the widest mod in the tree (measured each frame) so names align without being clipped.
    private static float _modColW = 250f;
    private const float TierColW = 36f;
    private const float ColGap = 6f;

    private static string _openKey = "";
    private static Condition _openRoot;
    private static string _openTitle = "";
    private static bool _openAllowCategory;

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
    }

    /// <summary>Closes the editor if it is currently editing <paramref name="root"/> (e.g. its node was deleted).</summary>
    public static void CloseIfEditing(Condition root)
    {
        if (ReferenceEquals(_openRoot, root)) _openKey = "";
    }

    /// <summary>Renders the open editor window, if any. Call once per frame from the crafts page (any sub-tab).</summary>
    public static void DrawWindow(string basePath, string domain, string itemClass, IReadOnlyList<string> clusterTypes = null)
    {
        if (_openKey.Length == 0 || _openRoot == null) return;

        var ctx = new Ctx(basePath, domain, itemClass, _openAllowCategory, clusterTypes);

        ImGui.SetNextWindowSize(new Vector2(700, 480), ImGuiCond.FirstUseEver);
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
        public readonly string BasePath;   // representative base for per-base library queries
        public readonly string Domain;
        public readonly string ItemClass;
        public readonly bool AllowCategory;
        public readonly IReadOnlyList<string> ClusterTypes;   // selected cluster passive-type tags (jewels only)
        public Ctx(string basePath, string domain, string itemClass, bool allowCategory, IReadOnlyList<string> clusterTypes)
        { BasePath = basePath; Domain = domain; ItemClass = itemClass; AllowCategory = allowCategory; ClusterTypes = clusterTypes; }
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
        AddBar(c, id);
        ImGui.Unindent(10);

        if (boxed) { ImGui.EndChild(); ImGui.PopStyleColor(); }
        return remove;
    }

    private static void AddBar(Condition group, string id)
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
        if (ImGui.Button("+ group")) group.Children.Add(Condition.NewCountGroup());
    }

    private static bool DrawLeaf(Condition c, Ctx ctx, string id)
    {
        var remove = false;

        NotToggle(c);
        ImGui.SameLine();

        var ti = LeafTypeIndex(c);
        ImGui.SetNextItemWidth(110);
        if (ImGui.Combo("##lt", ref ti, LeafTypes, LeafTypes.Length)) ApplyLeafType(c, ti);
        ImGui.SameLine();

        switch (c.Kind)
        {
            case CondKind.Field: DrawField(c, id); break;
            case CondKind.HasMod: DrawHasMod(c, ctx, id); break;
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
        var max = ImGui.CalcTextSize("(click to pick a mod)").X;
        void Walk(Condition c)
        {
            if (c == null) return;
            if (c.Kind == CondKind.HasMod)
            {
                var lbl = string.IsNullOrEmpty(c.Mod?.Group) ? "(click to pick a mod)" : c.Mod?.Label ?? "";
                max = Math.Max(max, ImGui.CalcTextSize(lbl).X);
            }
            if (c.Children != null)
                foreach (var ch in c.Children) Walk(ch);
        }
        Walk(root);

        const float leftReserve = 175f;   // not + type combo
        const float rightReserve = 175f;  // T1+ slot + min-val + x
        var maxCol = Math.Max(140f, availWidth - leftReserve - rightReserve);
        return Math.Clamp(max + 14f, 140f, maxCol);
    }

    private static void DrawHasMod(Condition c, Ctx ctx, string id)
    {
        c.Mod ??= new ModRequirement { Category = ModCategory.Explicit };
        var req = c.Mod;
        var hasMod = !string.IsNullOrEmpty(req.Group);
        var label = hasMod ? req.Label : "(click to pick a mod)";
        var color = hasMod ? UiColors.ForAffix(req.AffixType) : UiColors.Muted;

        var mode = req.Category switch
        {
            ModCategory.Implicit => PickMode.Implicit,
            ModCategory.Enchant => PickMode.Enchant,
            _ => PickMode.Explicit,
        };

        // Fixed-width mod cell (auto-sized to the widest mod) so the "T1+" / min-val / "x" that follow line up.
        var modX = ImGui.GetCursorPosX();
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        if (ImGui.Selectable(label, false, ImGuiSelectableFlags.None, new Vector2(_modColW, 0)))
            ModAffixPicker.Show($"{id}_mod");
        ImGui.PopStyleColor();
        // On a too-narrow window a long name can still clip, so the tooltip carries the full label + change hint.
        if (hasMod && ImGui.IsItemHovered()) ImGui.SetTooltip(label + "\nClick to change this mod");

        if (hasMod && req.Tier > 0)
        {
            ImGui.SameLine(modX + _modColW + ColGap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled($"T{req.Tier}+");
        }

        // Fractured choice is only for explicit affixes in item selection (checks match both lists).
        var showFractured = hasMod && ctx.AllowCategory && req.Category is ModCategory.Explicit or ModCategory.Fractured;
        if (showFractured)
        {
            ImGui.SameLine();
            var frac = req.Category == ModCategory.Fractured;
            if (ImGui.Checkbox("fractured", ref frac))
                req.Category = frac ? ModCategory.Fractured : ModCategory.Explicit;
        }

        // Optional minimum rolled value(s): a summary button that opens a per-value slider popup. Sits in a
        // fixed column (after the tier slot) so it aligns too - unless fractured pushed the row wider.
        if (hasMod)
        {
            if (showFractured) ImGui.SameLine();
            else ImGui.SameLine(modX + _modColW + ColGap + TierColW);
            DrawMinRolls(req, ctx, id);
        }

        var picked = ModAffixPicker.Draw($"{id}_mod", ctx.BasePath, ctx.Domain, ctx.ItemClass, mode, ctx.ClusterTypes);
        if (picked != null)
        {
            var cat = req.Category;   // preserve the leaf's pool (implicit/enchant/explicit/fractured)
            c.Mod = ModAffixPicker.ToRequirement(picked.Value, ctx.Domain);   // carries influence/essence sub-type
            c.Mod.Category = cat;
        }
    }

    /// <summary>
    /// The minimum-roll control: a compact summary button (">= 33" / "min val") that opens a popup with one
    /// range-clamped slider per rolled value. Hybrid mods (e.g. Nautilus's +armour/+life) get a labeled
    /// slider per value; stats that never vary (eldritch presence flags, unused 0-0 slots) are skipped.
    /// A slider at the stat's floor means "any" and stores null.
    /// </summary>
    private static void DrawMinRolls(ModRequirement req, Ctx ctx, string id)
    {
        // The attainable roll range depends on the tier bound: "T2 and better" can only ever roll what the
        // top two tiers roll, so the sliders span those tiers only (TierPool applies the bound). Influence
        // and essence pools are resolved by ModFamilies.
        var pool = ModFamilies.TierPool(req, ctx.BasePath, ctx.ItemClass);
        var valueCount = ModFamilies.ValueCount(pool);
        if (valueCount == 0) return;
        var axes = ModFamilies.Axes(pool);
        if (axes.Count == 0) return;   // nothing rollable to bound

        req.MinValues ??= new List<int?>();
        while (req.MinValues.Count < valueCount) req.MinValues.Add(null);

        var set = req.MinValues.Where(v => v.HasValue).Select(v => $">={v.Value}").ToList();
        var label = set.Count > 0 ? string.Join(" / ", set) : axes.Count > 1 ? "min values" : "min val";

        // Caller positions the cursor (fixed column); just draw the button here.
        if (set.Count > 0) ImGui.PushStyleColor(ImGuiCol.Text, UiColors.Accent);
        var clicked = ImGui.Button($"{label}##minv");
        if (set.Count > 0) ImGui.PopStyleColor();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(RollTooltip(req, axes));
        if (clicked) ImGui.OpenPopup($"minvals_{id}");

        if (!ImGui.BeginPopup($"minvals_{id}")) return;

        ImGui.TextDisabled(req.Tier > 0
            ? $"Required minimum rolls within T{req.Tier}+ (far left = any, ctrl+click to type):"
            : "Required minimum rolls (far left = any, ctrl+click to type):");
        if (ImGui.BeginTable($"##mvt_{id}", 3, ImGuiTableFlags.SizingFixedFit))
        {
            foreach (var ax in axes)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(ax.Label);
                ImGui.TableSetColumnIndex(1);
                ImGui.SetNextItemWidth(170);
                var cur = req.MinValues[ax.Index] ?? ax.Min;
                var fmt = cur <= ax.Min ? "any" : ">= %d";
                if (ImGui.SliderInt($"##mv{ax.Index}", ref cur, ax.Min, ax.Max, fmt, ImGuiSliderFlags.AlwaysClamp))
                    req.MinValues[ax.Index] = cur > ax.Min ? cur : (int?)null;
                ImGui.TableSetColumnIndex(2);
                ImGui.TextDisabled($"{ax.Min}-{ax.Max}");
            }
            ImGui.EndTable();
        }
        if (ImGui.Button("clear (any roll)"))
            for (var i = 0; i < req.MinValues.Count; i++) req.MinValues[i] = null;
        ImGui.EndPopup();
    }

    private static string RollTooltip(ModRequirement req, List<ModFamilies.ValueAxis> axes)
    {
        var sb = new StringBuilder("Minimum rolled values - click to edit.");
        foreach (var ax in axes)
        {
            var b = ax.Index < req.MinValues.Count ? req.MinValues[ax.Index] : null;
            sb.Append('\n').Append(ax.Label).Append(": ")
              .Append(b.HasValue ? $">={b.Value}" : "any")
              .Append(req.Tier > 0 ? $"  (T{req.Tier}+ rolls {ax.Min}-{ax.Max})" : $"  (rolls {ax.Min}-{ax.Max})");
        }
        return sb.ToString();
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

    // 0 Property | 1 Mod | 2 Advanced (legacy Implicit/Enchant leaves still render as Mod)
    private static int LeafTypeIndex(Condition c)
    {
        if (c.Kind == CondKind.Field) return 0;
        if (c.Kind == CondKind.Raw) return 2;
        return 1;
    }

    private static void ApplyLeafType(Condition c, int ti)
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
            _ => "(any)",
        },
    };
}
