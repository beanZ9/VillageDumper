using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using ImGuiNET;
using ItemFilterLibrary;
using SharpDX;
using RectangleF = SharpDX.RectangleF;
using Vector2 = System.Numerics.Vector2;

namespace VillageDumper;

public class VillageDumper : BaseSettingsPlugin<Settings> {
    private bool _initialized;
    private VillageRewardWindow _rewardWindow;
    private Element _mapRunnerWindow;
    private SyncTask<bool> _currentOperation;
    private string _customFilter = string.Empty;
    private string _previousQuery = string.Empty;
    private string _newFilterDisplayName = string.Empty;
    private string _newFilterQuery = string.Empty;
    private string _editFilterQuery = string.Empty;
    private int? _editFilterIndex;
    private bool _testCustomQuery = true;

    private Dictionary<string, QueryOrException> _cachedQueries;
    private record QueryOrException(ItemQuery Query, Exception Exception);
    private bool MoveCancellationRequested => Settings.CancelWithRightMouseButton && (Control.MouseButtons & MouseButtons.Right) != 0;
    private IngameState InGameState => GameController.IngameState;
    private Vector2 WindowOffset => GameController.Window.GetWindowRectangleTimeCache.TopLeft.ToVector2Num();

    public override bool Initialise() {
        Graphics.InitImage(Path.Combine(DirectoryFullName, "images\\pick.png").Replace('\\', '/'), false);

        if (Settings.SavedFilters == null || Settings.SavedFilters.Count == 0) {
            Settings.SavedFilters = CreateDefaultFilters();
        }

        _cachedQueries = new Dictionary<string, QueryOrException>();

        _rewardWindow = InGameState.IngameUi.VillageRewardWindow;
        _initialized = true;
        return true;
    }

    private List<Filter> CreateDefaultFilters() {
        return new() {
            new Filter {
                DisplayName = "Everything", Query = "True", Include = true, CanBeExcluded = false, CanBeEdited = false
            },
            new Filter { DisplayName = "Currency", Query = "ClassName.Contains(\"Currency\")", Include = true },
            new Filter { DisplayName = "Fragments", Query = "ClassName.Equals(\"MapFragment\")", Include = true },
            new Filter { DisplayName = "Maps", Query = "ClassName.Equals(\"Map\")", Include = true },
            new Filter {
                DisplayName = "Divination Cards", Query = "ClassName.Equals(\"DivinationCard\")", Include = true
            },
            new Filter {
                DisplayName = "Gear",
                Query =
                    "HasTag(\"armour\") || HasTag(\"weapon\") || HasTag(\"quiver\") || HasTag(\"belt\") || HasTag(\"amulet\") || HasTag(\"ring\")",
                Exclude = true
            },
            new Filter {
                DisplayName = "High Value Uniques",
                Query =
                    "Name.Contains(\"Mageblood\") ||\nName.Contains(\"Nimis\") ||\nName.Contains(\"Headhunter\") ||\nName.Contains(\"Voidforge\") ||\nName.Contains(\"Squire\") ||\nName.Contains(\"Kalandra\")",
                Include = true
            },
            new Filter {
                DisplayName = "Jewels", Query = "HasTag(\"jewel\") || HasTag(\"abyss_jewel\")", Exclude = true
            }
        };
    }

    public override void AreaChange(AreaInstance area) {
        _mouseStateForRect.Clear();
        _rewardWindow = InGameState.IngameUi.VillageRewardWindow;
        _mapRunnerWindow = InGameState.IngameUi[125];
    }

    void CheckboxWithAction(string label, Func<bool> getter, Action<bool> setter) {
        bool currentValue = getter();
        ImGui.PushID(label);
        if (ImGui.Checkbox(label, ref currentValue)) {
            setter(currentValue);
        }
        ImGui.PopID();
    }

