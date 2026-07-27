# Collapsed Island Navigation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让折叠态灵动岛的网络、时间、事项摘要和吉祥物分别直接展开到状态、今天、月历和快问页签。

**Architecture:** 将三个纯显示元素包成透明按钮，使现有标题栏交互过滤自然排除它们，避免同时触发 280ms 通用单击。四个按钮处理器复用现有 `Expand` 与 `Show*Dashboard` 方法，不修改拖动、双击或页签实现。

**Tech Stack:** WPF XAML、C#、xUnit UI 契约测试

---

### Task 1: 锁定折叠态点击契约

**Files:**
- Create: `tests/ChronoIsle.UiTests/CollapsedIslandNavigationContractTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Theory]
[InlineData("NetworkStatusButton_Click", "ShowTelemetryDashboard();")]
[InlineData("ClockButton_Click", "ShowTodayDashboard();")]
[InlineData("AgendaSummaryButton_Click", "ShowCalendarDashboard();")]
[InlineData("MascotButton_Click", "ShowQuickAskDashboard();")]
public void CollapsedControl_OpensExpectedDashboard(string handler, string dashboardCall)
{
    // 读取 XAML 与代码，验证独立按钮、事件处理器、Expand 和目标页签调用。
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/ChronoIsle.UiTests/ChronoIsle.UiTests.csproj --filter FullyQualifiedName~CollapsedIslandNavigationContractTests -m:1 -p:UseSharedCompilation=false -nr:false`

Expected: FAIL because the three new buttons and four dashboard routing contracts do not exist.

### Task 2: 实现四个直接导航入口

**Files:**
- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml`
- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs`

- [ ] **Step 1: Add transparent header buttons**

为事项摘要、网络图标和时间增加透明按钮与自动化标识；保留现有内部元素名称供状态更新和宽度测量使用。

- [ ] **Step 2: Add minimal click handlers**

```csharp
void NetworkStatusButton_Click(object sender, RoutedEventArgs e)
{
    e.Handled = true;
    headerSingleClickTimer.Stop();
    Expand();
    ShowTelemetryDashboard();
}
```

其余三个处理器使用相同顺序并调用各自目标页签；吉祥物在打开快问后聚焦输入框。

- [ ] **Step 3: Run targeted tests**

Run: `dotnet test tests/ChronoIsle.UiTests/ChronoIsle.UiTests.csproj --filter FullyQualifiedName~CollapsedIslandNavigationContractTests -m:1 -p:UseSharedCompilation=false -nr:false`

Expected: PASS.

### Task 3: 回归验证、发布并启动

**Files:**
- No source changes expected.

- [ ] **Step 1: Run both test projects**

Run: `dotnet test tests/ChronoIsle.Tests/ChronoIsle.Tests.csproj -m:1 -p:UseSharedCompilation=false -nr:false`

Run: `dotnet test tests/ChronoIsle.UiTests/ChronoIsle.UiTests.csproj -m:1 -p:UseSharedCompilation=false -nr:false`

Expected: all tests pass.

- [ ] **Step 2: Publish over the existing test build**

Run: `dotnet publish src/ChronoIsle.App/ChronoIsle.App.csproj -c Release -r win-x64 --self-contained true -m:1 -p:UseSharedCompilation=false -nr:false`

Expected: the existing `publish` directory is updated.

- [ ] **Step 3: Restart and verify**

停止当前测试版进程，启动发布目录中的 `ChronoIsle.exe`，并核对进程路径与响应状态。
