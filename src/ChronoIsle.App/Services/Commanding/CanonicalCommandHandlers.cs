using System.Globalization;
using System.Text.Json;
using System.Xml;
using Microsoft.Data.Sqlite;
using ChronoIsle.App.Services.Domain;
using ChronoIsle.App.Services.Persistence;

namespace ChronoIsle.App.Services.Commanding;

public sealed record CommandHandlerResult(
    bool Succeeded,
    string Code,
    string ResultJson,
    IReadOnlyList<string> ItemIds)
{
    public static CommandHandlerResult Unsupported(string command) =>
        new(false, "unsupported", JsonSerializer.Serialize(new { command, error = "unsupported" }), []);
}

public sealed record AssistantCommandExecutionContext(
    IUnitOfWork UnitOfWork,
    DateTimeOffset NowUtc,
    IReadOnlyList<TargetRowVersion> Targets,
    IOccurrenceTimeResolver OccurrenceResolver,
    ITimeZoneCatalog TimeZones);

public interface ICommandHandler<in T> where T : IAssistantCommandArgumentsV1
{
    CommandHandlerResult Execute(AssistantCommandExecutionContext context, T arguments);
}

internal static class CanonicalCommandHandlers
{
    public static CommandHandlerResult Execute(AssistantCommandExecutionContext context, AssistantCommandEnvelope envelope) =>
        (envelope.Command, envelope.Arguments) switch
        {
            (AssistantCommandName.CreateTodo, CreateTodoArgumentsV1 arguments) =>
                new CreateTodoCommandHandler().Execute(context, arguments),
            (AssistantCommandName.CreateReminder, CreateReminderArgumentsV1 arguments) =>
                new CreateReminderCommandHandler().Execute(context, arguments),
            (AssistantCommandName.CreateEvent, CreateEventArgumentsV1 arguments) =>
                new CreateEventCommandHandler().Execute(context, arguments),
            (AssistantCommandName.CreateLongTermItem, CreateLongTermItemArgumentsV1 arguments) =>
                new CreateLongTermItemCommandHandler().Execute(context, arguments),
            (AssistantCommandName.ListItems, ListItemsArgumentsV1 arguments) =>
                new ListItemsCommandHandler().Execute(context, arguments),
            (AssistantCommandName.UpdateTodo, UpdateTodoArgumentsV1 arguments) =>
                new UpdateTodoCommandHandler().Execute(context, arguments),
            (AssistantCommandName.CompleteTodo, CompleteTodoArgumentsV1 arguments) =>
                new CompleteTodoCommandHandler().Execute(context, arguments),
            (AssistantCommandName.DeleteTodo, DeleteTodoArgumentsV1 arguments) =>
                new DeleteTodoCommandHandler().Execute(context, arguments),
            (AssistantCommandName.CreateRecurringTask, CreateRecurringTaskArgumentsV1 arguments) =>
                new CreateRecurringTaskCommandHandler().Execute(context, arguments),
            (AssistantCommandName.RescheduleItem, RescheduleItemArgumentsV1 arguments) =>
                new RescheduleItemCommandHandler().Execute(context, arguments),
            _ => CommandHandlerResult.Unsupported(AssistantCommandEnvelopeJson.CommandName(envelope.Command))
        };

    internal static TargetRowVersion SingleTarget(AssistantCommandExecutionContext context)
    {
        if (context.Targets.Count != 1) throw new InvalidOperationException("A single resolved target is required.");
        return context.Targets[0];
    }

    internal static void RequireAffected(int affected)
    {
        if (affected != 1) throw new CanonicalConcurrencyException();
    }

    internal static string ItemResult(string id, long rowVersion = 1) =>
        JsonSerializer.Serialize(new { itemId = id, rowVersion });
}

public sealed class CanonicalConcurrencyException() : InvalidOperationException("The target item changed before the command executed.");

internal sealed record ResolvedAssistantTime(
    DateTime LocalDateTime,
    DateTimeOffset UtcInstant,
    string IanaTimeZoneId,
    string? WindowsTimeZoneId);

