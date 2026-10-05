using System.Text.Json;
using System.Text.Json.Nodes;
using ChronoIsle.App.Services.Persistence;
using ChronoIsle.App.Services.Scheduling;
using Microsoft.Data.Sqlite;

namespace ChronoIsle.App.Services.Sync;

internal sealed record CloudSyncEntity(string EntityType, string EntityId, long Revision, int SchemaVersion, JsonElement? Data, bool Deleted);
internal sealed record CloudSyncOperation(Guid OperationId, string EntityType, string EntityId, long BaseRevision, int SchemaVersion, JsonElement? Data, bool Deleted);
public sealed record CloudSyncConflict(string EntityType, string EntityId, string Title, bool CloudDeleted, string LocalSummary, string CloudSummary);

/// <summary>Only supported business aggregates cross the wire. Tokens, keys, audit commands and notification state stay local.</summary>
internal sealed class CloudSyncStore
{
    const string Adapter = "island-desktop-v1";
    static readonly (string Table, string Key)[] ItemTables =
    [ ("life_items", "id"), ("todos", "id"), ("calendar_events", "id"), ("single_reminders", "id"),
      ("recurring_reminders", "id"), ("archived_todos", "id"), ("recurrence_rules", "series_item_id"),
      ("assistant_reminder_schedules", "series_item_id"), ("occurrence_overrides", "series_item_id") ];
    static readonly HashSet<string> DevicePreferences = [ "WindowsNotifications", "ToastInboxEnabled", "IslandTaskbarDocked", "IslandTaskbarMonitor", "IslandTaskbarHorizontalRatio" ];
    readonly IDbWriteQueue queue;
    readonly LifePreferencesService preferences;
    internal CloudSyncStore(LifeDataService data, LifePreferencesService preferences)
    {
        this.preferences = preferences;
        queue = LifeDataStoreRuntimeRegistry.GetOrCreate(data.DatabasePath).WriteQueue;
        new OccurrenceOverrideStore(queue);
        queue.Execute(u => Execute(u, """
            CREATE TABLE IF NOT EXISTS cloud_sync_meta(id INTEGER PRIMARY KEY CHECK(id=1),owner TEXT NOT NULL,cursor INTEGER NOT NULL DEFAULT 0,last_success TEXT);
            CREATE TABLE IF NOT EXISTS cloud_sync_state(type TEXT NOT NULL,id TEXT NOT NULL,revision INTEGER NOT NULL,data TEXT,deleted INTEGER NOT NULL,PRIMARY KEY(type,id));
            CREATE TABLE IF NOT EXISTS cloud_sync_pending(type TEXT NOT NULL,id TEXT NOT NULL,operation TEXT NOT NULL,PRIMARY KEY(type,id));
            CREATE TABLE IF NOT EXISTS cloud_sync_conflicts(type TEXT NOT NULL,id TEXT NOT NULL,remote TEXT NOT NULL,local_copy TEXT,PRIMARY KEY(type,id));
            """));
    }
    internal void Bind(Guid owner) => queue.Execute(u =>
    {
        var saved = Scalar(u, "SELECT owner FROM cloud_sync_meta WHERE id=1") as string;
        if (saved is not null && saved != owner.ToString()) throw new InvalidOperationException("同步数据属于另一个账号，已停止同步。");
        Execute(u, "INSERT OR IGNORE INTO cloud_sync_meta(id,owner) VALUES(1,$owner)", ("$owner", owner.ToString()));
    });
    internal long Cursor => queue.Execute(u => Convert.ToInt64(Scalar(u, "SELECT cursor FROM cloud_sync_meta WHERE id=1") ?? 0));
    internal DateTimeOffset? LastSuccess => queue.Execute(u => DateTimeOffset.TryParse(Scalar(u, "SELECT last_success FROM cloud_sync_meta WHERE id=1") as string, out var at) ? (DateTimeOffset?)at : null);
    internal void Finish() => queue.Execute(u => Execute(u, "UPDATE cloud_sync_meta SET last_success=$at WHERE id=1", ("$at", DateTimeOffset.UtcNow.ToString("O"))));
    internal IReadOnlyList<CloudSyncConflict> Conflicts => queue.Execute(u =>
    {
        using var c = Command(u, "SELECT type,id,remote,local_copy FROM cloud_sync_conflicts ORDER BY type,id");
        using var r = c.ExecuteReader(); var result = new List<CloudSyncConflict>();
        while (r.Read())
        {
            var entity = JsonSerializer.Deserialize<CloudSyncEntity>(r.GetString(2))!;
            var title = r.GetString(0) == "preferences" ? "外观与偏好设置" : r.GetString(1);
            if (!r.IsDBNull(3))
            {
                using var json = JsonDocument.Parse(r.GetString(3));
                var root = json.RootElement;
                if (root.TryGetProperty("tables", out var tables))
                    foreach (var name in new[] { "life_items", "archived_todos" })
                        if (tables.TryGetProperty(name, out var rows) && rows.GetArrayLength() > 0 && rows[0].TryGetProperty("title", out var t)) title = t.GetString() ?? title;
                if (root.TryGetProperty("row", out var row) && row.TryGetProperty("title", out var text)) title = text.GetString() ?? title;
            }
            result.Add(new(r.GetString(0), r.GetString(1), title, entity.Deleted,
                Summary(r.IsDBNull(3) ? null : r.GetString(3)), entity.Deleted ? "已删除" : Summary(entity.Data?.GetRawText())));
        }
        return (IReadOnlyList<CloudSyncConflict>)result;
    });
    internal IReadOnlyList<CloudSyncOperation> Stage() => queue.Execute(u =>
    {
        var local = Capture(u);
        var state = States(u);
        foreach (var key in local.Keys.Concat(state.Keys).Distinct())
        {
            if (!Supported(key.Type) || Exists(u, "cloud_sync_pending", key) || Exists(u, "cloud_sync_conflicts", key)) continue;
            local.TryGetValue(key, out var value); state.TryGetValue(key, out var prior);
            if (key.Type == "preferences" && prior is null && DefaultPreferences(value)) continue;
            if (Same(value, prior)) continue;
            // A server tombstone is permanent. Retain a local recreation for an explicit decision.
            if (prior is { Deleted: true } && value is not null) { Conflict(u, prior, value); continue; }
            var deleted = value is null || IsTombstone(value);
            var operation = new CloudSyncOperation(Guid.NewGuid(), key.Type, key.Id, prior?.Revision ?? 0, 1, deleted ? null : Element(value), deleted);
            if (value?.Length > 262144) throw new InvalidOperationException("某条事项或聊天内容超过同步大小限制，请保留本机数据并缩短该条内容后重试。");
            Execute(u, "INSERT INTO cloud_sync_pending(type,id,operation) VALUES($type,$id,$op)", ("$type", key.Type), ("$id", key.Id), ("$op", JsonSerializer.Serialize(operation)));
        }
        using var c = Command(u, "SELECT operation FROM cloud_sync_pending ORDER BY type,id");
        using var r = c.ExecuteReader(); var result = new List<CloudSyncOperation>();
        while (r.Read()) result.Add(JsonSerializer.Deserialize<CloudSyncOperation>(r.GetString(0))!);
        return (IReadOnlyList<CloudSyncOperation>)result;
    });
    internal void Acknowledge(CloudSyncOperation op, long revision) => queue.Execute(u =>
    {
        State(u, new(op.EntityType, op.EntityId, revision, 1, op.Data, op.Deleted));
        Remove(u, "cloud_sync_pending", (op.EntityType, op.EntityId));
    });
    internal void Reject(CloudSyncOperation op, CloudSyncEntity remote) => queue.Execute(u =>
    {
        Capture(u).TryGetValue((op.EntityType, op.EntityId), out var local);
        if (Same(local, remote)) State(u, remote); else Conflict(u, remote, local);
        Remove(u, "cloud_sync_pending", (op.EntityType, op.EntityId));
    });
    internal int ApplyPage(IReadOnlyList<CloudSyncEntity> entities, long cursor)
    {
        var originalPreferences = preferences.Load();
        try { return queue.Execute(u =>
        {
        var local = Capture(u); var states = States(u); var applied = 0;
        foreach (var remote in entities.OrderBy(e => e.EntityType == "preferences"))
        {
            if (!Supported(remote.EntityType)) continue;
            if (remote.SchemaVersion != 1) throw new InvalidOperationException("云端数据版本较新，请更新时屿后同步。");
            var key = (remote.EntityType, remote.EntityId);
            states.TryGetValue(key, out var prior);
            if (prior?.Revision >= remote.Revision) continue;
            local.TryGetValue(key, out var current);
            if (Same(current, remote)) { State(u, remote); states[key] = remote; continue; }
            var cleanDefault = remote.EntityType == "preferences" && prior is null && DefaultPreferences(current);
            if (!Same(current, prior) && !cleanDefault || Exists(u, "cloud_sync_pending", key) || Exists(u, "cloud_sync_conflicts", key))
                Conflict(u, remote, current);
            else
            {
                Apply(u, remote); State(u, remote); applied++; states[key] = remote;
                if (remote.Deleted) local.Remove(key); else local[key] = remote.Data!.Value.GetRawText();
            }
        }
        Execute(u, "UPDATE cloud_sync_meta SET cursor=$cursor WHERE id=1", ("$cursor", cursor));
        return applied;
        }); }
        catch
        {
            if (preferences.Load() != originalPreferences) preferences.Save(originalPreferences);
            throw;
        }
    }
    internal void Resolve(CloudSyncConflict conflict, bool useLocal) => queue.Execute(u =>
    {
        var key = (conflict.EntityType, conflict.EntityId);
        var text = Scalar(u, "SELECT remote FROM cloud_sync_conflicts WHERE type=$type AND id=$id", ("$type", key.EntityType), ("$id", key.EntityId)) as string
            ?? throw new InvalidOperationException("该冲突已处理，请刷新同步状态。");
        var remote = JsonSerializer.Deserialize<CloudSyncEntity>(text)!;
        if (useLocal && remote.Deleted) throw new InvalidOperationException("云端已删除此记录，不能原位恢复。请先在本机另存为新事项。");
        if (!useLocal) Apply(u, remote);
        State(u, remote); Remove(u, "cloud_sync_conflicts", key);
    });
    Dictionary<(string Type, string Id), string> Capture(IUnitOfWork u)
    {
        var result = new Dictionary<(string, string), string>();
        var tables = ItemTables.ToDictionary(t => t.Table, t => ReadRows(u, t.Table));
        var ids = tables["life_items"].Concat(tables["archived_todos"]).Select(r => r["id"]!.GetValue<string>()).Distinct();
        foreach (var id in ids)
        {
            var selected = ItemTables.ToDictionary(t => t.Table, t => tables[t.Table].Where(r => r[t.Key]?.GetValue<string>() == id).ToArray());
            result[("items", id)] = JsonSerializer.Serialize(new { adapter = Adapter, tables = selected });
        }
        foreach (var (table, type) in new[] { ("chat_sessions", "chat_sessions"), ("chat_messages", "chat_messages") })
            foreach (var row in ReadRows(u, table)) result[(type, row["id"]!.GetValue<string>())] = JsonSerializer.Serialize(new { adapter = Adapter, row });
        var portable = Portable(preferences.Load());
        result[("preferences", "desktop")] = JsonSerializer.Serialize(new { adapter = Adapter, values = portable });
        return result;
    }
    internal static JsonObject Portable(LifePreferences value)
    {
        var all = JsonSerializer.SerializeToNode(value)!.AsObject();
        return new JsonObject(all.Where(p => !DevicePreferences.Contains(p.Key)).OrderBy(p => p.Key).Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value?.DeepClone())));
    }
    void Apply(IUnitOfWork u, CloudSyncEntity entity)
    {
        JsonObject? payload = entity.Deleted ? null : JsonNode.Parse(entity.Data?.GetRawText() ?? "null") as JsonObject;
        if (!entity.Deleted && (payload?["adapter"]?.GetValue<string>() != Adapter)) throw new InvalidOperationException("云端数据格式尚不受当前时屿版本支持，已保留本地数据。");
        if (entity.EntityType == "preferences")
        {
            if (entity.Deleted || entity.EntityId != "desktop") throw new InvalidOperationException("云端设置记录无效。");
            var current = JsonSerializer.SerializeToNode(preferences.Load())!.AsObject();
            var values = payload!["values"] as JsonObject ?? throw new InvalidOperationException("云端设置格式无效。");
            var allowed = Portable(preferences.Load());
            foreach (var p in values)
            {
                if (!allowed.ContainsKey(p.Key) || p.Value is null ||
                    p.Value.GetValueKind() != allowed[p.Key]!.GetValueKind() && !(p.Value.GetValueKind() is JsonValueKind.True or JsonValueKind.False && allowed[p.Key]!.GetValueKind() is JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidOperationException("云端设置字段无效。");
                current[p.Key] = p.Value.DeepClone();
            }
            preferences.Save(current.Deserialize<LifePreferences>()!);
            return;
        }
        if (entity.EntityType == "items")
        {
            if (entity.Deleted)
            {
                // Keep canonical tombstones for referenced focus/history records and remove active legacy projections.
                Execute(u, "UPDATE life_items SET deleted_at=COALESCE(deleted_at,$at),row_version=row_version+1 WHERE id=$id", ("$id", entity.EntityId), ("$at", DateTimeOffset.UtcNow.ToString("O")));
                foreach (var t in ItemTables.Skip(1)) Execute(u, $"DELETE FROM {t.Table} WHERE {t.Key}=$id", ("$id", entity.EntityId));
                return;
            }
            var tables = payload!["tables"] as JsonObject ?? throw new InvalidOperationException("云端事项格式无效。");
            if (tables.Count != ItemTables.Length || ItemTables.Any(t => tables[t.Table] is not JsonArray)) throw new InvalidOperationException("云端事项表结构无效。");
            foreach (var t in ItemTables)
            {
                var rows = (JsonArray)tables[t.Table]!;
                var columns = Columns(u, t.Table);
                foreach (var row in rows.OfType<JsonObject>())
                    if (row[t.Key]?.GetValue<string>() != entity.EntityId || row.Any(p => !columns.Contains(p.Key) || p.Key is "notified_at" or "last_notified_at" || t.Table == "occurrence_overrides" && p.Key == "id")) throw new InvalidOperationException("云端事项标识或字段无效。");
                if (rows.Any(r => r is not JsonObject)) throw new InvalidOperationException("云端事项行格式无效。");
                if (t.Key == "id" && rows.Count > 1) throw new InvalidOperationException("云端事项存在重复行。");
                if (t.Table == "occurrence_overrides" || rows.Count == 0 && t.Table != "life_items")
                    Execute(u, $"DELETE FROM {t.Table} WHERE {t.Key}=$id", ("$id", entity.EntityId));
                else if (t.Table == "recurrence_rules")
                {
                    using var clean = Command(u, "DELETE FROM recurrence_rules WHERE series_item_id=$id AND id NOT IN (" + string.Join(',', rows.Select((_, i) => "$r" + i)) + ")", ("$id", entity.EntityId));
                    for (var i = 0; i < rows.Count; i++) clean.Parameters.AddWithValue("$r" + i, rows[i]!["id"]!.GetValue<string>());
                    clean.ExecuteNonQuery();
                }
                foreach (var row in rows.OfType<JsonObject>()) Upsert(u, t.Table, row, t.Table == "occurrence_overrides");
            }
            return;
        }
        var table = entity.EntityType == "chat_sessions" ? "chat_sessions" : "chat_messages";
        if (entity.Deleted) Execute(u, $"DELETE FROM {table} WHERE id=$id", ("$id", entity.EntityId));
        else
        {
            var row = payload!["row"] as JsonObject ?? throw new InvalidOperationException("云端聊天格式无效。");
            if (row["id"]?.GetValue<string>() != entity.EntityId || row.Any(p => !Columns(u, table).Contains(p.Key))) throw new InvalidOperationException("云端聊天标识或字段无效。");
            Upsert(u, table, row, false);
        }
    }
    static void Upsert(IUnitOfWork u, string table, JsonObject row, bool append)
    {
        var fields = row.Select(p => p.Key).ToArray();
        var update = fields.Where(f => f != "id").Select(f => $"\"{f}\"=excluded.\"{f}\"").ToList();
        if (table is "todos" or "calendar_events" or "single_reminders")
            update.Add($"notified_at=CASE WHEN {table}.remind_at IS NOT excluded.remind_at THEN NULL ELSE {table}.notified_at END");
        if (table == "recurring_reminders")
            update.Add("last_notified_at=CASE WHEN recurring_reminders.reminder_time IS NOT excluded.reminder_time OR recurring_reminders.recurrence IS NOT excluded.recurrence OR recurring_reminders.weekdays IS NOT excluded.weekdays THEN NULL ELSE recurring_reminders.last_notified_at END");
        if (table == "assistant_reminder_schedules")
        {
            var prior = Scalar(u, "SELECT schedule_json FROM assistant_reminder_schedules WHERE series_item_id=$id", ("$id", row["series_item_id"]!.GetValue<string>())) as string;
            if (prior is not null && prior != row["schedule_json"]!.GetValue<string>())
                Execute(u, "UPDATE recurring_reminders SET last_notified_at=NULL WHERE id=$id", ("$id", row["series_item_id"]!.GetValue<string>()));
        }
        using var c = Command(u, $"INSERT INTO {table}({string.Join(',', fields.Select(f => $"\"{f}\""))}) VALUES({string.Join(',', fields.Select((_, i) => "$p" + i))})" +
            (append ? "" : " ON CONFLICT DO UPDATE SET " + string.Join(',', update)));
        for (var i = 0; i < fields.Length; i++)
        {
            var v = row[fields[i]];
            object value = v is null ? DBNull.Value : v.GetValueKind() switch
            { JsonValueKind.String => v.GetValue<string>(), JsonValueKind.Number => v.GetValue<long>(), _ => throw new InvalidOperationException("云端数据值类型无效。") };
            c.Parameters.AddWithValue("$p" + i, value);
        }
        c.ExecuteNonQuery();
    }
    static List<JsonObject> ReadRows(IUnitOfWork u, string table)
    {
        var result = new List<JsonObject>();
        using var c = Command(u, $"SELECT * FROM {table} ORDER BY " + (table == "occurrence_overrides" ? "original_start_utc,rule_version,created_at_utc" : table == "assistant_reminder_schedules" ? "series_item_id" : "id"));
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            var row = new JsonObject();
            for (var i = 0; i < r.FieldCount; i++)
            {
                var name = r.GetName(i);
                if (name is "notified_at" or "last_notified_at" || table == "occurrence_overrides" && name == "id") continue;
                row[name] = r.IsDBNull(i) ? null : JsonSerializer.SerializeToNode(r.GetValue(i));
            }
            result.Add(row);
        }
        return result;
    }
    static HashSet<string> Columns(IUnitOfWork u, string table)
    {
        using var c = Command(u, $"PRAGMA table_info({table})"); using var r = c.ExecuteReader(); var result = new HashSet<string>();
        while (r.Read()) result.Add(r.GetString(1)); return result;
    }
    static Dictionary<(string Type, string Id), CloudSyncEntity> States(IUnitOfWork u)
    {
        using var c = Command(u, "SELECT type,id,revision,data,deleted FROM cloud_sync_state"); using var r = c.ExecuteReader();
        var result = new Dictionary<(string, string), CloudSyncEntity>();
        while (r.Read()) result[(r.GetString(0), r.GetString(1))] = new(r.GetString(0), r.GetString(1), r.GetInt64(2), 1, r.IsDBNull(3) ? null : Element(r.GetString(3)), r.GetBoolean(4));
        return result;
    }
    static bool Supported(string type) => type is "items" or "preferences" or "chat_sessions" or "chat_messages";
    static bool Same(string? local, CloudSyncEntity? remote) => local is null ? remote is null || remote.Deleted : IsTombstone(local) ? remote is { Deleted: true } : remote is { Deleted: false } && Equivalent(local, remote.Data?.GetRawText());
    static bool IsTombstone(string value)
    {
        var root = JsonNode.Parse(value);
        return root?["tables"] is JsonObject tables && tables["life_items"] is JsonArray { Count: > 0 } life &&
            life[0]?["deleted_at"] is not null && tables["archived_todos"] is JsonArray { Count: 0 };
    }
    static bool Equivalent(string? a, string? b) => a is not null && b is not null && JsonNode.DeepEquals(JsonNode.Parse(a), JsonNode.Parse(b));
    static bool DefaultPreferences(string? value) => Equivalent(value, JsonSerializer.Serialize(new { adapter = Adapter, values = Portable(LifePreferences.Default) }));
    static string Summary(string? value)
    {
        if (value is null) return "已删除";
        var root = JsonNode.Parse(value);
        if (root?["values"] is JsonObject settings) return $"主题：{settings["ThemeMode"]} · 配色：{settings["AccentScheme"]} · 时钟：{settings["IslandShowClock"]}";
        JsonNode? row = root?["row"];
        if (root?["tables"] is JsonObject tables)
            row = (tables["life_items"] as JsonArray)?.FirstOrDefault() ?? (tables["archived_todos"] as JsonArray)?.FirstOrDefault();
        var text = row is null ? "记录详情待检查" : string.Join(" · ", new[] { "title", "status", "notes", "content", "updated_at" }.Select(k => row[k]?.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)));
        return text.Length > 220 ? text[..220] + "…" : text;
    }
    static JsonElement? Element(string? value)
    { if (value is null) return null; using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }
    static void State(IUnitOfWork u, CloudSyncEntity e) => Execute(u, "INSERT INTO cloud_sync_state(type,id,revision,data,deleted) VALUES($type,$id,$revision,$data,$deleted) ON CONFLICT(type,id) DO UPDATE SET revision=excluded.revision,data=excluded.data,deleted=excluded.deleted",
        ("$type", e.EntityType), ("$id", e.EntityId), ("$revision", e.Revision), ("$data", e.Data?.GetRawText()), ("$deleted", e.Deleted ? 1 : 0));
    static void Conflict(IUnitOfWork u, CloudSyncEntity e, string? local) => Execute(u, "INSERT INTO cloud_sync_conflicts(type,id,remote,local_copy) VALUES($type,$id,$remote,$local) ON CONFLICT(type,id) DO UPDATE SET remote=excluded.remote,local_copy=excluded.local_copy",
        ("$type", e.EntityType), ("$id", e.EntityId), ("$remote", JsonSerializer.Serialize(e)), ("$local", local));
    static bool Exists(IUnitOfWork u, string table, (string Type, string Id) key) => Convert.ToInt64(Scalar(u, $"SELECT EXISTS(SELECT 1 FROM {table} WHERE type=$type AND id=$id)", ("$type", key.Type), ("$id", key.Id))) != 0;
    static void Remove(IUnitOfWork u, string table, (string Type, string Id) key) => Execute(u, $"DELETE FROM {table} WHERE type=$type AND id=$id", ("$type", key.Type), ("$id", key.Id));
    static SqliteCommand Command(IUnitOfWork u, string sql, params (string Name, object? Value)[] values)
    { var c = u.Connection.CreateCommand(); c.Transaction = u.Transaction; c.CommandText = sql; foreach (var p in values) c.Parameters.AddWithValue(p.Name, p.Value ?? DBNull.Value); return c; }
    static object? Scalar(IUnitOfWork u, string sql, params (string Name, object? Value)[] values) { using var c = Command(u, sql, values); return c.ExecuteScalar(); }
    static void Execute(IUnitOfWork u, string sql, params (string Name, object? Value)[] values) { using var c = Command(u, sql, values); c.ExecuteNonQuery(); }
}