    private void DrawFilterUI(string windowTitle, ref string filterText, Vector2 defaultPosition) {
        if (!Settings.ShowFilterPanel) return;
        ImGui.SetNextWindowPos(new Vector2(defaultPosition.X, defaultPosition.Y), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(windowTitle, ImGuiWindowFlags.AlwaysAutoResize)) {
            CheckboxWithAction("Respect 'Highlight Items'", () => Settings.TakeOnlyFiltered, value => Settings.TakeOnlyFiltered = value);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Only take items that match your in-game search. Has no effect when search is empty.");
            if (Settings.SavedFilters.Any() && (Settings.OpenSavedFilterList = ImGui.TreeNodeEx("Manage IFL Filters",
                        Settings.OpenSavedFilterList
                            ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None))) {
                foreach (var (savedFilter, index) in Settings.SavedFilters.Select((x, i) => (x, i)).ToList()) {
                    ImGui.PushID($"saved{index}");
                    ImGui.Button("≡");
                    if (ImGui.IsItemHovered()) {
                        ImGui.SetTooltip("Drag to reorder");
                    }
                    if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.None)) {
                        ImGuiHelpers.SetDragDropPayload("FILTER_ITEM", index);
                        ImGui.EndDragDropSource();
                    }
                    if (ImGui.BeginDragDropTarget()) {
                        var payload = ImGuiHelpers.AcceptDragDropPayload<int>("FILTER_ITEM");

                        if (payload.HasValue) {
                            int srcIndex = payload.Value;
                            if (srcIndex >= 0 && srcIndex < Settings.SavedFilters.Count && srcIndex != index) {
                                var item = Settings.SavedFilters[srcIndex];
                                Settings.SavedFilters.RemoveAt(srcIndex);
                                int insertIndex = index;
                                if (insertIndex > srcIndex) insertIndex--;
                                Settings.SavedFilters.Insert(insertIndex, item);
                            }
                        }

                        ImGui.EndDragDropTarget();
                    }

                    ImGui.SameLine();
                    bool includeChecked = savedFilter.Include;
                    if (ImGui.Checkbox("##included", ref includeChecked)) {
                        savedFilter.Include = includeChecked;
                        if (includeChecked) savedFilter.Exclude = false;
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Include");

                    ImGui.SameLine();
                    bool excludeChecked = savedFilter.Exclude;
                    if (ImGui.Checkbox("##excluded", ref excludeChecked)) {
                        if (savedFilter.CanBeExcluded) {
                            savedFilter.Exclude = excludeChecked;
                        } else {
                            excludeChecked = false;
                        }
                        if (excludeChecked) savedFilter.Include = false;
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(savedFilter.CanBeExcluded ? "Exclude" : "Can't be excluded!");

                    ImGui.SameLine();
                    ImGui.TextUnformatted(savedFilter.DisplayName);

                    if (savedFilter.CanBeEdited) {
                        ImGui.SameLine();
                        if (ImGui.Button("Edit")) {
                            ImGui.OpenPopup($"edit_query-{index}");
                            _editFilterIndex = index;
                            _editFilterQuery = savedFilter.Query;
                            _newFilterDisplayName = savedFilter.DisplayName;
                        }
                        if (ImGui.BeginPopup($"edit_query-{index}")) {
                            ImGui.InputTextWithHint("Name", "Enter display name", ref _newFilterDisplayName, 64);
                            ImGui.InputTextMultiline("IFL Query", ref _editFilterQuery, 10000, new Vector2(320, 128));
                            if (_testCustomQuery) {
                                if (string.IsNullOrEmpty(filterText)) {
                                    _previousQuery = filterText;
                                }
                                filterText = _editFilterQuery;
                            }
                            if (ImGui.Button("Save")) {
                                savedFilter.Query = _editFilterQuery;
                                savedFilter.DisplayName = _newFilterDisplayName;
                                ImGui.CloseCurrentPopup();
                            }
                            ImGui.EndPopup();
                        } else if (_editFilterIndex.HasValue && _editFilterIndex == index) {
                            _editFilterIndex = null;
                            _editFilterQuery = string.Empty;
                            _newFilterDisplayName = string.Empty;
                            filterText = _previousQuery;
                            _previousQuery = string.Empty;
                        }

                        ImGui.SameLine();
                        if (ImGui.Button("Delete")) {
                            if (ImGui.IsKeyDown(ImGuiKey.ModShift)) {
                                Settings.SavedFilters.Remove(savedFilter);
                            }
                        } else if (ImGui.IsItemHovered()) {
                            ImGui.SetTooltip("Hold Shift");
                        }
                    }
                    ImGui.PopID();
                }
                ImGui.TreePop();
            }

            if (ImGui.TreeNodeEx("Add new IFL Filter")) {
                
                ImGui.InputTextMultiline("IFL Query", ref _newFilterQuery, 2000, new Vector2(320, 128));

                if (!string.IsNullOrWhiteSpace(_newFilterQuery)) {
                    if (!Settings.SavedFilters.Any(x => x.Query.Equals(_newFilterQuery))) {
                        if (ImGui.Button("Add")) {
                            ImGui.OpenPopup("set_display_name");
                        }
                        if (ImGui.BeginPopupContextItem("set_display_name")) {
                            ImGui.InputTextWithHint("##display_name", "Enter display name", ref _newFilterDisplayName, 64);
                            ImGui.SameLine();
                            if (ImGui.Button("Save")) {
                                Settings.SavedFilters.Add(new Filter { Include = true, DisplayName = _newFilterDisplayName, Query = _newFilterQuery });
                                ImGui.CloseCurrentPopup();
                                _newFilterQuery = string.Empty;
                                _newFilterDisplayName = string.Empty;
                                filterText = string.Empty;
                            }
                            ImGui.EndPopup();
                        }
                    }

                    ImGui.SameLine();
                    if (ImGui.Button("Clear")) {
                        _newFilterQuery = string.Empty;
                        filterText = string.Empty;
                        return;
                    }
                }
                    
                ImGui.Checkbox("Test query", ref _testCustomQuery);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Visualize IFL query during editing by highlighting matching items.");
                if (_testCustomQuery && string.IsNullOrEmpty(_editFilterQuery)) {
                    filterText = _newFilterQuery;
                }

                ImGui.TreePop();
            }

            ImGui.Separator();

            if (ImGui.TreeNodeEx("Advanced Options")) {
                CheckboxWithAction("Auto discard", () => Settings.AutoDiscardRemaining, value => Settings.AutoDiscardRemaining = value);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("(Careful) Automatically discard remaining items.");
                if (Settings.AutoDiscardRemaining) {
                    CheckboxWithAction("Auto open next", () => Settings.AutoOpenNext, value => Settings.AutoOpenNext = value);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Automatically open next set of rewards if available.");
                }
                if (Settings.AutoDiscardRemaining && Settings.AutoOpenNext) {
                    CheckboxWithAction("Auto start on next", () => Settings.KeepDumping, value => Settings.KeepDumping = value);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("(Careful) Automatically start dumping on the next set of rewards.\nKeeps dumping items until inventory full or aborted.");
                }
                ImGui.TreePop();
            }

            ImGui.End();
        }
    }