internal static class AssistantCommandTimeResolver
{
    public static ResolvedAssistantTime Resolve(AssistantCommandExecutionContext context, AssistantTimeExpressionV1 expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var iana = string.IsNullOrWhiteSpace(expression.TimeZoneHint)
            ? context.TimeZones.LocalIanaTimeZoneId
            : expression.TimeZoneHint.Trim();
        if (!context.TimeZones.TryResolveIana(iana, out var zone, out var windows) || zone is null)
            throw new TimeZoneNotFoundException($"Unknown IANA time zone '{iana}'.");

        DateTime local;
        if (expression.LocalDate is not null && expression.LocalTime is not null)
        {
            local = expression.LocalDate.Value.ToDateTime(expression.LocalTime.Value, DateTimeKind.Unspecified);
        }
        else if (!string.IsNullOrWhiteSpace(expression.RelativeExpression))
        {
            var duration = ParseRelative(expression.RelativeExpression);
            local = DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(context.NowUtc, zone).DateTime.Add(duration), DateTimeKind.Unspecified);
        }
        else
        {
            throw new InvalidOperationException("The time expression has no deterministic local value.");
        }

        var temporal = new TemporalValue(local, null, iana, windows, TimeSemantics.ZonedWallClock);
        return new(local, context.OccurrenceResolver.ResolveSingleLocalTime(temporal), iana, windows);
    }

    static TimeSpan ParseRelative(string value)
    {
        var normalized = value.Trim();
        var compact = normalized.Replace(" ", "", StringComparison.Ordinal);
        if (compact == "\u4e00\u5c0f\u65f6\u540e") return TimeSpan.FromHours(1);
        if (compact == "\u534a\u5c0f\u65f6\u540e") return TimeSpan.FromMinutes(30);

        const string MinutesLater = "\u5206\u949f\u540e";
        if (compact.EndsWith(MinutesLater, StringComparison.Ordinal) &&
            int.TryParse(compact[..^MinutesLater.Length], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) && minutes > 0)
            return TimeSpan.FromMinutes(minutes);

        const string HoursLater = "\u5c0f\u65f6\u540e";
        if (compact.EndsWith(HoursLater, StringComparison.Ordinal) &&
            int.TryParse(compact[..^HoursLater.Length], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) && hours > 0)
            return TimeSpan.FromHours(hours);

        if (normalized.StartsWith("P", StringComparison.OrdinalIgnoreCase))
        {
            try { return XmlConvert.ToTimeSpan(normalized.ToUpperInvariant()); }
            catch (FormatException) { }
        }
        if (TimeSpan.TryParse(normalized, CultureInfo.InvariantCulture, out var duration) && duration > TimeSpan.Zero)
            return duration;
        throw new InvalidOperationException("Relative time must be a positive ISO-8601 duration or TimeSpan.");
    }

    public static void Add(SqliteCommand command, string prefix, ResolvedAssistantTime? value)
    {
        command.Parameters.AddWithValue($"${prefix}Local", Db(value?.LocalDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture)));
        command.Parameters.AddWithValue($"${prefix}Utc", Db(value?.UtcInstant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));
        command.Parameters.AddWithValue($"${prefix}Iana", Db(value?.IanaTimeZoneId));
        command.Parameters.AddWithValue($"${prefix}Windows", Db(value?.WindowsTimeZoneId));
        command.Parameters.AddWithValue($"${prefix}Semantics", Db(value is null ? null : "ZonedWallClock"));
    }

    public static string? Legacy(ResolvedAssistantTime? value) =>
        value?.LocalDateTime.ToString("O", CultureInfo.InvariantCulture);

    static object Db(string? value) => value is null ? DBNull.Value : value;
}

