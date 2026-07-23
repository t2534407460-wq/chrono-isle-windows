# Open Island 本地功能测试计划

更新日期：2026-07-21  
适用版本：当前工作区（.NET 8 / WPF / SQLite / xUnit）  
质量目标：SQLite 是唯一事实源；模型输出只是不可信的建议，必须经本地严格校验、确认和事务执行。任何解析失败、时间无效或外部服务失败均不得产生业务写入。

## 1. 现状与实现判定

| 能力 | 当前判定 | 证据 / 备注 |
|---|---|---|
| 待办、提醒、日程持久化 | 后端已实现，待完整 UI 验收 | `LifeDataService`、`LifeSchemaMigrator`、`CanonicalLifeItemWriter`；已有迁移与并发测试 |
| AI 结构化命令本地边界 | 已实现 | `AssistantCommandEnvelopeJson` 严格反序列化；`AssistantCommandPipeline` 负责策略、确认、审计和本地执行 |
| 相对时间、时区与 DST | 已实现 | 命令管线与 `OccurrenceTimeResolver` 可注入时钟；`LifeDataService` 接受 `Func<DateTime>`，`ReminderService.ScanOnceAsync(now)` 接受调用方时刻 |
| 单次/重复提醒投递 | 后端已实现 | `ReminderDeliveryStore`、`NotificationOutboxStore`、`NotificationDispatcher` 有独占领取、重试和幂等键测试 |
| SQLite 初始化及旧库迁移 | 已实现 | 迁移幂等、失败回滚、旧四类数据迁移已有自动化测试 |
| Windows 通知 | 已接线，需隔离验收 | 已有 `INotificationTransport` 用于新出站；旧 `WindowsNotificationService` 仍需适配 fake |
| WPF 悬浮窗与生活助手 UI | 已实现（P2 手工验收保留） | `tests/OpenIsland.UiTests` 已验证关键 `AutomationId` 合约；专用 UIA 命令在隔离进程中验证悬浮窗可见性 |
| DeepSeek 客户端隔离 | 已实现（OpenAI-compatible） | `IChatCompletionClient` 抽象已注入命令解析与执行服务；生产端仍使用 `OpenAiChatService`，测试可注入 fake，自动化无需真实 API |
| 启动、休眠、重启恢复 | 已实现（补发策略待产品确认） | `ReminderService.ScanOnceAsync(now)` 可由启动恢复和测试显式调用；提醒领取、出站与幂等状态持久化 |
| 外部同步 | 不纳入本轮 | 仓库中存在 Microsoft Graph / ICS 代码，但按本轮范围不作为发布阻断测试对象 |

## 2. 范围与排除

本轮覆盖 WPF 前端、ViewModel、命令解析与本地校验、待办/提醒/日程、时间与重复、SQLite、通知、启动恢复、异常、基础性能与 Windows 10 19041+ 兼容性。

明确排除 Microsoft To Do、Outlook Calendar、Microsoft Graph、OAuth、ICS、云同步、多用户与专项安全测试。已有相应代码的测试可以保留，但不得把其通过当作本轮生活助手发布条件。

## 3. 需求缺口与待确认规则

1. **AI 协议归属**：采用兼容 OpenAI Chat Completions 的通用 `IChatCompletionClient`，保留可配置端点以支持 DeepSeek；生产端由 `OpenAiChatService` 实现，首批自动化必须注入 fake，绝不调用真实 API。
2. **模糊时间**：`晚点`、`下午`、`下班后`必须返回 `Ambiguous` / 待澄清，不能猜定时间；需把产品文案和 API 状态名固定。
3. **重复月末**：已确认每月 31 日在短月跳过该月，绝不移至月末；`MonthlyRecurrenceCalculator` 以本地墙上时间计算下一次 occurrence。
4. **重复事项的修改范围**：已按 `docs/engineering-freeze/01-life-item-model.md` 与 `05-reminder-scheduling.md` 冻结：`Snooze` 只变更当前 occurrence 的提醒时间，`SkipOccurrence` 只写当前 occurrence override，`Reschedule` 才修改事项/规则并递增版本；单期变化不得改写系列主项。UI 入口和持久化 API 必须遵循该边界。
5. **提醒补发策略**：已按 `docs/engineering-freeze/05-reminder-scheduling.md` 冻结：重启扫描过期 lease、待处理 outbox 与 deferred 队列，不得重置为全新通知；退出勿扰仅投递一次聚合摘要，不逐条补弹。
6. **同名目标**：当前策略以命中数量决定澄清/确认；需确认同名同类型且同时间时是否可由时间提示唯一定位。
7. **删除语义**：已按 `docs/engineering-freeze/01-life-item-model.md` 冻结：删除保留 tombstone 供撤销、审计、报告和同步使用；恢复是显式操作；仅显式清空回收站可物理删除。默认查询继续隐藏已删除记录。