    public override void Render() {
        if (!_initialized) Initialise();
        if (_currentOperation != null) {
            DebugWindow.LogMsg("Running the rewards dump procedure...");
            TaskUtils.RunOrRestart(ref _currentOperation, () => null);
        }

        if (!Settings.Enable)
            return;

        // Render on mapping device map queue 
        if (_mapRunnerWindow.IsVisible && (Settings.HighlightGoodRunnerMaps || Settings.HighlightBadRunnerMaps)) {
            var mapQueue = _mapRunnerWindow[4];

            if (mapQueue is { IsValid: true, ChildCount: > 2 and <= 14 }) {
                for (int i = 2; i < mapQueue.ChildCount; i++) {
                    var mapTooltip = mapQueue[i].Tooltip;
                    if (mapTooltip?.Children == null || mapTooltip.ChildCount < 2) {
                        DebugWindow.LogMsg("Can't read map completion prediction from tooltip. Hover each map to load.");
                        continue;
                    }
                    var expectationText = string.Empty;
                    try {
                        expectationText = mapTooltip.Children[1].Text;
                    } catch (Exception ex) {
                        DebugWindow.LogMsg("Can't read map completion prediction from tooltip. Hover each map to load.");
                    }
                    if (string.IsNullOrEmpty(expectationText)) continue;

                    string pattern = @"^Map\sCompletion\:\s\d+\%";
                    Match match = Regex.Match(expectationText, pattern);
                    if (match.Success) {
                        // map is completed
                        continue;
                    }

                    pattern = @"(\d+)%\schance\sfor\san\sAtlas\sRunner\sto\sbe\spermanently\skilled\sin\sthis\sMap";
                    match = Regex.Match(expectationText, pattern);
                    if (match.Success) {
                        int deathChance = int.Parse(match.Groups[1].Value);
                        //DebugWindow.LogMsg($"Map runner death chance for map at index {i}: " + deathChance);
                        var mapRect = mapQueue[i].GetClientRectCache;
                        mapRect.Inflate(-1, -1);
                        if (deathChance == 1) {
                            Graphics.DrawFrame(mapRect, Settings.HighlightGoodRunnerMapsColor, Settings.FilterFrameThickness);
                        } else if (deathChance > 1) {
                            Graphics.DrawFrame(mapRect, Settings.HighlightBadRunnerMapsColor, Settings.FilterFrameThickness);
                        }
                    }
                }
            }
        }

        // Render on village reward screen (mapping device and boat)
        if (_rewardWindow.IsVisible && _rewardWindow.TabContainer != null && _rewardWindow.TabContainer.ChildCount > 1) {
            var rewardsRect = _rewardWindow.GetClientRectCache;
            var windowPos = new Vector2(rewardsRect.Right, rewardsRect.Top);

            // Filter UI
            DrawFilterUI("Rewards Filter", ref _customFilter, windowPos);

            // Draw highlights
            List<NormalInventoryItem> highlightedItems;
            try {
                highlightedItems = GetHighlightedItems(_rewardWindow.TabContainer.VisibleStash.VisibleInventoryItems);
            } catch (Exception ex) {
                DebugWindow.LogMsg($"Failed getting items from reward window: {ex}");
                return;
            }
            int? stackSizes = 0;
            foreach (var item in highlightedItems) {
                stackSizes += item.Item?.GetComponent<Stack>()?.Size;
                var rect = item.GetClientRect();
                var deflateFactor = Settings.FilterBorderDeflation / 200.0;
                var deflateWidth = (int)(rect.Width * deflateFactor + Settings.FilterFrameThickness / 2);
                var deflateHeight = (int)(rect.Height * deflateFactor + Settings.FilterFrameThickness / 2);
                rect.Inflate(-deflateWidth, -deflateHeight);
                var topLeft = rect.TopLeft;
                var bottomRight = rect.BottomRight;
                if (string.IsNullOrEmpty(_customFilter)) {
                    Graphics.DrawFrame(topLeft.ToVector2Num(), bottomRight.ToVector2Num(), Settings.FilterFrameColor, Settings.FilterBorderRounding, Settings.FilterFrameThickness, 0);
                } else {
                    Graphics.DrawFrame(topLeft.ToVector2Num(), bottomRight.ToVector2Num(), Settings.TestQueryFrameColor, Settings.FilterBorderRounding, Settings.FilterFrameThickness, 0);
                }
            }

            if (Settings.DumpButtonEnable) {
                var buttonSize = 38;
                var buttonPos = Settings.UseCustomDumpButtonPosition
                    ? new Vector2(Settings.CustomDumpButtonPosition.Value.X, Settings.CustomDumpButtonPosition.Value.Y)
                    : rewardsRect.BottomRight.ToVector2Num() + new Vector2(-95, -120);
                var buttonRect = new RectangleF(buttonPos.X, buttonPos.Y, buttonSize, buttonSize);

                Graphics.DrawImage("pick.png", buttonRect);

                if (IsButtonPressed(buttonRect)) {
                    _currentOperation = CollectRewards();
                }

                var countText = Settings.ShowStackSizes && highlightedItems.Count != stackSizes && stackSizes != null
                    ? Settings.ShowStackCountWithSize
                        ? $"{stackSizes} / {highlightedItems.Count}"
                        : $"{stackSizes}"
                    : $"{highlightedItems.Count}";

                var countPos = new Vector2(buttonRect.Left - 2, buttonRect.Y + buttonRect.Height / 2 - 11);
                Graphics.DrawText($"{countText}", new Vector2(countPos.X, countPos.Y + 2), Color.Black, FontAlign.Right);
                Graphics.DrawText($"{countText}", new Vector2(countPos.X - 2, countPos.Y), Color.White, FontAlign.Right);

            }
            if (Settings.MoveToInventoryHotkey.IsPressed()) {
                _currentOperation = CollectRewards();
            }
        } else {
            if (Settings.ResetCustomFilterOnPanelClose) {
                _customFilter = "";
            }
        }
    }