internal abstract class CreateItemCommandHandler
{
    protected static string Insert(
        AssistantCommandExecutionContext context,
        string kind,
        string title,
        string? notes,
        ResolvedAssistantTime? due,
        ResolvedAssistantTime? remind,
        ResolvedAssistantTime? start,
        ResolvedAssistantTime? end,
        bool mirrorLegacy = true)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new InvalidOperationException("A title is required.");
        var id = Guid.NewGuid().ToString("N");
        using (var command = context.UnitOfWork.Connection.CreateCommand())
        {
            command.Transaction = context.UnitOfWork.Transaction;
            command.CommandText = """
                INSERT INTO life_items(
                    id,kind,title,notes,status,row_version,
                    due_local_datetime,due_utc_instant,due_iana_time_zone_id,due_windows_time_zone_id_cache,due_time_semantics,
                    remind_local_datetime,remind_utc_instant,remind_iana_time_zone_id,remind_windows_time_zone_id_cache,remind_time_semantics,
                    start_local_datetime,start_utc_instant,start_iana_time_zone_id,start_windows_time_zone_id_cache,start_time_semantics,
                    end_local_datetime,end_utc_instant,end_iana_time_zone_id,end_windows_time_zone_id_cache,end_time_semantics,
                    origin_type,origin_adapter,is_readonly,readonly_reason,raw_external_payload_id,created_at,updated_at,deleted_at)
                VALUES($id,$kind,$title,$notes,'Pending',1,
                    $dueLocal,$dueUtc,$dueIana,$dueWindows,$dueSemantics,
                    $remindLocal,$remindUtc,$remindIana,$remindWindows,$remindSemantics,
                    $startLocal,$startUtc,$startIana,$startWindows,$startSemantics,
                    $endLocal,$endUtc,$endIana,$endWindows,$endSemantics,
                    'Local',NULL,0,NULL,NULL,$now,$now,NULL)
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$title", title.Trim());
            command.Parameters.AddWithValue("$notes", notes is null ? DBNull.Value : notes);
            AssistantCommandTimeResolver.Add(command, "due", due);
            AssistantCommandTimeResolver.Add(command, "remind", remind);
            AssistantCommandTimeResolver.Add(command, "start", start);
            AssistantCommandTimeResolver.Add(command, "end", end);
            command.Parameters.AddWithValue("$now", context.NowUtc.ToUniversalTime().ToString("O"));
            command.ExecuteNonQuery();
        }
        if (mirrorLegacy) MirrorLegacy(context, id, kind, title.Trim(), notes, due, remind, start, end);
        return id;
    }

    static void MirrorLegacy(
        AssistantCommandExecutionContext context,
        string id,
        string kind,
        string title,
        string? notes,
        ResolvedAssistantTime? due,
        ResolvedAssistantTime? remind,
        ResolvedAssistantTime? start,
        ResolvedAssistantTime? end)
    {
        using var command = context.UnitOfWork.Connection.CreateCommand();
        command.Transaction = context.UnitOfWork.Transaction;
        var now = context.NowUtc.ToLocalTime().DateTime.ToString("O");
        if (kind == "Todo")
        {
            command.CommandText = """
                INSERT INTO todos(id,title,notes,completed,due_at,remind_at,notified_at,created_at,updated_at)
                VALUES($id,$title,$notes,0,$due,$remind,NULL,$now,$now)
                """;
            command.Parameters.AddWithValue("$due", (object?)AssistantCommandTimeResolver.Legacy(due) ?? DBNull.Value);
            command.Parameters.AddWithValue("$remind", (object?)AssistantCommandTimeResolver.Legacy(remind) ?? DBNull.Value);
        }
        else if (kind == "Reminder")
        {
            command.CommandText = """
                INSERT INTO single_reminders(id,title,notes,remind_at,notified_at,created_at,updated_at)
                VALUES($id,$title,$notes,$remind,NULL,$now,$now)
                """;
            command.Parameters.AddWithValue("$remind", AssistantCommandTimeResolver.Legacy(remind)!);
        }
        else if (kind == "Event")
        {
            command.CommandText = """
                INSERT INTO calendar_events(id,title,notes,start_at,end_at,remind_at,notified_at,created_at,updated_at)
                VALUES($id,$title,$notes,$start,$end,$remind,NULL,$now,$now)
                """;
            command.Parameters.AddWithValue("$start", AssistantCommandTimeResolver.Legacy(start)!);
            command.Parameters.AddWithValue("$end", AssistantCommandTimeResolver.Legacy(end)!);
            command.Parameters.AddWithValue("$remind", (object?)AssistantCommandTimeResolver.Legacy(remind) ?? DBNull.Value);
        }
        else return;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$notes", notes is null ? DBNull.Value : notes);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
    }
}

internal sealed class CreateTodoCommandHandler : CreateItemCommandHandler, ICommandHandler<CreateTodoArgumentsV1>
{
    public CommandHandlerResult Execute(AssistantCommandExecutionContext context, CreateTodoArgumentsV1 arguments)
    {
        var due = arguments.Due is null ? null : AssistantCommandTimeResolver.Resolve(context, arguments.Due);
        var remind = arguments.Remind is null ? null : AssistantCommandTimeResolver.Resolve(context, arguments.Remind);
        var id = Insert(context, "Todo", arguments.Title!, arguments.Notes, due, remind, null, null);
        return new(true, "created", CanonicalCommandHandlers.ItemResult(id), [id]);
    }
}

internal sealed class CreateReminderCommandHandler : CreateItemCommandHandler, ICommandHandler<CreateReminderArgumentsV1>
{
    public CommandHandlerResult Execute(AssistantCommandExecutionContext context, CreateReminderArgumentsV1 arguments)
    {
        var remind = AssistantCommandTimeResolver.Resolve(context, arguments.Remind!);
        var due = arguments.Due is null ? null : AssistantCommandTimeResolver.Resolve(context, arguments.Due);
        var id = Insert(context, "Reminder", arguments.Title!, arguments.Notes, due, remind, null, null);
        return new(true, "created", CanonicalCommandHandlers.ItemResult(id), [id]);
    }
}

internal sealed class CreateEventCommandHandler : CreateItemCommandHandler, ICommandHandler<CreateEventArgumentsV1>
{
    public CommandHandlerResult Execute(AssistantCommandExecutionContext context, CreateEventArgumentsV1 arguments)
    {
        var start = AssistantCommandTimeResolver.Resolve(context, arguments.Start!);
        var end = AssistantCommandTimeResolver.Resolve(context, arguments.End!);
        if (end.UtcInstant <= start.UtcInstant) throw new InvalidOperationException("Event end must be later than start.");
        if (!string.Equals(start.IanaTimeZoneId, end.IanaTimeZoneId, StringComparison.Ordinal))
            throw new InvalidOperationException("Editable event start and end must share one time zone.");
        var remind = arguments.Remind is null ? null : AssistantCommandTimeResolver.Resolve(context, arguments.Remind);
        var id = Insert(context, "Event", arguments.Title!, arguments.Notes, null, remind, start, end);
        return new(true, "created", CanonicalCommandHandlers.ItemResult(id), [id]);
    }
}

internal sealed class CreateLongTermItemCommandHandler : CreateItemCommandHandler, ICommandHandler<CreateLongTermItemArgumentsV1>
{
    public CommandHandlerResult Execute(AssistantCommandExecutionContext context, CreateLongTermItemArgumentsV1 arguments)
    {
        var due = arguments.Due is null ? null : AssistantCommandTimeResolver.Resolve(context, arguments.Due);
        var remind = arguments.Remind is null ? null : AssistantCommandTimeResolver.Resolve(context, arguments.Remind);
        var id = Insert(context, "Todo", arguments.Title!, arguments.Notes, due, remind, null, null, mirrorLegacy: false);
        using var command = context.UnitOfWork.Connection.CreateCommand();
        command.Transaction = context.UnitOfWork.Transaction;
        command.CommandText = "UPDATE life_items SET item_type='LongTerm' WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        CanonicalCommandHandlers.RequireAffected(command.ExecuteNonQuery());
        return new(true, "created", CanonicalCommandHandlers.ItemResult(id), [id]);
    }
}

internal sealed class CreateRecurringTaskCommandHandler : CreateItemCommandHandler, ICommandHandler<CreateRecurringTaskArgumentsV1>
{
    public CommandHandlerResult Execute(AssistantCommandExecutionContext context, CreateRecurringTaskArgumentsV1 arguments)
    {
        if (arguments.Kind is not (AssistantItemKindV1.Todo or AssistantItemKindV1.Reminder))
            throw new InvalidOperationException("A recurring task must be a todo or reminder.");

        var recurrence = arguments.Recurrence!;
        var wallExpression = arguments.WallStart!;
        var zone = string.IsNullOrWhiteSpace(wallExpression.TimeZoneHint)
            ? context.TimeZones.LocalIanaTimeZoneId : wallExpression.TimeZoneHint!;
        if (!context.TimeZones.TryResolveIana(zone, out var zoneInfo, out _) || zoneInfo is null)
            throw new TimeZoneNotFoundException($"Unknown IANA time zone '{zone}'.");
        var localDate = wallExpression.LocalDate ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(context.NowUtc, zoneInfo).DateTime);
        if (recurrence.Frequency == AssistantRecurrenceFrequencyV1.Monthly)
        {
            var before = localDate.ToDateTime(wallExpression.LocalTime!.Value).AddTicks(-1);
            localDate = DateOnly.FromDateTime(MonthlyRecurrenceCalculator.NextAfter(before, recurrence.MonthDay!.Value));
        }
        var wall = AssistantCommandTimeResolver.Resolve(context, wallExpression with { LocalDate = localDate, TimeZoneHint = zone });
        var kind = arguments.Kind == AssistantItemKindV1.Todo ? "Todo" : "Reminder";
        var id = Insert(context, kind, arguments.Title!, arguments.Notes,
            kind == "Todo" ? wall : null, kind == "Reminder" ? wall : null, null, null);

        using var command = context.UnitOfWork.Connection.CreateCommand();
        command.Transaction = context.UnitOfWork.Transaction;
        command.CommandText = """
            INSERT INTO recurrence_rules(id,series_item_id,rule_version,start_local_datetime,iana_time_zone_id,
              windows_time_zone_id_cache,frequency,interval,weekdays,month_day,end_kind,end_local_datetime,
              occurrence_count,next_occurrence_utc,row_version,created_at,updated_at,deleted_at)
            VALUES($id,$series,1,$start,$iana,$windows,$frequency,$interval,$weekdays,$monthDay,$endKind,$until,
              $count,$next,1,$now,$now,NULL)
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$series", id);
        command.Parameters.AddWithValue("$start", wall.LocalDateTime.ToString("O"));
        command.Parameters.AddWithValue("$iana", wall.IanaTimeZoneId);
        command.Parameters.AddWithValue("$windows", (object?)wall.WindowsTimeZoneId ?? DBNull.Value);
        command.Parameters.AddWithValue("$frequency", recurrence.Frequency!.ToString());
        command.Parameters.AddWithValue("$interval", recurrence.Interval ?? 1);
        command.Parameters.AddWithValue("$weekdays", recurrence.Weekdays is { Count: > 0 } ? string.Join(',', recurrence.Weekdays.Select(day => (int)day)) : DBNull.Value);
        command.Parameters.AddWithValue("$monthDay", (object?)recurrence.MonthDay ?? DBNull.Value);
        command.Parameters.AddWithValue("$endKind", recurrence.End?.Kind?.ToString() ?? "Never");
        command.Parameters.AddWithValue("$until", recurrence.End?.UntilDate is { } until ? until.ToDateTime(TimeOnly.MaxValue).ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("$count", (object?)recurrence.End?.Count ?? DBNull.Value);
        command.Parameters.AddWithValue("$next", wall.UtcInstant.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$now", context.NowUtc.ToUniversalTime().ToString("O"));
        command.ExecuteNonQuery();
        return new(true, "created", CanonicalCommandHandlers.ItemResult(id), [id]);
    }
}
public sealed class ListItemsCommandHandler : ICommandHandler<ListItemsArgumentsV1>
{
    public CommandHandlerResult Execute(AssistantCommandExecutionContext context, ListItemsArgumentsV1 arguments)
    {
        using var command = context.UnitOfWork.Connection.CreateCommand();
        command.Transaction = context.UnitOfWork.Transaction;
        command.CommandText = """
            SELECT id,COALESCE(item_type,kind),title,status,row_version,due_utc_instant,remind_utc_instant,start_utc_instant,end_utc_instant
            FROM life_items
            WHERE deleted_at IS NULL AND ($kind IS NULL
              OR ($kind='LongTerm' AND item_type='LongTerm')
              OR ($kind<>'LongTerm' AND kind=$kind AND item_type IS NULL))
              AND ($completed=1 OR status<>'Completed')
            ORDER BY COALESCE(start_utc_instant,due_utc_instant,remind_utc_instant,created_at),title
            LIMIT 200
            """;
        command.Parameters.AddWithValue("$kind", arguments.Kind is null ? DBNull.Value : arguments.Kind.ToString());
        command.Parameters.AddWithValue("$completed", arguments.IncludeCompleted == true ? 1 : 0);
        using var reader = command.ExecuteReader();
        var items = new List<object>();
        var ids = new List<string>();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            ids.Add(id);
            items.Add(new
            {
                id,
                kind = reader.GetString(1),
                title = reader.GetString(2),
                status = reader.GetString(3),
                rowVersion = reader.GetInt64(4),
                dueAt = reader.IsDBNull(5) ? null : reader.GetString(5),
                remindAt = reader.IsDBNull(6) ? null : reader.GetString(6),
                startAt = reader.IsDBNull(7) ? null : reader.GetString(7),
                endAt = reader.IsDBNull(8) ? null : reader.GetString(8)
            });
        }
        return new(true, "listed", JsonSerializer.Serialize(new { items }), ids);
    }
}

public sealed class CompleteTodoCommandHandler : ICommandHandler<CompleteTodoArgumentsV1>
{
    public CommandHandlerResult Execute(AssistantCommandExecutionContext context, CompleteTodoArgumentsV1 arguments)
    {
        var target = CanonicalCommandHandlers.SingleTarget(context);
        var kind = ReadKind(context, target.ItemId);
        using (var command = context.UnitOfWork.Connection.CreateCommand())
        {
            command.Transaction = context.UnitOfWork.Transaction;
            command.CommandText = """
                UPDATE life_items SET status='Completed',completed_at_utc=$now,row_version=row_version+1,updated_at=$now
                WHERE id=$id AND row_version=$version AND kind IN ('Todo','Reminder','Event','LongTerm')
                  AND deleted_at IS NULL AND is_readonly=0
                """;
            command.Parameters.AddWithValue("$id", target.ItemId);
            command.Parameters.AddWithValue("$version", target.RowVersion);
            command.Parameters.AddWithValue("$now", context.NowUtc.ToString("O"));
            CanonicalCommandHandlers.RequireAffected(command.ExecuteNonQuery());
        }
        var legacySql = kind switch
        {
            "Todo" => "UPDATE todos SET completed=1,updated_at=$now WHERE id=$id",
            "Reminder" => "DELETE FROM single_reminders WHERE id=$id",
            "Event" => "DELETE FROM calendar_events WHERE id=$id",
            _ => null
        };
        if (legacySql is not null) ExecuteLegacy(context, legacySql, target.ItemId);
        return new(true, "completed", CanonicalCommandHandlers.ItemResult(target.ItemId, target.RowVersion + 1), [target.ItemId]);
    }

