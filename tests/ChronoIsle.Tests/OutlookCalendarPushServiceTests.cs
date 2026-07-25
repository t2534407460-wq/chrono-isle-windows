using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Data.Sqlite;
using ChronoIsle.App.Services.Persistence;
using ChronoIsle.App.Services.Sync;

namespace ChronoIsle.Tests;

public sealed class OutlookCalendarPushServiceTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), "chrono-isle-calendar-push-" + Guid.NewGuid().ToString("N") + ".db");

    [Fact]
    public async Task One_way_push_sends_lossless_zoned_event_and_persists_mapping()
    {
        var factory = CreateDatabase();
        var queue = new SqliteDbWriteQueue(factory);
        var store = new GraphSyncStore(factory, queue);
        var handler = new CalendarHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.example/v1.0/") };
        var service = new OutlookCalendarPushService(factory, store, new TokenAuthentication(), http);

        var result = await service.PushAllAsync("account", "calendar-id");

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Created);
        Assert.Equal(["POST v1.0/me/calendars/calendar-id/events"], handler.Requests);
        Assert.Contains("China Standard Time", handler.Payloads.Single(), StringComparison.Ordinal);
        Assert.NotNull(store.FindActiveMapping("account", SyncProvider.OutlookCalendar, "event"));
    }

    SqliteConnectionFactory CreateDatabase()
    {
        var start = new DateTimeOffset(2026, 7, 22, 9, 0, 0, TimeSpan.FromHours(8));
        var end = start.AddHours(1);
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            new LifeSchemaMigrator("Asia/Shanghai").Migrate(connection);
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO life_items(id,kind,title,status,row_version,
                  start_local_datetime,start_utc_instant,start_iana_time_zone_id,start_windows_time_zone_id_cache,start_time_semantics,
                  end_local_datetime,end_utc_instant,end_iana_time_zone_id,end_windows_time_zone_id_cache,end_time_semantics,
                  origin_type,is_readonly,created_at,updated_at)
                VALUES('event','Event','Planning','Pending',1,
                  $startLocal,$startUtc,'Asia/Shanghai','China Standard Time','ZonedWallClock',
                  $endLocal,$endUtc,'Asia/Shanghai','China Standard Time','ZonedWallClock',
                  'Local',0,$now,$now)
                """;
            command.Parameters.AddWithValue("$startLocal", start.DateTime.ToString("s"));
            command.Parameters.AddWithValue("$startUtc", start.UtcDateTime.ToString("O"));
            command.Parameters.AddWithValue("$endLocal", end.DateTime.ToString("s"));
            command.Parameters.AddWithValue("$endUtc", end.UtcDateTime.ToString("O"));
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        return new SqliteConnectionFactory(path);
    }

    sealed class TokenAuthentication : IMicrosoftGraphAuthenticationService
    {
        public Task<string?> AcquireAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>("token");
        public Task<MicrosoftGraphAuthenticationResult> SignInAsync(MicrosoftGraphSignInOptions options, IntPtr parentWindowHandle = default, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SignOutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    sealed class CalendarHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public List<string> Payloads { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.PathAndQuery.TrimStart('/')}");
            Payloads.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"remote-event\"}", Encoding.UTF8, "application/json") };
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(file)) File.Delete(file);
    }
}
