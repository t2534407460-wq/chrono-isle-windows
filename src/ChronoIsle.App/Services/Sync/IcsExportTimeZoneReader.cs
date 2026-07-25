using Microsoft.Data.Sqlite;

namespace ChronoIsle.App.Services.Sync;

internal static class IcsExportTimeZoneReader
{
    public static IReadOnlyList<string> Read(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT zone FROM (
              SELECT due_iana_time_zone_id AS zone FROM life_items WHERE deleted_at IS NULL AND is_readonly=0
              UNION SELECT remind_iana_time_zone_id FROM life_items WHERE deleted_at IS NULL AND is_readonly=0
              UNION SELECT start_iana_time_zone_id FROM life_items WHERE deleted_at IS NULL AND is_readonly=0
              UNION SELECT end_iana_time_zone_id FROM life_items WHERE deleted_at IS NULL AND is_readonly=0
            ) WHERE zone IS NOT NULL AND trim(zone) <> '' ORDER BY zone
            """;
        using var reader = command.ExecuteReader();
        var zones = new List<string>();
        while (reader.Read()) zones.Add(reader.GetString(0));
        return zones;
    }
}
