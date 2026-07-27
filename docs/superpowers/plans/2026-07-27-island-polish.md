# 灵动岛视觉与交互稳定性 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 区分网络与事项状态图标，消除顶部吸附悬停闪烁和摘要轮播宽度跳动，并把设置按钮移动到展开态右上角。

**Architecture:** 视觉调整集中在 `LifeIslandWindow.xaml`，状态画刷和顶部悬停状态机继续由 `LifeIslandWindow.xaml.cs` 管理。轮播稳定宽度只在事项和网速同时启用时生效；顶部折叠增加独立短延迟定时器，不复用五秒内容收起定时器。

**Tech Stack:** C# 12、.NET 8、WPF XAML、xUnit、PowerShell、`dotnet test`、`dotnet publish`

---

## 文件结构

- 修改 `src/ChronoIsle.App/Views/LifeIslandWindow.xaml`：网络矢量图和展开态设置按钮布局。
- 修改 `src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs`：网络图标画刷、轮播固定宽度、顶部悬停离开防抖和快捷操作列表。
- 修改 `tests/ChronoIsle.UiTests/SystemStatusUiContractTests.cs`：网络矢量图契约。
- 修改 `tests/ChronoIsle.UiTests/CollapsedIslandCustomizationContractTests.cs`：固定轮播宽度契约。
- 修改 `tests/ChronoIsle.UiTests/TopDockAutoFoldContractTests.cs`：顶部悬停防抖契约。
- 创建 `tests/ChronoIsle.UiTests/IslandSettingsPlacementContractTests.cs`：设置按钮位置契约。

### Task 1: 网络状态矢量图

**Files:**
- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml:233-236`
- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs:1714-1728`
- Test: `tests/ChronoIsle.UiTests/SystemStatusUiContractTests.cs`

- [ ] **Step 1: 写入失败的 UI 契约测试**

在 `Island_ProvidesSystemStatusDashboardAndCollapsedNetworkState` 中增加：

```csharp
Assert.Contains("x:Name=\"NetworkStatusGlyph\"", xaml, StringComparison.Ordinal);
Assert.Contains("Data=\"M 1.5,2.5 L 6,8 L 10.5,2.5\"", xaml, StringComparison.Ordinal);
Assert.Contains("Width=\"12\" Height=\"10\"", xaml, StringComparison.Ordinal);
Assert.Contains("NetworkStatusGlyph.Stroke = statusBrush;", source, StringComparison.Ordinal);
Assert.DoesNotContain(
    "<Ellipse x:Name=\"NetworkStatusLight\" Width=\"6\" Height=\"6\"",
    xaml,
    StringComparison.Ordinal);
```

- [ ] **Step 2: 运行测试并确认按预期失败**

Run:

```powershell
$env:DOTNET_CLI_USE_MSBUILD_SERVER='0'
dotnet test tests\ChronoIsle.UiTests\ChronoIsle.UiTests.csproj --no-restore --nologo -m:1 -p:UseSharedCompilation=false -nr:false --filter "FullyQualifiedName~SystemStatusUiContractTests.Island_ProvidesSystemStatusDashboardAndCollapsedNetworkState"
```

Expected: FAIL，缺少 `NetworkStatusGlyph`。

- [ ] **Step 3: 实现三节点网络矢量图**

将原网络圆点替换为：

```xml
<Grid x:Name="NetworkStatusLight" Width="12" Height="10"
      Margin="0,0,8,0" VerticalAlignment="Center" ToolTip="网络状态">
  <Canvas Width="12" Height="10">
    <Path x:Name="NetworkStatusGlyph"
          Data="M 1.5,2.5 L 6,8 L 10.5,2.5"
          Stroke="{DynamicResource Brush.Success}"
          StrokeThickness="1.4"
          StrokeStartLineCap="Round"
          StrokeEndLineCap="Round"
          StrokeLineJoin="Round"/>
    <Ellipse Width="3" Height="3" Canvas.Left="0" Canvas.Top="1"
             Fill="{Binding Stroke, ElementName=NetworkStatusGlyph}"/>
    <Ellipse Width="3" Height="3" Canvas.Left="4.5" Canvas.Top="6.5"
             Fill="{Binding Stroke, ElementName=NetworkStatusGlyph}"/>
    <Ellipse Width="3" Height="3" Canvas.Left="9" Canvas.Top="1"
             Fill="{Binding Stroke, ElementName=NetworkStatusGlyph}"/>
  </Canvas>
</Grid>
```

