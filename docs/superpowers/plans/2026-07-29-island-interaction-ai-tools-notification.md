# Island Interaction, AI, Tools, and Notification Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 修复灵动岛音乐控件闪烁、悬浮提示、AI 相对时间解析、重复入口、工具页、通知方向和右键菜单，并发布验证后推送 `main`。

**Architecture:** 保留现有 `LifeIslandWindow`、`IslandNotificationWindow` 和 `NamingWindow` 边界。交互与导航只修改现有 WPF 窗口；AI 继续使用 DeepSeek 稳定 `json_object`，通过完整结构提示、本地归一化和现有严格契约保证可解析；所有行为先由单元或 UI 契约测试锁定。

**Tech Stack:** .NET 8、WPF、C#、xUnit、DeepSeek OpenAI-compatible Chat Completions、PowerShell 发布脚本、Git/Gitee。

---

### Task 1: 建立 AI 相对时间结构回归测试

**Files:**
- Modify: `tests/ChronoIsle.Tests/AssistantConversationPlanningV2Tests.cs`
- Test: `tests/ChronoIsle.Tests/AssistantConversationPlanningV2Tests.cs`

- [ ] **Step 1: 写失败测试，证明标量相对时间必须归一化**

在 `OperationParser_NormalizesArgumentsAndDropsExtraFields` 后加入：

```csharp
[Fact]
public void OperationParser_NormalizesScalarRelativeReminder()
{
    const string response = """
        {
          "schemaVersion": 1,
          "command": "create_reminder",
          "arguments": {
            "title": "测试",
            "notes": null,
            "remind": "十分钟后",
            "due": null,
            "recurrence": null,
            "priority": null
          },
          "missingFields": [],
          "ambiguityReasons": []
        }
        """;

    Assert.True(OperationArgumentParserV2.TryNormalizeAndParse(
        response,
        ConversationOperationV2.CreateReminder,
        out var envelope,
        out var error), error);
    var arguments = Assert.IsType<CreateReminderArgumentsV1>(envelope!.Arguments);
    Assert.Equal("十分钟后", arguments.Remind!.RelativeExpression);
    Assert.Equal("十分钟后", arguments.Remind.OriginalText);
}
```

- [ ] **Step 2: 写失败测试，证明请求提示包含完整时间对象**

扩展 `QueueChatClient` 记录调用消息：

```csharp
public IReadOnlyList<ModelMessage> LastMessages { get; private set; } = [];

public Task<string> Complete(
    ProviderSettings provider,
    IEnumerable<ModelMessage> messages,
    bool jsonObject = false)
{
    CallCount++;
    LastMessages = messages.ToArray();
    return Task.FromResult(queue.Dequeue());
}
```

加入：

```csharp
[Fact]
public async Task OperationParser_RequestsConcreteTimeShape()
{
    var chat = new QueueChatClient("""
        {"schemaVersion":1,"command":"create_reminder","arguments":{
          "title":"测试","notes":null,
          "remind":{"localDate":null,"localTime":null,"relativeExpression":"十分钟后",
                    "timeZoneHint":null,"originalText":"十分钟后"},
          "due":null,"recurrence":null,"priority":null},
          "missingFields":[],"ambiguityReasons":[]}
        """);
    var parser = new OperationArgumentParserV2(chat);
    var segment = new ConversationSegmentV2(
        "s1",
        ConversationSegmentKindV2.Command,
        ConversationOperationV2.CreateReminder,
        "十分钟后提醒我测试",
        []);

    var result = await parser.ParseAsync(
        ProviderSettings.Default with { ApiKey = "test" },
        segment,
        AssistantTurnContextV2.Empty);

    Assert.Null(result.ErrorMessage);
    var prompt = chat.LastMessages.First(message =>
        message.Role == "system" &&
        message.Content.Contains("argument parser", StringComparison.Ordinal)).Content;
    Assert.Contains("\"relativeExpression\":null", prompt, StringComparison.Ordinal);
    Assert.Contains("\"originalText\":null", prompt, StringComparison.Ordinal);
}
```

