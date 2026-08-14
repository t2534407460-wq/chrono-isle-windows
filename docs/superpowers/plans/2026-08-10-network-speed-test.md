# 网络测速工具 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在灵动岛“工具”页提供用户主动发起的国内节点下载、上传测速和五个平台 HTTPS 连接延迟检测，并用主题同步的仪表盘展示真实进度。

**Architecture:** `NetworkSpeedTestService` 是唯一持有取消令牌、HTTP 请求和测速快照的单例；它把节点选择、平台探测和两个传输阶段发布为不可变快照。`LifeIslandWindow` 只订阅快照并更新现有工具页中的 XAML 控件，所有装饰色通过动态主题资源而非硬编码颜色获得。

**Tech Stack:** .NET 8、WPF、现有 `HttpClient`、`CancellationTokenSource`、xUnit、现有 UI 合同测试。

---

## 文件结构

| 文件 | 责任 |
| --- | --- |
| `src/ChronoIsle.App/Services/NetworkSpeedTestService.cs` | 国内节点选择、平台响应头计时、下载/上传字节计数、取消、状态快照。 |
| `src/ChronoIsle.App/App.xaml.cs` | 将测速服务注册为单例。 |
| `src/ChronoIsle.App/ChronoIsle.App.csproj` | 显式编译新增服务文件；项目关闭默认编译项。 |
| `src/ChronoIsle.App/Resources/Controls.xaml` | 五个本地平台图标 Geometry。 |
| `src/ChronoIsle.App/Views/LifeIslandWindow.xaml` | 工具二级页签、仪表盘、三项结果卡、平台卡和主题同步按钮样式。 |
| `src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs` | 服务订阅、页签切换、按钮启动/取消和快照到控件的映射。 |
| `tests/ChronoIsle.Tests/NetworkSpeedTestServiceTests.cs` | 纯测速逻辑、HTTP 超时/失败和取消的回归测试。 |
| `tests/ChronoIsle.UiTests/NetworkSpeedTestUiContractTests.cs` | 工具页结构、动画、图标和 `Brush.Accent` 动态资源约束。 |

### Task 1: 定义并用测试锁定测速服务的纯行为

**Files:**

- Create: `tests/ChronoIsle.Tests/NetworkSpeedTestServiceTests.cs`
- Create: `src/ChronoIsle.App/Services/NetworkSpeedTestService.cs`
- Modify: `src/ChronoIsle.App/ChronoIsle.App.csproj:37`

- [ ] **Step 1: 写入会失败的节点选择和速率计算测试。**

```csharp
using ChronoIsle.App.Services;

namespace ChronoIsle.Tests;

public sealed class NetworkSpeedTestServiceTests
{
    [Fact]
    public void SelectNode_UsesLowestMedianAcrossSuccessfulDomesticCandidates()
    {
        var tsinghua = new NetworkSpeedTestNode("清华大学", new Uri("https://iptv.tsinghua.edu.cn/st/"));
        var wuhan = new NetworkSpeedTestNode("武汉大学图书馆", new Uri("https://www.lib.whu.edu.cn/speedtest/backend/"));

        var selected = NetworkSpeedTestService.SelectNode(
        [
            new NetworkSpeedTestNodeProbe(tsinghua, [10, 100]),
            new NetworkSpeedTestNodeProbe(wuhan, [60, 60])
        ]);

        Assert.Equal(tsinghua, selected);
    }

    [Theory]
    [InlineData(12_500_000L, 2.0, 50.0)]
    [InlineData(0L, 5.0, 0.0)]
    [InlineData(1L, 0.0, 0.0)]
    public void ToMegabitsPerSecond_UsesActualBytesAndElapsedTime(long bytes, double seconds, double expected)
    {
        Assert.Equal(expected, NetworkSpeedTestService.ToMegabitsPerSecond(bytes, TimeSpan.FromSeconds(seconds)), 3);
    }
}
```

- [ ] **Step 2: 运行测试，确认缺少类型和方法而失败。**

Run: `dotnet test tests\ChronoIsle.Tests\ChronoIsle.Tests.csproj --filter "FullyQualifiedName~NetworkSpeedTestServiceTests"`

