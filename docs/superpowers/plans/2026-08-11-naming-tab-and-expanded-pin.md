# 取名页签与展开图钉 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将完整取名助手嵌入岛屿工具页签，并在展开状态提供遵循强调色的三态图钉。

**Architecture:** `LifeIslandWindow` 是唯一的岛屿与取名承载界面，原 `NamingWindow` 的控件和事件处理迁入既有 `NamingToolPanel`。不持久化的 `ExpandedPinState` 集中控制自动折叠和 WPF 顶层窗口状态，图标颜色引用 `Brush.Accent` 动态资源。

**Tech Stack:** .NET 8、WPF XAML、xUnit UI 契约测试、Microsoft.Extensions.DependencyInjection。

---

## 文件结构

- `src/ChronoIsle.App/Views/LifeIslandWindow.xaml`：完整取名表单与中空/实心图钉 Path。
- `src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs`：命名状态、三态图钉、自动折叠和顶层窗口切换。
- `src/ChronoIsle.App/App.xaml.cs`：独立窗口入口改为岛屿取名页签。
- `src/ChronoIsle.App/ChronoIsle.App.csproj`：移除独立命名窗口项目项。
- 删除 `src/ChronoIsle.App/Views/NamingWindow.xaml` 和 `NamingWindow.xaml.cs`。
- `tests/ChronoIsle.UiTests/NamingWindowContractTests.cs`：岛屿内嵌取名与入口路由契约。
- `tests/ChronoIsle.UiTests/TaskbarTopmostContractTests.cs`、`ExternalFocusCollapseContractTests.cs`：图钉、自动折叠和任务栏置顶契约。

### Task 1: 写入失败的 UI 契约测试

**Files:**

- Modify: `tests/ChronoIsle.UiTests/NamingWindowContractTests.cs`
- Modify: `tests/ChronoIsle.UiTests/TaskbarTopmostContractTests.cs`
- Modify: `tests/ChronoIsle.UiTests/ExternalFocusCollapseContractTests.cs`

- [ ] **Step 1: 将独立窗口测试改为岛屿内嵌测试。**

```csharp
[Fact]
public void Island_embeds_the_full_naming_tool_and_has_no_standalone_naming_window()
{
    var workspace = FindWorkspace();
    var islandXaml = File.ReadAllText(Path.Combine(workspace, "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml"));
    var project = File.ReadAllText(Path.Combine(workspace, "src", "ChronoIsle.App", "ChronoIsle.App.csproj"));

    Assert.Contains("x:Name=\"NamingToolPanel\"", islandXaml, StringComparison.Ordinal);
    Assert.Contains("AutomationProperties.AutomationId=\"NamingMeaningInput\"", islandXaml, StringComparison.Ordinal);
    Assert.Contains("AutomationProperties.AutomationId=\"NamingGenerateButton\"", islandXaml, StringComparison.Ordinal);
    Assert.Contains("AutomationProperties.AutomationId=\"NamingResultCard\"", islandXaml, StringComparison.Ordinal);
    Assert.False(File.Exists(Path.Combine(workspace, "src", "ChronoIsle.App", "Views", "NamingWindow.xaml")));
    Assert.DoesNotContain("NamingWindow.xaml", project, StringComparison.Ordinal);
}

[Fact]
public void Naming_entries_open_the_island_naming_tab_instead_of_a_standalone_window()
{
    var workspace = FindWorkspace();
    var app = File.ReadAllText(Path.Combine(workspace, "src", "ChronoIsle.App", "App.xaml.cs"));
    var island = File.ReadAllText(Path.Combine(workspace, "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml.cs"));

    Assert.Contains("public void OpenNamingTool()", island, StringComparison.Ordinal);
    Assert.Contains("ShowToolsDashboard();", island, StringComparison.Ordinal);
    Assert.Contains("ShowNamingTool();", island, StringComparison.Ordinal);
    Assert.Contains("OpenNamingTool();", app, StringComparison.Ordinal);
    Assert.DoesNotContain("GetRequiredService<NamingWindow>()", app, StringComparison.Ordinal);
}
```

