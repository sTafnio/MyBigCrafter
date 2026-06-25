using System;
using System.Collections.Generic;
using System.Numerics;

namespace MyBigCrafter.Data;

/// <summary>A currency/essence stack found in a source, with total count and a click target (center + size).</summary>
public sealed class CurrencyStack
{
    public string BaseName { get; init; } = "";
    public int Count { get; init; }
    public Vector2 Center { get; init; }
    public Vector2 Size { get; init; }
    public ClickTarget Target => new(Center, Size);
}

/// <summary>Builds a BaseName -> stack lookup from read items (currency available to apply).</summary>
public static class CurrencyIndex
{
    public static Dictionary<string, CurrencyStack> Build(IEnumerable<ReadItem> items)
    {
        var map = new Dictionary<string, CurrencyStack>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in items)
        {
            var name = it.Data?.BaseName;
            if (string.IsNullOrEmpty(name)) continue;

            var count = it.Data.StackInfo?.Count ?? 1;
            if (map.TryGetValue(name, out var existing))
                map[name] = new CurrencyStack { BaseName = name, Count = existing.Count + count, Center = existing.Center, Size = existing.Size };
            else
                map[name] = new CurrencyStack { BaseName = name, Count = count, Center = it.Center, Size = it.Size };
        }

        return map;
    }
}
