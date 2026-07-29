namespace ChronoIsle.App.Services;

public sealed partial class LifeDataService
{
    bool ArchiveLongTermItem(
        Services.Persistence.IUnitOfWork unitOfWork,
        string id,
        string reason,
        DateTime archivedAt)
    {
        using var read = unitOfWork.Connection.CreateCommand();
        read.Transaction = unitOfWork.Transaction;
        read.CommandText = """
            SELECT title,notes,due_local_datetime,remind_local_datetime
            FROM life_items
            WHERE id=$id AND item_type='LongTerm' AND deleted_at IS NULL
            """;
        read.Parameters.AddWithValue("$id", id);
        using var reader = read.ExecuteReader();
        if (!reader.Read()) return false;
        var title = reader.GetString(0);
        var notes = Text(reader, 1);
        var due = LongTermDate(reader, 2);
        var remind = LongTermDate(reader, 3);
        reader.Close();

        SaveArchivedItem(
            unitOfWork,
            id,
            "long_term",
            title,
            notes,
            due,
            remind,
            archivedAt,
            reason,
            null);
        canonicalWriter.SoftDelete(unitOfWork.Connection, unitOfWork.Transaction, id, archivedAt);
        return true;
    }

    bool RestoreLongTermItem(
        Services.Persistence.IUnitOfWork unitOfWork,
        string id,
        DateTime restoredAt)
    {
        using var command = unitOfWork.Connection.CreateCommand();
        command.Transaction = unitOfWork.Transaction;
        command.CommandText = """
            UPDATE life_items
            SET status='Pending',completed_at_utc=NULL,deleted_at=NULL,
                row_version=row_version+1,updated_at=$updated
            WHERE id=$id AND item_type='LongTerm' AND deleted_at IS NOT NULL
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$updated", new DateTimeOffset(restoredAt).ToUniversalTime().ToString("O"));
        return command.ExecuteNonQuery() == 1;
    }
}