将网络状态更新改为：

```csharp
NetworkStatusGlyph.Stroke = statusBrush;
NetworkStatusLight.ToolTip = snapshot.LatencyMilliseconds is { } latency
    ? $"{label} · {latency} ms"
    : label;
```

- [ ] **Step 4: 运行定向测试并确认通过**

Run Task 1 Step 2 的命令。

Expected: PASS。

- [ ] **Step 5: 提交网络图标改动**

```powershell
git add src/ChronoIsle.App/Views/LifeIslandWindow.xaml src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs tests/ChronoIsle.UiTests/SystemStatusUiContractTests.cs
git commit -m "feat: 区分灵动岛网络状态图标"
```

### Task 2: 稳定轮播摘要宽度

**Files:**
- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs:714-735`
- Test: `tests/ChronoIsle.UiTests/CollapsedIslandCustomizationContractTests.cs`

- [ ] **Step 1: 写入失败的固定宽度契约测试**

在 `CollapsedIsland_UsesFiveSecondSummaryRotationAndContentWidthBounds` 中增加：

```csharp
Assert.Contains("const double RotatingSummaryWidth = 160;", source, StringComparison.Ordinal);
Assert.Contains(
    "Summary.Width = rotatingSummary ? RotatingSummaryWidth : double.NaN;",
    source,
    StringComparison.Ordinal);
```

- [ ] **Step 2: 运行测试并确认按预期失败**

Run:

```powershell
$env:DOTNET_CLI_USE_MSBUILD_SERVER='0'
dotnet test tests\ChronoIsle.UiTests\ChronoIsle.UiTests.csproj --no-restore --nologo -m:1 -p:UseSharedCompilation=false -nr:false --filter "FullyQualifiedName~CollapsedIslandCustomizationContractTests.CollapsedIsland_UsesFiveSecondSummaryRotationAndContentWidthBounds"
```

Expected: FAIL，缺少 `RotatingSummaryWidth`。

- [ ] **Step 3: 只在双来源轮播时固定摘要槽**

在窗口常量区添加：

```csharp
const double RotatingSummaryWidth = 160;
```

在 `ApplyCollapsedPreferences` 中计算并设置：

```csharp
var rotatingSummary =
    currentPreferences.IslandShowAgendaSummary &&
    currentPreferences.IslandShowNetworkSpeed &&
    currentPreferences.TelemetryEnabled;
Summary.Width = rotatingSummary ? RotatingSummaryWidth : double.NaN;
```

继续使用现有 `TextTrimming="CharacterEllipsis"`，不改变单来源摘要的按内容测宽行为。

- [ ] **Step 4: 运行定向测试并确认通过**

Run Task 2 Step 2 的命令。

Expected: PASS。

- [ ] **Step 5: 提交轮播宽度改动**

```powershell
git add src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs tests/ChronoIsle.UiTests/CollapsedIslandCustomizationContractTests.cs
git commit -m "fix: 稳定灵动岛轮播宽度"
```

### Task 3: 顶部吸附悬停防抖

**Files:**
- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs:60-95`
- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs:185-215`
- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs:2916-2968`
- Test: `tests/ChronoIsle.UiTests/TopDockAutoFoldContractTests.cs`

- [ ] **Step 1: 写入失败的悬停离开防抖契约测试**

在 `TopDockedIsland_FoldsToSemanticIslandIndicatorStripWhenPointerLeaves` 中增加：

```csharp
Assert.Contains(
    "readonly DispatcherTimer topDockHoverExitTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };",
    source,
    StringComparison.Ordinal);
Assert.Contains("topDockHoverExitTimer.Stop();", source, StringComparison.Ordinal);
Assert.Contains("void ConfirmTopDockHoverExit()", source, StringComparison.Ordinal);
Assert.Contains(
    "if (placement != IslandPlacement.Top || expanded || IsMouseOver) return;",
    source,
    StringComparison.Ordinal);
Assert.DoesNotContain(
    "if (placement == IslandPlacement.Top) SetTopDockFolded(true);",
    source,
    StringComparison.Ordinal);
```