- [ ] **Step 2: 在两个既有图钉相关测试中加入以下断言。**

```csharp
Assert.DoesNotContain("Topmost=\"True\"", xaml, StringComparison.Ordinal);
Assert.Contains("x:Name=\"ExpandedPinButton\"", xaml, StringComparison.Ordinal);
Assert.Contains("x:Name=\"ExpandedPinOutline\"", xaml, StringComparison.Ordinal);
Assert.Contains("x:Name=\"ExpandedPinSolid\"", xaml, StringComparison.Ordinal);
Assert.Contains("Click=\"ExpandedPin_Click\"", xaml, StringComparison.Ordinal);
Assert.Contains("enum ExpandedPinState", source, StringComparison.Ordinal);
Assert.Contains("ExpandedPinState.KeepExpanded", source, StringComparison.Ordinal);
Assert.Contains("ExpandedPinState.Topmost", source, StringComparison.Ordinal);
Assert.Contains("void AutoCollapse()", source, StringComparison.Ordinal);
Assert.Contains("expandedPinState == ExpandedPinState.Normal", source, StringComparison.Ordinal);
Assert.Contains("placement != IslandPlacement.Taskbar || expandedPinState != ExpandedPinState.Topmost", source, StringComparison.Ordinal);
```

`ExternalFocusCollapseContractTests` 还应断言 `collapseTimer.Tick` 与外部焦点路径调用 `AutoCollapse()`；`TaskbarTopmostContractTests` 断言守卫中的第三态条件。

- [ ] **Step 3: 运行测试并确认红灯来自缺少的新行为。**

Run:

```powershell
dotnet test .\tests\ChronoIsle.UiTests\ChronoIsle.UiTests.csproj --filter "FullyQualifiedName~NamingWindowContractTests|FullyQualifiedName~TaskbarTopmostContractTests|FullyQualifiedName~ExternalFocusCollapseContractTests"
```

Expected: FAIL；缺少岛屿内嵌完整命名控件、`ExpandedPinState` 与新的任务栏置顶条件，而不是测试编译错误。

### Task 2: 将取名助手迁入岛屿工具页签

**Files:**

- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml:Window.Resources, ToolsPanel`
- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs:constructor, tool navigation, naming handlers`
- Modify: `src/ChronoIsle.App/App.xaml.cs:service registration, island event wiring, OpenNaming`
- Modify: `src/ChronoIsle.App/ChronoIsle.App.csproj:Compile/Page item groups`
- Delete: `src/ChronoIsle.App/Views/NamingWindow.xaml`
- Delete: `src/ChronoIsle.App/Views/NamingWindow.xaml.cs`
- Test: `tests/ChronoIsle.UiTests/NamingWindowContractTests.cs`

- [ ] **Step 1: 移入原表单和所需资源。**

将 `NamingWindow.xaml` 中 `ContentScroller` 的唯一 `StackPanel` 子节点完整移动为 `NamingToolPanel` 的内容：其中包含中文含义卡片、`MeaningInput`、`KindSelector`、`GenerateButton`、`LoadingPanel`、`StatusNotice`、`EmptyState`、`ResultCard`、候选翻页、推荐命名和 `FormatRows`。把外层 `ScrollViewer` 改为下列属性，并把 `NavigationButton`、`CopyButton`、`ResultRow` 移到 `LifeIslandWindow` 资源：

```xml
<ScrollViewer x:Name="NamingContentScroller"
              VerticalScrollBarVisibility="Auto"
              HorizontalScrollBarVisibility="Disabled" />
```

删除旧“打开”按钮及其 `NamingTool_Click` 绑定，不删改命名表单的输入、生成或复制控件。

- [ ] **Step 2: 移入原有命名逻辑。**

