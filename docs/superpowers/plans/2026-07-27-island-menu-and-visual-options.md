# Island Menu And Visual Options Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 同步灵动岛右键菜单的当前页签与设置功能，增加顶部吸附自动收缩开关，并把设置页七个文字复选框改成使用项目真实元素的迷你灵动岛。

**Architecture:** 在 `LifePreferences` 末尾追加兼容布尔字段；继续使用现有 `IslandQuickAction` 驱动菜单生成和页面路由。设置页保留七个原复选框名称与保存逻辑，只替换为共享视觉模板和实际灵动岛内容，避免引入新的状态模型。

**Tech Stack:** .NET 8、WPF XAML、C#、xUnit

---

### Task 1: 顶部自动收缩偏好兼容

**Files:**
- Modify: `src/ChronoIsle.App/LifeModels.cs`
- Modify: `tests/ChronoIsle.Tests/ThemePreferencesTests.cs`

- [ ] **Step 1: Write the failing legacy JSON test**

在 `LegacyPreferences_DefaultToEmeraldAndVisibleCollapsedControls` 中增加：

```csharp
Assert.True(preferences.IslandTopDockAutoFold);
```

- [ ] **Step 2: Run the test and verify RED**

Run:

```powershell
dotnet test tests/ChronoIsle.Tests/ChronoIsle.Tests.csproj --filter FullyQualifiedName~LegacyPreferences_DefaultToEmeraldAndVisibleCollapsedControls -m:1 -p:UseSharedCompilation=false -nr:false
```

Expected: compilation fails because `IslandTopDockAutoFold` does not exist.

- [ ] **Step 3: Append the compatible preference field**

将 `LifePreferences` 最后一个参数改为：

```csharp
bool IslandShowExpandIndicator = true,
bool IslandTopDockAutoFold = true)
```

- [ ] **Step 4: Re-run the targeted test**

Expected: PASS.

### Task 2: 同步右键菜单与自动收缩控制

**Files:**
- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs`
- Modify: `tests/ChronoIsle.UiTests/IslandContextMenuContractTests.cs`
- Modify: `tests/ChronoIsle.UiTests/TopDockAutoFoldContractTests.cs`

- [ ] **Step 1: Write failing menu contract tests**

验证 `IslandQuickAction` 包含：

```csharp
ViewToday, ViewCalendar, ViewStatus, ViewMedia, QuickAsk, ManageItems, Settings,
PauseReminders, ToggleDoNotDisturb, ToggleTopDockAutoFold
```

验证标签包含“今天、月历、状态、音乐、快问、设置、顶部吸附自动收缩”；验证五个页签动作都先 `Expand()` 再调用对应 `Show*Dashboard()`；验证 `MenuItem.Tag`、`IsCheckable` 和打开菜单时同步勾选状态。

- [ ] **Step 2: Write failing auto-fold guard tests**

验证：

```csharp
folded = folded && collapsedPreferences.IslandTopDockAutoFold
```

以及关闭开关时依次调用：

```csharp
preferences.Save(current with { IslandTopDockAutoFold = enabled });
topDockHoverExitTimer.Stop();
SetTopDockFolded(false);
```

- [ ] **Step 3: Run targeted UI tests and verify RED**

Run:

```powershell
dotnet test tests/ChronoIsle.UiTests/ChronoIsle.UiTests.csproj --filter "FullyQualifiedName~IslandContextMenuContractTests|FullyQualifiedName~TopDockAutoFoldContractTests" -m:1 -p:UseSharedCompilation=false -nr:false
```

Expected: FAIL because the actions, labels, checkable state and preference guard are missing.

- [ ] **Step 4: Implement the minimal menu actions**

扩展 `IslandQuickAction`，在 `CreateQuickActionMenu` 创建菜单项时保存 `Tag = action`，仅为 `ToggleDoNotDisturb` 与 `ToggleTopDockAutoFold` 设置 `IsCheckable = true`。在 `Header_ContextMenuOpening` 中从 `reminders` 与 `preferences.Load()` 刷新勾选状态。

页面动作使用：

```csharp
case IslandQuickAction.ViewCalendar: Expand(); ShowCalendarDashboard(); break;
case IslandQuickAction.ViewStatus: Expand(); ShowTelemetryDashboard(); break;
case IslandQuickAction.ViewMedia: Expand(); ShowMediaDashboard(); break;
case IslandQuickAction.QuickAsk:
    Expand();
    ShowQuickAskDashboard();
    Dispatcher.BeginInvoke(QuickAskInput.Focus);
    break;