## 4. 架构可测性分析

可直接测试的纯/近纯边界包括：命令契约、执行策略、时间解析、重复 occurrence、迁移器、SQLite 写队列、出站存储和通知调度器。它们已大多支持临时数据库、`Func<DateTimeOffset>` 或接口 fake。

需最小化改造后再测的边界：

- 模型客户端已抽象为 `IChatCompletionClient`，`AssistantActionService` 与命令解析服务通过该接口接收客户端；测试 fake 覆盖固定文本、空串、超时和畸形 JSON。
- 提醒扫描已抽成可调用一次的 `ScanOnceAsync(now)`，后台循环只负责定时调用；通知经 `INotificationTransport` 出站，自动化可使用 fake。
- 将仍依赖 `DateTime.Now` 的业务时钟收敛为 `TimeProvider` 或已采用的 `Func<DateTimeOffset>`，UI 展示时间可保留系统时钟。
- 已为 WPF 根窗、展开输入、发送、确认和快速添加控件补稳定 `AutomationId`；独立进程与专用测试数据库的 UI 自动化工程已建立，并提供可选的 UIA 冒烟命令。

## 5. 测试分层与项目结构

```text
tests/OpenIsland.Tests/                 # xUnit：领域、命令、SQLite、服务和 ViewModel
tests/OpenIsland.Tests/TestDoubles/     # FakeClock、FakeDeepSeekClient、FakeNotificationSender、临时 DB fixture
tests/OpenIsland.UiTests/               # 后续：少量 Windows UIA 冒烟，单独分类
tests/OpenIsland.SmokeTest/             # 已有：发布后启动/退出探针
```

所有数据库测试创建 GUID 命名的临时 SQLite 文件；清理 WAL/SHM，绝不连接 `%AppData%` 或用户生产库。测试不使用真实网络、不调用 `Thread.Sleep`，时间均由 fake 时钟推进。一个测试只验证一个主要行为。

## 6. 功能测试矩阵

字段含义：`自动` 为可自动化；`阻断` 为失败即阻断发布。

| 编号 | 模块 | 标题 | 优先级 | 前置条件 | 输入 | 操作步骤 | 预期结果 | 自动 | 阻断 |
|---|---|---|---|---|---|---|---|---|---|
| FUN-001 | 待办 | 创建普通待办 | P0 | 空临时库 | 标题“买牛奶” | 提交已验证 create_todo | 仅新增一条 Pending Todo | 是 | 是 |
| FUN-002 | 提醒 | 创建带提醒待办 | P0 | 固定时钟 | 标题、合法提醒时刻 | 提交并确认 | 项目和提醒原子写入 | 是 | 是 |
| FUN-003 | 日程 | 创建日程 | P0 | 空临时库 | 标题、开始、结束 | 提交合法 create_event | Event 含有效 start/end | 是 | 是 |
| FUN-004 | 编辑 | 仅修改目标字段 | P1 | 已有待办 | 新标题 | 提交 update 并确认 | 目标字段变化，其他字段和其他项目不变 | 是 | 是 |
| FUN-005 | 状态 | 完成与恢复 | P1 | 已有待办 | 目标选择器 | 完成后恢复 | 状态往返，完成时间一致 | 是 | 否 |
| FUN-006 | 删除 | 删除/取消不得误删 | P0 | 两个同类项目 | 精确目标 | 删除并查询 | 仅目标软删除且默认查询不可见 | 是 | 是 |
| FUN-007 | 延期 | 延期提醒 | P1 | 待提醒项目 | +5、+10、自定义 | 延期 | 新到期时间准确且不重复投递 | 是 | 否 |
| FUN-008 | 查询 | 今日/本周/本月与筛选 | P1 | 跨范围种子数据 | range、状态、类型、标题 | 分别查询 | 结果边界和排序正确 | 是 | 否 |
| FUN-009 | 目标选择 | 同名/模糊目标 | P0 | 两条同名项目 | “删除那个任务” | 提交命令 | 不写库，返回澄清/确认 | 是 | 是 |
| FUN-010 | 持久化 | 重启后数据存在 | P0 | 文件型临时库 | 创建项目 | 释放并重建服务 | 项目仍可查询 | 是 | 是 |

