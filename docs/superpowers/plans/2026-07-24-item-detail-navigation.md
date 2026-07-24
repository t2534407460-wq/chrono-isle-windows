# 事项详情定位 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 从灵动岛的事项展示直接打开事项管理，并定位且高亮对应事项。

**Architecture:** 灵动岛通过事件传递事项 ID 与类型，`App` 将请求交给主窗口打开管理对话框。管理窗口记录每个事项行，待内容布局完成后滚动和闪烁目标行。

**Tech Stack:** WPF、C#、现有 `TodayDashboardItem` 与 `ManagedLifeItem` 数据模型。

---

### Task 1: 事项跳转请求与岛内点击入口

**Files:**
- Modify: `src/OpenIsland.App/Views/LifeIslandWindow.xaml.cs`
- Modify: `src/OpenIsland.App/App.xaml.cs`
- Modify: `src/OpenIsland.App/Views/LifeMainWindow.xaml.cs`

- [ ] 添加携带 ID、类型的详情请求事件。
- [ ] 将日历事项文本和今日概览事项文本设为点击入口；按钮点击不冒泡到入口。
- [ ] 应用层打开主窗口，并将目标传给事项管理对话框。

### Task 2: 管理页定位与闪烁

**Files:**
- Modify: `src/OpenIsland.App/Views/LifeManagementWindow.xaml`
- Modify: `src/OpenIsland.App/Views/LifeManagementWindow.xaml.cs`

- [ ] 为事项列表滚动容器命名，并记录每项对应的行容器。
- [ ] 在对话框打开前接收目标，刷新后将目标滚动到可见区域。
- [ ] 对目标行执行两次边框高亮动画；找不到项时不报错。

### Task 3: 验证

**Files:**
- Test: `tests/OpenIsland.Tests/TodayDashboardServiceTests.cs`

- [ ] 运行 Release 构建和与仪表盘相关的定向测试。
- [ ] 手动确认日程/概览点击、滚动定位和闪烁效果。