- [ ] **Step 2: 运行测试并确认按预期失败**

Run:

```powershell
$env:DOTNET_CLI_USE_MSBUILD_SERVER='0'
dotnet test tests\ChronoIsle.UiTests\ChronoIsle.UiTests.csproj --no-restore --nologo -m:1 -p:UseSharedCompilation=false -nr:false --filter "FullyQualifiedName~TopDockAutoFoldContractTests.TopDockedIsland_FoldsToSemanticIslandIndicatorStripWhenPointerLeaves"
```

Expected: FAIL，缺少 `topDockHoverExitTimer`。

- [ ] **Step 3: 实现延迟确认**

添加独立定时器：

```csharp
readonly DispatcherTimer topDockHoverExitTimer =
    new() { Interval = TimeSpan.FromMilliseconds(160) };
```

构造函数中绑定，窗口关闭时停止：

```csharp
topDockHoverExitTimer.Tick += (_, _) => ConfirmTopDockHoverExit();
```

```csharp
topDockHoverExitTimer.Stop();
```

鼠标进入时先取消待折叠：

```csharp
topDockHoverExitTimer.Stop();
pointerHover = true;
collapseTimer.Stop();
SetTopDockFolded(false);
ResizeCollapsedToContent();
```

折叠态顶部窗口离开时只启动确认定时器：

```csharp
if (!expanded && placement == IslandPlacement.Top)
{
    topDockHoverExitTimer.Stop();
    topDockHoverExitTimer.Start();
    return;
}
```

新增确认方法：

```csharp
void ConfirmTopDockHoverExit()
{
    topDockHoverExitTimer.Stop();
    if (placement != IslandPlacement.Top || expanded || IsMouseOver) return;
    pointerHover = false;
    SetTopDockFolded(true);
    ResizeCollapsedToContent();
}
```

- [ ] **Step 4: 运行定向测试并确认通过**

Run Task 3 Step 2 的命令。

Expected: PASS。

- [ ] **Step 5: 提交顶部防抖改动**

```powershell
git add src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs tests/ChronoIsle.UiTests/TopDockAutoFoldContractTests.cs
git commit -m "fix: 防止顶部灵动岛悬停闪烁"
```

### Task 4: 移动设置按钮

**Files:**
- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml:319-325`
- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs:142`
- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs:1059-1067`
- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs:1266-1311`
- Create: `tests/ChronoIsle.UiTests/IslandSettingsPlacementContractTests.cs`

- [ ] **Step 1: 创建失败的按钮位置契约测试**

```csharp
using System.IO;

namespace ChronoIsle.UiTests;

public sealed class IslandSettingsPlacementContractTests
{
    [Fact]
    public void SettingsButton_IsRightAlignedInExpandedDashboardToolbar()
    {
        var workspace = FindWorkspace();
        var xaml = File.ReadAllText(Path.Combine(
            workspace, "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml"));
        var source = File.ReadAllText(Path.Combine(
            workspace, "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml.cs"));

        Assert.Contains("<Grid x:Name=\"DashboardTabs\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"IslandSettingsButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Grid.Column=\"1\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"⚙ 设置\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"Settings_Click\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "IslandQuickAction.Naming, IslandQuickAction.Settings",
            source,
            StringComparison.Ordinal);
    }

    static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ChronoIsle.sln")))
                return directory.FullName;
        throw new DirectoryNotFoundException("ChronoIsle.sln was not found from the UI test host.");
    }
}
```

- [ ] **Step 2: 运行测试并确认按预期失败**

Run:

```powershell
$env:DOTNET_CLI_USE_MSBUILD_SERVER='0'
dotnet test tests\ChronoIsle.UiTests\ChronoIsle.UiTests.csproj --no-restore --nologo -m:1 -p:UseSharedCompilation=false -nr:false --filter "FullyQualifiedName~IslandSettingsPlacementContractTests"
```

Expected: FAIL，缺少 `IslandSettingsButton`。

- [ ] **Step 3: 将设置按钮移动到标签栏右侧**

把标签行改为两列 Grid：

