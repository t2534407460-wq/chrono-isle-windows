# 时间、时区、DST 与重复事项

状态：工程冻结  
适用里程碑：M0B-M5  
目的：保留用户原始墙上时间语义，同时提供稳定 UTC 触发点，避免跨时区、DST 和外部导入导致时间漂移。

## 1. 字段级时间值

due、remind、start、end 各自使用独立 `TemporalValue`，不能用事项级单一语义替代：

```text
local_datetime
utc_instant
iana_time_zone_id
windows_time_zone_id_cache
semantics
```

`semantics` 只能是：

- `AbsoluteInstant`：明确偏移或远端绝对时刻；`utc_instant` 是事实值，本地值用于展示。
- `ZonedWallClock`：绑定固定 IANA 时区的墙上时间；自然语言创建默认使用此语义。
- `DeviceLocalFloatingWallClock`：不绑定固定时区，始终按设备当前系统时区解释，仅在用户明确要求“无论在哪都按当地时间”时使用。

IANA ID 是数据库标准，例如 `Asia/Shanghai`；Windows ID 只是可空缓存和 Graph/Windows 边界辅助值，不得成为主标识。可编辑 Event 的 start/end 必须具有相同语义；若为 Zoned，还必须使用同一 IANA 时区。外部数据无法满足时作为只读镜像处理。

## 2. 时区能力与解析

应用启动时执行 `ITimeZoneCatalog` 自检：

1. 解析 UTC、当前设备时区和至少一个具有 DST 的典型 IANA 区域。
2. 验证 IANA 到 Windows、Windows 到 IANA 的转换。
3. 验证 `IOccurrenceTimeResolver` 对 gap 与 overlap 的固定用例。

优先使用 .NET/ICU 的 `TimeZoneInfo` 转换能力。运行环境不能直接解析 IANA 时，使用内置、版本化的 IANA-Windows 映射兼容层；仍不能解析的时区必须拒绝排程，不得猜测偏移或静默改用 UTC。自检失败进入可观测的降级状态，但不能阻止不依赖该时区的本地读取。

系统时区变化时：

- `AbsoluteInstant` 的 UTC 不变，只更新展示换算。
- `ZonedWallClock` 仍绑定原 IANA 时区，重新计算派生 UTC 仅限规则或时区数据库版本要求的重建流程。
- `DeviceLocalFloatingWallClock` 按新设备时区重新计算未来触发点。

## 3. DST 唯一解析入口

所有单次、重复、迁移、ICS 和 Outlook 时间均调用 `IOccurrenceTimeResolver`，业务代码不得自行写 DST 分支：

```text
ResolveSingleLocalTime(local, zone, semantics)
ResolveRecurringOccurrence(local, zone, ruleRevision)
ResolveGap(local, zone)
ResolveOverlap(local, zone)
```

固定规则：

- DST gap：目标本地时间不存在时，移动到该时区的下一有效本地时间；不得固定加一小时，也不得静默跳过该 occurrence。
- DST overlap：目标本地时间出现两次时，选择 UTC 时间更早的那个瞬时点，并且只生成一次。
- 解析结果同时返回原始本地值、实际本地值、UTC、所用偏移和 `WasAdjustedForDst`，便于审计与测试。

## 4. 重复事项

`recurrence_rules` 保存：

```text
life_item_id
start_local_datetime
iana_time_zone_id
windows_time_zone_id_cache
frequency
interval
by_weekday / by_month_day
end_condition
rule_revision
next_occurrence_utc
```

重复频率只使用受支持的强类型字段。核心层可由这些字段生成兼容的 RFC 5545 RRULE，但不得执行任意 RRULE 字符串。每天、每周、每月都先在本地日历中推进墙上时间，再由 resolver 求 UTC；禁止用“上一 UTC + 24 小时/7 天”生成下一期。

`next_occurrence_utc` 是派生缓存，不是规则事实。每次修改规则时递增 `rule_revision`，重新计算未来 occurrence。单期 override 的键固定为：

```text
series_item_id
rule_revision
original_start_local_datetime
original_start_utc
time_zone_id
```

规则修改后无法映射的旧 override 标记为 `orphaned`，不得套用到新一期。

## 5. 迁移规则

旧时间没有可靠时区信息时，按迁移时当前机器时区解释，并显式标记 `interpreted_as_current_machine_timezone`。迁移审计逐条保存原值、来源表与 ID、假定时区、新本地值、新 UTC、调整信息和迁移版本。

迁移顺序固定为：在线备份、事务内复制、字段/数量校验、`integrity_check`、提交。任何时间无法解析时整批回滚，不得写入部分转换数据。旧表改名 `_legacy_v1` 并保留一个稳定版本。

## 6. 测试点

- 三种语义在设备时区变化前后的 UTC 与显示结果。
- IANA 直接解析、Windows 转换缓存、兼容映射和完全无法解析四条路径。
- DST gap 移至下一有效时间，overlap 选择更早瞬时点且仅生成一次。
- 每日、每周、每月跨 DST 的墙上时间保持不变。
- Event start/end 语义或时区不兼容时拒绝编辑、外部导入转只读镜像。
- 规则修订后 next occurrence 重建、override 匹配与 orphaned 标记。
- 旧数据迁移假设可追溯，任一解析失败时事务回滚。

