# 灵动岛从任务视图隐藏 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让新旧两种灵动岛不出现在 Windows 任务视图和 Alt+Tab 中，并保持现有桌面交互不变。

**Architecture:** 新增一个仅负责原生窗口扩展样式的内部辅助类，在窗口句柄创建后读取并保留现有样式，再加入 `WS_EX_TOOLWINDOW`。`LifeIslandWindow` 复用已有 `SourceInitialized` 处理，`DynamicIslandWindow` 新增同阶段调用；UI 合约测试静态验证两个入口都接入该行为。

**Tech Stack:** .NET 8、WPF、Win32 User32、xUnit

---

### Task 1: 写出失败的窗口样式合约测试

**Files:**
- Create: `tests/ChronoIsle.UiTests/IslandTaskViewVisibilityContractTests.cs`

- [ ] **Step 1: 创建合约测试**

```csharp
namespace ChronoIsle.UiTests;

public sealed class IslandTaskViewVisibilityContractTests
{
    [Theory]
    [InlineData("LifeIslandWindow.xaml.cs")]
    [InlineData("DynamicIslandWindow.xaml.cs")]
    public void Island_window_applies_tool_window_style_after_source_initialization(string fileName)
    {
        var source = ReadViewSource(fileName);

        Assert.Contains("SourceInitialized +=", source, StringComparison.Ordinal);
        Assert.Contains("IslandWindowStyles.HideFromTaskView(this);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Tool_window_style_preserves_existing_extended_styles()
    {
        var source = ReadViewSource("IslandWindowStyles.cs");

        Assert.Contains("WsExToolWindow", source, StringComparison.Ordinal);
        Assert.Contains("currentStyle | WsExToolWindow", source, StringComparison.Ordinal);
        Assert.Contains("GetWindowLongPtr", source, StringComparison.Ordinal);
        Assert.Contains("SetWindowLongPtr", source, StringComparison.Ordinal);
    }

    static string ReadViewSource(string fileName)
    {
        var workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        return File.ReadAllText(Path.Combine(
            workspace, "src", "ChronoIsle.App", "Views", fileName));
    }
}
```

- [ ] **Step 2: 运行测试并确认失败**

Run:

```powershell
dotnet test tests/ChronoIsle.UiTests/ChronoIsle.UiTests.csproj --filter IslandTaskViewVisibilityContractTests
```

Expected: FAIL，因为 `IslandWindowStyles.cs` 和两个调用尚不存在。

- [ ] **Step 3: 提交失败测试**

```powershell
git add tests/ChronoIsle.UiTests/IslandTaskViewVisibilityContractTests.cs
git commit -m "test: 覆盖灵动岛任务视图可见性"
```

### Task 2: 应用工具窗口扩展样式

**Files:**
- Create: `src/ChronoIsle.App/Views/IslandWindowStyles.cs`
- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs`
- Modify: `src/ChronoIsle.App/Views/DynamicIslandWindow.xaml.cs`

- [ ] **Step 1: 新增窗口样式辅助类**

```csharp
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ChronoIsle.App.Views;

internal static class IslandWindowStyles
{
    const int GwlExStyle = -20;
    const long WsExToolWindow = 0x00000080L;

    public static void HideFromTaskView(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var currentStyle = GetWindowLongPtr(handle, GwlExStyle);
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(currentStyle.ToInt64() | WsExToolWindow));
    }

    static IntPtr GetWindowLongPtr(IntPtr handle, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(handle, index) : new IntPtr(GetWindowLong32(handle, index));

    static IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(handle, index, value) : new IntPtr(SetWindowLong32(handle, index, value.ToInt32()));

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    static extern int GetWindowLong32(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    static extern IntPtr GetWindowLongPtr64(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    static extern int SetWindowLong32(IntPtr handle, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    static extern IntPtr SetWindowLongPtr64(IntPtr handle, int index, IntPtr value);
}
```

- [ ] **Step 2: 接入新版灵动岛**

在 `LifeIslandWindow` 已有 `SourceInitialized` 处理中，在获取句柄后加入：

```csharp
IslandWindowStyles.HideFromTaskView(this);
```

- [ ] **Step 3: 接入旧版灵动岛**

在 `DynamicIslandWindow` 构造函数中加入：

```csharp
SourceInitialized += (_, _) => IslandWindowStyles.HideFromTaskView(this);
```

- [ ] **Step 4: 运行定向测试并确认通过**

Run:

```powershell
dotnet test tests/ChronoIsle.UiTests/ChronoIsle.UiTests.csproj --filter IslandTaskViewVisibilityContractTests
```

Expected: PASS，2 个窗口调用和样式保留合约均成立。

- [ ] **Step 5: 运行完整 UI 合约测试**

Run:

```powershell
dotnet test tests/ChronoIsle.UiTests/ChronoIsle.UiTests.csproj
```

Expected: PASS，无 UI 合约回归。

- [ ] **Step 6: 提交实现**

```powershell
git add src/ChronoIsle.App/Views/IslandWindowStyles.cs src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs src/ChronoIsle.App/Views/DynamicIslandWindow.xaml.cs
git commit -m "fix: 从任务视图隐藏灵动岛"
```

### Task 3: 构建、发布并替换当前程序

**Files:**
- Read: `README.md`
- Read: `releases/README.md`
- Modify only the confirmed installed application directory outside the repository.

- [ ] **Step 1: 运行应用构建**

Run:

```powershell
dotnet build src/ChronoIsle.App/ChronoIsle.App.csproj -c Release
```

Expected: Build succeeded，0 errors。

- [ ] **Step 2: 运行项目自动化测试**

Run:

```powershell
dotnet test -c Release
```

Expected: 所有项目测试通过。

- [ ] **Step 3: 查明发布方式与当前程序路径**

读取仓库发布说明；用进程可执行文件路径、开始菜单快捷方式目标或卸载项信息确认正在使用的 ChronoIsle 目录。不得根据目录名猜测目标。

- [ ] **Step 4: 生成 Release 发布目录**

使用仓库已有发布命令；若无专用脚本，则运行：

```powershell
dotnet publish src/ChronoIsle.App/ChronoIsle.App.csproj -c Release -r win-x64 --self-contained true
```

Expected: 发布目录包含新的 `ChronoIsle.exe` 及其运行依赖。

- [ ] **Step 5: 替换并重新启动**

确认目标路径后关闭 ChronoIsle 进程，将新发布目录内容复制覆盖至当前安装目录，并从同一路径重新启动 `ChronoIsle.exe`。不删除用户数据目录。

- [ ] **Step 6: 进行运行验证**

确认灵动岛正常显示和交互；打开 Windows 任务视图和 Alt+Tab，确认新旧灵动岛窗口均不再列出。