case IslandQuickAction.Settings: OpenSettings(); break;
```

今天入口同样先 `Expand()` 再 `ShowTodayDashboard()`。

- [ ] **Step 5: Implement the top auto-fold toggle and final guard**

```csharp
void ToggleTopDockAutoFold()
{
    var current = preferences.Load();
    var enabled = !current.IslandTopDockAutoFold;
    preferences.Save(current with { IslandTopDockAutoFold = enabled });
    if (enabled) return;
    topDockHoverExitTimer.Stop();
    SetTopDockFolded(false);
}
```

`SetTopDockFolded` 的 `folded` 条件增加 `collapsedPreferences.IslandTopDockAutoFold`，使所有收缩入口共享保护；关闭仍允许恢复。

- [ ] **Step 6: Re-run targeted UI tests**

Expected: PASS.

### Task 3: 设置页迷你灵动岛视觉开关

**Files:**
- Modify: `src/ChronoIsle.App/Views/LifeSettingsWindow.xaml`
- Modify: `tests/ChronoIsle.UiTests/SettingsCustomizationContractTests.cs`

- [ ] **Step 1: Write the failing visual contract**

在“折叠态显示”区域范围内验证：

```csharp
Assert.Contains("x:Key=\"IslandVisualOption\"", xaml, StringComparison.Ordinal);
Assert.Contains("x:Name=\"CollapsedIslandOptions\"", xaml, StringComparison.Ordinal);
Assert.Contains("Assets/island-mascot.png", options, StringComparison.Ordinal);
Assert.Contains("M 3,8 L 7.5,13 L 10,3 L 15,8", options, StringComparison.Ordinal);
Assert.Contains("Background=\"{DynamicResource Brush.Island}\"", options, StringComparison.Ordinal);
Assert.DoesNotContain("Content=\"吉祥物\"", options, StringComparison.Ordinal);
Assert.DoesNotContain("Content=\"时间\"", options, StringComparison.Ordinal);
```

继续验证七个原有 `x:Name` 都存在，确保加载和保存逻辑不变。

- [ ] **Step 2: Run the settings contract and verify RED**

Run:

```powershell
dotnet test tests/ChronoIsle.UiTests/ChronoIsle.UiTests.csproj --filter FullyQualifiedName~SettingsCustomizationContractTests -m:1 -p:UseSharedCompilation=false -nr:false
```

Expected: FAIL because the shared template, mini island and real project visuals are missing.

- [ ] **Step 3: Add the shared visual checkbox style**

新增 `IslandVisualOption`，模板保持内容尺寸稳定；`IsChecked=True` 使用 `Brush.Accent` 和 `Brush.AccentSoft`，`IsChecked=False` 只降低内容不透明度，`IsMouseOver=True` 使用 `Brush.Hover`。

- [ ] **Step 4: Replace the text grid with a mini island**

使用 `CollapsedIslandOptions` 容器和一行七个 `CheckBox`。内容依次为项目吉祥物图片、事项状态点、“今天暂无安排”、网速示例、当前四节点网络矢量、时间和箭头。每项只使用 `ToolTip` 与 `AutomationProperties.Name` 保存文字语义，不设置可见文字标签。

- [ ] **Step 5: Re-run the settings contract**

Expected: PASS.

### Task 4: 完整验证、覆盖发布与启动

**Files:**
- No additional source changes expected.

- [ ] **Step 1: Run all tests**

```powershell
dotnet test tests/ChronoIsle.Tests/ChronoIsle.Tests.csproj -m:1 -p:UseSharedCompilation=false -nr:false
dotnet test tests/ChronoIsle.UiTests/ChronoIsle.UiTests.csproj -m:1 -p:UseSharedCompilation=false -nr:false
```

Expected: all tests pass.

- [ ] **Step 2: Check the diff**

```powershell
git diff --check
```

Expected: no whitespace errors.

- [ ] **Step 3: Overwrite the test build**

停止路径匹配的现有测试进程，然后运行：

```powershell
dotnet publish src/ChronoIsle.App/ChronoIsle.App.csproj -c Release -r win-x64 --self-contained true -m:1 -p:UseSharedCompilation=false -nr:false
```

- [ ] **Step 4: Restart and verify**

启动目标 `publish/ChronoIsle.exe`，验证进程路径、PID 和 `Responding = true`。
