# 时屿客户端同步契约与当前边界

核对日期：2026-10-05。状态：已修改待测试。本页描述实际代码，不能替代完整三端互通验收。

## 项目与接口

- 时屿客户端：当前工作树 `open-island-windows`；独立后端源码 `D:/project/open-island-backend`；自身同步访问 `https://zhuisu.leadjet.com.cn/island-api/v1/sync/`。
- 公共账号：`/identity/v1/`，`island-windows` 客户端。JWT 的 owner、audience、scope 由服务端验证；客户端不会上传邮箱密码或刷新令牌到同步实体。
- 21day：独立 Android 工程 `E:/21day`、独立 `/21day-api/v1/`。习惯来源与命令契约由该工程维护于 `E:/21day/docs/INTEROP_CONTRACT.md`。时屿使用下述来源卡片/命令接口；主数据保留在21day。
- 知识库：原有知识读取权限独立。时屿邮箱登录不自动授予知识库访问权限；knowledge v2 / 用户 ACL 与生产部署本轮未修改、未交付。

## 已实现的时屿同步

`CloudSyncService` 使用已部署的通用 v1 协议：POST `push`，GET `pull?after=<cursor>&maximum=12`，上传/拉取每批12项以限制请求及响应体。每个 operation 为 `{operationId,entityType,entityId,baseRevision,schemaVersion:1,data,deleted}`。缺省语义是 replace；不发送新的计数/计时 Kind，也不需要升级后端公共包。

实体 data 均有 `adapter: "island-desktop-v1"`。当前客户端支持：

| entityType | entityId | data 与行为 |
| --- | --- | --- |
| items | 本地事项 ID | `{adapter,tables:{life_items:[],todos:[],calendar_events:[],single_reminders:[],recurring_reminders:[],archived_todos:[],recurrence_rules:[],assistant_reminder_schedules:[],occurrence_overrides:[]}}`；同一事项的规范模型、旧表投影、周期规则/例外与归档作为一个 aggregate，应用在本地单个事务内 |
| chat_sessions | 会话 ID | `{adapter,row:<会话行>}` |
| chat_messages | 消息 ID | `{adapter,row:<消息行>}` |
| preferences | desktop | `{adapter,values:<可携带的 LifePreferences 字段>}` |

通知已发送标记、Windows 通知权限、任务栏显示器/停靠位置不跨设备复制。修改提醒时刻时重置本机已通知标记，单纯修改标题时保留标记。API Key、知识库密钥、账号令牌、待执行 AI 指令、审计详情、历史报表/完整历史事件、专注会话与资源文件未加入此轮同步；界面明确显示范围，不称为全部数据已同步。

持久待发送 operation 原样重放；成功回执才前移实体基线；pull 的业务应用与 cursor 在同一本地事务中提交。设置文件使用临时文件替换，分页失败时恢复原偏好；进程异常中断产生的差异会保留并重新检测。云端合并/冲突选择前创建本地便携备份。两端均修改时保留本机、展示冲突摘要，用户选择后再次同步。云端永久删除不允许原 ID 复活。

同步为账号中心的手动“立即同步”，不会在登录时静默上传全部本机数据。设置页有未保存的偏好修改时，提示先保存；防止后续保存旧表单覆盖刚下载的云端偏好。

## 21day来源卡片与操作

`Day21HabitClient` 固定访问 GET `/21day-api/v1/habits/cards?timeZoneId=<IANA>` 和 POST `/21day-api/v1/habits/commands`，使用当前同邮箱公共账号 bearer，禁止重定向。入口为灵动岛“21day”、事项工作台“21day 打卡”、历史报表“21day 来源历史”。历史来源页保留原日期和记录值，未记录与真实0分开；不改动本地报表快照和导出范围。

本地SQLite的 `day21_source_cards/meta/requests` 按owner保存缓存、水位、不可变命令和回执，不创建 `life_items`/`todos` 副本。来源卡片含 sourceProject/sourceId/sourceRevision/day/data/actions；缓存完整验证后在单事务替换，登出隐藏缓存，空快照也记录刷新时间。

