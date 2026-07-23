# 确认状态机、幂等与审计

状态：工程冻结  
适用里程碑：M1-M3  
目的：确保旧确认卡、双击、重试、并发更新或应用崩溃都不会误执行或重复执行命令。

## 1. 状态机

状态只允许按下列路径流转：

```text
Received
-> Parsing
-> Rejected | ClarificationRequired | PolicyReady
PolicyReady
-> AutoExecuting | AwaitingConfirmation
AutoExecuting | AwaitingConfirmation
-> Executing
Executing
-> Succeeded | Failed
AwaitingConfirmation
-> Cancelled | Expired | Stale
```

终态不可再次执行。`ClarificationRequired` 的后续输入作为同一 `user_request_id` 下的新 `parse_attempt_id` 重新解析，不修改旧命令快照。

自动执行只适用于 AI 协议文档定义的明确、单条、非破坏性且无冲突的创建。删除、批量创建、修改时间、多目标匹配和日程冲突必须确认。UI 中用户明确点击“完成、仅稍后提醒、跳过本次”等本地动作可以由 UI 直接构造本地命令，不需要模型再次确认，但仍需幂等和版本校验。

## 2. 本地标识与确认记录

标识全部由本地生成：

```text
user_request_id     一次用户输入及其澄清链
parse_attempt_id    一次模型解析尝试
client_request_id   一个可执行内部命令
confirmation_id     一张确认卡
action_event_id     一次实际执行事件
```

确认记录必须保存：

```text
confirmation_id
client_request_id
command_hash
target_snapshot_hash
expires_at_utc
status
created_at_utc
confirmed_at_utc
display_snapshot
```

`display_snapshot` 是确认时用户看到的规范化摘要，不参与执行。普通确认 15 分钟过期；任务拆解草稿 24 小时过期。过期检查使用本地当前 UTC，并在尝试确认时再次执行，不能只依赖 UI 定时器。

## 3. 命令与目标快照

`command_hash` 来自经过 Schema、本地业务和时间校验后的规范化内部命令。确认后不得用新模型输出替换 arguments。

`target_snapshot_hash` 对所有目标的 `(item_id, row_version)` 按 item ID 排序后形成规范化序列并计算哈希。创建命令没有既有目标时使用固定空快照；冲突预览中引用的关联事项也应纳入目标快照。

确认执行前，在一个写队列任务内依次验证：

1. `confirmation_id` 存在且状态为 `AwaitingConfirmation`；
2. 当前时间未超过 `expires_at_utc`；
3. 持久化规范化命令重新计算的哈希一致；
4. `client_request_id` 尚无成功动作事件；
5. 所有目标存在、未软删除且允许该操作；
6. 当前目标 ID 与 `row_version` 快照哈希一致。

任一步失败都不得执行：超时标记 `Expired`，目标变化标记 `Stale`，已执行请求直接返回既有结果。`Stale` 必须重新生成预览和新的确认记录，不能让用户强行沿用旧快照。

## 4. 原子执行与幂等

`action_events.client_request_id` 建立条件唯一索引。确认状态的条件更新、业务变更、`row_version` 递增、成功动作事件和确认终态必须在同一 `IDbWriteQueue/UnitOfWork` 短事务中提交。

事务首先以条件 UPDATE 将确认从 `AwaitingConfirmation` 改为 `Executing`；影响行数不是 1 时停止。随后 Handler 执行业务写入，并写入 `Succeeded` 动作事件和确认终态。事务失败时全部回滚；再以独立短事务写入脱敏 `Failed` 事件，不能留下半完成业务数据。

自动执行使用同一流程，只是没有确认卡：依靠 `client_request_id` 唯一索引和目标版本条件保证幂等。应用崩溃后可用 client request 查询结果；已有成功事件时返回原结果 ID，不重复创建。

## 5. 动作审计与隐私

`action_events` 记录标识链、命令类型、状态变化、目标 ID、结果 ID 和脱敏错误。成功业务记录可保存 `created_by_action_event_id`；清理审计后该外键通过 `SET NULL` 保留业务数据。

- 最近 30 天可保留原始输入、解析结果和确认摘要。
- 到期后删除原始输入与标题，只保留命令类型、时间、状态和结果计数等结构化摘要。
- 用户可在设置中清理全部本地审计；聊天删除不自动级联审计。
- 普通日志不得保存完整模型原文、任务标题或同步令牌。

“刚才创建了什么”必须查询结构化动作事件，不能让模型根据对话猜测。

## 6. 测试点

- 所有合法和非法状态流转；终态不可重入。
- 15 分钟边界、24 小时草稿边界以及 UI 未刷新时的服务端过期检查。
- command hash 被修改、目标被删除、`row_version` 变化和关联冲突事项变化。
- 双击确认、多线程并发确认、崩溃后重试只产生一个成功动作。
- 事务中任意一步失败时无业务半写入，且存在脱敏失败审计。
- 审计 30 天脱敏、用户清除、外键 `SET NULL` 和日志敏感信息检查。

