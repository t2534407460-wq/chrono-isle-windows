# Windows 通知接入

时屿使用 Windows 的 `UserNotificationListener`，在同一 UI 调度线程每 500ms 异步读取新的应用通知，并在主岛旁显示独立通知卡片。卡片沿用当前深色、浅色和强调色资源，包含应用图标（不可用时显示首字）、来源、时间、标题、正文、打开应用和清除操作。

## 启用

1. 发布 `ChronoIsle.App` 到正常安装目录。
2. 在经过用户同意的管理员 PowerShell 中运行 `installer/package-identity/build-and-register-development-identity.ps1 -PublishDirectory <发布目录>`，安装附带 `uap3:userNotificationListener` 能力的外部位置包。脚本复用或生成 `CN=ChronoIsle Development` 本地开发签名，并将其公钥证书加入本机 `TrustedPeople`。这是本地开发安装步骤，正式分发应使用正式签名。
3. 重启时屿，在 Windows 的通知访问提示中允许访问。可在“设置 → Windows 通知”查看连接状态；拒绝后可通过“通知访问权限”打开系统设置，再选择“重新连接”。开关遵循原有 `ToastInboxEnabled` 偏好，修改后保存生效。

Windows 可能需要一次 UAC 管理员确认和一次通知访问许可。没有通知访问权限或监听失败时，时屿不会关闭系统横幅。

## 显示与交互

- 顶部或自由位置在主岛下方显示；任务栏吸附时在上方显示，并限制在当前工作区。
- 不改变主岛尺寸、展开状态、音乐模式、顶部折叠或任务栏吸附。
- 显示五秒后收起；鼠标悬停暂停计时，离开后重新计时。新消息立即替换旧卡片，不积压旧消息。
- 超时收起和“打开应用”均保留 Windows 通知中心的原消息。“清除”只删除当前这一条通知。
- “打开应用”启动来源应用，不承诺跳转到原通知指定的会话或支持内联回复。
- 初次连接与重新连接只显示之后的新通知，不重放通知中心历史。
- 尊重时屿的勿扰偏好。卡片不会在主岛不可见时强行出现。

## 横幅替换和恢复

Windows 没有公开的第三方横幅替换接口。接管期间只为通知设置中已注册的应用设置 `ShowBanner=0`，并在监听中为新增应用补齐。支持 Windows PowerShell 等使用 `{KnownFolderGuid}\path\app.exe` 标识的传统桌面应用，仅修改应用叶节点，不改文件夹节点。不会写全局通知开关、勿扰模式、声音或 `ShowInActionCenter`，也不会在收到消息时自动删除通知。

包身份为当前用户的 `Software\Microsoft\Windows\CurrentVersion\Notifications\Settings` 分支声明注册表写入隔离例外，其他注册表路径保持原配置。该限定范围配置需要 Windows build 20348 或更新版本；本机已在 Windows 11 上安装验证。

每次写入前先将原有值（包括原本不存在的值）原子保存到 `%APPDATA%\ChronoIsle\notification-banner-backup.json`。关闭接入、权限撤销、监听失败或正常退出时恢复；意外终止时在下一次启动恢复。接管期间被用户主动改回的横幅设置保持用户的新值。

不同 Windows 版本对注册表设置的响应可能不同。新应用的第一条通知或系统缓存未刷新的场景仍可能显示原生横幅，需要实机验收；采用应用自绘弹窗、没有进入 Windows 通知中心的消息不属于该 API 的接入范围。

## 验证

自动化覆盖通知文本解析、轮询所属调度线程、日志脱敏、横幅设置恢复/崩溃恢复/失败回退/新增应用、WPF 深浅主题实际渲染、长文本/空正文/快速替换、工作区边界，以及应用接线和包身份契约。

人工测试需要：

- 完成 Windows 权限授权，分别由已有应用和新安装应用发通知，核对只出现灵动岛卡片及通知中心仍有消息。
- 连续来消息、音乐播放、顶部折叠、任务栏吸附、混合 DPI 多屏下检查展示位置与悬停。
- 打开来源应用、清除单条消息，核对操作范围；禁用接入、撤销权限和退出，确认系统横幅设置恢复。

## 技术参考

- [参考项目的通知接入](https://github.com/sadeeshasathsara/dynamic-island-on-windows)
- [Microsoft：通知监听与授权](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/notification-listener)
- [Microsoft：通知权限所属 uap3 命名空间](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-uap3-capability-manual)
- [Microsoft：MSIX 证书信任错误](https://learn.microsoft.com/en-us/windows/msix/msix-troubleshooting-guide)
