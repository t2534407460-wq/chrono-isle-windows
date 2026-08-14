# 周报卡片浅色主题适配 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让灵动岛的本周复盘与历史周报卡片在浅色和深色主题下均使用当前主题调色板，而不是固定深色画刷。

**Architecture:** 两张卡片由 `LifeIslandWindow` 的 partial class 在运行时创建。保留结构和数据刷新，只通过既有 `SetThemeResource` 绑定 `ThemeService` 的动态资源。契约测试读取两份 partial class，防止固定画刷重新进入该组件。

**Tech Stack:** .NET 8、WPF、xUnit、DynamicResource、ThemeService。

---

### Task 1: 写入周报主题契约

**Files:**
- Create: `tests/ChronoIsle.UiTests/WeeklyReportThemeContractTests.cs`
- Read: `src/ChronoIsle.App/Services/Reporting/LifeIslandWeeklyReports.cs`
- Read: `src/ChronoIsle.App/Services/Reporting/LifeIslandWeeklyReportHistory.cs`

- [ ] **Step 1: 新建失败测试**

```csharp
using System.IO;

namespace ChronoIsle.UiTests;

public sealed class WeeklyReportThemeContractTests
{
    [Fact]
    public void Weekly_report_cards_use_dynamic_theme_resources()
    {
        var root = FindRepositoryRoot();
        var weekly = File.ReadAllText(Path.Combine(root, "src", "ChronoIsle.App", "Services", "Reporting", "LifeIslandWeeklyReports.cs"));
        var history = File.ReadAllText(Path.Combine(root, "src", "ChronoIsle.App", "Services", "Reporting", "LifeIslandWeeklyReportHistory.cs"));

        Assert.Contains("SetThemeResource(weeklyReportPanel, Border.BackgroundProperty, \"Brush.Card\");", weekly, StringComparison.Ordinal);
        Assert.Contains("SetThemeResource(weeklyReportPanel, Border.BorderBrushProperty, \"Brush.Stroke\");", weekly, StringComparison.Ordinal);
        Assert.Contains("SetThemeResource(weeklyHistoryPanel, Border.BackgroundProperty, \"Brush.Card\");", history, StringComparison.Ordinal);
        Assert.Contains("SetThemeResource(weeklyHistoryPanel, Border.BorderBrushProperty, \"Brush.Stroke\");", history, StringComparison.Ordinal);
        Assert.DoesNotContain("new SolidColorBrush", weekly, StringComparison.Ordinal);
        Assert.DoesNotContain("new SolidColorBrush", history, StringComparison.Ordinal);
        Assert.DoesNotContain("Brushes.White", weekly, StringComparison.Ordinal);
        Assert.DoesNotContain("Brushes.White", history, StringComparison.Ordinal);
    }

    static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "ChronoIsle.App"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
```

- [ ] **Step 2: 确认红灯**

```powershell
dotnet test .\tests\ChronoIsle.UiTests\ChronoIsle.UiTests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~WeeklyReportThemeContractTests" --verbosity minimal
```

Expected: 1 项失败，提示缺少 `Brush.Card` 绑定并仍包含固定画刷。

### Task 2: 绑定本周复盘卡片的动态资源

**Files:**
- Modify: `src/ChronoIsle.App/Services/Reporting/LifeIslandWeeklyReports.cs:20-82`

- [ ] **Step 1: 只替换画刷来源**

保留字号、边距、`RenderWeeklyReports`、计时器、插入索引和按钮行为；删除所有固定前景色与边框/背景画刷。创建文本元素后加入：

```csharp
SetThemeResource(weeklyReportText, TextBlock.ForegroundProperty, "Brush.Success");
SetThemeResource(monthlyReportText, TextBlock.ForegroundProperty, "Brush.TextSecondary");
SetThemeResource(nextWeekPlanText, TextBlock.ForegroundProperty, "Brush.Accent");
SetThemeResource(headingText, TextBlock.ForegroundProperty, "Brush.TextPrimary");
SetThemeResource(weeklyReportPanel, Border.BackgroundProperty, "Brush.Card");
SetThemeResource(weeklyReportPanel, Border.BorderBrushProperty, "Brush.Stroke");
```

`headingText` 是原“本周复盘”标题先保存的局部 `TextBlock`，再加入 `heading`。

### Task 3: 绑定历史周报卡片的动态资源

**Files:**
- Modify: `src/ChronoIsle.App/Services/Reporting/LifeIslandWeeklyReportHistory.cs:19-54`

- [ ] **Step 1: 只替换画刷来源**

保留历史文本、布局、刷新计时器和插入位置；删除固定画刷，创建元素后加入：

```csharp
SetThemeResource(weeklyHistoryText, TextBlock.ForegroundProperty, "Brush.TextSecondary");
SetThemeResource(historyHeading, TextBlock.ForegroundProperty, "Brush.TextPrimary");
SetThemeResource(weeklyHistoryPanel, Border.BackgroundProperty, "Brush.Card");
SetThemeResource(weeklyHistoryPanel, Border.BorderBrushProperty, "Brush.Stroke");
```

`historyHeading` 是原“历史周报”标题先保存的局部 `TextBlock`，再加入卡片的 `StackPanel`。

- [ ] **Step 2: 确认定向测试转绿**

```powershell
dotnet test .\tests\ChronoIsle.UiTests\ChronoIsle.UiTests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~WeeklyReportThemeContractTests" --verbosity minimal
```

Expected: 1/1 通过。

### Task 4: 回归验证并提交

**Files:**
- Modify: `src/ChronoIsle.App/Services/Reporting/LifeIslandWeeklyReports.cs`
- Modify: `src/ChronoIsle.App/Services/Reporting/LifeIslandWeeklyReportHistory.cs`
- Create: `tests/ChronoIsle.UiTests/WeeklyReportThemeContractTests.cs`

- [ ] **Step 1: 运行完整 UI 契约测试**

```powershell
$env:MSBUILDDISABLENODEREUSE='1'; dotnet test .\tests\ChronoIsle.UiTests\ChronoIsle.UiTests.csproj -c Release --no-restore -p:NuGetAudit=false -m:1 -nodeReuse:false --logger "console;verbosity=minimal"
```

Expected: 新增周报主题测试通过；若远程 `2a8bf6b` 的 3 个已知旧断言仍失败，记录名称并确认没有新增失败。

- [ ] **Step 2: 构建、检查并提交**

```powershell
dotnet build .\src\ChronoIsle.App\ChronoIsle.App.csproj -c Release --no-restore -v:minimal; git diff --check; git add -- src/ChronoIsle.App/Services/Reporting/LifeIslandWeeklyReports.cs src/ChronoIsle.App/Services/Reporting/LifeIslandWeeklyReportHistory.cs tests/ChronoIsle.UiTests/WeeklyReportThemeContractTests.cs; git diff --cached --check; git commit -m "fix: adapt weekly reports to light theme"
```

Expected: Release 构建 0 警告、0 错误；仅三份实现/测试文件提交，既有未跟踪计划文档保持未暂存。