## 7. AI 语料与结构化命令矩阵

| 编号 | 模块 | 标题 | 优先级 | 前置条件 | 输入 | 操作步骤 | 预期结果 | 自动 | 阻断 |
|---|---|---|---|---|---|---|---|---|---|
| AI-001 | 意图 | 明确提醒 | P0 | Fake AI | “明天下午三点提醒我交电费” | 解析→本地校验→确认 | 合法 reminder，执行后仅一条记录 | 是 | 是 |
| AI-002 | 意图 | 相对提醒 | P0 | 固定时钟 | “一小时后提醒我喝水” | 同上 | 本地解析为 now+1h，不信任模型绝对时刻 | 是 | 是 |
| AI-003 | 意图 | 周期会议 | P1 | Fake AI | “每周一上午九点开周会” | 解析并确认 | Weekly 规则含 Monday/09:00 | 是 | 否 |
| AI-004 | 修改 | 修改日期时间 | P1 | 已有会议 | “把明天的会议改到下午四点” | 解析、选中、确认 | 仅目标时间更新 | 是 | 否 |
| AI-005 | 删除 | 删除待办 | P0 | 已有“买牛奶” | “删除买牛奶这个待办” | 解析、确认 | 仅匹配项目删除 | 是 | 是 |
| AI-006 | 查询 | 今日事项 | P1 | 有今日数据 | “查一下今天有哪些任务” | 解析→执行 | 无创建/删除副作用，返回查询结果 | 是 | 否 |
| AI-007 | 模糊 | 晚点提醒 | P0 | Fake AI | “晚点提醒我” | 解析→策略 | `Ambiguous`/澄清，零业务写入 | 是 | 是 |
| AI-008 | 模糊 | 下周开会 | P0 | Fake AI | “下周开会” | 同上 | 缺少日期/时间，零业务写入 | 是 | 是 |
| AI-009 | 模糊 | 删除那个任务 | P0 | 两个候选 | “把那个任务删掉” | 同上 | 返回澄清，不删除 | 是 | 是 |
| AI-010 | 上下文 | 多轮更正与取消 | P1 | 会话 fake | 五句连续语料（明天→三点→后天→八点→取消） | 逐句处理 | 最终取消，未确认动作不落业务库 | 是 | 否 |
| AI-011 | 失败 | 空/非 JSON/截断 JSON | P0 | Fake AI | 三种响应 | 处理响应 | 可理解失败；零业务写入 | 是 | 是 |
| AI-012 | 失败 | Markdown JSON、缺字段、错误类型、未知 intent | P0 | Fake AI | 六种畸形响应 | 处理响应 | 严格拒绝或 Incomplete；零业务写入 | 是 | 是 |
| AI-013 | 失败 | 非法日期/开始晚于结束 | P0 | Fake AI | 日期非法、event end<start | 本地验证 | 返回失败；事务无半条数据 | 是 | 是 |
| AI-014 | 普通聊天 | 非任务对话 | P0 | Fake AI | “今天天气真好” | 处理响应 | 仅聊天消息，无 life_items 写入 | 是 | 是 |
| AI-015 | 配置 | API Key 缺失 | P0 | 空 key | 发送消息 | 调用服务/VM | UI 显示错误，应用继续可用，无业务写入 | 是 | 是 |

