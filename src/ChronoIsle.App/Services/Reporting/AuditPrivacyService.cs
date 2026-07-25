using System.Globalization;
using Microsoft.Data.Sqlite;
using ChronoIsle.App.Services.Persistence;

namespace ChronoIsle.App.Services.Reporting;

public sealed record RecentAuditEntry(DateTimeOffset CreatedAtUtc, string Command, string Status);

public sealed class AuditPrivacyService
{
    public static readonly TimeSpan DefaultDetailedRetention = TimeSpan.FromDays(30);
    readonly IDbWriteQueue writeQueue;

    public AuditPrivacyService(IDbWriteQueue writeQueue)
    {
        this.writeQueue = writeQueue ?? throw new ArgumentNullException(nameof(writeQueue));
        ReportSchema.EnsureCreated(writeQueue);
    }

    public int SanitizeExpired(DateTimeOffset nowUtc, TimeSpan? retention = null)
    {
        var keep = retention ?? DefaultDetailedRetention;
        if (keep < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retention));
        return SanitizeBefore(nowUtc.ToUniversalTime().Subtract(keep), nowUtc);
    }

    public int SanitizeAllDetailed(DateTimeOffset nowUtc) => SanitizeBefore(DateTimeOffset.MaxValue, nowUtc);

    public int DeleteAll() => writeQueue.Execute(unitOfWork =>
    {
        using var command = Command(unitOfWork, "DELETE FROM action_events");
        return command.ExecuteNonQuery();
    });

    public IReadOnlyList<RecentAuditEntry> GetRecent(int maximum = 10)
    {
        if (maximum is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(maximum));
        return writeQueue.Execute(unitOfWork =>
        {
            using var command = Command(unitOfWork, """
                SELECT COALESCE(created_at_utc,created_at),
                       COALESCE(command_name,event_type,'操作'),
                       COALESCE(status,'Recorded')
                FROM action_events
                ORDER BY COALESCE(created_at_utc,created_at) DESC,action_event_id DESC
                LIMIT $maximum
                """);
            command.Parameters.AddWithValue("$maximum", maximum);
            using var reader = command.ExecuteReader();
            var entries = new List<RecentAuditEntry>();
            while (reader.Read())
            {
                var when = DateTimeOffset.TryParse(reader.GetString(0), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var parsed) ? parsed : DateTimeOffset.MinValue;
                entries.Add(new RecentAuditEntry(when.ToUniversalTime(), reader.GetString(1), reader.GetString(2)));
            }
            return (IReadOnlyList<RecentAuditEntry>)entries;
        });
    }

    int SanitizeBefore(DateTimeOffset cutoff, DateTimeOffset sanitizedAt) => writeQueue.Execute(unitOfWork =>
    {
        using var command = Command(unitOfWork, """
            UPDATE action_events
            SET source_text=NULL,raw_parsed_json=NULL,error_text=NULL,command_json=NULL,
                result_json=NULL,error_message=NULL,sanitized_at_utc=$sanitized,redacted_at=$sanitized
            WHERE COALESCE(created_at_utc,created_at) < $cutoff
              AND (source_text IS NOT NULL OR raw_parsed_json IS NOT NULL OR error_text IS NOT NULL
                   OR command_json IS NOT NULL OR result_json IS NOT NULL OR error_message IS NOT NULL)
            """);
        command.Parameters.AddWithValue("$sanitized", Format(sanitizedAt));
        command.Parameters.AddWithValue("$cutoff", cutoff == DateTimeOffset.MaxValue
            ? "9999-12-31T23:59:59.9999999+00:00" : Format(cutoff));
        return command.ExecuteNonQuery();
    });

    static SqliteCommand Command(IUnitOfWork unitOfWork, string sql)
    { var command = unitOfWork.Connection.CreateCommand(); command.Transaction = unitOfWork.Transaction; command.CommandText = sql; return command; }
    static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