    private async SyncTask<bool> MoveItemsCommonPreamble() {
        while (Control.MouseButtons == MouseButtons.Left || MoveCancellationRequested) {
            if (MoveCancellationRequested) {
                return false;
            }

            await TaskUtils.NextFrame();
        }

        if (Settings.IdleMouseDelay.Value == 0) {
            return true;
        }

        var mousePos = Input.MousePositionNum;
        var sw = Stopwatch.StartNew();
        await TaskUtils.NextFrame();
        while (true) {
            if (MoveCancellationRequested) {
                return false;
            }

            var newPos = Input.MousePositionNum;
            if (mousePos != newPos) {
                mousePos = newPos;
                sw.Restart();
            } else if (sw.ElapsedMilliseconds >= Settings.IdleMouseDelay.Value) {
                return true;
            } else {
                await TaskUtils.NextFrame();
            }
        }
    }

    private async SyncTask<bool> CollectRewards(bool recursive = false) {
        var prevMousePos = Input.MousePositionNum;

        foreach (var tabInventory in _rewardWindow.TabContainer.Inventories) {
            if (!await MoveItemsCommonPreamble()) {
                return false;
            }

            var tab = tabInventory.Inventory;
            if (tab.Address != _rewardWindow.TabContainer.VisibleStash.Address) {
                await ClickButton(tabInventory.TabButton.GetClientRectCache.Center.ToVector2Num());
            }
            var items = GetHighlightedItems(tabInventory.Inventory.VisibleInventoryItems)
                .OrderBy(item => item.X)
                .ThenBy(item => item.Y)
                .Select(item => item.GetClientRectCache)
                .ToList();
            Input.KeyDown(Keys.LControlKey);
            await Wait(KeyDelay, true);
            for (var i = 0; i < items.Count; i++) {
                var item = items[i];
                if (MoveCancellationRequested) {
                    DebugWindow.LogMsg("Village Dumper: Dumping procedure interrupted manually");
                    Input.KeyUp(Keys.LControlKey);
                    return false;
                }

                if (!_rewardWindow.IsVisible) {
                    DebugWindow.LogMsg("Village Dumper: Rewards window closed, aborting loop");
                    break;
                }

                if (!InGameState.IngameUi.InventoryPanel.IsVisible) {
                    DebugWindow.LogMsg("Village Dumper: Inventory Panel closed, aborting loop");
                    break;
                }

                if (IsInventoryFull()) {
                    DebugWindow.LogMsg("Village Dumper: Inventory full, aborting loop");
                    break;
                }

                await MoveItem(item.Center.ToVector2Num());
            }

            Input.KeyUp(Keys.LControlKey);
            await Wait(KeyDelay, false);
        }

        await Task.Delay(Settings.IdleMouseDelay);

        int remainingItemsCount = 0;
        foreach (var tabInventory in _rewardWindow.TabContainer.Inventories) {
            remainingItemsCount += GetHighlightedItems(tabInventory.Inventory.ServerInventory.InventorySlotItems).Count();
        }

        if (!_rewardWindow.IsVisible || remainingItemsCount > 0) {
            DebugWindow.LogMsg("Village Dumper: There are still matching items left, aborting loop");
            Input.SetCursorPos(prevMousePos);
            return true;
        }

        if (Settings.AutoDiscardRemaining) {
            if (MoveCancellationRequested) return false;
            await ClickButton(_rewardWindow[6].GetClientRectCache.Center.ToVector2Num());
            if (MoveCancellationRequested) return false;
            await ClickButton(InGameState.IngameUi.PopUpWindow[0][0][3][0].GetClientRectCache.Center.ToVector2Num());

            if (Settings.AutoOpenNext) {
                if (MoveCancellationRequested) return false;
                var queue = _mapRunnerWindow[4];
                if (queue.IsVisible && queue.ChildCount > 2) {
                    var firstMap = queue.Children.Where(c => c.Entity.IsValid && c.Entity.Type == EntityType.Item).MinBy(c => c.GetClientRectCache.Left);
                    var firstMapRect = firstMap.GetClientRectCache.Center.ToVector2Num();
                    await ClickButton(firstMapRect);
                    if (GameController.IngameState.ServerData.PlayerInventories.FirstOrDefault(x => x.TypeId == InventoryNameE.Cursor1)?.Inventory.ItemCount > 0) {
                        await ClickButton(firstMapRect);
                        return false;
                    }
                    if (Settings.KeepDumping) {
                        if (MoveCancellationRequested) return false;
                        await CollectRewards(true);
                    }
                }
            }
        }

        if (!recursive) {
            Input.SetCursorPos(prevMousePos);
            await Wait(MouseMoveDelay, false);
        }

        return true;
    }