```xml
<Grid x:Name="DashboardTabs" Margin="12,12,12,10">
  <Grid.ColumnDefinitions>
    <ColumnDefinition Width="*"/>
    <ColumnDefinition Width="Auto"/>
  </Grid.ColumnDefinitions>
  <StackPanel Orientation="Horizontal">
    <Button x:Name="TodayDashboardTab" Content="今天" Click="TodayTab_Click" Style="{StaticResource IslandTab}"/>
    <Button x:Name="CalendarDashboardTab" Content="月历" Click="CalendarTab_Click" Style="{StaticResource IslandTab}" Background="{DynamicResource Brush.Hover}" BorderBrush="{DynamicResource Brush.TextTertiary}" Foreground="{DynamicResource Brush.TextPrimary}" Margin="7,0,0,0"/>
    <Button x:Name="StatusDashboardTab" Content="状态" Click="StatusTab_Click" Style="{StaticResource IslandTab}" Margin="7,0,0,0"/>
    <Button x:Name="MediaDashboardTab" Content="音乐" Click="MediaTab_Click" Style="{StaticResource IslandTab}" Margin="7,0,0,0"/>
    <Button x:Name="QuickAskDashboardTab" Content="快问" Click="QuickAskTab_Click" Style="{StaticResource IslandTab}" Margin="7,0,0,0"/>
  </StackPanel>
  <Button x:Name="IslandSettingsButton"
          Grid.Column="1"
          Content="⚙ 设置"
          Click="Settings_Click"
          Style="{StaticResource IslandTab}"
          AutomationProperties.AutomationId="IslandSettingsButton"/>
</Grid>
```

从今天页快捷操作数组中删除 `IslandQuickAction.Settings`，并删除枚举、`QuickActionLabel` 和 `RunQuickAction` 中不再使用的 Settings 分支；保留现有 `Settings_Click` 与 `OpenSettings()`。

- [ ] **Step 4: 运行定向测试并确认通过**

Run Task 4 Step 2 的命令。

Expected: PASS。

- [ ] **Step 5: 提交设置按钮布局改动**

```powershell
git add src/ChronoIsle.App/Views/LifeIslandWindow.xaml src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs tests/ChronoIsle.UiTests/IslandSettingsPlacementContractTests.cs
git commit -m "feat: 移动灵动岛设置按钮"
```

### Task 5: 完整验证、覆盖发布并重启

**Files:**
- Verify: `tests/ChronoIsle.Tests/ChronoIsle.Tests.csproj`
- Verify: `tests/ChronoIsle.UiTests/ChronoIsle.UiTests.csproj`
- Publish: `src/ChronoIsle.App/ChronoIsle.App.csproj`

- [ ] **Step 1: 运行核心测试**

```powershell
$env:DOTNET_CLI_USE_MSBUILD_SERVER='0'
dotnet test tests\ChronoIsle.Tests\ChronoIsle.Tests.csproj --no-restore --nologo -m:1 -p:UseSharedCompilation=false -nr:false
```

Expected: 0 failures。

- [ ] **Step 2: 运行 UI 契约测试**

```powershell
$env:DOTNET_CLI_USE_MSBUILD_SERVER='0'
dotnet test tests\ChronoIsle.UiTests\ChronoIsle.UiTests.csproj --no-restore --nologo -m:1 -p:UseSharedCompilation=false -nr:false
```

Expected: 0 failures。

- [ ] **Step 3: 检查差异格式**

```powershell
git diff --check
```

Expected: exit code 0。

- [ ] **Step 4: 覆盖同一测试版目录**

```powershell
$env:DOTNET_CLI_USE_MSBUILD_SERVER='0'
dotnet publish src\ChronoIsle.App\ChronoIsle.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:UseSharedCompilation=false --no-restore --nologo -m:1 -nr:false
```

Expected: `src/ChronoIsle.App/bin/Release/net8.0-windows10.0.19041.0/win-x64/publish/ChronoIsle.exe` 被覆盖更新。

- [ ] **Step 5: 关闭旧实例并启动新测试版**

先读取所有 `ChronoIsle` 进程路径，只终止已确认的旧实例；随后启动上述发布目录中的 `ChronoIsle.exe`。最后读取新进程的 PID、`Path` 和 `Responding`，确认路径指向本工作树的发布目录且 `Responding=True`。
