using System.Collections.Concurrent;
using System.IO;

namespace OpenIsland.App.Services.Persistence;

internal sealed record LifeDataStoreRuntime(
    SqliteConnectionFactory ConnectionFactory,
    IDbWriteQueue WriteQueue);

internal static class LifeDataStoreRuntimeRegistry
{
    static readonly ConcurrentDictionary<string, Lazy<LifeDataStoreRuntime>> Runtimes =
        new(StringComparer.OrdinalIgnoreCase);

    public static LifeDataStoreRuntime GetOrCreate(string databasePath)
    {
        var normalizedPath = Path.GetFullPath(databasePath);
        var lazy = Runtimes.GetOrAdd(normalizedPath, static path => new Lazy<LifeDataStoreRuntime>(() =>
        {
            var factory = new SqliteConnectionFactory(path);
            return new LifeDataStoreRuntime(factory, new SqliteDbWriteQueue(factory));
        }, LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return lazy.Value;
        }
        catch
        {
            if (Runtimes.TryGetValue(normalizedPath, out var current) && ReferenceEquals(current, lazy))
                Runtimes.TryRemove(normalizedPath, out _);
            throw;
        }
    }
}