真实 DeepSeek 连通性仅以 `External` 分类单独执行，默认 CI 与本地自动化均排除。

## 8. 时间与重复规则矩阵

| 编号 | 模块 | 标题 | 优先级 | 前置条件 | 输入 | 操作步骤 | 预期结果 | 自动 | 阻断 |
|---|---|---|---|---|---|---|---|---|---|
| TIME-001 | 相对时间 | 10 分钟/1 小时后 | P0 | 固定 now | 两种表达 | 解析 | 精确加时且 UTC 一致 | 是 | 是 |
| TIME-002 | 日期 | 明天/后天/下周一 | P0 | 月末前固定 now | 三种表达 | 解析 | 日期跨日、跨月正确 | 是 | 是 |
| TIME-003 | 边界 | 跨年、月末、闰年 | P0 | 2028-02-28 等 | 日期表达 | 解析 | 合法日期准确，非法拒绝 | 是 | 是 |
| TIME-004 | 校验 | 过去时间与 end<start | P0 | 固定 now | 过去提醒、倒置日程 | 提交 | 失败且无写入 | 是 | 是 |
| TIME-005 | 时区 | DST gap/overlap | P1 | IANA 可用 | New York 两个边界 | 解析 occurrence | gap 前移到有效时间，overlap 取较早 instant | 是 | 否 |
| REC-001 | 重复 | 每日下一次 | P0 | 固定 now | Daily interval=1 | 计算下一次 | 结果正确 | 是 | 是 |
| REC-002 | 重复 | 每周跨周、多工作日 | P0 | 周日固定 now | Mon/Fri | 计算下一次 | 选择最早未来 occurrence | 是 | 是 |
| REC-003 | 重复 | 每月 31 日 | P0 | 多个短月 | Monthly day=31 | 计算 | 短月跳过，不移至月末 | 是 | 是 |
| REC-004 | 重复 | 次数/截止日期 | P1 | 规则种子 | Count、Until | 展开 occurrence | 不超过上限/截止点 | 是 | 否 |
| REC-005 | occurrence | 跳过、改单次、删单次/改整组 | P1 | 已确认规则模型 | 各操作 | 执行 | 稳定键覆盖同一 occurrence，系列主项不变；悬浮窗入口可执行跳过与改单次改期 | 是 | 否 |

## 9. SQLite 与迁移矩阵

| 编号 | 模块 | 标题 | 优先级 | 前置条件 | 输入 | 操作步骤 | 预期结果 | 自动 | 阻断 |
|---|---|---|---|---|---|---|---|---|---|
| DB-001 | 初始化 | 首次/空库/重复初始化 | P0 | 新临时文件 | 无 | 连续初始化 | schema 可用且幂等 | 是 | 是 |
| DB-002 | 迁移 | 顺序执行与旧数据升级 | P0 | 旧 todos/events/reminders 表 | 合法旧行 | 迁移两次 | 仅首次迁移，审计准确 | 是 | 是 |
| DB-003 | 事务 | 迁移失败回滚 | P0 | 含一条非法旧行 | end<start | 迁移 | 新 schema 与部分复制均回滚 | 是 | 是 |
| DB-004 | CRUD | 日期、状态和软删除查询 | P1 | 种子数据 | 多种项目 | CRUD/查询 | 默认不见软删，筛选正确 | 是 | 否 |
| DB-005 | 并发 | RowVersion 冲突 | P0 | 两个写者 | 旧版本更新 | 并发更新 | 一方冲突，无覆盖丢失 | 是 | 是 |
| DB-006 | 原子性 | 项目+提醒原子写入 | P0 | 强制第二写失败 | 合法创建 | 执行 | 两者均不落库 | 是 | 是 |
| DB-007 | 韧性 | 只读、锁定、非法路径 | P1 | 三种受控连接 | 写入 | 执行 | 返回可处理错误，不崩溃、不半写 | 是 | 否 |

## 10. 提醒、通知与恢复矩阵