- [ ] **Step 3: 运行测试并确认先失败**

Run:

```powershell
dotnet test tests/ChronoIsle.Tests/ChronoIsle.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~AssistantConversationPlanningV2Tests" --verbosity minimal
```

Expected: `OperationParser_NormalizesScalarRelativeReminder` 或 `OperationParser_RequestsConcreteTimeShape` 失败，证明当前实现没有完整结构约束。

### Task 2: 实现 AI 完整结构提示与标量时间归一化

**Files:**
- Modify: `src/ChronoIsle.App/Services/Commanding/OperationArgumentParserV2.cs`
- Test: `tests/ChronoIsle.Tests/AssistantConversationPlanningV2Tests.cs`

- [ ] **Step 1: 将占位 Schema 改为完整嵌套结构**

在 `OperationArgumentParserV2` 中增加常量：

```csharp
const string TimeSchema =
    """{"localDate":null,"localTime":null,"relativeExpression":null,"timeZoneHint":null,"originalText":null}""";
const string RecurrenceSchema =
    """{"frequency":null,"interval":null,"weekdays":[],"monthDay":null,"end":{"kind":null,"count":null,"untilDate":null}}""";
const string TargetSchema =
    """{"candidateRef":null,"title":null,"kind":null,"timeHint":{"localDate":null,"localTime":null,"relativeExpression":null,"timeZoneHint":null,"originalText":null}}""";
```

把 `SchemaFor` 完整改为：

```csharp
static string SchemaFor(ConversationOperationV2 operation) => operation switch
{
    ConversationOperationV2.CreateTodo =>
        $$"""{"title":null,"notes":null,"due":{{TimeSchema}},"remind":{{TimeSchema}},"recurrence":{{RecurrenceSchema}},"priority":null}""",
    ConversationOperationV2.CreateReminder =>
        $$"""{"title":null,"notes":null,"remind":{{TimeSchema}},"due":{{TimeSchema}},"recurrence":{{RecurrenceSchema}},"priority":null}""",
    ConversationOperationV2.CreateEvent =>
        $$"""{"title":null,"notes":null,"start":{{TimeSchema}},"end":{{TimeSchema}},"remind":{{TimeSchema}}}""",
    ConversationOperationV2.CreateLongTermItem =>
        $$"""{"title":null,"notes":null,"due":{{TimeSchema}},"remind":{{TimeSchema}},"priority":null}""",
    ConversationOperationV2.CreateRecurringTask =>
        $$"""{"title":null,"notes":null,"kind":null,"wallStart":{{TimeSchema}},"recurrence":{{RecurrenceSchema}},"priority":null}""",
    ConversationOperationV2.UpdateTodo =>
        $$"""{"target":{{TargetSchema}},"changes":{"title":null,"notes":null,"due":{{TimeSchema}},"remind":{{TimeSchema}},"priority":null,"clearFields":[]}}""",
    ConversationOperationV2.CompleteTodo or ConversationOperationV2.DeleteTodo =>
        $$"""{"target":{{TargetSchema}}}""",
    ConversationOperationV2.RescheduleItem =>
        $$"""{"target":{{TargetSchema}},"newTime":{{TimeSchema}},"newReminder":{{TimeSchema}}}""",
    ConversationOperationV2.DecomposeGoal =>
        """{"goal":null,"constraints":[],"maxItems":null,"proposedTasks":[]}""",
    _ => throw new ArgumentOutOfRangeException(nameof(operation), "该操作不使用命令参数解析器。")
};
```

- [ ] **Step 2: 明确相对时间的输出规则**

在 `BuildPrompt` 末尾加入：

```csharp
Relative time values must use the complete time object. Put the user's exact wording in both
relativeExpression and originalText. Do not return a time as a JSON string.
```

- [ ] **Step 3: 本地归一化常见标量相对时间**

将 `Time` 改为：

