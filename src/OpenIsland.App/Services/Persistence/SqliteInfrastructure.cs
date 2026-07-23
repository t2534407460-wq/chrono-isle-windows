using Microsoft.Data.Sqlite;

namespace OpenIsland.App.Services.Persistence;

public interface IUnitOfWork : IDisposable
{
    SqliteConnection Connection { get; }
    SqliteTransaction Transaction { get; }
    bool IsCommitted { get; }
    void Commit();
}

public interface IDbWriteQueue
{
    T Execute<T>(Func<IUnitOfWork, T> operation, CancellationToken cancellationToken = default);
    void Execute(Action<IUnitOfWork> operation, CancellationToken cancellationToken = default);
    Task<T> ExecuteAsync<T>(Func<IUnitOfWork, CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default);
    Task ExecuteAsync(Func<IUnitOfWork, CancellationToken, Task> operation, CancellationToken cancellationToken = default);
}

public sealed class SqliteConnectionFactory
{
    readonly string connectionString;
    readonly int busyTimeoutMilliseconds;

    public SqliteConnectionFactory(string databasePath, int busyTimeoutMilliseconds = 5000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (busyTimeoutMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(busyTimeoutMilliseconds));

        this.busyTimeoutMilliseconds = busyTimeoutMilliseconds;
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = Math.Max(1, (busyTimeoutMilliseconds + 999) / 1000)
        }.ToString();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL";
        var journalMode = Convert.ToString(command.ExecuteScalar());
        if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"SQLite WAL initialization failed. Actual journal mode: {journalMode ?? "<null>"}.");
    }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        try
        {
            ConfigureConnection(connection, busyTimeoutMilliseconds);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public IUnitOfWork BeginUnitOfWork() => new SqliteUnitOfWork(OpenConnection());

    public static void ConfigureConnection(SqliteConnection connection, int busyTimeoutMilliseconds = 5000)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.State != System.Data.ConnectionState.Open)
            throw new InvalidOperationException("SQLite connection must be open before it is configured.");
        if (busyTimeoutMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(busyTimeoutMilliseconds));

        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_keys=ON; PRAGMA busy_timeout={busyTimeoutMilliseconds};";
        command.ExecuteNonQuery();
    }
}

public sealed class SqliteUnitOfWork : IUnitOfWork
{
    bool disposed;

    public SqliteUnitOfWork(SqliteConnection connection)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        Transaction = connection.BeginTransaction();
    }

    public SqliteConnection Connection { get; }
    public SqliteTransaction Transaction { get; }
    public bool IsCommitted { get; private set; }

    public void Commit()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (IsCommitted) return;
        Transaction.Commit();
        IsCommitted = true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Transaction.Dispose();
        Connection.Dispose();
    }
}

public sealed class SqliteDbWriteQueue : IDbWriteQueue
{
    readonly SqliteConnectionFactory connectionFactory;
    readonly SemaphoreSlim gate = new(1, 1);
    readonly AsyncLocal<bool> isExecuting = new();

    public SqliteDbWriteQueue(SqliteConnectionFactory connectionFactory)
    {
        this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public T Execute<T>(Func<IUnitOfWork, T> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ThrowIfNestedWrite();
        gate.Wait(cancellationToken);
        try
        {
            isExecuting.Value = true;
            using var unitOfWork = connectionFactory.BeginUnitOfWork();
            var result = operation(unitOfWork);
            unitOfWork.Commit();
            return result;
        }
        finally
        {
            isExecuting.Value = false;
            gate.Release();
        }
    }

    public void Execute(Action<IUnitOfWork> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Execute(unitOfWork =>
        {
            operation(unitOfWork);
            return true;
        }, cancellationToken);
    }

    public async Task<T> ExecuteAsync<T>(
        Func<IUnitOfWork, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ThrowIfNestedWrite();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            isExecuting.Value = true;
            using var unitOfWork = connectionFactory.BeginUnitOfWork();
            var result = await operation(unitOfWork, cancellationToken).ConfigureAwait(false);
            unitOfWork.Commit();
            return result;
        }
        finally
        {
            isExecuting.Value = false;
            gate.Release();
        }
    }

    public Task ExecuteAsync(
        Func<IUnitOfWork, CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return ExecuteAsync(async (unitOfWork, token) =>
        {
            await operation(unitOfWork, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    void ThrowIfNestedWrite()
    {
        if (isExecuting.Value)
            throw new InvalidOperationException("A database write cannot enqueue another write on the same queue.");
    }
}