    private List<NormalInventoryItem> GetHighlightedItems(IList<NormalInventoryItem> rewards) {
        try {
            var filteredRewards = new List<NormalInventoryItem>();

            if (!string.IsNullOrEmpty(_customFilter)) {
                var queryObj = GetQuery(_customFilter);
                filteredRewards = rewards
                    .Where(item => queryObj.Query.CompiledQuery(new ItemData(item.Item, GameController)))
                    .ToList();
            } else {
                foreach (var filterEntry in Settings.SavedFilters.Where(x => x.Include || x.Exclude)) {
                    var queryObj = GetQuery(filterEntry.Query);
                    if (filterEntry.Include) {
                        var tmp = rewards
                            .Where(item => !filteredRewards.Contains(item))
                            .Where(item => queryObj.Query.CompiledQuery(new ItemData(item.Item, GameController)))
                            .ToList();
                        filteredRewards.AddRange(tmp);
                    } else if (filterEntry.Exclude) {
                        filteredRewards.RemoveAll(item => queryObj.Query.CompiledQuery(new ItemData(item.Item, GameController)));
                    }
                }
                if (Settings.TakeOnlyFiltered) {
                    filteredRewards.RemoveAll(item => !item.IsSaturated);
                }
            }
            return filteredRewards;
        } catch {
            return [];
        }
    }

