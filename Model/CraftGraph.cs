using System.Collections.Generic;
using System.Linq;

namespace MyBigCrafter.Model;

public enum NodeType { Start, Apply, Check, Finish, Harvest }

/// <summary>A node in the crafting flow chart. Output links are node ids (-1 = unconnected).</summary>
public sealed class CraftNode
{
    public int Id { get; set; }
    public NodeType Type { get; set; }
    public float X { get; set; }
    public float Y { get; set; }

    public List<string> Methods { get; set; } = new();  // Apply: currencies/essences in priority order (top = first used)
    public bool UseShift { get; set; }             // Apply: hold Shift to keep the currency selected across uses
    public Condition Check { get; set; } = new();    // Check: condition tree evaluated against the item
    public string HarvestCraft { get; set; } = "";   // Harvest: the craft's display name (matches the live CraftDisplayName)

    public int Next { get; set; } = -1;     // Start / Apply -> next node
    public int OnTrue { get; set; } = -1;    // Check -> success
    public int OnFalse { get; set; } = -1;   // Check -> failure
}

/// <summary>The crafting flow chart: nodes + their links. Walked per item by the executor.</summary>
public sealed class CraftGraph
{
    public List<CraftNode> Nodes { get; set; } = new();
    public int NextId { get; set; } = 1;

    public CraftNode Get(int id) => id < 0 ? null : Nodes.FirstOrDefault(n => n.Id == id);

    public CraftNode Start => Nodes.FirstOrDefault(n => n.Type == NodeType.Start);

    /// <summary>True if any node applies a harvest craft - such a craft can only run in Harvest mode.</summary>
    public bool HasHarvestNode => Nodes.Any(n => n.Type == NodeType.Harvest);

    public CraftNode Add(NodeType type, float x, float y)
    {
        var node = new CraftNode { Id = NextId++, Type = type, X = x, Y = y };
        Nodes.Add(node);
        return node;
    }

    /// <summary>
    /// Seeds a brand-new (empty) graph with a Start and a Finish so the canvas isn't blank; on an existing
    /// graph it only guarantees a Start exists (so we never re-add a Finish the user deleted).
    /// </summary>
    public void EnsureDefaults()
    {
        if (Nodes.Count == 0)
        {
            Add(NodeType.Start, 30, 120);
            Add(NodeType.Finish, 360, 120);
            return;
        }
        if (Start == null) Add(NodeType.Start, 30, 120);
    }

    public void Remove(int id)
    {
        var node = Get(id);
        if (node == null || node.Type == NodeType.Start) return;
        Nodes.Remove(node);
        foreach (var n in Nodes)
        {
            if (n.Next == id) n.Next = -1;
            if (n.OnTrue == id) n.OnTrue = -1;
            if (n.OnFalse == id) n.OnFalse = -1;
        }
    }
}
