using System.Text.Json;
using Microsoft.Data.Sqlite;
using ChronoIsle.App.Services.Persistence;

namespace ChronoIsle.App.Services.Sync;

public enum LocalBackupKind { MigrationSafety, UserPortable }

public sealed record PortableBackupManifest(
    int FormatVersion,
    DateTimeOffset CreatedAtUtc,
    LocalBackupKind Kind,
    bool ContainsAccountTokens,
    bool ContainsDeltaLinks,
    bool ContainsMsalCache,
    bool RequiresExternalAccountReauthentication);

public sealed class SqliteOnlineBackupService
{
    readonly SqliteConnectionFactory connectionFactory;
    readonly IDbWriteQueue writeQueue;
    readonly Func<DateTimeOffset> utcNow;

    public SqliteOnlineBackupService(SqliteConnectionFactory connectionFactory, IDbWriteQueue writeQueue, Func<DateTimeOffset>? utcNow = null)
    {
        this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        this.writeQueue = writeQueue ?? throw new ArgumentNullException(nameof(writeQueue));
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public PortableBackupManifest Create(string destinationPath, LocalBackupKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var target = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
        var portable = kind == LocalBackupKind.UserPortable;
        var manifest = new PortableBackupManifest(1, utcNow(), kind, !portable, !portable, !portable, portable);
        try
        {
            using (var source = connectionFactory.OpenConnection())
            using (var destination = Open(temporary))
                source.BackupDatabase(destination);
            if (portable) Sanitize(temporary);
            Validate(temporary);
            File.Move(temporary, target, true);
            File.WriteAllText(target + ".manifest.json", JsonSerializer.Serialize(manifest));
            return manifest;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void RestorePortableBusinessData(string backupPath)
    {
        var source = Path.GetFullPath(backupPath);
        Validate(source);
        writeQueue.Execute(uow =>
        {
            using var attach = uow.Connection.CreateCommand();
            attach.Transaction = uow.Transaction;
            attach.CommandText = "ATTACH DATABASE $path AS portable";
            attach.Parameters.AddWithValue("$path", source);
            attach.ExecuteNonQuery();
            foreach (var table in new[] { "life_items", "recurrence_rules", "raw_external_payloads" })
                CopyTable(uow.Connection, uow.Transaction, table);
            MarkReauthentication(uow.Connection, uow.Transaction);
        });
    }

    static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    static void Validate(string path)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        if (!string.Equals(Convert.ToString(command.ExecuteScalar()), "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SQLite backup failed integrity_check.");
        if (!TableExists(connection, null, "main", "life_items"))
            throw new InvalidOperationException("SQLite backup does not contain life_items.");
    }

    static void Sanitize(string path)
    {
        using var connection = Open(path);
        using var transaction = connection.BeginTransaction();
        foreach (var table in TableNames(connection, transaction))
        {
            var lower = table.ToLowerInvariant();
            if (lower.Contains("token") || lower.Contains("credential") || lower.Contains("auth") ||
                lower.Contains("msal") || lower.Contains("sync_cursor") || lower.Contains("delta"))
            {
                Execute(connection, transaction, $"DELETE FROM {Quote(table)}");
                continue;
            }
            var sensitive = Columns(connection, transaction, table).Where(IsSensitive).ToArray();
            if (sensitive.Length == 0) continue;
            try
            {
                Execute(connection, transaction, $"UPDATE {Quote(table)} SET " +
                    string.Join(",", sensitive.Select(column => $"{Quote(column)}=NULL")));
            }
            catch (SqliteException)
            {
                Execute(connection, transaction, $"DELETE FROM {Quote(table)}");
            }
        }
        MarkReauthentication(connection, transaction);
        transaction.Commit();
    }

    static bool IsSensitive(string name)
    {
        var value = name.ToLowerInvariant();
        return value.Contains("token") || value.Contains("secret") || value.Contains("credential") ||
               value.Contains("delta_link") || value.Contains("dpapi") || value.Contains("msal_cache");
    }

    static void MarkReauthentication(SqliteConnection connection, SqliteTransaction transaction)
    {
        foreach (var table in TableNames(connection, transaction).Where(name => name.Contains("account", StringComparison.OrdinalIgnoreCase)))
        {
            var status = Columns(connection, transaction, table)
                .FirstOrDefault(name => name.Equals("status", StringComparison.OrdinalIgnoreCase));
            if (status is not null)
                Execute(connection, transaction, $"UPDATE {Quote(table)} SET {Quote(status)}='ReauthRequired'");
        }
    }

    static void CopyTable(SqliteConnection connection, SqliteTransaction transaction, string table)
    {
        if (!TableExists(connection, transaction, "portable", table) || !TableExists(connection, transaction, "main", table)) return;
        var source = Columns(connection, transaction, table, "portable");
        var target = Columns(connection, transaction, table);
        var columns = source.Intersect(target, StringComparer.OrdinalIgnoreCase).ToArray();
        if (columns.Length == 0) return;
        var list = string.Join(",", columns.Select(Quote));
        Execute(connection, transaction,
            $"INSERT OR REPLACE INTO main.{Quote(table)} ({list}) SELECT {list} FROM portable.{Quote(table)}");
    }

    static IReadOnlyList<string> TableNames(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    static IReadOnlyList<string> Columns(SqliteConnection connection, SqliteTransaction transaction, string table, string schema = "main")
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA {Quote(schema)}.table_info({Quote(table)})";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(1));
        return names;
    }

    static bool TableExists(SqliteConnection connection, SqliteTransaction? transaction, string schema, string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT EXISTS(SELECT 1 FROM {Quote(schema)}.sqlite_master WHERE type='table' AND name=$name)";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(command.ExecuteScalar()) == 1;
    }

    static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    static string Quote(string value) => '"' + value.Replace("\"", "\"\"") + '"';
}
