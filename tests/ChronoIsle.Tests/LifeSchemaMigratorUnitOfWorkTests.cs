using Microsoft.Data.Sqlite;
using ChronoIsle.App.Services.Persistence;

namespace ChronoIsle.Tests;

public sealed class LifeSchemaMigratorUnitOfWorkTests
{
    [Fact]
    public void ExistingTransactionOverload_DoesNotTakeOwnershipOfCommit()
    {
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        using var transaction = db.BeginTransaction();

        new LifeSchemaMigrator("Etc/UTC").Migrate(db, transaction);
        Assert.True(TableExists(db, transaction, "life_items"));

        transaction.Rollback();
        Assert.False(TableExists(db, null, "life_items"));
    }

    static bool TableExists(SqliteConnection db, SqliteTransaction? transaction, string name)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name";
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }
}
