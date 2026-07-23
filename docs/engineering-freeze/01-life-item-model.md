# LifeItem 数据约束与状态流转

状态：工程冻结  
适用里程碑：M0B-M5  
目的：统一待办、提醒和日程的持久化边界，防止非法字段组合污染提醒、状态中心、报告和同步。

## 1. 数据边界

`life_items` 是本地事项的事实源。`kind` 只能是 `Todo`、`Reminder`、`Event`，应用层使用强类型枚举、分 Kind 构造器和验证器，数据库层使用 `CHECK` 兜底。业务代码不得以任意字符串判断 Kind。

所有事项至少包含：

- `id`、`kind`、非空白 `title`、`status`；
- `row_version`，初值为 1；
- `created_at_utc`、`updated_at_utc`、可空 `deleted_at_utc`；
- 可空的 due、remind、start、end 四组 `TemporalValue`；
- `origin_type`、`origin_adapter`、`is_readonly`、`readonly_reason`、`raw_external_payload_id`；
- M3 才启用的优先级、类别、预计分钟数、能量、父事项、完成时间和延期次数。

Kind 级约束固定如下：

| Kind | 必填时间 | 可空时间 | 必须为空 |
|---|---|---|---|
| Todo | 无 | due、remind | start、end |
| Reminder | remind | due | start、end |
| Event | start、end，且 end 晚于 start | remind | due |

Event 的 start/end 必须具有兼容的时间语义与时区；无法无损满足该条件的外部事项只能作为只读镜像导入。

## 2. SQLite 约束

所有 `CHECK` 必须显式判断 `NULL`。禁止只写 `end_at_utc > start_at_utc`，因为 SQLite 将结果为 `NULL` 的 CHECK 视为通过。约束应采用以下形态：

```sql
CHECK (
  kind != 'Event'
  OR (
    start_at_utc IS NOT NULL
    AND end_at_utc IS NOT NULL
    AND end_at_utc > start_at_utc
    AND due_at_utc IS NULL
  )
)
```

同理，Reminder 必须显式要求 remind 全组所需字段非空；Todo 和 Reminder 必须显式要求 start/end 为空。标题约束同时检查 `title IS NOT NULL` 与 `length(trim(title)) > 0`。

涉及软删除记录的唯一性使用 partial unique index，只约束活跃记录：

```sql
CREATE UNIQUE INDEX ux_active_sync_mapping
ON sync_mappings(account_id, adapter, remote_id)
WHERE deleted_at_utc IS NULL;
```

## 3. 状态与操作

事项状态为 `Pending`、`InProgress`、`Completed`、`Deferred`、`Cancelled`、`Ignored`。删除不是状态，而由 `deleted_at_utc` 正交表达。

- Todo：`Pending` 可进入 `InProgress/Completed/Deferred/Cancelled/Ignored`；`InProgress` 可进入 `Completed/Deferred/Cancelled`；`Deferred` 经重新安排后回到 `Pending`。
- Reminder：`Pending` 可进入 `Completed/Deferred/Cancelled/Ignored`；仅稍后提醒不会改变事项状态。
- Event：`Pending` 可进入 `Cancelled`；事件是否已过去由时间推导，不自动改为 `Completed`。
- 终态不得被普通更新命令隐式恢复；恢复必须是显式操作并重新通过 Kind 校验。
- 只读镜像不允许完成、重排、拆解或写回远端，只允许查看、隐藏以及软删除本地副本。

`Snooze` 只改变当前 occurrence 的提醒时间；`Reschedule` 才会更新事项业务时间并递增 `row_version`；`SkipOccurrence` 只写 occurrence override，不修改系列主项状态。

## 4. 并发、删除与关系

`row_version` 是应用管理的单调递增整数，不使用 SQLite trigger。所有业务更新必须经过 Repository/UnitOfWork，并采用比较后更新：

```sql
UPDATE life_items
SET title = $title,
    updated_at_utc = $now,
    row_version = row_version + 1
WHERE id = $id
  AND row_version = $expected
  AND deleted_at_utc IS NULL;
```

影响行数为 0 表示记录不存在、已删除或版本冲突，调用方不得覆盖。完成、重排、业务延期、取消、忽略、软删除和恢复均遵守同一规则。所有写入通过统一 `IDbWriteQueue/UnitOfWork`；禁止后台服务直接拼接绕过版本递增的 UPDATE。

软删除保留 tombstone，供撤销、审计、报告和同步使用；只有显式清空回收站才物理删除。业务记录对已清理审计事件的外键使用 `SET NULL`。

关系规则：

- 一个 LifeItem 最多关联一条活跃 `recurrence_rules`；规则修订递增 `rule_revision`。
- occurrence 是规则计算结果，不为每一期复制一条 LifeItem；单期变化写入 `occurrence_overrides`。
- M3 的拆解使用可空 `parent_item_id`；草稿确认前只存在于 `command_drafts/draft_items`，不得提前建立正式父子关系。

## 5. 失败路径与测试点

- 应用校验失败：在入队前返回字段错误，不打开事务。
- SQLite CHECK 失败：事务整体回滚并记录脱敏诊断，不能尝试降级写入。
- 版本冲突：返回明确并发冲突；确认卡标记 `Stale`。
- 只读操作越权：拒绝执行并返回 `readonly_reason`。
- 测试必须覆盖三个 Kind 的所有合法/非法字段组合，尤其是 NULL 绕过、空白标题和 Event 结束时间。
- 每条更新路径都验证 `row_version + 1`；旧版本更新影响行数必须为 0。
- 验证软删除后的 partial unique index、恢复冲突、父子关系和 recurrence 单一活跃规则。

