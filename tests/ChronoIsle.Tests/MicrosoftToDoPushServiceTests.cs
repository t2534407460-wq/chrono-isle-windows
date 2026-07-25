using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Data.Sqlite;
using ChronoIsle.App.Services.Persistence;
using ChronoIsle.App.Services.Productivity;
using ChronoIsle.App.Services.Sync;

namespace ChronoIsle.Tests;

public sealed class MicrosoftToDoPushServiceTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), "chrono-isle-todo-push-" + Guid.NewGuid().ToString("N") + ".db");

    [Fact]
    public async Task One_way_push_creates_default_list_task_and_idempotent_local_mapping()
    {
        var factory = CreateDatabase();
        var queue = new SqliteDbWriteQueue(factory);
        var store = new GraphSyncStore(factory, queue);
        var handler = new GraphHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.example/v1.0/") };
        var service = new MicrosoftToDoPushService(factory, store, new TokenAuthentication(), http);

        var result = await service.PushAllAsync("account");

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Created);
        Assert.Equal(0, result.Updated);
        Assert.NotNull(store.FindActiveMapping("account", SyncProvider.MicrosoftToDo, "todo"));
        Assert.Equal(["GET v1.0/me/todo/lists", "POST v1.0/me/todo/lists", "POST v1.0/me/todo/lists/list/tasks"], handler.Requests);
        Assert.All(handler.Authorization, value => Assert.Equal("Bearer token", value));
    }

    SqliteConnectionFactory CreateDatabase()
    {
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            new LifeSchemaMigrator("Asia/Shanghai").Migrate(connection);
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO life_items(id,kind,title,status,row_version,origin_type,is_readonly,created_at,updated_at) VALUES('todo','Todo','Push me','Pending',1,'Local',0,$now,$now)";
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        new ProductivitySchemaInitializer(new SqliteDbWriteQueue(new SqliteConnectionFactory(path))).Initialize();
        return new SqliteConnectionFactory(path);
    }

    sealed class TokenAuthentication : IMicrosoftGraphAuthenticationService
    {
        public Task<string?> AcquireAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>("token");
        public Task<MicrosoftGraphAuthenticationResult> SignInAsync(MicrosoftGraphSignInOptions options, IntPtr parentWindowHandle = default, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SignOutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    sealed class GraphHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public List<string?> Authorization { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.PathAndQuery.TrimStart('/')}");
            Authorization.Add(request.Headers.Authorization?.ToString());
            var body = request.Method == HttpMethod.Get ? "{\"value\":[]}" : request.RequestUri!.AbsolutePath.EndsWith("/lists") ? "{\"id\":\"list\"}" : "{\"id\":\"task\"}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(file)) File.Delete(file);
    }
}
