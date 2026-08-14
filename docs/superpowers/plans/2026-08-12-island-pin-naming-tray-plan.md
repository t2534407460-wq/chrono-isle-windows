# 灵动岛图钉、取名配色与托盘双击 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将灵动岛固定为独立的顶层窗口，把展开图钉简化为两态自动收起控制，修复取名空状态前景色，并让托盘双击恢复默认位置后展开。

**Architecture:** `LifeIslandWindow` 负责顶层窗口、展开状态和默认位置；图钉不再影响窗口层级。`LifeTrayService` 明确区分右键菜单与左键双击恢复事件，`App` 只负责路由。取名页保持局部主题资源，不修改全局主题。

**Tech Stack:** .NET 8、WPF、Windows Forms `NotifyIcon`、xUnit 源码契约测试。

---

### Task 1: 将顶层窗口和两态图钉职责拆分

**Files:**
- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml:1-10,430-442`
- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs:34-42,78,146-148,260-283,494-562,3380-3410`
- Modify: `tests/ChronoIsle.UiTests/TaskbarTopmostContractTests.cs`
- Test: `tests/ChronoIsle.UiTests/ExternalFocusCollapseContractTests.cs`

- [ ] **Step 1: 把旧三态任务栏契约改为失败的两态/常驻置顶契约**

  将 `TaskbarTopmostContractTests` 改为断言窗口根元素含 `Topmost="True"`，并断言源码仅含两个图钉状态、没有 `ExpandedPinState.Topmost`、`taskbarTopmostTimer`、`EnsureTaskbarTopmost` 或 `SetWindowPos`。保留 `ExternalFocusCollapseContractTests` 对 Normal 自动收起、KeepExpanded 停止计时器的断言。

  ```csharp
  Assert.Contains("Topmost=\"True\"", xaml, StringComparison.Ordinal);
  Assert.Contains("enum ExpandedPinState { Normal, KeepExpanded }", source, StringComparison.Ordinal);
  Assert.Contains("ExpandedPinState.Normal => ExpandedPinState.KeepExpanded", click, StringComparison.Ordinal);
  Assert.Contains("_ => ExpandedPinState.Normal", click, StringComparison.Ordinal);
  Assert.DoesNotContain("ExpandedPinState.Topmost", source, StringComparison.Ordinal);
  Assert.DoesNotContain("taskbarTopmostTimer", source, StringComparison.Ordinal);
  Assert.DoesNotContain("EnsureTaskbarTopmost", source, StringComparison.Ordinal);
  Assert.DoesNotContain("SetWindowPos(", source, StringComparison.Ordinal);
  ```

- [ ] **Step 2: 运行图钉契约，确认因仍保留第三态而失败**

  Run:

  ```powershell
  dotnet test .\tests\ChronoIsle.UiTests\ChronoIsle.UiTests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~TaskbarTopmostContractTests|FullyQualifiedName~ExternalFocusCollapseContractTests" --verbosity minimal
  ```

  Expected: 图钉契约失败，原因是 XAML 没有永久 `Topmost`、代码仍有第三态和任务栏置顶轮询。

- [ ] **Step 3: 最小化实现常驻置顶和两态图钉**

  在窗口根元素增加 `Topmost="True"`。在 `LifeIslandWindow.xaml.cs` 中移除：`Topmost` 图钉枚举值、`taskbarTopmostTimer`、原生 `SetWindowPos` 声明及其相关常量、`windowHandle`、`UpdateTaskbarTopmostTimer`、`EnsureTaskbarTopmost`，以及构造/关闭/摆放路径上的调用。

  将点击和状态更新保持为下列等价逻辑：

  ```csharp
  void ExpandedPin_Click(object sender, RoutedEventArgs e) =>
      SetExpandedPinState(expandedPinState == ExpandedPinState.Normal
          ? ExpandedPinState.KeepExpanded
          : ExpandedPinState.Normal);

  void SetExpandedPinState(ExpandedPinState state)
  {
      expandedPinState = state;
      var keepExpanded = state == ExpandedPinState.KeepExpanded;
      ExpandedPinOutline.Visibility = keepExpanded ? Visibility.Collapsed : Visibility.Visible;
      ExpandedPinSolid.Visibility = keepExpanded ? Visibility.Visible : Visibility.Collapsed;
      SetThemeResource(ExpandedPinOutline, Shape.StrokeProperty,
          keepExpanded ? "Brush.Accent" : "Brush.TextSecondary");
      SetThemeResource(ExpandedPinSolid, Shape.FillProperty,
          keepExpanded ? "Brush.Accent" : "Brush.TextSecondary");
      if (keepExpanded) collapseTimer.Stop();
      else if (expanded && !pointerHover) ScheduleMouseLeaveCollapse();
  }
  ```

  不改动 `SetFullscreenAvoidance` 的移动/隐藏和恢复逻辑。