    internal static string ReadKind(AssistantCommandExecutionContext context, string id)
    {
        using var command = context.UnitOfWork.Connection.CreateCommand();
        command.Transaction = context.UnitOfWork.Transaction;
        command.CommandText = "SELECT COALESCE(item_type,kind) FROM life_items WHERE id=$id AND deleted_at IS NULL";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as string ?? throw new CanonicalConcurrencyException();
    }

    internal static void ExecuteLegacy(AssistantCommandExecutionContext context, string sql, string id)
    {
        using var command = context.UnitOfWork.Connection.CreateCommand();
        command.Transaction = context.UnitOfWork.Transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$now", context.NowUtc.ToLocalTime().DateTime.ToString("O"));
        command.ExecuteNonQuery();
    }
}

public sealed class DeleteTodoCommandHandler : ICommandHandler<DeleteTodoArgumentsV1>
{
    public CommandHandlerResult Execute(AssistantCommandExecutionContext context, DeleteTodoArgumentsV1 arguments)
    {
        var target = CanonicalCommandHandlers.SingleTarget(context);
        var kind = CompleteTodoCommandHandler.ReadKind(context, target.ItemId);
        using (var command = context.UnitOfWork.Connection.CreateCommand())
        {
            command.Transaction = context.UnitOfWork.Transaction;
            command.CommandText = """
                UPDATE life_items SET status='Cancelled',deleted_at=$now,updated_at=$now,row_version=row_version+1
                WHERE id=$id AND row_version=$version AND kind IN ('Todo','Reminder','Event','LongTerm')
                  AND deleted_at IS NULL AND is_readonly=0
                """;
            command.Parameters.AddWithValue("$id", target.ItemId);
            command.Parameters.AddWithValue("$version", target.RowVersion);
            command.Parameters.AddWithValue("$now", context.NowUtc.ToString("O"));
            CanonicalCommandHandlers.RequireAffected(command.ExecuteNonQuery());
        }
        var table = kind switch { "Todo" => "todos", "Reminder" => "single_reminders", "Event" => "calendar_events", _ => null };
        if (table is not null) CompleteTodoCommandHandler.ExecuteLegacy(context, $"DELETE FROM {table} WHERE id=$id", target.ItemId);
        return new(true, "deleted", CanonicalCommandHandlers.ItemResult(target.ItemId, target.RowVersion + 1), [target.ItemId]);
    }
}

public sealed class UpdateTodoCommandHandler : ICommandHandler<UpdateTodoArgumentsV1>
{
    public CommandHandlerResult Execute(AssistantCommandExecutionContext context, UpdateTodoArgumentsV1 arguments)
    {
        var target = CanonicalCommandHandlers.SingleTarget(context);
        var kind = CompleteTodoCommandHandler.ReadKind(context, target.ItemId);
        var changes = arguments.Changes!;
        var sets = new List<string> { "row_version=row_version+1", "updated_at=$now" };
        using var command = context.UnitOfWork.Connection.CreateCommand();
        command.Transaction = context.UnitOfWork.Transaction;
        command.Parameters.AddWithValue("$id", target.ItemId);
        command.Parameters.AddWithValue("$version", target.RowVersion);
        command.Parameters.AddWithValue("$now", context.NowUtc.ToString("O"));
        if (!string.IsNullOrWhiteSpace(changes.Title))
        {
            sets.Add("title=$title");
            command.Parameters.AddWithValue("$title", changes.Title.Trim());
        }
        if (changes.Notes is not null || changes.ClearFields?.Contains(UpdateTodoClearFieldV1.Notes) == true)
        {
            sets.Add("notes=$notes");
            command.Parameters.AddWithValue("$notes", changes.ClearFields?.Contains(UpdateTodoClearFieldV1.Notes) == true ? DBNull.Value : changes.Notes);
        }
        if (kind != "Event")
            AddTemporalChange(context, command, sets, "due", changes.Due,
                changes.ClearFields?.Contains(UpdateTodoClearFieldV1.Due) == true);
        AddTemporalChange(context, command, sets, "remind", changes.Remind,
            changes.ClearFields?.Contains(UpdateTodoClearFieldV1.Remind) == true);
        command.CommandText = $"UPDATE life_items SET {string.Join(',', sets)} WHERE id=$id AND row_version=$version AND kind IN ('Todo','Reminder','Event','LongTerm') AND deleted_at IS NULL AND is_readonly=0";
        CanonicalCommandHandlers.RequireAffected(command.ExecuteNonQuery());
        MirrorLegacy(context, target.ItemId, kind, changes);
        return new(true, "updated", CanonicalCommandHandlers.ItemResult(target.ItemId, target.RowVersion + 1), [target.ItemId]);
    }

