using System.Collections.Generic;
using System.Windows.Forms;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using Vector2 = System.Numerics.Vector2;
using Color = SharpDX.Color;

namespace VillageDumper;

public class Settings : ISettings {
    public bool[,] IgnoredCells { get; set; } = new bool[5, 12];

    public ToggleNode Enable { get; set; } = new(true);

    [Menu("Enable Dump Button")]
    public ToggleNode DumpButtonEnable { get; set; } = new(true);

    public ToggleNode ShowStackSizes { get; set; } = new(true);

    [Menu("Show Stack Count Next to Stack Size")]
    public ToggleNode ShowStackCountWithSize { get; set; } = new(true);

    public HotkeyNodeV2 MoveToInventoryHotkey { get; set; } = new(Keys.None);
    public ToggleNode ShowFilterPanel { get; set; } = new(true);
    public ToggleNode ResetCustomFilterOnPanelClose { get; set; } = new(true);
    public ColorNode FilterFrameColor { get; set; } = new(Color.Green);
    public ColorNode TestQueryFrameColor { get; set; } = new(Color.Violet);
    public RangeNode<int> FilterFrameThickness { get; set; } = new(2, 1, 20);
    public RangeNode<float> FilterBorderRounding { get; set; } = new(0, 0, 25);
    public RangeNode<int> FilterBorderDeflation { get; set; } = new(8, 0, 100);
    public RangeNode<int> ExtraDelay { get; set; } = new(20, 0, 100);
    public RangeNode<int> ExtraRandomDelay { get; set; } = new(5, 0, 100);
    [Menu("Random Click Offset X", "Offset clicks by a random number of pixels between 0 and this value.\nDepending on resolution large values may cause misclicks.")]
    public RangeNode<int> RandomClickOffsetX { get; set; } = new(10, 0, 30);
    [Menu("Random Click Offset Y", "Offset clicks by a random number of pixels between 0 and this value.\nDepending on resolution large values may cause misclicks.")]
    public RangeNode<int> RandomClickOffsetY { get; set; } = new(20, 0, 30);
    [Menu("Use Thread.Sleep", "Is a little faster, but HUD will hang while clicking")]
    public ToggleNode UseThreadSleep { get; set; } = new(false);

    [Menu("Idle mouse delay", "Wait this long after the user lets go of the button and stops moving the mouse")]
    public RangeNode<int> IdleMouseDelay { get; set; } = new(200, 0, 1000);

    public ToggleNode CancelWithRightMouseButton { get; set; } = new(true);

    public ToggleNode VerifyButtonIsNotObstructed { get; set; } = new(true);

    public ToggleNode UseCustomDumpButtonPosition { get; set; } = new(false);
    public RangeNode<Vector2> CustomDumpButtonPosition { get; set; } = new(Vector2.Zero, Vector2.Zero, Vector2.One * 6000);
    public ToggleNode HighlightGoodRunnerMaps { get; set; } = new(true);
    public ColorNode HighlightGoodRunnerMapsColor { get; set; } = new(Color.Green);
    public ToggleNode HighlightBadRunnerMaps { get; set; } = new(true);
    public ColorNode HighlightBadRunnerMapsColor { get; set; } = new(Color.Red);

    [IgnoreMenu]
    public List<Filter> SavedFilters { get; set; } = [];
    [IgnoreMenu]
    public bool OpenSavedFilterList { get; set; } = true;
    [IgnoreMenu]
    public bool OpenIncludedFilterList { get; set; } = true;
    [IgnoreMenu]
    public bool OpenExcludedFilterList { get; set; } = true;

    [IgnoreMenu]
    public bool AutoDiscardRemaining { get; set; } = false;
    [IgnoreMenu]
    public bool AutoOpenNext { get; set; } = false;
    [IgnoreMenu]
    public bool KeepDumping { get; set; } = false;

    [IgnoreMenu]
    public bool TakeOnlyFiltered { get; set; } = false;


}