命令为 `{operation:{operationId,entityType:"habits",entityId,baseRevision,schemaVersion:1,data,deleted:false,baseline,kind},action,day,timeZoneId,deviceId}`。支持 count、confirm、set_total、timer_start、timer_finish、timer_cancel、timer_takeover、archive。计次是atomic count，总量是明确replace；计时完成使用开始日，保留备注、余秒和session。另一设备计时需在线刷新及用户确认后接管；开始时间与总量不变。

请求在POST前持久保存，断网/响应丢失重放原body/operationId；不自动重建请求。已应用回执立即更新来源缓存，即使随后的GET失败也不重复动作。HTTP400和冲突保留原请求，刷新核对后显式结束；同事项未确认操作禁止叠加。生产21day服务先读取owner+完整请求hash的回执，再校验新命令，避免跨午夜归档重放被误拒绝。

## 时屿事项只读来源API与部署交接

新增 GET `/island-api/v1/items/cards`，要求 `project-sync` JWT策略，从sub对应的独立island存储读取快照。响应 `{sourceProject:"island",cards:[{sourceId,sourceRevision,title,status,dueAt}]}`。只投影 schemaVersion1、adapter `island-desktop-v1`、单个匹配ID的非删除规范事项；不返回备注、聊天、设置或密钥，不写入主数据。21day可关联该来源而不接管时屿事项。

独立后端 build0.1.1，依赖已交接的公共包0.1.4，未改公共源码。Linux x64框架依赖包：`D:/project/open-island-backend/artifacts/island-source-0.1.1-sync0.1.4-20261005.zip`；SHA256 `BBC177BDEFB1245A89E58968D0DCB04EAA8B462F3BE98B377BBD30675A5FA5BD`。包仅含运行产物，排除测试、包源与旧artifacts；由“实现项目规划中的三端互通”聊天统一部署。已核对其证据 `E:/21day/artifacts/island-source011-deploy-probe.log`：17个运行文件hash一致、配置未变、health build0.1.1、匿名401、A账号规范事项/字段准确、未知adapter过滤、B账号空、读取前后cursor不变；2个合成账号已清理、未发邮件。服务器备份 `/data/three-project/backups/island-source011-20261004-160125`。本聊天负责Windows产物替换和重启，不发布21day APK、不部署身份/知识服务。

## 完整互通的验收边界

账号中心的时屿同步和21day来源操作是两个独立入口。全量历史事件、专注/资源文件、跨项目事务投递不在当前客户端支持范围；知识密钥入口保留，knowledge v2/ACL客户端联调由另一聊天负责。共享账号与健康检查不等于完整三端人工验收通过。

## 验证

本地两个独立数据空间与协议替身验证事项/归档/周期规则/聊天/偏好往返、无回声、断网重放、冲突显式选择、删除不复活、无效 aggregate 回滚、上传中的本机变更保留、通知状态及重定向阻断。替身测试不等于真实账号的生产往返，需用户在新版账号中心完成注册/登录及立即同步人工验证。

后续来源联调：53项桌面账号/来源/同步/历史/偏好回归通过；独立 `tests/Day21.Contracts` 使用本机21day真实服务源码和0.1.4包，11项操作/领域契约通过（需要 `E:/21day/backend` 工程，未复制/改写其校验规则）；事项API3项投影/真实PG隔离检查通过。账号/注册/同步与来源页真实WPF控件流程1项通过，包含计次/总量0/错误输入保留/另一设备计时按钮、工作台选中状态、来源历史与时屿筛选/导出分开；25张深浅主题及小窗口渲染已生成并核对代表图。生产用户设备的收信、跨端打卡与持续恢复仍待人工验收。

2026-10-05 04:07 Windows新版已备份替换原发布目录App/Knowledge DLL及PDB并重启，新PID32228、Responding及设置loaded/visible日志正常，原启动任务XML已恢复。App DLL SHA256 `0A4FB0360D4C92AB51BE2D5A03DB986F5D9C3EA9BCEFBD6197AECA734D89BCA8`，完整清单见 `tmp/day21-source-qa/restart-day21-source-result.json`。屏幕可用性及真实用户同步仍待人工确认；知识程序集来自另一聊天的独立知识过滤变更。