Expected: FAIL，错误指向尚不存在的 `NetworkSpeedTestService`、`NetworkSpeedTestNode` 和 `NetworkSpeedTestNodeProbe`。

- [ ] **Step 3: 以最小公开快照和内部测试类型实现纯逻辑。**

```csharp
namespace ChronoIsle.App.Services;

public enum NetworkSpeedTestPhase { Idle, SelectingNode, MeasuringPlatforms, MeasuringDownload, MeasuringUpload, Completed, Unavailable, Cancelled }

public sealed record NetworkSpeedTestNode(string Name, Uri BaseUri);
internal sealed record NetworkSpeedTestNodeProbe(NetworkSpeedTestNode Node, IReadOnlyList<long> Latencies);

public sealed record PlatformConnectionLatency(string Key, string Name, long? Milliseconds);
public sealed record NetworkSpeedTestSnapshot(
    NetworkSpeedTestPhase Phase,
    string Status,
    double? DownloadMegabitsPerSecond,
    double? UploadMegabitsPerSecond,
    long? NodeLatencyMilliseconds,
    IReadOnlyList<PlatformConnectionLatency> Platforms)
{
    public bool IsRunning => Phase is NetworkSpeedTestPhase.SelectingNode
        or NetworkSpeedTestPhase.MeasuringPlatforms
        or NetworkSpeedTestPhase.MeasuringDownload
        or NetworkSpeedTestPhase.MeasuringUpload;

    public static NetworkSpeedTestSnapshot Idle { get; } = new(
        NetworkSpeedTestPhase.Idle, "准备测速", null, null, null, []);
}

public sealed class NetworkSpeedTestService
{
    internal static NetworkSpeedTestNode? SelectNode(IEnumerable<NetworkSpeedTestNodeProbe> probes) =>
        probes.Where(probe => probe.Latencies.Count > 0)
            .Select(probe => new { probe.Node, Median = Median(probe.Latencies) })
            .OrderBy(candidate => candidate.Median)
            .Select(candidate => candidate.Node)
            .FirstOrDefault();

    static double Median(IReadOnlyList<long> values)
    {
        var ordered = values.Order().ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0 ? (ordered[middle - 1] + ordered[middle]) / 2d : ordered[middle];
    }

    internal static double ToMegabitsPerSecond(long bytes, TimeSpan elapsed) =>
        bytes <= 0 || elapsed <= TimeSpan.Zero ? 0 : bytes * 8d / elapsed.TotalSeconds / 1_000_000d;
}
```

在 `.csproj` 的显式 `Services\*.cs` 列表中追加 `Services\NetworkSpeedTestService.cs`；不要启用默认编译项，也不要修改现有 `SystemTelemetryService.cs`。

- [ ] **Step 4: 运行测试，确认纯逻辑通过。**

Run: `dotnet test tests\ChronoIsle.Tests\ChronoIsle.Tests.csproj --filter "FullyQualifiedName~NetworkSpeedTestServiceTests"`

Expected: PASS，4 个测试用例全绿。

- [ ] **Step 5: 提交这一可独立验证的服务骨架。**

```powershell
git add src/ChronoIsle.App/Services/NetworkSpeedTestService.cs src/ChronoIsle.App/ChronoIsle.App.csproj tests/ChronoIsle.Tests/NetworkSpeedTestServiceTests.cs
git commit -m "feat: add speed test service model"
```

### Task 2: 实现受限、可取消的真实网络测量

**Files:**

- Modify: `src/ChronoIsle.App/Services/NetworkSpeedTestService.cs`
- Modify: `tests/ChronoIsle.Tests/NetworkSpeedTestServiceTests.cs`

- [ ] **Step 1: 为节点全失败、平台超时和取消写入失败测试。**

在测试文件中添加一个只使用 `HttpMessageHandler` 的假处理器，以及以下事实测试：

