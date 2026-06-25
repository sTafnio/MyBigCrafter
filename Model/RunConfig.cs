using System.Collections.Generic;

namespace MyBigCrafter.Model;

public enum RunMode { Inventory, Stash, Harvest }

/// <summary>Machine-local run configuration (not part of a shareable craft): mode, stash tabs, and the run queue.</summary>
public sealed class RunConfig
{
    public RunMode Mode { get; set; } = RunMode.Inventory;

    // Stash-mode tab names (resolved to indices at run time).
    public string InputTab { get; set; } = "";
    public string CurrencyTab { get; set; } = "";
    public string OutputTab { get; set; } = "";

    /// <summary>Saved craft names to run, in order.</summary>
    public List<string> Queue { get; set; } = new();
}