    private List<ServerInventory.InventSlotItem> GetHighlightedItems(IList<ServerInventory.InventSlotItem> rewards) {
        try {
            var filteredRewards = new List<ServerInventory.InventSlotItem>();

            if (!string.IsNullOrEmpty(_customFilter)) {
                var queryObj = GetQuery(_customFilter);
                filteredRewards = rewards
                    .Where(item => queryObj.Query.CompiledQuery(new ItemData(item.Item, GameController)))
                    .ToList();
            } else {
                foreach (var filterEntry in Settings.SavedFilters.Where(x => x.Include || x.Exclude)) {
                    var queryObj = GetQuery(filterEntry.Query);
                    if (filterEntry.Include) {
                        var tmp = rewards
                            .Where(item => !filteredRewards.Contains(item))
                            .Where(item => queryObj.Query.CompiledQuery(new ItemData(item.Item, GameController)))
                            .ToList();
                        filteredRewards.AddRange(tmp);
                    } else if (filterEntry.Exclude) {
                        filteredRewards.RemoveAll(item => queryObj.Query.CompiledQuery(new ItemData(item.Item, GameController)));
                    }
                }
            }
            return filteredRewards;
        } catch {
            return [];
        }
    }

    private QueryOrException GetQuery(string query) {
        if (_cachedQueries.TryGetValue(query, out var queryObj)) {
            return queryObj;
        }

        var newQueryObj = new QueryOrException(ItemQuery.Load(query), null);
        _cachedQueries.Add(query, newQueryObj);
        return newQueryObj;
    }

