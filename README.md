<div align="center">

# 时屿 ChronoIsle

**常驻 Windows 桌面的本地生活与效率助手**

[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Windows 10/11](https://img.shields.io/badge/Windows-10%20%2F%2011-0078D4.svg)]()
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

把待办、日程、提醒、专注和 AI 助手放进一个可以停靠在屏幕顶部或任务栏中的灵动岛。

</div>

---

## 项目简介

时屿（ChronoIsle）是一个使用 WPF 和 .NET 8 构建的 Windows 桌面应用。它以灵动岛作为常驻入口，并提供完整的事项管理、日历、提醒、自然语言助手、专注计时和本地数据管理能力。

应用默认将业务数据保存在本机。除非主动使用 AI 对话或取名助手，否则日历、提醒、事项管理、专注和报表等核心能力不需要连接模型服务。

## 主要功能

### 灵动岛

- 支持屏幕顶部、自由悬浮和底部任务栏三种位置。
- 拖到屏幕顶部或底部任务栏附近时自动吸附。
- 任务栏模式可自由左右移动，并自动绕开任务栏应用图标和右侧状态区域。
- 当前任务栏没有足够空位时自动退化为自由移动，不会把灵动岛卡死在图标旁。
- 顶部模式向下展开，任务栏模式向上展开；展开过程中保持头部锚点不动。
- 支持多显示器，任务栏显示器和水平相对位置会跨重启保存。
- 单击展开或折叠；快速双击恢复到主屏幕顶部中央的初始位置。
- 鼠标移出后 5 秒自动折叠；切换到其他应用时立即折叠。
- 任务栏模式保持最高置顶层级，不会被任务栏覆盖。

### 今天与日历

- 查看今日事项、逾期事项、待整理 Inbox 和下一行动。
- 月历显示待办、日程、提醒及中国法定节假日、调休工作日。
- 在灵动岛内快速添加待办或提醒。
- 检测同一天的日程时间冲突。
- 根据可用时间、预计耗时和当前能量推荐可执行事项。

### 事项管理

- 管理待办、日程、单次提醒和周期提醒。
- 支持每天、工作日、法定工作日、法定节假日和指定星期等重复方式。
- 支持完成、归档、批量归档和详情跳转。
- 可记录预计耗时、能量等级、优先级等任务属性。

### AI 助手

- 使用自然语言创建、安排和查询事项，例如“明早九点提醒我开会”。
- 涉及写入的操作会先生成待确认卡片，确认后才执行。
- 支持干练、温柔、轻松和专注四种回复人格；人格只影响措辞，不改变执行规则。
- 默认预设为 DeepSeek，也兼容 OpenAI Chat Completions 格式的模型服务。
- API Key 使用 Windows DPAPI 加密后保存在当前用户目录。

### 提醒与专注

- 到期时显示 Windows 通知，并同步在灵动岛中提示。
- 支持勿扰和延迟提醒汇总。
- 支持开始、暂停、继续和结束专注计时。
- 专注结束后可选择完成对应待办或保留未完成状态。

### 周报与复盘

- 汇总本周完成、逾期和高优先级事项。
- 保存历史周报并生成下周行动建议。
- 今日面板提供基于本地数据的优先事项建议。

### 取名助手

- 根据中文语义生成变量、函数、类型、数据库对象、文件目录、常量或通用名称。
- 为每个候选稳定生成多种常用命名格式。
- 支持一键复制推荐名称及各格式结果。

### 本地数据与托盘

- 使用 SQLite 保存事项、会话、提醒、专注记录、报表和审计信息。
- 支持创建及恢复便携备份。
- 支持导入、导出 iCalendar（ICS）文件。
- 托盘菜单可快速打开助手、事项管理、取名助手和设置，也可切换通知与勿扰状态。
- 支持当前用户开机自启。

## 灵动岛操作

| 操作 | 结果 |
| --- | --- |
| 单击头部 | 展开或折叠 |
| 快速双击头部 | 恢复到主屏幕顶部中央 |
| 拖到屏幕顶部 28 DIP 内 | 吸附到顶部 |
| 拖到底部任务栏 28 DIP 内 | 吸附到任务栏 |
| 从已吸附位置纵向拖出 48 DIP | 解除吸附 |
| 在任务栏中左右拖动 | 自动选择图标之间的可用位置 |
| 右键头部 | 打开快捷操作菜单 |
| 鼠标移出展开面板 | 5 秒后折叠 |
| 切换到其他应用 | 立即折叠 |

> 当前仅支持底部横向任务栏。顶部、侧边或无法检测到有效任务栏区域时不会进入任务栏吸附模式。

## 安装与运行

### 使用发布版本

从 [Gitee Releases](https://gitee.com/Tr11111/chrono-isle-windows/releases) 下载以下任一版本：

- `ChronoIsle-Setup-X.Y.Z-win-x64.exe`：推荐，安装到当前用户目录。
- `ChronoIsle-vX.Y.Z-win-x64.zip`：便携版，解压后运行 `ChronoIsle.exe`。

发布包为 self-contained，不需要另外安装 .NET Runtime。当前目标平台为 Windows 10 2004（Build 19041）及以上版本的 x64 系统。

> 当前发布包未进行代码签名，Windows SmartScreen 可能显示安全提示。请只从可信的项目发布页下载。

### 首次使用

1. 启动 `ChronoIsle.exe`。
2. 单击灵动岛，查看“今天”或“月历”。
3. 右键灵动岛打开快捷菜单，或通过托盘进入完整助手。
4. 如需使用 AI 能力，在“设置”中填写 Base URL、模型和 API Key，并先执行“测试连接”。
5. 将灵动岛拖到屏幕顶部或底部任务栏，选择适合自己的停靠方式。

从 OpenIsland 升级时，首次启动会把 `%APPDATA%\OpenIsland` 和 `%LOCALAPPDATA%\OpenIsland` 中尚未存在的新目录文件复制到 ChronoIsle；旧目录不会删除，新目录中已有的数据也不会被覆盖。

## 从源码构建

### 环境要求

- Windows 10/11 x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- PowerShell 7 或 Windows PowerShell

### 获取并运行

```powershell
git clone https://gitee.com/Tr11111/chrono-isle-windows.git
cd chrono-isle-windows
dotnet restore ChronoIsle.sln
dotnet build ChronoIsle.sln --no-restore
dotnet run --project src\ChronoIsle.App\ChronoIsle.App.csproj --no-build
```

### 测试

```powershell
dotnet test ChronoIsle.sln --no-restore
```

也可以分别运行逻辑测试和 UI 契约测试：

```powershell
dotnet test tests\ChronoIsle.Tests\ChronoIsle.Tests.csproj --no-restore
dotnet test tests\ChronoIsle.UiTests\ChronoIsle.UiTests.csproj --no-restore
```

### 本地发布并重启

仓库提供了开发重启脚本。它会停止由本项目构建目录启动的实例、发布 self-contained Release、更新桌面快捷方式并启动新版本：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\dev-restart.ps1
```

手动发布命令：

```powershell
dotnet publish src\ChronoIsle.App\ChronoIsle.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=false `
  -o publish\ChronoIsle
```

## 项目结构

```text
ChronoIsle.sln
├─ src/
│  ├─ ChronoIsle.App/       当前 WPF 桌面应用
│  ├─ ChronoIsle.Core/      共享基础组件
│  ├─ ChronoIsle.Hooks/     命令行 Hook 组件
│  └─ ChronoIsle.Setup/     Hook 安装与配置工具
├─ tests/
│  ├─ ChronoIsle.Tests/     领域、存储、调度与定位测试
│  └─ ChronoIsle.UiTests/   WPF 窗口和交互契约测试
├─ scripts/                 开发、发布和重启脚本
├─ installer/               Inno Setup 安装脚本
└─ docs/                    设计、工程约束和实现记录
```

当前桌面应用的主要运行链路：

```text
WPF 窗口
  ├─ 灵动岛 / 托盘
  ├─ AI 对话
  ├─ 事项管理
  ├─ 设置与取名助手
  │
  ▼
应用服务
  ├─ 事项、日历、提醒与专注
  ├─ AI 命令解析与确认
  ├─ 周报、推荐与审计
  └─ 备份及 ICS 导入导出
  │
  ├────────► SQLite / 本地 JSON
  ├────────► Windows 通知
  └────────► 用户配置的 OpenAI 兼容模型服务
```

## 数据与隐私

默认数据目录：

```text
%APPDATA%\ChronoIsle\
├─ life-assistant.db       事项、会话、提醒、专注和报表
├─ life-preferences.json   通知、人格和灵动岛位置
└─ provider.json           模型配置及 DPAPI 加密后的 API Key
```

- 核心业务数据保存在本机 SQLite 数据库。
- 便携备份不包含外部账户令牌；恢复后需要重新配置相关凭据。
- 只有在使用 AI 对话、自然语言命令或取名助手时，相关输入才会发送到用户配置的模型服务。
- 本地审计只记录命令、结果状态和时间，不展示事项标题或原始聊天内容。

## 当前边界

- 任务栏吸附仅针对底部横向任务栏。
- 任务栏图标避让依赖 Windows 可访问性信息；无法识别图标或没有空位时会回退为自由移动。
- 当前版本以本地数据为主，第三方云日历同步不是默认能力。
- AI 功能需要用户自行配置兼容的模型服务，模型可用性和数据策略由对应服务提供方决定。

## 参与开发

提交代码前建议至少运行：

```powershell
dotnet build ChronoIsle.sln --no-restore
dotnet test ChronoIsle.sln --no-restore
```

请保持改动范围清晰，并为定位、调度、存储或关键 UI 行为补充相应测试。

## 许可证

[MIT](LICENSE) © 2025 ludiwangfpga；ChronoIsle 修改 © 2026 Tr11111