| 编号 | 模块 | 标题 | 优先级 | 前置条件 | 输入 | 操作步骤 | 预期结果 | 自动 | 阻断 |
|---|---|---|---|---|---|---|---|---|---|
| REM-001 | 扫描 | 准时/提前/多条到期 | P0 | FakeClock/FakeSender | 多个到期点 | 单次扫描 | 对应通知各一次 | 是 | 是 |
| REM-002 | 延期 | 5 分钟、10 分钟、自定义 | P1 | 已领取提醒 | 三种时长 | 延期并扫描 | 下次投递时刻正确 | 是 | 否 |
| REM-003 | 终止 | 完成或删除后不提醒 | P0 | 已排程项目 | 完成/删除 | 扫描 | 不发送任何通知 | 是 | 是 |
| REM-004 | 幂等 | 重复扫描不重复通知 | P0 | 已投递记录 | 相同 now | 连续扫描 | 一个幂等键最多一次 Delivered | 是 | 是 |
| REM-005 | 恢复 | 关机/睡眠/重启后恢复 | P0 | 文件型临时库 | 漏掉的提醒 | 重建服务后 `ScanOnceAsync(now)` | 按确认补发策略恢复，不能重复 | 是 | 是 |
| REM-006 | 异常 | 通知服务失败 | P0 | FakeSender 抛异常 | 到期提醒 | 单次扫描 | 应用不崩；重试/死信与健康状态正确 | 是 | 是 |

## 11. WPF 前端测试矩阵

| 编号 | 模块 | 标题 | 优先级 | 前置条件 | 输入 | 操作步骤 | 预期结果 | 自动 | 阻断 |
|---|---|---|---|---|---|---|---|---|---|
| UI-001 | ViewModel | 输入、发送、取消、重复提交 | P1 | fake 服务 | 文本与连续点击 | 调用命令 | 状态、禁用逻辑与消息正确，无重复写入 | 是 | 否 |
| UI-002 | ViewModel | loading/成功/失败/超时 | P1 | 可控 Task | 四种完成状态 | 发送 | 用户可理解状态，失败可恢复 | 是 | 否 |
| UI-003 | 确认卡 | 出现、确认、取消 | P0 | fake parsed action | 待确认命令 | 发送→确认/取消 | 确认才写库；取消零写入 | 是 | 是 |
| UI-004 | 悬浮窗 | 启动、可见、展开/收起 | P0 | 专用 UIA 环境 | 无 | 启动并操作 | 隔离进程中窗口可见，状态可达 | 是 | 是 |
| UI-005 | 窗口 | 拖动、位置恢复、多屏 DPI | P2 | Win10/11 多显示器 | 坐标/DPI | 操作并重启 | 位置合法且可见 | 人工+UIA | 否 |
| UI-006 | 可用性 | 长文本、Emoji、中英文、输入法、深浅色 | P2 | 两主题 | 多语料 | 人工输入 | 不截断、不崩溃、可读 | 人工 | 否 |

## 12. 异常、性能与兼容性

| 编号 | 模块 | 标题 | 优先级 | 前置条件 | 输入 | 操作步骤 | 预期结果 | 自动 | 阻断 |
|---|---|---|---|---|---|---|---|---|---|
| NFR-001 | 启动 | 缺失 API Key | P0 | 清空配置 | 启动 | 启动后发送 | 可启动，显示配置错误 | 是 | 是 |
| NFR-002 | 网络 | 模型超时/5xx | P0 | fake 客户端 | 超时/错误 | 发送 | UI 复位，不写业务库 | 是 | 是 |
| NFR-003 | 性能 | 1000 条本地查询 | P2 | 临时种子 | 今日/本周查询 | 计时 | 满足后续确定的预算；先记录基线 | 是 | 否 |
| NFR-004 | 兼容 | Windows 10 19041 与 Windows 11 | P1 | 两系统 VM | 安装/启动/通知 | 冒烟 | 启动、悬浮窗、通知可用 | 人工/CI VM | 是 |

## 13. 自动化与人工边界

自动化验证所有本地规则、事务、协议拒绝、时钟、SQLite、通知出站与 ViewModel 状态。UI 自动化只覆盖启动到确认的主链路；真实 toast 以 fake transport 替代。人工测试负责视觉布局、拖动手感、多显示器/DPI、输入法、主题、通知视觉及 Windows 版本差异。真实 DeepSeek、网络与用户数据库永不作为默认测试依赖。

