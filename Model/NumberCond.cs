namespace MyBigCrafter.Model;

public enum NumberMode { Any, AtLeast, AtMost, Between }

/// <summary>A numeric condition: any, min (&gt;=), max (&lt;=), or a [min,max] range.</summary>
public sealed class NumberCond
{
    public NumberMode Mode { get; set; } = NumberMode.Any;
    public int Value { get; set; }   // for min / max
    public int Min { get; set; }     // for range
    public int Max { get; set; }     // for range

    public bool Matches(int v) => Mode switch
    {
        NumberMode.AtLeast => v >= Value,
        NumberMode.AtMost => v <= Value,
        NumberMode.Between => v >= Min && v <= Max,
        // Default ("≥1" / present): 0 means absent, so it must NOT pass (0 open prefixes = no open prefixes).
        _ => v >= 1,
    };
}