```csharp
static JsonNode? Time(JsonNode? value)
{
    if (value is JsonValue scalar &&
        scalar.TryGetValue<string>(out var text) &&
        !string.IsNullOrWhiteSpace(text))
    {
        var normalized = text.Trim();
        return Pick(new JsonObject
        {
            ["relativeExpression"] = normalized,
            ["originalText"] = normalized
        },
        ("localDate", Node), ("localTime", Node), ("relativeExpression", Node),
        ("timeZoneHint", Node), ("originalText", Node));
    }
    if (value is not JsonObject source) return Node(value);
    return Pick(source,
        ("localDate", Node), ("localTime", Node), ("relativeExpression", Node),
        ("timeZoneHint", Node), ("originalText", Node));
}
```

- [ ] **Step 4: 运行 AI 回归测试**

Run:

```powershell
dotnet test tests/ChronoIsle.Tests/ChronoIsle.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~AssistantConversationPlanningV2Tests|FullyQualifiedName~AssistantRuntimeV2RegressionTests" --verbosity minimal
```

Expected: 所有筛选测试通过，标量“十分钟后”得到包含 `relativeExpression` 和 `originalText` 的 `AssistantTimeExpressionV1`。

- [ ] **Step 5: 提交 AI 修复**

```powershell
git add -- src/ChronoIsle.App/Services/Commanding/OperationArgumentParserV2.cs tests/ChronoIsle.Tests/AssistantConversationPlanningV2Tests.cs
git commit -m "fix: 强化 AI 命令参数结构"
```

### Task 3: 建立音乐捕获与全局悬浮提示回归测试

**Files:**
- Modify: `tests/ChronoIsle.UiTests/MusicLineLyricsContractTests.cs`
- Modify: `tests/ChronoIsle.UiTests/SystemStatusUiContractTests.cs`
- Test: `tests/ChronoIsle.UiTests/ChronoIsle.UiTests.csproj`

- [ ] **Step 1: 锁定 Header 捕获期间不隐藏音乐控件**

在 `CollapsedMusicControls_ArePinnedAfterClickAndExcludedFromHeaderGestures` 增加：

```csharp
Assert.Contains("if (Header.IsMouseCaptured) return;", island, StringComparison.Ordinal);
Assert.Contains(
    "if (!collapsedMediaControlsPinned && !CollapsedMediaTrack.IsMouseOver)",
    island,
    StringComparison.Ordinal);
Assert.Contains("RestoreCollapsedMediaHoverAfterHeaderCapture();", island, StringComparison.Ordinal);
```

- [ ] **Step 2: 锁定灵动岛不再包含可视 ToolTip**

在 `SystemStatusUiContractTests` 加入：

```csharp
[Fact]
public void Island_DoesNotCreateVisualMouseTooltips()
{
    var (xaml, source) = IslandFiles();

    Assert.DoesNotContain("ToolTip=", xaml, StringComparison.Ordinal);
    Assert.DoesNotContain(".ToolTip =", source, StringComparison.Ordinal);
    Assert.Contains(
        "AutomationProperties.Name=\"实时音轨，悬浮显示播放控制\"",
        xaml,
        StringComparison.Ordinal);
}
```

- [ ] **Step 3: 强化通知不依赖音乐模式的契约**

在 `ToastInbox_DoesNotChangeMusicOrTopDockState` 增加：

```csharp
var showToastStart = source.IndexOf("void ShowSystemToast", StringComparison.Ordinal);
var positionStart = source.IndexOf("void PositionNotificationWindow", showToastStart, StringComparison.Ordinal);
var showToast = source[showToastStart..positionStart];
Assert.DoesNotContain("media", showToast, StringComparison.OrdinalIgnoreCase);
Assert.DoesNotContain("musicModeActive", showToast, StringComparison.Ordinal);
Assert.Contains("notificationWindow.ShowMessage(message);", showToast, StringComparison.Ordinal);
```

- [ ] **Step 4: 运行 UI 测试并确认先失败**

Run:

```powershell
dotnet test tests/ChronoIsle.UiTests/ChronoIsle.UiTests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~MusicLineLyricsContractTests|FullyQualifiedName~SystemStatusUiContractTests" --verbosity minimal
```