## 14. 首批自动化清单（30 项）

优先复用和补强现有 xUnit：FUN-001/002/003/004/006/010，AI-001/002/005/007/008/009/011/012/013/014/015，TIME-001/002/003/004，REC-001/002/003，DB-001/002/003/005/006，REM-003/004/006。现有测试已覆盖其中大量项目；本轮新增测试应优先填补“API Key 缺失不崩”“普通聊天无副作用”“缺字段/模糊时间状态”和“真实文件库重启恢复”等空缺。

## 15. 分阶段实施与验收

1. **基础设施**：临时数据库 fixture、fake clock、fake AI/通知；验收为无网络、可并行、无 `Thread.Sleep`。
2. **领域/时间/重复**：完成 TIME/REC P0；验收为固定时钟下的跨日、闰年、周规则稳定。
3. **AI 契约与命令**：完成 AI P0；验收为任何非法输出零业务写入。
4. **SQLite 与命令服务**：完成 DB/FUN P0；验收为回滚、幂等和误删保护通过。
5. **提醒恢复**：完成 REM P0；验收为投递一次、失败可恢复、重启后按策略补发。
6. **ViewModel/UI 冒烟**：完成 UI-001~004；验收为启动、展开、输入、确认、退出主链路。
7. **发布门禁**：所有 P0 自动测试通过；P1 失败须有批准的豁免；Windows 10/11 冒烟通过；待确认规则不得被静默假设。

每阶段都记录新增/修改文件、测试数量、运行命令、通过/失败数、业务缺陷、生产改动理由和剩余风险。默认命令：`dotnet test OpenIsland.sln --no-restore`；外部与 UIA 用分类过滤后单独运行。

## 16. 修正记录

| 编号 | 日期 | 修正项 | 变更 | 验证 | 状态 |
|---|---|---|---|---|---|
| FIX-001 | 2026-07-22 | NFR-003 / 测试时钟 | `TodayDashboardServiceTests.SnapshotSeparatesTodayOverdueAndInbox_UsingCanonicalRows` 不再依赖真实系统时间，避免跨越本地午夜时将昨日逾期事项错误断言为“今日”。 | `dotnet test OpenIsland.sln --no-restore`：271/271 通过。 | 已验证 |
| FIX-002 | 2026-07-22 | AI-001/011/012/015 / 客户端隔离 | 新增 `IChatCompletionClient`；命令解析、旧解析器与执行服务均依赖抽象，应用启动时绑定 `OpenAiChatService`。 | fake 注入测试；`dotnet test OpenIsland.sln --no-restore`：271/271 通过。 | 已验证 |
| FIX-003 | 2026-07-22 | REM-005 / 可控恢复扫描 | `ReminderService` 增加 `ScanOnceAsync(now)`；定时轮询委托该入口，扫描业务时间完全由调用方提供。 | 新增同一时刻重复扫描仅投递一次的测试；`dotnet test OpenIsland.sln --no-restore`：271/271 通过。 | 已验证 |
| FIX-004 | 2026-07-22 | TIME-001~005 / 业务时钟 | `LifeDataService` 及其提醒子存储改用可注入 `Func<DateTime>`，不再直接读取系统时钟；提醒扫描使用显式 `now`。 | 新增持久化时间戳使用注入时钟的测试；`dotnet test OpenIsland.sln --no-restore`：271/271 通过。 | 已验证 |
| FIX-005 | 2026-07-22 | UI-001/003/004 / UIA 基线 | 为主窗口和悬浮窗的根元素、输入、发送、确认、取消、展开与快速添加控件配置稳定 `AutomationId`。 | WPF 项目由全量测试构建验证；`dotnet test OpenIsland.sln --no-restore`：271/271 通过。独立 UIA 进程工程仍待建立。 | 已验证 |

