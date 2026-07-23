# 提醒到期、策略、投递与恢复

状态：工程冻结  
适用里程碑：M0A、M2、M4  
目的：将业务到期与 UI/Toast 投递分离，并通过 lease、outbox 和幂等键保证崩溃后可恢复且不重复轰炸。

## 1. 组件边界

- `ReminderDueDetector`：根据 LifeItem、重复规则和 override 生成到期 occurrence，并原子认领；不调用 Toast 或 WPF。
- `ReminderPolicyService`：根据优先级、普通/全屏/专注/勿扰状态决定立即投递、延后或抑制；不改变任务完成状态。
- `NotificationDispatcher`：执行 Windows Toast 与灵动岛横幅动作；不重新计算业务到期。
- `notification_outbox`：只保存 Register、Replace、Cancel、Deliver 及其重试状态。
- `notification_deliveries`：保存每次投递尝试与结果；投递成功不等于事项完成。
- `sync_outbox`：只处理外部平台同步，任何通知组件都不得读写它。

所有数据库写入经过 `IDbWriteQueue/UnitOfWork`；到期扫描可用独立只读连接，认领和 outbox 落库使用短事务。Toast、WPF 和其他 I/O 在事务外执行。

## 2. Occurrence 与 override

重复 occurrence 的稳定键为：

```text
series_item_id
rule_revision
original_start_local_datetime
original_start_utc
time_zone_id
```

单次事项使用同样可规范化的 occurrence ID。`occurrence_overrides` 绑定完整键并保存：override 类型、原提醒时间、新提醒时间、新开始时间、创建时间和 orphaned 状态。规则修订后无法重新匹配的 override 标记为 orphaned，不得自动作用于新 occurrence。

动作语义固定：

- `Snooze`：“仅稍后提醒”，只写当前 occurrence 的新提醒时间。
- `Reschedule`：“修改任务时间”，更新 LifeItem 的业务时间并递增 `row_version`。
- `SkipOccurrence`：“跳过本次重复”，只写当前 occurrence override。

“10 分钟后/1 小时后”按钮必须显示“再次提醒”。“今晚 20:00”已过时则隐藏，不能自动解释为明晚。

## 3. 认领 lease

待投递 occurrence 至少包含：

```text
claimed_at_utc
claim_token
claim_expires_at_utc
delivery_status
attempt_count
next_retry_at_utc
last_error
```

状态为 `Pending` 或 lease 已过期的 `Claimed` 才能被认领。认领使用随机本地 `claim_token` 和条件 UPDATE，并检查影响行数；lease 固定为 60 秒。处理完成时必须同时匹配 occurrence ID 与 claim token，旧 worker 不能覆盖新认领者结果。

应用在“认领后、创建 outbox 前”崩溃时，lease 到期后可重新认领；在“outbox 已提交、投递前”崩溃时，由 outbox worker 重试。任何处理中状态都不能永久排除于后续扫描。

## 4. 通知幂等与重试

每个 outbox 动作生成 `notification_idempotency_key`：

```text
item_id
occurrence_key
notification_action_type
rule_revision
target_delivery_time_utc
```

键使用规范化值计算，并对未删除/未取消的活跃记录建立 partial unique index。相同动作的重启、扫描或重试复用原记录；Replace 和 Cancel 是独立动作类型，但必须指向同一稳定通知标识。

worker 先短事务认领 outbox，再在事务外投递，最后以 claim token 条件更新结果。重试采用有上限的指数退避：初始 30 秒、倍率 2、最长 15 分钟、最多 8 次；超过上限标记 `Failed`，更新调度健康状态并在应用内提示用户。错误信息必须脱敏。

Windows Toast API 无法提供严格 exactly-once 保证，因此本地目标是“持久化至少一次尝试 + 稳定 Toast 标识替换”；相同 occurrence 不生成多个本地 outbox 动作或不同 Toast 标识。

## 5. 策略与延后

M2 的普通策略直接投递 Toast 与灵动岛横幅。M4 接入策略矩阵时，不修改 DueDetector：

- 全屏：普通提醒可仅进入灵动岛，紧急提醒允许 Toast。
- 专注：低/普通优先级延后，高优先级进入岛内，紧急提醒穿透。
- 勿扰：除紧急外写入 `deferred_notifications`。

`deferred_notifications` 保存 item、occurrence、原始到期时间、延后原因、优先级、延后截止、Toast 是否被抑制和摘要批次。退出勿扰只生成一次聚合摘要，不逐条补弹；汇总本身也通过 notification outbox 幂等投递。

## 6. 调度健康与失败路径

调度器使用 `SemaphoreSlim` 单飞，禁止 tick 重入。每轮更新 `last_successful_tick_at` 或 `last_failed_tick_at`、连续失败数和健康状态；异常必须被顶层捕获，不能终止后台服务。

- 数据校验失败：隔离该 occurrence，记录脱敏错误，其余提醒继续。
- 数据库忙：由 5 秒 `busy_timeout` 和写队列串行处理；失败后按 outbox 规则重试。
- Toast 失败：不改变业务事项或标记完成。
- Snooze/Reschedule 与正在投递并发：依赖 `row_version`、规则版本和幂等键，旧动作取消或失效。
- 应用重启：扫描过期 lease、待处理 outbox 和 deferred 队列，不能重置为全新通知。

## 7. 测试点

- 单飞 tick、顶层异常恢复和连续失败健康状态。
- 两个 worker 并发认领时仅一个成功；60 秒 lease 到期后可重领，旧 token 无法完成。
- 认领前后各崩溃点的恢复，以及 outbox 重启重试。
- 相同幂等键只存在一个活跃动作；Replace/Cancel 不生成重复 Toast。
- Snooze 不改业务时间，Reschedule 递增版本，Skip 只影响当前 occurrence。
- 规则修订后 override 正确匹配或标记 orphaned。
- 勿扰退出只投递一个摘要，紧急提醒按策略穿透。