```csharp
[Fact]
public async Task StartAsync_WhenAllNodeProbesFail_PublishesFriendlyUnavailableSnapshot()
{
    using var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

    await service.StartAsync();

    Assert.Equal(NetworkSpeedTestPhase.Unavailable, service.Current.Phase);
    Assert.Equal("暂时没有可用的国内测速节点，请稍后重试", service.Current.Status);
    Assert.Null(service.Current.DownloadMegabitsPerSecond);
    Assert.Null(service.Current.UploadMegabitsPerSecond);
}

[Fact]
public async Task StartAsync_WhenPlatformProbeTimesOut_ReportsNoMeasurementInsteadOfExceptionText()
{
    using var service = CreateService(async (_, token) =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return new HttpResponseMessage(HttpStatusCode.OK);
    });

    await service.StartAsync();

    Assert.Contains(service.Current.Platforms, item => item.Key == "douyin" && item.Milliseconds is null);
    Assert.DoesNotContain("Exception", service.Current.Status, StringComparison.OrdinalIgnoreCase);
}

[Fact]
public async Task Cancel_StopsTheActiveRunAndPublishesCancelled()
{
    using var service = CreateService(async (_, token) =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return new HttpResponseMessage(HttpStatusCode.OK);
    });

    var run = service.StartAsync();
    service.Cancel();
    await run;

    Assert.Equal(NetworkSpeedTestPhase.Cancelled, service.Current.Phase);
    Assert.Equal("测速已取消", service.Current.Status);
}
```

`CreateService` 必须传入 20ms 探测超时和 20ms 传输时长的内部测试时间表，避免测试访问真实网络或等待 1.5 秒。

- [ ] **Step 2: 运行测试，确认网络会话 API 尚不存在或不符合状态约定。**

Run: `dotnet test tests\ChronoIsle.Tests\ChronoIsle.Tests.csproj --filter "FullyQualifiedName~NetworkSpeedTestServiceTests"`

Expected: FAIL，错误指向 `StartAsync`、`Cancel`、`Current` 或内部测试构造器缺失。

- [ ] **Step 3: 添加服务会话、固定节点和固定平台定义。**

服务中使用以下固定值；这些不是用户设置，也不从远程 JSON 下载：

```csharp
static readonly NetworkSpeedTestNode[] DomesticNodes =
[
    new("清华大学", new Uri("https://iptv.tsinghua.edu.cn/st/")),
    new("武汉大学图书馆", new Uri("https://www.lib.whu.edu.cn/speedtest/backend/"))
];

static readonly (string Key, string Name, Uri Uri)[] PlatformProbes =
[
    ("league", "英雄联盟", new Uri("https://lol.qq.com/favicon.ico")),
    ("douyin", "抖音", new Uri("https://www.douyin.com/favicon.ico")),
    ("jd", "京东", new Uri("https://www.jd.com/favicon.ico")),
    ("ctrip", "携程", new Uri("https://www.ctrip.com/favicon.ico")),
    ("toutiao", "今日头条", new Uri("https://www.toutiao.com/favicon.ico"))
];

const int WorkerCount = 4;
static readonly TimeSpan NodeAndPlatformTimeout = TimeSpan.FromMilliseconds(1500);
static readonly TimeSpan TransferDuration = TimeSpan.FromSeconds(7);
static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(250);
```

实现以下会话规则：

