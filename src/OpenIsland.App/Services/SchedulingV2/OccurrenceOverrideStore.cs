using System.Globalization;
using Microsoft.Data.Sqlite;
using OpenIsland.App.Services.Persistence;

namespace OpenIsland.App.Services.Scheduling;

public enum OccurrenceOverrideType { Snooze, Reschedule, Skip }

public sealed record OccurrenceOverrideKey(string SeriesItemId, int RuleVersion, DateTime OriginalStartLocal, DateTimeOffset OriginalStartUtc, string IanaTimeZoneId);
public sealed record OccurrenceOverride(OccurrenceOverrideKey Key, OccurrenceOverrideType Type, DateTimeOffset? NewRemindAtUtc, DateTimeOffset? NewStartAtUtc, DateTimeOffset CreatedAtUtc);

public sealed class OccurrenceOverrideStore
{
    readonly IDbWriteQueue queue;
    public OccurrenceOverrideStore(IDbWriteQueue queue)
    {
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        queue.Execute(unitOfWork =>
        {
            using var command = Command(unitOfWork, """
                CREATE TABLE IF NOT EXISTS occurrence_overrides(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    series_item_id TEXT NOT NULL,
                    rule_version INTEGER NOT NULL CHECK(rule_version >= 1),
                    original_start_local_datetime TEXT NOT NULL,
                    original_start_utc TEXT NOT NULL,
                    iana_time_zone_id TEXT NOT NULL,
                    override_type TEXT NOT NULL,
                    new_remind_at_utc TEXT,
                    new_start_at_utc TEXT,
                    created_at_utc TEXT NOT NULL,
                    deleted_at_utc TEXT);
                CREATE UNIQUE INDEX IF NOT EXISTS ux_occurrence_override_active_key
                    ON occurrence_overrides(series_item_id,rule_version,original_start_local_datetime,original_start_utc,iana_time_zone_id)
                    WHERE deleted_at_utc IS NULL;
                """);
            command.ExecuteNonQuery();
        });
    }

    public void Save(OccurrenceOverride value)
    {
        if (value.Key.RuleVersion < 1) throw new ArgumentOutOfRangeException(nameof(value));
        ArgumentException.ThrowIfNullOrWhiteSpace(value.Key.SeriesItemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(value.Key.IanaTimeZoneId);
        queue.Execute(u =>
        {
            using var command = Command(u, """
                INSERT INTO occurrence_overrides(series_item_id,rule_version,original_start_local_datetime,original_start_utc,
                  iana_time_zone_id,override_type,new_remind_at_utc,new_start_at_utc,created_at_utc,deleted_at_utc)
                VALUES($series,$version,$local,$utc,$zone,$type,$remind,$start,$created,NULL)
                ON CONFLICT(series_item_id,rule_version,original_start_local_datetime,original_start_utc,iana_time_zone_id)
                  WHERE deleted_at_utc IS NULL
                DO UPDATE SET override_type=excluded.override_type,new_remind_at_utc=excluded.new_remind_at_utc,
                  new_start_at_utc=excluded.new_start_at_utc,created_at_utc=excluded.created_at_utc
                """);
            command.Parameters.AddWithValue("$series", value.Key.SeriesItemId);
            command.Parameters.AddWithValue("$version", value.Key.RuleVersion);
            command.Parameters.AddWithValue("$local", value.Key.OriginalStartLocal.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$utc", Utc(value.Key.OriginalStartUtc));
            command.Parameters.AddWithValue("$zone", value.Key.IanaTimeZoneId);
            command.Parameters.AddWithValue("$type", value.Type.ToString());
            command.Parameters.AddWithValue("$remind", Db(value.NewRemindAtUtc));
            command.Parameters.AddWithValue("$start", Db(value.NewStartAtUtc));
            command.Parameters.AddWithValue("$created", Utc(value.CreatedAtUtc));
            command.ExecuteNonQuery();
        });
    }

    static SqliteCommand Command(IUnitOfWork unitOfWork, string sql)
    { var command = unitOfWork.Connection.CreateCommand(); command.Transaction = unitOfWork.Transaction; command.CommandText = sql; return command; }
    static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    static object Db(DateTimeOffset? value) => value is null ? DBNull.Value : Utc(value.Value);
}