Expected: 音乐捕获与 ToolTip 测试失败；通知独立性测试保持通过。

### Task 4: 修复音乐闪烁并移除全部灵动岛 ToolTip

**Files:**
- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml`
- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs`
- Test: `tests/ChronoIsle.UiTests/MusicLineLyricsContractTests.cs`
- Test: `tests/ChronoIsle.UiTests/SystemStatusUiContractTests.cs`

- [ ] **Step 1: 忽略 Header 捕获造成的临时 MouseLeave**

把音乐离开处理改为：

```csharp
void CollapsedMediaTrack_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
{
    if (Header.IsMouseCaptured) return;
    if (!collapsedMediaControlsPinned) AnimateCollapsedMediaControls(false);
}
```

增加：

```csharp
void RestoreCollapsedMediaHoverAfterHeaderCapture()
{
    if (!collapsedMediaControlsPinned && !CollapsedMediaTrack.IsMouseOver)
        AnimateCollapsedMediaControls(false);
}
```

在正常 `Header_MouseUp` 的 `Header.ReleaseMouseCapture();` 后调用该方法，保留原有空白点击展开/收起。

- [ ] **Step 2: 删除 XAML ToolTip 属性**

从 `LifeIslandWindow.xaml` 删除全部 `ToolTip="..."` 和 `ToolTip="{Binding ...}"` 属性，保留 `AutomationProperties.Name`。

- [ ] **Step 3: 删除代码生成的 ToolTip**

从 `Refresh`、`RefreshFocusSummary`、`UpdateTelemetryView`、`UpdateCollapsedMediaView`、`BuildCalendar` 和事项摘要构造中删除 `.ToolTip =` 或 `ToolTip =` 赋值。若 `IndicatorTooltip` 仅剩无调用，则删除该私有方法。

- [ ] **Step 4: 运行相关 UI 测试**

Run:

```powershell
dotnet test tests/ChronoIsle.UiTests/ChronoIsle.UiTests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~MusicLineLyricsContractTests|FullyQualifiedName~SystemStatusUiContractTests" --verbosity minimal
```

Expected: 所有筛选测试通过，源码检索不到灵动岛可视 ToolTip。

- [ ] **Step 5: 提交交互修复**

```powershell
git add -- src/ChronoIsle.App/Views/LifeIslandWindow.xaml src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs tests/ChronoIsle.UiTests/MusicLineLyricsContractTests.cs tests/ChronoIsle.UiTests/SystemStatusUiContractTests.cs
git commit -m "fix: 消除音乐控件闪烁和悬浮提示"
```

### Task 5: 建立工具页、重复按钮与右键菜单回归测试

**Files:**
- Modify: `tests/ChronoIsle.UiTests/NamingWindowContractTests.cs`
- Modify: `tests/ChronoIsle.UiTests/IslandContextMenuContractTests.cs`
- Test: `tests/ChronoIsle.UiTests/ChronoIsle.UiTests.csproj`

- [ ] **Step 1: 把旧取名快捷按钮契约改为工具页契约**

用以下测试替换 `Island_naming_action_is_automated_and_not_added_to_context_menu`：

```csharp
[Fact]
public void Island_exposes_a_text_only_tools_tab_with_naming_as_its_first_tool()
{
    var workspace = FindWorkspace();
    var xaml = File.ReadAllText(Path.Combine(
        workspace, "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml"));
    var code = File.ReadAllText(Path.Combine(
        workspace, "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml.cs"));

    Assert.Contains(
        "x:Name=\"ToolsDashboardTab\" Content=\"工具\" Click=\"ToolsTab_Click\"",
        xaml,
        StringComparison.Ordinal);
    Assert.DoesNotContain("Content=\"✎ 工具\"", xaml, StringComparison.Ordinal);
    Assert.Contains("x:Name=\"ToolsPanel\"", xaml, StringComparison.Ordinal);
    Assert.Contains("x:Name=\"NamingToolTab\" Content=\"取名\"", xaml, StringComparison.Ordinal);
    Assert.Contains(
        "AutomationProperties.AutomationId=\"IslandNamingButton\"",
        xaml,
        StringComparison.Ordinal);
    Assert.Contains("void ShowToolsDashboard()", code, StringComparison.Ordinal);
    Assert.Contains("SelectDashboardTab(ToolsDashboardTab);", code, StringComparison.Ordinal);
    Assert.Contains("void NamingTool_Click", code, StringComparison.Ordinal);
}
```