```csharp
public async Task StartAsync()
{
    if (Current.IsRunning) return;
    cancellation?.Dispose();
    cancellation = new CancellationTokenSource();
    try
    {
        Publish(Current with { Phase = NetworkSpeedTestPhase.SelectingNode, Status = "正在选择国内节点" });
        var selected = await SelectReachableNodeAsync(cancellation.Token);
        if (selected is null)
        {
            Publish(new(NetworkSpeedTestPhase.Unavailable, "暂时没有可用的国内测速节点，请稍后重试", null, null, null, []));
            return;
        }
        var node = selected.Node;
        var nodeLatency = (long)Math.Round(Median(selected.Latencies), MidpointRounding.AwayFromZero);
        Publish(Current with
        {
            Phase = NetworkSpeedTestPhase.MeasuringPlatforms,
            Status = "正在检测平台连接延迟",
            NodeLatencyMilliseconds = nodeLatency
        });
        var platforms = await MeasurePlatformsAsync(cancellation.Token);
        Publish(Current with { Platforms = platforms, Status = "正在测试下载" });
        var download = await MeasureTransferAsync(node, isDownload: true, cancellation.Token);
        Publish(Current with { Phase = NetworkSpeedTestPhase.MeasuringUpload, Status = "正在测试上传", DownloadMegabitsPerSecond = download });
        var upload = await MeasureTransferAsync(node, isDownload: false, cancellation.Token);
        Publish(Current with { Phase = NetworkSpeedTestPhase.Completed, Status = "测速完成", UploadMegabitsPerSecond = upload });
    }
    catch (OperationCanceledException)
    {
        Publish(Current with { Phase = NetworkSpeedTestPhase.Cancelled, Status = "测速已取消" });
    }
    catch
    {
        Publish(Current with { Phase = NetworkSpeedTestPhase.Unavailable, Status = "测速暂时不可用，请稍后重试" });
    }
}

public void Cancel() => cancellation?.Cancel();
```

`SelectReachableNodeAsync` 对每个节点并行执行两次 `GET empty.php?x={Guid.NewGuid():N}`，用 `HttpCompletionOption.ResponseHeadersRead` 和独立超时令牌计时，只收集成功 HTTP 状态的毫秒数，并返回包含节点和中位延迟的 `NetworkSpeedTestNodeProbe`。在发布“正在检测平台连接延迟”快照时把该中位值写入 `NodeLatencyMilliseconds`。`MeasurePlatformsAsync` 使用同样的响应头计时方式探测五个平台，并为超时/失败返回 `Milliseconds = null`。

`MeasureTransferAsync` 启动四个工作流，下载工作流反复读取 `garbage.php?ckSize=10485760&x={Guid.NewGuid():N}` 的流，上传工作流反复以 1MiB 随机缓冲区 POST 到 `empty.php?x={Guid.NewGuid():N}`。每个工作流在阶段截止时间或取消时退出；用 `Interlocked.Add` 汇总实际字节数，并每 250ms 发布 `ToMegabitsPerSecond` 的瞬时阶段值。所有 `HttpResponseMessage`、内容流和取消令牌都使用 `using`/`await using` 释放。

- [ ] **Step 4: 运行测试，确认无网络测试全绿。**

Run: `dotnet test tests\ChronoIsle.Tests\ChronoIsle.Tests.csproj --filter "FullyQualifiedName~NetworkSpeedTestServiceTests"`

Expected: PASS，包含节点不可用、平台超时与取消测试；测试不向真实地址发送请求。

- [ ] **Step 5: 提交网络会话实现。**

```powershell
git add src/ChronoIsle.App/Services/NetworkSpeedTestService.cs tests/ChronoIsle.Tests/NetworkSpeedTestServiceTests.cs
git commit -m "feat: measure domestic network speed"
```

### Task 3: 先锁定工具页的测速 UI 合同

**Files:**

- Create: `tests/ChronoIsle.UiTests/NetworkSpeedTestUiContractTests.cs`
- Modify: `src/ChronoIsle.App/Resources/Controls.xaml`
- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml`

- [ ] **Step 1: 写入会失败的 XAML 合同测试。**

```csharp
using System.IO;

namespace ChronoIsle.UiTests;

public sealed class NetworkSpeedTestUiContractTests
{
    [Fact]
    public void Tools_dashboard_exposes_network_speed_test_tabs_gauge_results_and_platform_cards()
    {
        var root = FindWorkspace();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml"));
        var controls = File.ReadAllText(Path.Combine(root, "src", "ChronoIsle.App", "Resources", "Controls.xaml"));

        Assert.Contains("x:Name=\"NetworkSpeedTestToolTab\" Content=\"网络测速\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NetworkSpeedTestToolPanel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NetworkSpeedTestGaugeNeedleRotation\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NetworkSpeedTestSpinnerRotation\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NetworkSpeedTestStartButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource IslandNetworkSpeedTestPrimary}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource Brush.Accent}", xaml, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource Brush.AccentSoft}", xaml, StringComparison.Ordinal);

        foreach (var name in new[] { "Download", "Upload", "NodeLatency", "League", "Douyin", "Jd", "Ctrip", "Toutiao" })
            Assert.Contains($"NetworkSpeedTest{name}", xaml, StringComparison.Ordinal);
        foreach (var key in new[] { "Icon.PlatformLeague", "Icon.PlatformDouyin", "Icon.PlatformJd", "Icon.PlatformCtrip", "Icon.PlatformToutiao" })
            Assert.Contains($"x:Key=\"{key}\"", controls, StringComparison.Ordinal);
    }

