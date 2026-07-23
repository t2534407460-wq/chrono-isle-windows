# AI 命令协议与本地执行边界

状态：工程冻结  
适用里程碑：M1-M4  
目的：让模型只承担自然语言理解，所有标识、歧义、权限、确认和执行均由本地代码决定。

## 1. 顶层协议

模型只返回一个 `AssistantCommandEnvelope`：

```json
{
  "schemaVersion": 1,
  "command": "create_reminder",
  "arguments": {},
  "missingFields": [],
  "ambiguityReasons": []
}
```

顶层仅允许这五个字段；未知字段、未知版本、未知命令、非对象 arguments 或类型不符都使本次解析进入 `Rejected`，不得尝试宽松修复后执行。`missingFields` 和 `ambiguityReasons` 只作为模型解释，不能替代本地验证结果。

模型输出中的 `user_request_id`、`parse_attempt_id`、`client_request_id`、`confirmation_id`、`action_event_id`、本地 item ID、`row_version` 和 confidence 一律忽略。本地命令管线独立生成标识，并通过本地查询解析目标。

## 2. 每命令独立 Schema

每个命令拥有独立、版本化、拒绝未知字段的参数类型：

| command | 参数类型 | 核心字段 |
|---|---|---|
| `create_todo` | `CreateTodoArgumentsV1` | title；可选 due/remind、重复规则 |
| `create_reminder` | `CreateReminderArgumentsV1` | title、remind；可选 due、重复规则 |
| `create_event` | `CreateEventArgumentsV1` | title、start、end；可选 remind |
| `create_recurring_task` | `CreateRecurringTaskArgumentsV1` | title、Kind、墙上开始时间、强类型规则 |
| `list_items` | `ListItemsArgumentsV1` | 本地可验证的范围与过滤条件 |
| `update_todo` | `UpdateTodoArgumentsV1` | 目标描述、允许修改的字段 |
| `complete_todo` | `CompleteTodoArgumentsV1` | 目标描述 |
| `delete_todo` | `DeleteTodoArgumentsV1` | 目标描述 |
| `reschedule_item` | `RescheduleItemArgumentsV1` | 目标描述、新业务时间 |
| `decompose_goal` | `DecomposeGoalArgumentsV1` | goal、可选明确约束；最多生成 10 项 |
| `summarize_period` | `SummarizePeriodArgumentsV1` | 明确的本地统计周期 |

时间参数是解析输入，不是可直接落库的 `TemporalValue`。它必须包含用户说出的本地日期时间/相对表达、可选时区线索和原始文本，之后由本地时间解析器生成字段级时间值。模型不得为未明确的时间补默认具体时刻。

更新、完成、删除、重排的“目标描述”只能包含用户原话中的名称、Kind 和时间线索；不得接受模型提供的数据库 ID。本地目标解析器返回零个、一个或多个候选。零个候选失败，多个候选必须澄清或确认。

## 3. 固定处理顺序

```text
接收用户输入并生成 user_request_id
-> 在写队列外调用模型，生成 parse_attempt_id
-> 严格解析 Envelope 与版本
-> 校验 command 白名单
-> 按 command 反序列化 ArgumentsVn
-> 本地字段与业务校验
-> 本地时间解析、目标解析和冲突检测
-> 本地判定 Exact / Incomplete / Ambiguous
-> 生成本地 client_request_id
-> 执行策略与确认状态机
-> ICommandHandler<T> 在短事务内执行
```

模型调用、重试和网络等待不得占用 `IDbWriteQueue`。只有确认后的最终命令、业务写入和动作结果进入统一短事务。

## 4. 明确性与执行策略

本地允许直接执行的范围仅为：单条、非破坏性、所有 Kind 必填字段明确、无冲突的创建命令，例如：

- “一小时后提醒我喝水”；
- “明天 09:00 提醒我开会”；
- “2026-07-18 15:00 提醒我提交报表”；
- “每周五 18:00 提醒我写周报”；
- “添加待办‘买牛奶’”（Todo 允许无时间）。

以下词语固定判定为歧义，不得直接写库：下午、晚上、今晚、周末、有空、尽快、过会儿、下班后、睡前。提醒或日程缺少必要时间也属于 `Incomplete`。

删除、批量创建、修改已有业务时间、多个目标匹配和新日程冲突始终进入确认。AI 拆解只生成持久化草稿；标题必填、时间可选、初始状态固定为待开始，模型不能生成已完成状态或在模糊约束下补时间。

## 5. 规范化与哈希

通过验证后，将命令转换为内部强类型命令，再生成规范化 JSON：属性按固定顺序、枚举使用固定名称、时间使用标准格式、空集合规范为空数组，不保留模型的多余文本。`command_hash` 只对该规范化表示计算；展示文案和模型解释不参与哈希。

每个强类型命令由唯一 `ICommandHandler<T>` 处理。Handler 不接收原始模型 JSON，也不得重新解释自然语言。排序、权限、只读限制、Kind 约束和时间解析均由本地服务给出确定结果。

## 6. 失败路径与测试点

- 模型超时或取消：结束解析，不产生可执行命令，不写业务表。
- JSON 非法、Schema 不符、未知字段/版本/命令：`Rejected`，记录脱敏错误。
- 本地校验缺字段或有歧义：`ClarificationRequired`，零业务写入。
- 目标不存在或多匹配：返回候选/澄清，不允许模型自行挑选。
- Handler 失败：业务事务回滚，失败事件另行写入审计。
- 每个 ArgumentsV1 都要覆盖合法、缺字段、错类型、未知字段和跨命令字段注入测试。
- 覆盖所有固定歧义词、相对时间、重复规则、模型伪造 ID/confidence 和多目标匹配。
- 验证相同规范化命令产生相同哈希，不同业务字段必然改变哈希。