加入首页快捷按钮检查：

```csharp
[Fact]
public void Today_dashboard_does_not_repeat_quick_ask_or_naming()
{
    var workspace = FindWorkspace();
    var code = File.ReadAllText(Path.Combine(
        workspace, "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml.cs"));

    Assert.Contains(
        "new[] { IslandQuickAction.AddTodo, IslandQuickAction.StartFocus, IslandQuickAction.ManageItems }",
        code,
        StringComparison.Ordinal);
    Assert.DoesNotContain("IslandQuickAction.QuickAsk", code, StringComparison.Ordinal);
    Assert.DoesNotContain("IslandQuickAction.Naming", code, StringComparison.Ordinal);
}
```

- [ ] **Step 2: 把右键菜单契约改为精简动作集合**

把 `ContextMenu_ContainsCurrentDashboardAndSettingsActions` 改为：

```csharp
[Fact]
public void ContextMenu_ContainsOnlyManagementSettingsAndStateToggles()
{
    var code = IslandCode();

    Assert.Contains("""
        var contextActions = new[]
        {
            IslandQuickAction.ManageItems,
            IslandQuickAction.Settings,
            IslandQuickAction.PauseReminders,
            IslandQuickAction.ToggleDoNotDisturb,
            IslandQuickAction.ToggleMusicMode,
            IslandQuickAction.ToggleTopDockAutoFold
        };
        """, code, StringComparison.Ordinal);
    foreach (var action in new[]
             {
                 "AddTodo", "AddReminder", "StartFocus", "ViewToday",
                 "ViewCalendar", "ViewStatus", "QuickAsk"
             })
        Assert.DoesNotContain($"IslandQuickAction.{action},", code[
            code.IndexOf("var contextActions", StringComparison.Ordinal)..
            code.IndexOf("foreach (var action in contextActions)", StringComparison.Ordinal)],
            StringComparison.Ordinal);
}
```

- [ ] **Step 3: 运行 UI 测试并确认先失败**

Run:

```powershell
dotnet test tests/ChronoIsle.UiTests/ChronoIsle.UiTests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~NamingWindowContractTests|FullyQualifiedName~IslandContextMenuContractTests" --verbosity minimal
```

Expected: 工具页、首页快捷按钮和精简菜单测试失败。

### Task 6: 实现工具页与精简入口

**Files:**
- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml`
- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs`
- Test: `tests/ChronoIsle.UiTests/NamingWindowContractTests.cs`
- Test: `tests/ChronoIsle.UiTests/IslandContextMenuContractTests.cs`

- [ ] **Step 1: 新增顶部纯文字工具页签**

在 `QuickAskDashboardTab` 后加入：

```xml
<Button x:Name="ToolsDashboardTab" Content="工具" Click="ToolsTab_Click"
        Style="{StaticResource IslandTab}" Margin="7,0,0,0"/>
```

- [ ] **Step 2: 新增可扩展工具面板**

在 `QuickAskPanel` 后加入：