    static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ChronoIsle.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("ChronoIsle.sln was not found from the UI test host.");
    }
}
```

- [ ] **Step 2: 运行测试，确认测速视图尚不存在而失败。**

Run: `dotnet test tests\ChronoIsle.UiTests\ChronoIsle.UiTests.csproj --filter "FullyQualifiedName~NetworkSpeedTestUiContractTests"`

Expected: FAIL，缺少网络测速页签、仪表盘命名元素和本地图标 Geometry。

- [ ] **Step 3: 添加本地图标资源和主题同步的测速面板。**

在 `Controls.xaml` 现有 `Icon.NetworkGlobe` 后添加五个 20px 可缩放 Geometry 键：

```xml
<Geometry x:Key="Icon.PlatformLeague">M4,3 H8 V17 H17 V21 H4 Z</Geometry>
<Geometry x:Key="Icon.PlatformDouyin">M14,3 V14 A4,4 0 1 1 11,10 V6 L20,9 V13 Z</Geometry>
<Geometry x:Key="Icon.PlatformJd">M4,7 H20 V20 H4 Z M8,7 C8,3 16,3 16,7</Geometry>
<Geometry x:Key="Icon.PlatformCtrip">M8,5 V3 H16 V5 M4,7 H20 V20 H4 Z M4,12 H20</Geometry>
<Geometry x:Key="Icon.PlatformToutiao">M4,4 H20 V20 H4 Z M7,8 H17 M7,12 H17 M7,16 H13</Geometry>
```

在 `LifeIslandWindow.xaml` 的 `Window.Resources` 添加局部样式，保留 `IslandPrimary` 的圆角、字体和按压行为，仅替换主题资源：

```xml
<Style x:Key="IslandNetworkSpeedTestPrimary" TargetType="Button" BasedOn="{StaticResource IslandPrimary}">
  <Setter Property="Background" Value="{DynamicResource Brush.Accent}"/>
  <Setter Property="BorderBrush" Value="{DynamicResource Brush.Accent}"/>
  <Setter Property="Foreground" Value="{DynamicResource Brush.Island}"/>
</Style>
```

将现有命名卡包入 `x:Name="NamingToolPanel"` 的容器，在它旁边放置初始为 `Collapsed` 的 `NetworkSpeedTestToolPanel`。该面板必须含有：两个二级页签、中心半圆 `Path` 外环、`NetworkSpeedTestSpinnerRotation` 和 `NetworkSpeedTestGaugeNeedleRotation`、下载/上传/节点延迟的命名值、五张分别引用 `Icon.PlatformLeague`、`Icon.PlatformDouyin`、`Icon.PlatformJd`、`Icon.PlatformCtrip`、`Icon.PlatformToutiao` 的 `Path` 平台卡，以及 `NetworkSpeedTestStartButton`。仪表盘、指针、图标装饰色、页签选中态和按钮只使用 `Brush.Accent`、`Brush.AccentSoft`、`Brush.Text*`、`Brush.Warning`、`Brush.Danger` 等动态资源；不得写入十六进制主题色或网络图片 URL。

- [ ] **Step 4: 运行 UI 合同测试，确认结构和主题约束通过。**

Run: `dotnet test tests\ChronoIsle.UiTests\ChronoIsle.UiTests.csproj --filter "FullyQualifiedName~NetworkSpeedTestUiContractTests"`

Expected: PASS，1 个测试全绿。

- [ ] **Step 5: 提交仅包含 UI 结构和图标的变更。**

```powershell
git add src/ChronoIsle.App/Resources/Controls.xaml src/ChronoIsle.App/Views/LifeIslandWindow.xaml tests/ChronoIsle.UiTests/NetworkSpeedTestUiContractTests.cs
git commit -m "feat: add network speed test dashboard"
```

### Task 4: 连接服务、页签和真实动画

**Files:**

- Modify: `src/ChronoIsle.App/App.xaml.cs:43-45`
- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs:46-56,172-204,1887-1911,1987-1991`
- Modify: `tests/ChronoIsle.UiTests/NetworkSpeedTestUiContractTests.cs`

