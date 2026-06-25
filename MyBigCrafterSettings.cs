using System.Windows.Forms;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using ImGuiNET;
using Newtonsoft.Json;

namespace MyBigCrafter;

public class MyBigCrafterSettings : ISettings
{
    // Mandatory: enables/disables the plugin.
    public ToggleNode Enable { get; set; } = new ToggleNode(false);

#pragma warning disable CS0618
    [Menu("Start/Stop hotkey", "Starts the craft queue, or stops the current run.")]
    public HotkeyNode StartStopHotkey { get; set; } = Keys.End;
#pragma warning restore CS0618

    [JsonIgnore]
    public CustomNode ShortcutsHeader { get; set; } = new(() => ImGui.SeparatorText("Shortcuts"));

    [Menu("Alteration -> Augmentation shortcut", "With an Orb of Alteration selected, holding Alt selects an Orb of Augmentation until released.")]
    public ToggleNode AltAugShortcut { get; set; } = new(false);

    [Menu("Chance -> Scouring shortcut", "With an Orb of Chance selected, holding Alt selects an Orb of Scouring until released.")]
    public ToggleNode ChanceScourShortcut { get; set; } = new(false);

    [Menu("Alchemy -> Scouring shortcut", "With an Orb of Alchemy selected, holding Alt selects an Orb of Scouring until released.")]
    public ToggleNode AlchScourShortcut { get; set; } = new(false);
}
