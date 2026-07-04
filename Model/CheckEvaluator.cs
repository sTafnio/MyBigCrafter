using System;
using System.Collections.Generic;
using ItemFilterLibrary;

namespace MyBigCrafter.Model;

/// <summary>Evaluates a Check node's <see cref="Condition"/> tree against an item (pass -> green, fail -> red).
/// <paramref name="sets"/> is the owning plan's mod-set list, which Set leaves count against.</summary>
public static class CheckEvaluator
{
    private static readonly Dictionary<string, ItemQuery> RawCache = new();

    public static bool Passes(Condition root, ItemData item, IReadOnlyList<ModSet> sets)
    {
        if (item == null) return false;
        if (root == null) return true;
        return Eval(root, item, sets);
    }

    private static bool Eval(Condition c, ItemData item, IReadOnlyList<ModSet> sets)
    {
        var r = c.Kind switch
        {
            CondKind.Group => EvalGroup(c, item, sets),
            CondKind.Field => EvalField(c, item),
            CondKind.HasMod => c.Mod != null && TargetEvaluator.SatisfiedForCheck(c.Mod, item),
            CondKind.Set => EvalSet(c, item, sets),
            CondKind.Raw => EvalRaw(c.Value, item),
            _ => true,
        };
        return c.Negate ? !r : r;
    }

    // The editor keeps set references non-dangling (renames propagate, deletes are blocked while referenced),
    // so an unknown name only occurs on a hand-edited file - fail the leaf rather than silently pass.
    private static bool EvalSet(Condition c, ItemData item, IReadOnlyList<ModSet> sets)
    {
        ModSet set = null;
        if (sets != null && !string.IsNullOrEmpty(c.Set))
            foreach (var s in sets)
                if (string.Equals(s.Name, c.Set, StringComparison.OrdinalIgnoreCase)) { set = s; break; }
        if (set == null) return false;
        return c.Number.Matches(TargetEvaluator.CountInSet(set.Mods, item, c.SetScope, c.SetInvert));
    }

    private static bool EvalField(Condition c, ItemData item)
    {
        var f = FieldCatalog.Get(c.Field);
        if (f == null) return true;
        return f.Type switch
        {
            FieldType.Number => c.Number.Matches(f.Num(item)),
            FieldType.Bool => f.Num(item) > 0,
            FieldType.Enum => string.Equals(f.Text(item), c.Value, StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
    }

    private static bool EvalGroup(Condition c, ItemData item, IReadOnlyList<ModSet> sets)
    {
        if (c.Children.Count == 0) return true;

        var t = 0;
        foreach (var ch in c.Children)
            if (Eval(ch, item, sets)) t++;

        return c.Op switch
        {
            GroupOp.All => t == c.Children.Count,
            GroupOp.Any => t > 0,
            GroupOp.Count => Compare(t, c.CountOp, c.CountValue),
            _ => true,
        };
    }

    private static bool Compare(int n, string op, int v) => op switch
    {
        ">" => n > v,
        ">=" => n >= v,
        "<" => n < v,
        "<=" => n <= v,
        "==" => n == v,
        "!=" => n != v,
        _ => false,
    };

    private static bool EvalRaw(string q, ItemData item)
    {
        if (string.IsNullOrWhiteSpace(q)) return true;
        if (!RawCache.TryGetValue(q, out var query))
        {
            query = ItemQuery.Load(q);
            RawCache[q] = query;
        }
        return !query.FailedToCompile && query.Matches(item);
    }
}