向 `LifeIslandWindow` 构造函数注入 `NamingSuggestionService naming`。迁移 `NamingWindow` 的 `result`、`resultKind`、`recommendation`、`candidateIndex`、`formatRows`、生成、翻页、复制、`RenderCandidate`、`SetBusy`、状态提示成员；移除窗口专属 `TitleBar_MouseDown` 与 `Close_Click`。初始化 `FormatRows.ItemsSource = formatRows`，并加入外部入口：

```csharp
public void OpenNamingTool()
{
    Show();
    Expand();
    ShowToolsDashboard();
    NamingContentScroller.ScrollToTop();
    MeaningInput.Focus();
    Touch();
}
```

移除 `NamingRequested` 事件与 `OpenNaming()`，保持 `NamingSuggestionService` 的生成规则、用户可读错误文本和剪贴板行为不变。

- [ ] **Step 3: 改写应用入口并删除页面。**

从 DI、项目文件和磁盘删除 `NamingWindow`。删除精确订阅语句 `island.NamingRequested += (_, _) => Dispatcher.BeginInvoke(OpenNaming);`，但保留托盘的 `NamingRequested` 事件，并让应用层入口关闭其他独立页后打开岛屿取名页签：

```csharp
public void OpenNaming()
{
    CloseStandalonePage();
    services!.GetRequiredService<LifeIslandWindow>().OpenNamingTool();
}
```

- [ ] **Step 4: 运行命名契约并确认绿灯。**

Run:

```powershell
dotnet test .\tests\ChronoIsle.UiTests\ChronoIsle.UiTests.csproj --filter "FullyQualifiedName~NamingWindowContractTests"
```

Expected: PASS；完整取名功能仅位于岛屿页签，岛屿和托盘入口均不解析或显示独立窗口。

- [ ] **Step 5: 提交通过测试的取名页签迁移。**

```powershell
git add --all -- src/ChronoIsle.App/App.xaml.cs src/ChronoIsle.App/ChronoIsle.App.csproj src/ChronoIsle.App/Views/LifeIslandWindow.xaml src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs src/ChronoIsle.App/Views/NamingWindow.xaml src/ChronoIsle.App/Views/NamingWindow.xaml.cs tests/ChronoIsle.UiTests/NamingWindowContractTests.cs
git commit -m "feat: embed naming tool in island tabs"
```

### Task 3: 实现展开三态图钉

**Files:**

- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml:root window, expanded toolbar`
- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs:collapse paths and taskbar topmost`
- Test: `tests/ChronoIsle.UiTests/TaskbarTopmostContractTests.cs`
- Test: `tests/ChronoIsle.UiTests/ExternalFocusCollapseContractTests.cs`

- [ ] **Step 1: 在展开操作区加入图标按钮。**

从根 `Window` 删除 `Topmost="True"`。在 `DashboardTabs` 的右侧操作区加入图标按钮，默认显示中空图钉，实心图钉默认折叠：

```xml
<Button x:Name="ExpandedPinButton" Click="ExpandedPin_Click" Style="{StaticResource IslandIcon}"
        AutomationProperties.AutomationId="IslandExpandedPinButton">
  <Grid Width="16" Height="16">
    <Path x:Name="ExpandedPinOutline" Data="M9,2 L15,2 L14,9 L18,13 L6,13 L10,9 Z M12,13 L12,22" Stroke="{DynamicResource Brush.TextSecondary}" />
    <Path x:Name="ExpandedPinSolid" Data="M9,2 L15,2 L14,9 L18,13 L13,13 L13,22 L11,22 L11,13 L6,13 L10,9 Z" Fill="{DynamicResource Brush.Accent}" Visibility="Collapsed" />
  </Grid>
</Button>
```

状态二调用 `SetResourceReference(Shape.StrokeProperty, "Brush.Accent")` 高亮中空图标；状态三隐藏中空图标并显示实心图标，强调色变更会即时生效。

