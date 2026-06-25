using System.Collections.Generic;
using ItemFilterLibrary;

namespace MyBigCrafter.Model;

/// <summary>
/// Read-only dry-run of a craft graph against an item's CURRENT state, to decide whether crafting it would
/// actually DO anything. Walks from Start following Check branches (via <see cref="CheckEvaluator"/>, no clicks);
/// returns true as soon as it reaches an Apply node that has currency or a Harvest node that has a craft (work
/// would happen), and false if it reaches Finish, a dead end, or a Check-only cycle first (nothing would be
/// done). Lets the run loops skip "already finished" items in place - leaving them where they are instead of
/// shuttling them to the bench / currency tab and back - and count the real work set up front.
/// </summary>
public static class GraphSimulator
{
    public static bool WillCraft(CraftGraph graph, ItemData item)
    {
        if (graph?.Start == null || item == null) return false;

        var current = graph.Get(graph.Start.Next);
        var seenChecks = new HashSet<int>();
        var guard = 0;

        while (current != null && guard++ < 2000)
        {
            switch (current.Type)
            {
                case NodeType.Apply:
                    if (current.Methods is { Count: > 0 }) return true;
                    current = graph.Get(current.Next);   // empty Apply does nothing - keep walking
                    break;

                case NodeType.Harvest:
                    if (!string.IsNullOrEmpty(current.HarvestCraft)) return true;
                    current = graph.Get(current.Next);   // empty Harvest does nothing - keep walking
                    break;

                case NodeType.Check:
                    if (!seenChecks.Add(current.Id)) return false;   // Check-only loop: never reaches work
                    var pass = CheckEvaluator.Passes(current.Check, item);
                    current = graph.Get(pass ? current.OnTrue : current.OnFalse);
                    break;

                case NodeType.Finish:
                    return false;   // reached the end with no crafting in between

                default: // Start
                    current = graph.Get(current.Next);
                    break;
            }
        }

        return false;   // dead end (unconnected branch) or guard trip: nothing to do
    }
}