- [ ] **Step 4: 运行图钉与自动收起契约，确认全部通过**

  Run the Step 2 command.

  Expected: 全部通过；Normal 会收起，KeepExpanded 不会自动收起，窗口置顶不再依赖图钉或任务栏。

- [ ] **Step 5: 提交图钉职责拆分**

  ```powershell
  git add -- src/ChronoIsle.App/Views/LifeIslandWindow.xaml src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs tests/ChronoIsle.UiTests/TaskbarTopmostContractTests.cs
  git commit -m "refactor: decouple island topmost from expanded pin"
  ```

### Task 2: 修复取名空状态的主题前景色

**Files:**
- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml:640-660`
- Modify: `tests/ChronoIsle.UiTests/NamingWindowContractTests.cs`

- [ ] **Step 1: 为空状态文字写失败契约**

  在 `NamingWindowContractTests` 解析 `x:Name="EmptyState"` 的子树，断言标题与说明分别显式引用主题前景色。

  ```csharp
  var emptyState = document.Descendants()
      .Single(element => (string?)element.Attribute(x + "Name") == "EmptyState")
      .ToString();
  Assert.Contains("Foreground=\"{DynamicResource Brush.TextPrimary}\"", emptyState, StringComparison.Ordinal);
  Assert.Contains("Foreground=\"{DynamicResource Brush.TextSecondary}\"", emptyState, StringComparison.Ordinal);
  ```

- [ ] **Step 2: 运行取名契约，确认标题缺少主题色而失败**

  Run:

  ```powershell
  dotnet test .\tests\ChronoIsle.UiTests\ChronoIsle.UiTests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~NamingWindowContractTests" --verbosity minimal
  ```

  Expected: 空状态主题色断言失败。

- [ ] **Step 3: 显式绑定标题和说明的主题前景色**

  在 `EmptyState` 下的第一个 `TextBlock` 加上：

  ```xml
  Foreground="{DynamicResource Brush.TextPrimary}"
  ```

  在说明 `TextBlock` 加上：

  ```xml
  Foreground="{DynamicResource Brush.TextSecondary}"
  ```

  保留现有 `Card.Subtle`、`Text.Secondary` 和布局属性；不要改全局颜色令牌。

- [ ] **Step 4: 重跑取名契约并构建应用**

  Run:

  ```powershell
  dotnet test .\tests\ChronoIsle.UiTests\ChronoIsle.UiTests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~NamingWindowContractTests" --verbosity minimal
  dotnet build .\src\ChronoIsle.App\ChronoIsle.App.csproj -c Release --no-restore -v:minimal
  ```

  Expected: 取名契约通过，应用 0 warnings / 0 errors。

- [ ] **Step 5: 提交取名配色修复**

  ```powershell
  git add -- src/ChronoIsle.App/Views/LifeIslandWindow.xaml tests/ChronoIsle.UiTests/NamingWindowContractTests.cs
  git commit -m "fix: apply theme colors to naming empty state"
  ```

### Task 3: 让托盘双击恢复默认位置并展开

**Files:**
- Modify: `src/ChronoIsle.App/Services/LifeTrayService.cs:12-52`
- Modify: `src/ChronoIsle.App/App.xaml.cs:122-142`
- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs:368-410,1822-1830`
- Create: `tests/ChronoIsle.UiTests/TrayDoubleClickContractTests.cs`

- [ ] **Step 1: 新建托盘双击失败契约**

  测试将断言 `LifeTrayService` 有 `RestoreIslandRequested` 事件，`MouseClick` 只路由右键，`MouseDoubleClick` 的左键会发布恢复事件；断言 `App` 订阅该事件并调用 `OpenDefaultExpanded()`；断言窗口入口重置位置、显示、展开且不调用 `Activate()`。

  ```csharp
  Assert.Contains("public event EventHandler? RestoreIslandRequested;", tray, StringComparison.Ordinal);
  Assert.Contains("icon.MouseClick += Icon_MouseClick;", tray, StringComparison.Ordinal);
  Assert.Contains("icon.MouseDoubleClick += Icon_MouseDoubleClick;", tray, StringComparison.Ordinal);
  Assert.Contains("args.Button != Forms.MouseButtons.Right", mouseClick, StringComparison.Ordinal);
  Assert.Contains("args.Button == Forms.MouseButtons.Left", mouseDoubleClick, StringComparison.Ordinal);
  Assert.Contains("RestoreIslandRequested?.Invoke(this, EventArgs.Empty);", mouseDoubleClick, StringComparison.Ordinal);
  Assert.Contains("tray.RestoreIslandRequested", app, StringComparison.Ordinal);
  Assert.Contains("island.OpenDefaultExpanded();", app, StringComparison.Ordinal);
  Assert.Contains("ResetToDefaultPlacement();", openDefaultExpanded, StringComparison.Ordinal);
  Assert.Contains("Show();", openDefaultExpanded, StringComparison.Ordinal);
  Assert.Contains("Expand();", openDefaultExpanded, StringComparison.Ordinal);
  Assert.DoesNotContain("Activate();", openDefaultExpanded, StringComparison.Ordinal);
  ```

