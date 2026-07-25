# ICS、Microsoft To Do 与 Outlook 同步映射

状态：工程冻结  
适用里程碑：M5A-M5F  
目的：在本地优先前提下渐进引入外部互通，明确字段损失、游标、冲突、密钥和只读镜像边界。

## 1. 通用同步边界

SQLite 始终是本地功能的事实源；同步失败不得阻塞本地创建、提醒、查询和专注。同步默认关闭，只有用户主动授权后启用。

持久化边界：

- `sync_accounts`：适配器、账户状态、授权引用；
- `sync_mappings`：local item、remote container、remote ID、远端版本、上次同步的本地 `row_version`；
- `sync_cursors`：按适配器规定的粒度保存完整 nextLink/deltaLink 和窗口；
- `sync_conflicts`：双边修改、删除冲突和不可无损映射原因；
- `sync_outbox`：远端 Create/Update/Delete 请求、幂等键、重试和限流状态；
- `raw_external_payloads`：只读镜像所需的原始外部结构。

网络与认证在写队列外执行；拉取结果、映射更新和 outbox 状态以短事务分批写入。软删除映射的唯一约束只作用于 `deleted_at_utc IS NULL` 的活跃记录。

单边变化可应用；本地 `row_version` 与远端版本均偏离上次同步基线时进入 `sync_conflicts`，禁止最后写入者静默覆盖。远端删除且本地未变化时创建本地 tombstone；本地已变化时生成冲突。

## 2. 来源与只读规则

外部导入设置 `origin_type`、`origin_adapter`、`is_readonly`、`readonly_reason` 和 `raw_external_payload_id`。只读镜像允许查看、隐藏和软删除本地副本，禁止完成、重排、拆解和写回远端；UI 必须展示原因。

无法识别时区、Event start/end 语义不兼容、重复规则超出强类型子集或远端字段无法无损表达时，不得猜测或丢字段，统一导入为只读镜像。

## 3. M5A-M5B：备份与 ICS

迁移安全备份与可携带用户备份分开。WAL 数据库使用 SQLite 在线备份 API并执行完整性验证，不能只复制 `.db`。可携带备份不包含 DPAPI 保护的 delta link、账户令牌或 MSAL 缓存。

ICS 导出：

- Event 映射为 `VEVENT`；Todo 映射为 `VTODO`；可无损表达的提醒和重复规则写入相应标准字段。
- 只导出受支持的强类型重复子集；不能表达的事项列入导出报告，不静默丢失。

ICS 导入只创建本地副本，不建立持续同步：

- `VEVENT` 映射 Event，`VTODO` 映射 Todo；可识别的提醒映射到 remind TemporalValue。
- 支持的 RRULE 转为强类型 `recurrence_rules`。
- 不支持的规则、混合时间语义或未知时区保留原始 payload 并成为只读镜像。

## 4. M5C-M5D：Microsoft To Do

首个远端容器由用户选择；未选择时创建单一 `ChronoIsle` Task List。不得按类别、今日或 Inbox 自动拆分多个列表。父子关系首版仅保留本地，不降级成信息不足的 checklist。

字段映射：

| 本地 | Microsoft To Do |
|---|---|
| Todo/Reminder title | task title |
| Todo due | dueDateTime |
| remind | reminderDateTime / isReminderOn |
| 受支持重复规则 | recurrence |
| Completed | completed 状态及完成时间 |
| 本地独有类别、能量、父子关系 | 仅保留本地 |

Event 不推送到 To Do。无法无损映射的字段不阻塞受支持字段，但必须保留在本地。来源关系使用 `sync_mappings` 或 linked resource，不能依靠标题匹配。

M5C 仅本地到远端：稳定本地 action ID 作为 outbox 幂等来源，重试不得创建重复 task。M5D 才启用双向：

- Task List delta cursor 按 account 保存；task delta cursor 按 `account_id + task_list_id` 保存。
- 完整保存服务返回的 `@odata.nextLink/@odata.deltaLink`，不得拆解 token。
- 远端完成、修改和删除按同步基线判断单边变化或冲突。

参考：[todoTask delta](https://learn.microsoft.com/en-us/graph/api/todotask-delta?view=graph-rest-1.0)、[todoTaskList delta](https://learn.microsoft.com/en-us/graph/api/todotasklist-delta?view=graph-rest-1.0)。

## 5. M5E-M5F：Outlook Calendar

Outlook 只映射 Event。M5E 先提供单向推送：单次 Event 和可无损映射的重复规则可推送；不兼容规则保持本地并展示原因。稳定本地 action ID 防止重试生成重复远端 event。

M5F 的 cursor 固定按以下维度保存：

```text
account_id
calendar_id
window_start_utc
window_end_utc
delta_link
last_success_at_utc
```

每个 calendar 独立同步；默认 calendar view 窗口为过去 90 天至未来 365 天。窗口滚动时只对新增区间执行初始同步。series、exception 和 cancellation 映射到规则版本及 occurrence override；无法无损表达的远端系列作为只读镜像并保留原始 Graph recurrence JSON。

参考：[Outlook event delta](https://learn.microsoft.com/en-us/graph/api/event-delta?view=graph-rest-1.0)。Windows 日历互通仅通过 Outlook 账户或 ICS，不接入私有 Windows 日历接口。

## 6. 授权、游标与恢复

WPF 登录优先使用 MSAL + WAM，不可用时回退系统浏览器。delta link 与敏感同步状态使用当前 Windows 用户范围的 DPAPI 保护。

DPAPI 数据不保证跨设备恢复。可携带备份恢复到新设备、DPAPI 解密失败、授权撤销或令牌失效时：

1. 本地事项保持可用且不回滚；
2. 对应账户标记 `ReauthRequired`；
3. 清除不可用游标引用，不猜测或复用部分 token；
4. 用户重新登录后执行该容器的初始同步并重建基线。

## 7. 失败路径与测试点

- 每个 M5 子阶段可独立启用、关闭和回滚，不提前运行后续阶段逻辑。
- 网络失败、限流和服务错误保留 outbox，按服务提示与本地退避重试，不阻塞本地写入。
- 覆盖首次同步、分页、delta、游标失效、授权撤销、远端删除、双边修改和重复推送。
- 验证 cursor 粒度：To Do 按 list，Outlook 按 account/calendar/window，禁止全局 cursor。
- 验证 DPAPI 失败与跨设备恢复只触发 `ReauthRequired`。
- 验证 ICS 不兼容规则、Outlook recurrence 和混合时间语义进入只读镜像，且所有禁止操作被拒绝。
- 验证可携带备份不含授权密钥、MSAL 缓存和可恢复 delta link。

