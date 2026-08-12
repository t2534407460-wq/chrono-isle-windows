# 灵动岛置顶、取名配色与托盘双击设计

## 目标

将窗口层级、展开策略和页面配色拆成互不干扰的职责：灵动岛始终位于普通应用窗口之上；图钉只控制展开面板是否自动收起；取名面板在当前主题下保持可读；托盘双击提供可预测的恢复入口。

## 已确认的约束

- 全屏避让继续由现有 `FullscreenAvoidanceService` 和 `LifeIslandWindow.SetFullscreenAvoidance` 处理；本设计不改动它的策略。
- 灵动岛在非全屏避让期间始终置顶，不能再由展开图钉改变窗口的层级。
- 图钉只有两个可见且可达状态。
- 左键单击托盘不执行任何动作；右键菜单保持不变。

## 行为不变量

### 窗口层级

`LifeIslandWindow` 在创建后与运行期间维持 `Topmost = true`。任务栏摆放和展开图钉都不能将它改为非置顶。现有的全屏避让仍可移动或隐藏窗口，并在避让结束后恢复显示。

不再需要“任务栏 + 第三态图钉”才调用原生置顶 API 的状态、计时器或轮询逻辑。

### 展开图钉

图钉只表示展开面板的收起策略：

| 状态 | 图标 | 颜色 | 计时器与失焦 |
| --- | --- | --- | --- |
| `Normal` | 中空图钉 | `Brush.TextSecondary` | 沿用五秒鼠标离开及外部失焦自动收起 |
| `KeepExpanded` | 实心图钉 | `Brush.Accent` | 不自动收起 |

点击顺序固定为 `Normal → KeepExpanded → Normal`。手动收起、恢复默认位置和应用关闭都复位为 `Normal`。图钉不设置 `Topmost`，也不触发任务栏置顶 API。

### 取名页配色

取名空状态的标题显式使用 `Brush.TextPrimary`，说明显式使用 `Brush.TextSecondary`。空状态卡片保留 `Card.Subtle` 的容器样式，保证暗色和浅色主题下文字均由主题资源控制，不依赖 WPF 默认黑色前景。

### 托盘双击

`LifeTrayService` 识别左键双击并发布一个独立事件。`App` 将该事件路由到 `LifeIslandWindow.OpenDefaultExpanded()`：

1. 取消任务栏停靠持久化并恢复默认顶部居中位置；
2. 显示窗口；
3. 展开面板；
4. 保持窗口置顶，但不抢占无关窗口的焦点。

左键单击不执行任何动作；右键菜单不变。左键双击只发布恢复岛事件，不再触发打开主窗口的事件。

## 代码边界

- `src/ChronoIsle.App/Views/LifeIslandWindow.xaml(.cs)`：窗口层级、两态图钉、空状态主题色、公开恢复并展开入口。
- `src/ChronoIsle.App/Services/LifeTrayService.cs`：区分左键单击与双击，并公开恢复岛事件。
- `src/ChronoIsle.App/App.xaml.cs`：订阅托盘恢复事件并调用岛窗口入口。
- `tests/ChronoIsle.UiTests`：以源码契约锁定两态图钉、永久窗口置顶、主题色和托盘事件路由。

## 验证

- 新契约测试先红后绿，分别覆盖窗口层级与图钉职责、取名空状态前景色、托盘双击路由。
- 运行 UI 契约测试全量和应用 Release 构建。
- 手动验收：普通窗口上方显示、图钉两态循环、取名空状态无黑字、双击托盘恢复顶部居中并展开；全屏避让不回归。