    private bool IsInventoryFull() {
        var inventoryItems = GameController.IngameState.ServerData.PlayerInventories[0].Inventory.InventorySlotItems;

        // quick sanity check
        if (inventoryItems.Count < 12) {
            return false;
        }

        // track each inventory slot
        bool[,] inventorySlot = new bool[12, 5];

        // iterate through each item in the inventory and mark used slots
        foreach (var inventoryItem in inventoryItems) {
            int x = inventoryItem.PosX;
            int y = inventoryItem.PosY;
            int height = inventoryItem.SizeY;
            int width = inventoryItem.SizeX;
            for (int row = x; row < x + width; row++) {
                for (int col = y; col < y + height; col++) {
                    inventorySlot[row, col] = true;
                }
            }
        }

        // check for any empty slots
        for (int x = 0; x < 12; x++) {
            for (int y = 0; y < 5; y++) {
                if (!inventorySlot[x, y]) {
                    return false;
                }
            }
        }

        // no empty slots, so inventory is full
        return true;
    }

    private static readonly TimeSpan KeyDelay = TimeSpan.FromMilliseconds(10);
    private TimeSpan MouseMoveDelay => TimeSpan.FromMilliseconds(20);
    private TimeSpan MouseDownDelay => TimeSpan.FromMilliseconds(25 + Settings.ExtraDelay.Value + Settings.ExtraRandomDelay.Value);

    private async SyncTask<bool> MoveItem(Vector2 itemPosition) {
        var r = Random.Shared.NextDouble();
        var offsetX = Settings.RandomClickOffsetX.Value;
        var offsetY = Settings.RandomClickOffsetY.Value;
        itemPosition += WindowOffset;
        itemPosition += new Vector2(-offsetX+ (int)(r * offsetX * 2), -offsetY + (int)(r * offsetY * 2));
        Input.SetCursorPos(itemPosition);
        await Wait(MouseMoveDelay, true);
        Input.Click(MouseButtons.Left);
        await Wait(MouseDownDelay, true);
        return true;
    }

    private async SyncTask<bool> ClickButton(Vector2 position) {
        var r = Random.Shared.NextDouble();
        var offsetX = Settings.RandomClickOffsetX.Value;
        var offsetY = Settings.RandomClickOffsetY.Value;
        position += WindowOffset;
        position += new Vector2(-offsetX + (int)(r * offsetX * 2), -offsetY + (int)(r * offsetY * 2));
        Input.SetCursorPos(position);
        await Wait(MouseMoveDelay * 5, false);
        if (MoveCancellationRequested) {
            return false;
        }
        Input.Click(MouseButtons.Left);
        await Wait(MouseDownDelay * 5, false);
        return true;
    }

    private async SyncTask<bool> Wait(TimeSpan period, bool canUseThreadSleep) {
        if (canUseThreadSleep && Settings.UseThreadSleep) {
            Thread.Sleep(period);
            return true;
        }

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < period) {
            await TaskUtils.NextFrame();
        }

        return true;
    }

    private readonly ConcurrentDictionary<RectangleF, bool?> _mouseStateForRect = [];

    private bool IsButtonPressed(RectangleF buttonRect) {
        var prevState = _mouseStateForRect.GetValueOrDefault(buttonRect);
        var isHovered = buttonRect.Contains(Input.MousePositionNum.X - WindowOffset.X, Input.MousePositionNum.Y - WindowOffset.Y);
        if (!isHovered) {
            _mouseStateForRect[buttonRect] = null;
            return false;
        }

        var isPressed = Control.MouseButtons == MouseButtons.Left && CanClickButtons;
        _mouseStateForRect[buttonRect] = isPressed;
        return isPressed &&
               prevState == false;
    }

    private bool CanClickButtons => !Settings.VerifyButtonIsNotObstructed || !ImGui.GetIO().WantCaptureMouse;
}