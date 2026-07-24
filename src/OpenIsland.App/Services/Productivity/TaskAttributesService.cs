using Microsoft.Data.Sqlite;
using OpenIsland.App.Services.Domain;
using OpenIsland.App.Services.Persistence;

namespace OpenIsland.App.Services.Productivity;

public sealed record TaskRecommendation(string ItemId, string Title, LifePriority Priority, int EstimatedMinutes, EnergyLevel? Energy, DateTimeOffset? DueAtUtc);
public sealed record TaskAttributes(string ItemId, long RowVersion, LifePriority Priority, string? Category, int? EstimatedMinutes, EnergyLevel? Energy,
    string? ParentItemId = null, DateTimeOffset? CompletedAtUtc = null, int DeferredCount = 0, int OverdueGraceMinutes = 5);
public enum TaskAttributesUpdateResult { Succeeded, NotFound, ReadOnly, ConcurrentConflict }

public sealed class TaskAttributesService
{
    readonly SqliteConnectionFactory connections;
    readonly IDbWriteQueue writeQueue;

    public TaskAttributesService(SqliteConnectionFactory connections, IDbWriteQueue writeQueue)
    {
        this.connections = connections ?? throw new ArgumentNullException(nameof(connections));
        this.writeQueue = writeQueue ?? throw new ArgumentNullException(nameof(writeQueue));
        new ProductivitySchemaInitializer(writeQueue).Initialize();
    }

    public TaskAttributes? Get(string itemId)
    {
        using var connection = connections.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT row_version,priority,category,estimated_minutes,energy,parent_item_id,completed_at_utc,deferred_count,COALESCE(overdue_grace_minutes,5) FROM life_items WHERE id=$id AND kind IN ('Todo','Reminder') AND deleted_at IS NULL";
        command.Parameters.AddWithValue("$id", itemId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new(itemId, reader.GetInt64(0), Enum.Parse<LifePriority>(reader.GetString(1)),
            reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt32(3),
            reader.IsDBNull(4) ? null : Enum.Parse<EnergyLevel>(reader.GetString(4)),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind),
            reader.GetInt32(7), reader.GetInt32(8));
    }

    public TaskAttributesUpdateResult Update(TaskAttributes attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        if (attributes.EstimatedMinutes is <= 0) throw new ArgumentOutOfRangeException(nameof(attributes));
        if (attributes.OverdueGraceMinutes < 0) throw new ArgumentOutOfRangeException(nameof(attributes));
        var category = string.IsNullOrWhiteSpace(attributes.Category) ? null : attributes.Category.Trim();
        return writeQueue.Execute(unitOfWork =>
        {
            using var state = unitOfWork.Connection.CreateCommand();
            state.Transaction = unitOfWork.Transaction;
            state.CommandText = "SELECT is_readonly FROM life_items WHERE id=$id AND kind IN ('Todo','Reminder') AND deleted_at IS NULL";
            state.Parameters.AddWithValue("$id", attributes.ItemId);
            var readOnly = state.ExecuteScalar();
            if (readOnly is null) return TaskAttributesUpdateResult.NotFound;
            if (Convert.ToInt64(readOnly) != 0) return TaskAttributesUpdateResult.ReadOnly;

            using var update = unitOfWork.Connection.CreateCommand();
            update.Transaction = unitOfWork.Transaction;
            update.CommandText = """
                UPDATE life_items SET priority=$priority,category=$category,estimated_minutes=$minutes,energy=$energy,overdue_grace_minutes=$grace,
                  row_version=row_version+1,updated_at=$updated
                WHERE id=$id AND row_version=$version AND kind IN ('Todo','Reminder') AND deleted_at IS NULL
                """;
            update.Parameters.AddWithValue("$priority", attributes.Priority.ToString());
            update.Parameters.AddWithValue("$category", (object?)category ?? DBNull.Value);
            update.Parameters.AddWithValue("$minutes", (object?)attributes.EstimatedMinutes ?? DBNull.Value);
            update.Parameters.AddWithValue("$energy", attributes.Energy is null ? DBNull.Value : attributes.Energy.ToString());
            update.Parameters.AddWithValue("$grace", attributes.OverdueGraceMinutes);
            update.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$id", attributes.ItemId);
            update.Parameters.AddWithValue("$version", attributes.RowVersion);
            return update.ExecuteNonQuery() == 1 ? TaskAttributesUpdateResult.Succeeded : TaskAttributesUpdateResult.ConcurrentConflict;
        });
    }
    public IReadOnlyList<TaskRecommendation> Recommend(int availableMinutes, EnergyLevel availableEnergy, DateTimeOffset nowUtc)
    {
        if (availableMinutes <= 0) throw new ArgumentOutOfRangeException(nameof(availableMinutes));
        using var connection = connections.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,title,priority,COALESCE(estimated_minutes,30),energy,due_utc_instant
            FROM life_items
            WHERE kind='Todo' AND deleted_at IS NULL AND is_readonly=0
              AND status NOT IN ('Completed','Cancelled','Ignored')
              AND COALESCE(estimated_minutes,30) <= $minutes
            """;
        command.Parameters.AddWithValue("$minutes", availableMinutes);
        using var reader = command.ExecuteReader();
        var all = new List<TaskRecommendation>();
        while (reader.Read())
        {
            EnergyLevel? energy = reader.IsDBNull(4) ? null : Enum.Parse<EnergyLevel>(reader.GetString(4));
            if (energy is not null && energy > availableEnergy) continue;
            all.Add(new(reader.GetString(0), reader.GetString(1), Enum.Parse<LifePriority>(reader.GetString(2)), reader.GetInt32(3), energy,
                reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        }
        var byId = all.ToDictionary(item => item.ItemId, StringComparer.Ordinal);
        return LocalTaskRanker.Rank(all.Select(item => new TaskRankCandidate(item.ItemId, item.DueAtUtc, item.Priority.ToString(), item.EstimatedMinutes)), nowUtc)
            .Select(candidate => byId[candidate.Id]).Take(3).ToArray();
    }

}