- [ ] **Step 1: 扩展 UI 合同测试，先锁定 DI、订阅、取消和动画入口。**

在现有 UI 测试中读取 `App.xaml.cs` 与 `LifeIslandWindow.xaml.cs`，加入以下断言：

```csharp
Assert.Contains("collection.AddSingleton<NetworkSpeedTestService>();", appSource, StringComparison.Ordinal);
Assert.Contains("networkSpeedTest.SnapshotChanged +=", source, StringComparison.Ordinal);
Assert.Contains("networkSpeedTest.SnapshotChanged -=", source, StringComparison.Ordinal);
Assert.Contains("networkSpeedTest.Cancel();", source, StringComparison.Ordinal);
Assert.Contains("async void NetworkSpeedTestStartButton_Click", source, StringComparison.Ordinal);
Assert.Contains("void ShowNetworkSpeedTestTool()", source, StringComparison.Ordinal);
Assert.Contains("void UpdateNetworkSpeedTestView(NetworkSpeedTestSnapshot snapshot)", source, StringComparison.Ordinal);
Assert.Contains("NetworkSpeedTestGaugeNeedleRotation.BeginAnimation", source, StringComparison.Ordinal);
Assert.Contains("NetworkSpeedTestSpinnerRotation.BeginAnimation", source, StringComparison.Ordinal);
Assert.DoesNotContain("#39C98B", source, StringComparison.OrdinalIgnoreCase);
```

- [ ] **Step 2: 运行 UI 合同测试，确认代码入口尚不存在而失败。**

Run: `dotnet test tests\ChronoIsle.UiTests\ChronoIsle.UiTests.csproj --filter "FullyQualifiedName~NetworkSpeedTestUiContractTests"`

Expected: FAIL，缺少测速服务注册、订阅、视图更新和动画调用。

- [ ] **Step 3: 注册服务并实现最小 UI 桥接。**

在 `App.xaml.cs` 中与 `SystemTelemetryService` 同级注册：

```csharp
collection.AddSingleton<NetworkSpeedTestService>();
```

在 `LifeIslandWindow` 构造器参数、字段和关闭处理里加入服务；事件订阅和释放必须使用以下形态：

```csharp
this.networkSpeedTest = networkSpeedTest;
networkSpeedTestSnapshotChanged = snapshot =>
    Dispatcher.BeginInvoke(() => UpdateNetworkSpeedTestView(snapshot));
networkSpeedTest.SnapshotChanged += networkSpeedTestSnapshotChanged;

Closed += (_, _) =>
{
    networkSpeedTest.Cancel();
    networkSpeedTest.SnapshotChanged -= networkSpeedTestSnapshotChanged;
};
```

`networkSpeedTestSnapshotChanged` 是 `readonly Action<NetworkSpeedTestSnapshot>` 字段，订阅与解除订阅必须使用同一个字段。

实现页签和按钮行为：

```csharp
void NamingToolTab_Click(object sender, RoutedEventArgs e) => ShowNamingTool();
void NetworkSpeedTestToolTab_Click(object sender, RoutedEventArgs e) => ShowNetworkSpeedTestTool();

async void NetworkSpeedTestStartButton_Click(object sender, RoutedEventArgs e)
{
    if (networkSpeedTest.Current.IsRunning) networkSpeedTest.Cancel();
    else await networkSpeedTest.StartAsync();
}
```