```xml
<Border x:Name="ToolsPanel" Background="{DynamicResource Brush.Card}"
        BorderBrush="{DynamicResource Brush.Stroke}" BorderThickness="1"
        CornerRadius="14" Margin="12,0,12,12" Padding="16"
        Visibility="Collapsed">
  <StackPanel>
    <StackPanel Orientation="Horizontal" Margin="0,0,0,12">
      <Button x:Name="NamingToolTab" Content="取名"
              Style="{StaticResource IslandTab}"/>
    </StackPanel>
    <Border Background="{DynamicResource Brush.Surface}"
            BorderBrush="{DynamicResource Brush.StrokeSoft}"
            BorderThickness="1" CornerRadius="10" Padding="14">
      <Grid>
        <Grid.ColumnDefinitions>
          <ColumnDefinition Width="*"/>
          <ColumnDefinition Width="Auto"/>
        </Grid.ColumnDefinitions>
        <StackPanel>
          <TextBlock Text="取名助手" Foreground="{DynamicResource Brush.TextPrimary}"
                     FontWeight="SemiBold" FontSize="14"/>
          <TextBlock Text="为变量、方法、类型和数据库对象生成稳定命名。"
                     Foreground="{DynamicResource Brush.TextSecondary}"
                     FontSize="11" Margin="0,5,14,0" TextWrapping="Wrap"/>
        </StackPanel>
        <Button Grid.Column="1" Content="打开" Click="NamingTool_Click"
                Style="{StaticResource IslandPrimary}"
                AutomationProperties.AutomationId="IslandNamingButton"
                VerticalAlignment="Center"/>
      </Grid>
    </Border>
  </StackPanel>
</Border>
```

- [ ] **Step 3: 接入工具页显示状态**

增加 `ToolsTab_Click`、`ShowToolsDashboard` 和 `NamingTool_Click`。所有 `Show*Dashboard` 方法都明确设置 `ToolsPanel.Visibility`；`SelectDashboardTab` 的数组加入 `ToolsDashboardTab`：

```csharp
void ToolsTab_Click(object sender, RoutedEventArgs e) => ShowToolsDashboard();

void ShowToolsDashboard()
{
    if (todayPanel is null) return;
    todayPanel.Visibility = Visibility.Collapsed;
    if (weeklyReportPanel is not null) weeklyReportPanel.Visibility = Visibility.Collapsed;
    if (weeklyHistoryPanel is not null) weeklyHistoryPanel.Visibility = Visibility.Collapsed;
    CalendarPanel.Visibility = Visibility.Collapsed;
    TelemetryPanel.Visibility = Visibility.Collapsed;
    QuickAskPanel.Visibility = Visibility.Collapsed;
    ToolsPanel.Visibility = Visibility.Visible;
    SelectDashboardTab(ToolsDashboardTab);
    Touch();
}

void NamingTool_Click(object sender, RoutedEventArgs e) => OpenNaming();
```

- [ ] **Step 4: 删除首页重复快捷按钮**

把首页数组改为：

```csharp
foreach (var action in new[]
         {
             IslandQuickAction.AddTodo,
             IslandQuickAction.StartFocus,
             IslandQuickAction.ManageItems
         })
```

删除原先为动态取名按钮设置 Automation ID 的特殊分支。

- [ ] **Step 5: 精简右键菜单动作与枚举**

`CreateQuickActionMenu` 使用：

```csharp
var contextActions = new[]
{
    IslandQuickAction.ManageItems,
    IslandQuickAction.Settings,
    IslandQuickAction.PauseReminders,
    IslandQuickAction.ToggleDoNotDisturb,
    IslandQuickAction.ToggleMusicMode,
    IslandQuickAction.ToggleTopDockAutoFold
};
foreach (var action in contextActions)
```

从 `IslandQuickAction`、`QuickActionLabel` 和 `RunQuickAction` 删除只服务于已移除入口的 `AddReminder`、`ViewToday`、`ViewCalendar`、`ViewStatus`、`QuickAsk` 和 `Naming`。`StartsQuickActionGroup` 只在 `PauseReminders` 前创建分隔线。

- [ ] **Step 6: 运行工具页与菜单测试**

Run:

```powershell
dotnet test tests/ChronoIsle.UiTests/ChronoIsle.UiTests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~NamingWindowContractTests|FullyQualifiedName~IslandContextMenuContractTests" --verbosity minimal
```

Expected: 所有筛选测试通过。

- [ ] **Step 7: 提交工具页与菜单修复**