- [ ] **Step 2: 以单一枚举驱动状态变化。**

在放置枚举旁加入三态并添加点击循环与自动折叠入口：

```csharp
enum ExpandedPinState { Normal, KeepExpanded, Topmost }

void ExpandedPin_Click(object sender, RoutedEventArgs e) => SetExpandedPinState(expandedPinState switch
{
    ExpandedPinState.Normal => ExpandedPinState.KeepExpanded,
    ExpandedPinState.KeepExpanded => ExpandedPinState.Topmost,
    _ => ExpandedPinState.Normal
});

void AutoCollapse()
{
    if (expandedPinState == ExpandedPinState.Normal) Collapse();
}
```

`SetExpandedPinState` 设置 `Topmost = state == ExpandedPinState.Topmost`、切换 Path 可见性并引用状态一的 `Brush.TextSecondary` 与状态二/三的 `Brush.Accent`。`collapseTimer.Tick`、`CollapseWhenForegroundMovesToAnotherProcess` 改为 `AutoCollapse()`；`Collapse()` 和 `ResetToDefaultPlacement()` 恢复 `Normal`，保证主动收起和下一次展开从第一态开始。

- [ ] **Step 3: 限制任务栏置顶重申到第三态。**

保留既有 `SetWindowPos` 标志，仅收紧 `EnsureTaskbarTopmost()` 守卫：

```csharp
if (placement != IslandPlacement.Taskbar || expandedPinState != ExpandedPinState.Topmost ||
    windowHandle == IntPtr.Zero || !IsVisible) return;
```

- [ ] **Step 4: 运行图钉契约并确认绿灯。**

Run:

```powershell
dotnet test .\tests\ChronoIsle.UiTests\ChronoIsle.UiTests.csproj --filter "FullyQualifiedName~TaskbarTopmostContractTests|FullyQualifiedName~ExternalFocusCollapseContractTests"
```

Expected: PASS；状态二和三不会自动折叠，且任务栏只在状态三重申置顶。

- [ ] **Step 5: 提交通过测试的图钉功能。**

```powershell
git add src/ChronoIsle.App/Views/LifeIslandWindow.xaml src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs tests/ChronoIsle.UiTests/TaskbarTopmostContractTests.cs tests/ChronoIsle.UiTests/ExternalFocusCollapseContractTests.cs
git commit -m "feat: add expanded island pin states"
```

### Task 4: 整体验证与发布替换

**Files:**

- Verify: `ChronoIsle.sln`
- Verify: `scripts/dev-restart.ps1`

- [ ] **Step 1: 运行完整测试和 Release 构建。**

```powershell
$env:MSBUILDDISABLENODEREUSE='1'
dotnet test .\tests\ChronoIsle.Tests\ChronoIsle.Tests.csproj -m:1 -nodeReuse:false
dotnet test .\tests\ChronoIsle.UiTests\ChronoIsle.UiTests.csproj -m:1 -nodeReuse:false
dotnet build .\ChronoIsle.sln -c Release -m:1 -nodeReuse:false --no-restore
```

Expected: 三条命令均以退出码 0 完成，Release 构建没有错误。

- [ ] **Step 2: 发布并替换当前程序。**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\dev-restart.ps1
```

Expected: 脚本结束旧进程，发布 `win-x64` 并启动新的发布目录可执行文件。

- [ ] **Step 3: 核验部署产物和人工 UI。**

```powershell
Get-Process -Name ChronoIsle | Select-Object Id,ProcessName,Responding
Get-FileHash -Algorithm SHA256 .\src\ChronoIsle.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\ChronoIsle.dll,.\src\ChronoIsle.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\ChronoIsle.dll
```

Expected: 新进程 `Responding=True`，两个 DLL 哈希相同。人工确认托盘和岛屿“取名”都打开页签；图钉三次点击依次为普通中空、强调色中空、强调色实心置顶并回到普通；在每个强调色设置下图标立即变色。