`ShowNamingTool` 与 `ShowNetworkSpeedTestTool` 只互斥切换两个工具容器并通过现有 `SelectDashboardTab` 风格设置二级页签。`UpdateNetworkSpeedTestView` 按快照更新按钮文案（开始测速/取消测速/重新测速）、阶段文字、三项数值和五个平台值；`null` 显示“未测得”。选择节点阶段对外环使用无限旋转，下载和上传阶段把速率按半圆范围插值到指针角度，完成/取消/不可用时停止两个动画。所有 `BeginAnimation` 都在 UI 线程调用。

- [ ] **Step 4: 运行目标 UI 测试和服务测试。**

Run: `dotnet test tests\ChronoIsle.UiTests\ChronoIsle.UiTests.csproj --filter "FullyQualifiedName~NetworkSpeedTestUiContractTests"`

Then run: `dotnet test tests\ChronoIsle.Tests\ChronoIsle.Tests.csproj --filter "FullyQualifiedName~NetworkSpeedTestServiceTests"`

Expected: 两个命令均 PASS；不需要真实网络。

- [ ] **Step 5: 提交服务与界面桥接。**

```powershell
git add src/ChronoIsle.App/App.xaml.cs src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs tests/ChronoIsle.UiTests/NetworkSpeedTestUiContractTests.cs
git commit -m "feat: run network speed tests from tools"
```

### Task 5: 全量验证与发布前真实网络验收

**Files:**

- Modify only if a failure directly exposes a defect in the files above; do not alter `SystemTelemetryService.cs` or `SystemStatusUiContractTests.cs`, which are pre-existing user changes.

- [ ] **Step 1: 运行全部自动测试。**

Run: `dotnet test ChronoIsle.sln -c Release --no-restore`

Expected: PASS，0 failed。

- [ ] **Step 2: 构建发布配置。**

Run: `dotnet build ChronoIsle.sln -c Release --no-restore`

Expected: Build succeeded，0 Error(s)。

- [ ] **Step 3: 在联网桌面应用中手动验收。**

执行以下情景并分别记录结果：

1. 打开“工具 → 网络测速”，确认默认仍停留在“取名”，点击“网络测速”后不改变顶层导航。
2. 从两种不同的设置色彩方案间切换，确认仪表盘外环、指针、平台图标装饰、选中页签和测速按钮同时换色；既有“打开”和“发送”按钮外观不变。
3. 点击“开始测速”，确认阶段顺序是选节点、平台探测、下载、上传、完成；仪表盘数值只在下载/上传阶段变化。
4. 在任一阶段点击“取消测速”，确认请求停止、文案变为“测速已取消”，没有错误码、异常或伪造的 `0 Mbps`。
5. 允许完整运行，确认至少一个国内节点成功，下载、上传、节点延迟和五个平台卡均有真实结果或“未测得”。

- [ ] **Step 4: 仅在前述自动与手动验证均记录后提交最终修正。**

```powershell
git status --short
git add src/ChronoIsle.App/App.xaml.cs src/ChronoIsle.App/ChronoIsle.App.csproj src/ChronoIsle.App/Resources/Controls.xaml src/ChronoIsle.App/Services/NetworkSpeedTestService.cs src/ChronoIsle.App/Views/LifeIslandWindow.xaml src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs tests/ChronoIsle.Tests/NetworkSpeedTestServiceTests.cs tests/ChronoIsle.UiTests/NetworkSpeedTestUiContractTests.cs
git commit -m "feat: add domestic network speed test"
```

## 计划自查

- 规格覆盖：Task 1-2 实现国内节点、下载上传、平台延迟、取消和友好失败；Task 3-4 实现工具页、动画、图标、主题同步和 DI；Task 5 分别验证自动测试、构建和真实联网行为。
- 范围控制：计划不添加数据库表、设置项、计划任务、远程节点清单下载、第三方 CLI、NuGet 依赖或对既有常态网络遥测的改动。
- 名称一致：服务、快照、阶段、节点、平台和 XAML 控件名称在所有任务中保持一致；`NetworkSpeedTestService` 是唯一网络测速入口。