- [ ] **Step 2: 运行托盘双击契约，确认事件和入口尚不存在而失败**

  Run:

  ```powershell
  dotnet test .\tests\ChronoIsle.UiTests\ChronoIsle.UiTests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~TrayDoubleClickContractTests" --verbosity minimal
  ```

  Expected: 失败，因为当前托盘左键单击会调用 `OpenRequested`，且没有双击恢复事件。

- [ ] **Step 3: 实现右键菜单、左键双击恢复事件和窗口入口**

  在 `LifeTrayService` 添加 `RestoreIslandRequested`，并以具名处理器替换内联点击代码：

  ```csharp
  void Icon_MouseClick(object? sender, Forms.MouseEventArgs args)
  {
      if (args.Button != Forms.MouseButtons.Right) return;
      ShowMenu(TrayMenuWindow.CaptureTrayHostAtCursor());
  }

  void Icon_MouseDoubleClick(object? sender, Forms.MouseEventArgs args)
  {
      if (args.Button == Forms.MouseButtons.Left)
          RestoreIslandRequested?.Invoke(this, EventArgs.Empty);
  }
  ```

  `App.OnStartup` 订阅事件：

  ```csharp
  tray.RestoreIslandRequested += (_, _) => Dispatcher.BeginInvoke(island.OpenDefaultExpanded);
  ```

  `LifeIslandWindow` 新增公共入口：

  ```csharp
  public void OpenDefaultExpanded()
  {
      ResetToDefaultPlacement();
      Show();
      Expand();
      Touch();
  }
  ```

  保留右键菜单内的 `OpenRequested` 路由；这不是托盘左键单击。

- [ ] **Step 4: 运行托盘契约、全量 UI 契约和 Release 构建**

  Run:

  ```powershell
  dotnet test .\tests\ChronoIsle.UiTests\ChronoIsle.UiTests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~TrayDoubleClickContractTests|FullyQualifiedName~TrayMenuContractTests|FullyQualifiedName~TaskbarTopmostContractTests|FullyQualifiedName~NamingWindowContractTests" --verbosity minimal
  dotnet test .\tests\ChronoIsle.UiTests\ChronoIsle.UiTests.csproj -c Release --no-restore -p:NuGetAudit=false --verbosity minimal
  dotnet build .\src\ChronoIsle.App\ChronoIsle.App.csproj -c Release --no-restore -v:minimal
  git diff --check
  ```

  Expected: UI 契约全绿，应用 0 warnings / 0 errors，差异无空白错误。

- [ ] **Step 5: 提交托盘双击恢复功能**

  ```powershell
  git add -- src/ChronoIsle.App/Services/LifeTrayService.cs src/ChronoIsle.App/App.xaml.cs src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs tests/ChronoIsle.UiTests/TrayDoubleClickContractTests.cs
  git commit -m "feat: restore and expand island on tray double click"
  ```

### Task 4: 最终发布验收

**Files:**
- Modify: none

- [ ] **Step 1: 重新发布并启动**

  先确认运行中的 `ChronoIsle` 进程可终止；然后运行：

  ```powershell
  .\scripts\dev-restart.ps1
  ```

  Expected: `win-x64\publish\ChronoIsle.exe` 启动，并输出新 PID。

- [ ] **Step 2: 手动验收四个用户可见不变量**

  1. 普通桌面窗口前，灵动岛持续显示在最上层；全屏避让按原有策略工作。
  2. 展开后连续点击图钉：中空自动收起 ↔ 实心固定展开；没有第三态。
  3. 打开工具/取名，空状态标题和说明在当前主题下可读且非黑色。
  4. 左键单击托盘无动作；左键双击将岛恢复顶部默认位置并展开；右键菜单仍可打开。

- [ ] **Step 3: 最终工作区检查**

  Run:

  ```powershell
  git status --short
  git log --oneline -4
  ```

  Expected: 仅保留先前存在的未跟踪计划文档；所有产品与测试改动已经提交。