    static void AddTemporalChange(
        AssistantCommandExecutionContext context,
        SqliteCommand command,
        List<string> sets,
        string prefix,
        AssistantTimeExpressionV1? expression,
        bool clear)
    {
        if (expression is null && !clear) return;
        var value = clear ? null : AssistantCommandTimeResolver.Resolve(context, expression!);
        sets.AddRange([
            $"{prefix}_local_datetime=${prefix}Local", $"{prefix}_utc_instant=${prefix}Utc",
            $"{prefix}_iana_time_zone_id=${prefix}Iana", $"{prefix}_windows_time_zone_id_cache=${prefix}Windows",
            $"{prefix}_time_semantics=${prefix}Semantics"]);
        AssistantCommandTimeResolver.Add(command, prefix, value);
    }

    static void MirrorLegacy(AssistantCommandExecutionContext context, string id, string kind, UpdateTodoChangesV1 changes)
    {
        if (kind == "LongTerm") return;
        var table = kind switch { "Todo" => "todos", "Reminder" => "single_reminders", "Event" => "calendar_events", _ => throw new InvalidOperationException("Unsupported item kind.") };
        var sets = new List<string> { "updated_at=$now" };
        using var command = context.UnitOfWork.Connection.CreateCommand();
        command.Transaction = context.UnitOfWork.Transaction;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$now", context.NowUtc.ToLocalTime().DateTime.ToString("O"));
        if (!string.IsNullOrWhiteSpace(changes.Title)) { sets.Add("title=$title"); command.Parameters.AddWithValue("$title", changes.Title.Trim()); }
        if (changes.Notes is not null || changes.ClearFields?.Contains(UpdateTodoClearFieldV1.Notes) == true)
        { sets.Add("notes=$notes"); command.Parameters.AddWithValue("$notes", changes.ClearFields?.Contains(UpdateTodoClearFieldV1.Notes) == true ? DBNull.Value : changes.Notes); }
        if (kind == "Todo" && (changes.Due is not null || changes.ClearFields?.Contains(UpdateTodoClearFieldV1.Due) == true))
        { sets.Add("due_at=$due"); command.Parameters.AddWithValue("$due", changes.Due is null ? DBNull.Value : AssistantCommandTimeResolver.Legacy(AssistantCommandTimeResolver.Resolve(context, changes.Due))); }
        if (changes.Remind is not null || changes.ClearFields?.Contains(UpdateTodoClearFieldV1.Remind) == true)
        { sets.Add("remind_at=$remind"); command.Parameters.AddWithValue("$remind", changes.Remind is null ? DBNull.Value : AssistantCommandTimeResolver.Legacy(AssistantCommandTimeResolver.Resolve(context, changes.Remind))); }
        command.CommandText = $"UPDATE {table} SET {string.Join(',', sets)} WHERE id=$id";
        command.ExecuteNonQuery();
    }
}

public sealed class RescheduleItemCommandHandler : ICommandHandler<RescheduleItemArgumentsV1>
{
    public CommandHandlerResult Execute(AssistantCommandExecutionContext context, RescheduleItemArgumentsV1 arguments)
    {
        var target = CanonicalCommandHandlers.SingleTarget(context);
        var next = AssistantCommandTimeResolver.Resolve(context, arguments.NewTime!);
        var row = ReadCurrent(context, target.ItemId);
        var sets = new List<string> { "row_version=row_version+1", "updated_at=$now" };
        using var command = context.UnitOfWork.Connection.CreateCommand();
        command.Transaction = context.UnitOfWork.Transaction;
        command.Parameters.AddWithValue("$id", target.ItemId);
        command.Parameters.AddWithValue("$version", target.RowVersion);
        command.Parameters.AddWithValue("$now", context.NowUtc.ToString("O"));
        ResolvedAssistantTime? nextEnd = null;
        var newReminder = arguments.NewReminder is null ? null : AssistantCommandTimeResolver.Resolve(context, arguments.NewReminder);
        if (row.Kind is "Todo" or "LongTerm") AddTime(command, sets, "due", next);
        else if (row.Kind == "Reminder") AddTime(command, sets, "remind", next);
        else if (row.Kind == "Event")
        {
            AddTime(command, sets, "start", next);
            var duration = row.EndUtc!.Value - row.StartUtc!.Value;
            var endLocal = next.LocalDateTime.Add(duration);
            nextEnd = AssistantCommandTimeResolver.Resolve(context,
                new AssistantTimeExpressionV1(DateOnly.FromDateTime(endLocal), TimeOnly.FromDateTime(endLocal), null, next.IanaTimeZoneId, null));
            AddTime(command, sets, "end", nextEnd);
        }
        else throw new InvalidOperationException("This item kind cannot be rescheduled.");
        if (newReminder is not null) AddTime(command, sets, "remind", newReminder);
        command.CommandText = $"UPDATE life_items SET {string.Join(',', sets)} WHERE id=$id AND row_version=$version AND kind IN ('Todo','Reminder','Event','LongTerm') AND deleted_at IS NULL AND is_readonly=0";
        CanonicalCommandHandlers.RequireAffected(command.ExecuteNonQuery());
        MirrorLegacy(context, target.ItemId, row.Kind, next, nextEnd, newReminder);
        return new(true, "rescheduled", CanonicalCommandHandlers.ItemResult(target.ItemId, target.RowVersion + 1), [target.ItemId]);
    }

