# 周报卡片浅色主题适配设计

## 目标

让灵动岛“本周复盘”和“历史周报”动态卡片在浅色主题下使用与页面一致的浅色卡面、主题描边和深色文字，同时保留深色主题的现有可读性。

## 范围

- 只修改 `LifeIslandWeeklyReports.cs` 与 `LifeIslandWeeklyReportHistory.cs` 中创建卡片的主题画刷绑定。
- 不改变周报生成、历史读取、定时刷新、按钮点击或卡片布局。
- 不新增专用主题色板；复用现有 `ThemeService` 动态资源。

## 设计

- 两个卡片的背景使用 `Brush.Card`，边框使用 `Brush.Stroke`。
- 标题使用 `Brush.TextPrimary`，正文使用 `Brush.TextSecondary`。
- 本周完成摘要可继续表示成功状态，但改为 `Brush.Success`，使浅色和深色主题均由当前调色板决定。
- 下周计划文字使用 `Brush.Accent`，使其跟随用户选择的强调色。
- 运行时通过 `SetResourceReference` 或现有 `SetThemeResource` 绑定资源，避免 `SolidColorBrush(Color.FromRgb(...))` 和 `Brushes.White` 等固定深色方案。

## 验证

- 新增 UI 契约测试，验证两个周报文件使用上述动态资源，且不再包含固定 RGB 画刷或白色标题画刷。
- 运行定向契约测试、完整 UI 契约测试和 Release 构建。
- 手动切换浅色/深色主题，确认两张卡片的背景、边框、标题和正文均立即更新。