```powershell
git add -- src/ChronoIsle.App/Views/LifeIslandWindow.xaml src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs tests/ChronoIsle.UiTests/NamingWindowContractTests.cs tests/ChronoIsle.UiTests/IslandContextMenuContractTests.cs
git commit -m "feat: 将取名入口整合到工具页"
```

### Task 7: 完整验证、发布与真实运行检查

**Files:**
- Verify: `ChronoIsle.sln`
- Verify: `scripts/dev-restart.ps1`
- Preserve: `releases/ChronoIsle-Setup-0.4.2-win-x64.exe`
- Preserve: `releases/ChronoIsle-v0.4.2-win-x64.zip`
- Preserve: `releases/SHA256SUMS.txt`

- [ ] **Step 1: 运行完整核心测试**

Run:

```powershell
dotnet test tests/ChronoIsle.Tests/ChronoIsle.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --verbosity minimal
```

Expected: 全部通过，无失败测试。

- [ ] **Step 2: 运行完整 UI 契约测试**

Run:

```powershell
dotnet test tests/ChronoIsle.UiTests/ChronoIsle.UiTests.csproj -c Release --no-restore -p:NuGetAudit=false --verbosity minimal
```

Expected: 全部通过，无失败测试。

- [ ] **Step 3: 运行 Release 构建**

Run:

```powershell
dotnet build ChronoIsle.sln -c Release --no-restore -p:NuGetAudit=false --verbosity minimal
```

Expected: Build succeeded，0 errors。

- [ ] **Step 4: 发布并重启当前程序**

Run:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/dev-restart.ps1
```

Expected: 旧 `ChronoIsle` 进程退出，Release self-contained 发布成功，新进程从 `src\ChronoIsle.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\ChronoIsle.exe` 启动。

- [ ] **Step 5: 核对部署哈希和进程路径**

Run:

```powershell
$process = Get-CimInstance Win32_Process -Filter "Name='ChronoIsle.exe'"
$process.ExecutablePath
Get-FileHash "$($process.ExecutablePath | Split-Path)\ChronoIsle.dll" -Algorithm SHA256
```

Expected: 进程路径是本次 publish 目录，DLL 时间和哈希与刚发布文件一致。

- [ ] **Step 6: 真实 UI 验收**

在运行程序中检查：

1. 音乐模式下连续点击标题与控件之间空白处，灵动岛正常展开/收起，播放控件不闪烁。
2. 悬浮音乐按钮、折叠态导航、状态灯和日历元素，不出现 ToolTip。
3. 顶部显示纯文字“工具”，进入后看到“取名”页签和“打开”按钮；首页没有重复“快问”和“取名”。
4. 右键菜单不再包含红框七项。
5. 音乐模式保持时触发一条新的 Windows 通知，独立通知卡立即出现；任务栏吸附时显示在灵动岛上方。
6. 输入“十分钟后提醒我测试”，不再出现 `argument_invalid`，提醒进入确认或创建流程。

若任何真实交互失败，返回对应 Task 补测试并修复，不能以契约测试代替。

### Task 8: 最终提交与远程 main 核验

**Files:**
- Verify: all files changed by Tasks 1–7

- [ ] **Step 1: 检查工作区与差异范围**

Run:

```powershell
git status --short --branch
git diff --check origin/main...HEAD
git diff --stat origin/main...HEAD
```

Expected: 只包含设计、计划、实现和测试提交；三个既有发布产物仍为未暂存修改。

- [ ] **Step 2: 提交实施计划**

```powershell
git add -- docs/superpowers/plans/2026-07-29-island-interaction-ai-tools-notification.md
git commit -m "docs: 添加灵动岛综合修复实施计划"
```

- [ ] **Step 3: 推送 main**

Run:

```powershell
git push origin main
```

Expected: Gitee 接受推送，`origin/main` 指向本地 `HEAD`。

- [ ] **Step 4: 核对远程提交**

Run:

```powershell
git fetch origin main
git rev-parse HEAD
git rev-parse origin/main
git status --short --branch
```

Expected: `HEAD` 与 `origin/main` 完全相同；仅保留未暂存的三个发布产物。