    static (string Kind, DateTimeOffset? StartUtc, DateTimeOffset? EndUtc) ReadCurrent(AssistantCommandExecutionContext context, string id)
    {
        using var command = context.UnitOfWork.Connection.CreateCommand();
        command.Transaction = context.UnitOfWork.Transaction;
        command.CommandText = "SELECT COALESCE(item_type,kind),start_utc_instant,end_utc_instant FROM life_items WHERE id=$id AND deleted_at IS NULL";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new CanonicalConcurrencyException();
        return (reader.GetString(0), reader.IsDBNull(1) ? null : DateTimeOffset.Parse(reader.GetString(1)), reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2)));
    }

    static void AddTime(SqliteCommand command, List<string> sets, string prefix, ResolvedAssistantTime value)
    {
        sets.AddRange([$"{prefix}_local_datetime=${prefix}Local", $"{prefix}_utc_instant=${prefix}Utc",
            $"{prefix}_iana_time_zone_id=${prefix}Iana", $"{prefix}_windows_time_zone_id_cache=${prefix}Windows",
            $"{prefix}_time_semantics=${prefix}Semantics"]);
        AssistantCommandTimeResolver.Add(command, prefix, value);
    }

    static void MirrorLegacy(AssistantCommandExecutionContext context, string id, string kind,
        ResolvedAssistantTime next, ResolvedAssistantTime? nextEnd, ResolvedAssistantTime? newReminder)
    {
        if (kind == "LongTerm") return;
        using var command = context.UnitOfWork.Connection.CreateCommand();
        command.Transaction = context.UnitOfWork.Transaction;
        command.CommandText = kind switch
        {
            "Todo" => $"UPDATE todos SET due_at=$time{(newReminder is null ? "" : ",remind_at=$reminder")},updated_at=$now WHERE id=$id",
            "Reminder" => "UPDATE single_reminders SET remind_at=$time,updated_at=$now WHERE id=$id",
            "Event" => $"UPDATE calendar_events SET start_at=$time,end_at=$end{(newReminder is null ? "" : ",remind_at=$reminder")},updated_at=$now WHERE id=$id",
            _ => throw new InvalidOperationException("Unsupported item kind.")
        };
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$time", AssistantCommandTimeResolver.Legacy(next)!);
        if (nextEnd is not null) command.Parameters.AddWithValue("$end", AssistantCommandTimeResolver.Legacy(nextEnd)!);
        if (newReminder is not null) command.Parameters.AddWithValue("$reminder", AssistantCommandTimeResolver.Legacy(newReminder)!);
        command.Parameters.AddWithValue("$now", context.NowUtc.ToLocalTime().DateTime.ToString("O"));
        command.ExecuteNonQuery();
    }
}