| FIX-006 | 2026-07-22 | REC-005 / REM-005 / 删除语义 | 以工程冻结规格取代计划中的待产品确认：单期 override、重启恢复扫描和回收站 tombstone 语义均有权威定义。 | 已核对 `docs/engineering-freeze/01-life-item-model.md` 与 `05-reminder-scheduling.md`；实现与测试继续按该定义补齐。 | 已验证规格 |
| FIX-007 | 2026-07-22 | FUN-006 / 删除恢复 | `TodayDashboardService.RestoreDeletedItem` 提供行版本保护的显式恢复；恢复 tombstone 时将已取消/忽略项恢复为 Pending。 | 新增旧版本恢复被拒绝、正确版本恢复成功的测试；`dotnet test OpenIsland.sln --no-restore`：272/272 通过。 | 已验证 |
| FIX-008 | 2026-07-22 | REC-003 / 月末重复 | 月度 31 日在短月跳过，不移至月末；结构化 create_recurring_task 已使用 MonthlyRecurrenceCalculator 生成首个 occurrence。 | 普通年、闰年和结构化命令管线测试；dotnet test OpenIsland.sln --no-restore：275/275 通过。 | 已验证 |

| FIX-009 | 2026-07-22 | REC-005 / occurrence override | 新增 OccurrenceOverrideStore，以系列、规则版本、原本地/UTC 时间和时区组成稳定键；同键更新只替换当前 occurrence。 | 新增 override 原子替换测试；dotnet test OpenIsland.sln --no-restore：276/276 通过。 | 已验证 |

| FIX-010 | 2026-07-22 | UI-001/003/004 / 独立测试工程 | 新增 tests/OpenIsland.UiTests 并纳入解决方案，验证主窗口和悬浮窗关键 AutomationId 的稳定合约；CI 已运行该工程。 | dotnet test OpenIsland.sln --no-restore：主测试 276/276、UI 合约 2/2 通过。真实进程 UIA 冒烟待建立。 | 已验证 |

| FIX-011 | 2026-07-22 | UI-004 / 真实 UIA 冒烟 | UIA 测试模式使用临时数据库并跳过 Windows 通知注册；专用命令以独立进程启动 Island，通过 AutomationId 验证悬浮窗可见性后安全退出。 | OPENISLAND_RUN_UIA=1 dotnet test tests/OpenIsland.UiTests/OpenIsland.UiTests.csproj --no-restore --filter Category=UIA：1/1 通过。 | 已验证 |

| FIX-012 | 2026-07-22 | REC-005 / 单次 override 入口 | 周期提醒的跳过、稍后提醒与改单次改期均写入稳定 occurrence override；悬浮窗的“跳过本次”和“改时间”已分别调用对应业务 API，系列规则不被改写。 | 新增周期 occurrence 跳过→改单次改期回归测试；定向 ReminderSnoozeTests 与 OccurrenceOverrideStoreTests：5/5 通过；最终全量 OpenIsland.Tests 277/277、UI 合约 3/3 通过。 | 已验证 |
| FIX-013 | 2026-07-22 | 下一行动问题定位 | 初次将工作日提醒纳入 `NextAgenda()`；经界面澄清后确认该入口属于灵动岛，不应显示长期提醒，最终方案见 FIX-015。 | 初步回归通过；后续由 FIX-015 覆盖最终 UI 归属与行为。 | 已替代 |
| FIX-014 | 2026-07-22 | 本地发布 / .NET 运行时 | 发布脚本不再覆盖 Debug 构建目录，改用专用 Release publish 目录，以保证桌面快捷方式指向自包含 .NET 8 x64 产物。 | 已发布并核对 runtimeconfig 使用 `includedFrameworks`；桌面快捷方式启动专用自包含发布物。 | 已验证 |
| FIX-015 | 2026-07-22 | 今日作战 / 下一行动 | 灵动岛的 `NextAgenda()` 继续排除长期周期提醒；今日作战的 `TodayDashboardService` 单独动态展开周期提醒，并将最近 occurrence 与普通事项按时间排序。 | 固定周三晚间场景验证今日作战选择周四 00:00 的法定工作日提醒、灵动岛不纳入长期提醒；定向 8/8，全量主测试 278/278、UI 合约 3/3 通过。 | 已验证 |