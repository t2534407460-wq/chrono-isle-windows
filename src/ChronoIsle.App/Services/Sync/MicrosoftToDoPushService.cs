using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ChronoIsle.App.Services.Persistence;

namespace ChronoIsle.App.Services.Sync;

public sealed record MicrosoftToDoPushSummary(int Created, int Updated, int Skipped, string? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>Opt-in, one-way local Todo push. Reads are concurrent; HTTP happens outside the database write queue.</summary>
public sealed class MicrosoftToDoPushService
{
    readonly SqliteConnectionFactory connections;
    readonly GraphSyncStore syncStore;
    readonly IMicrosoftGraphAuthenticationService authentication;
    readonly HttpClient http;

    public MicrosoftToDoPushService(SqliteConnectionFactory connections, GraphSyncStore syncStore,
        IMicrosoftGraphAuthenticationService authentication, HttpClient? http = null)
    {
        this.connections = connections ?? throw new ArgumentNullException(nameof(connections));
        this.syncStore = syncStore ?? throw new ArgumentNullException(nameof(syncStore));
        this.authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        this.http = http ?? new HttpClient { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
    }

    public async Task<MicrosoftToDoPushSummary> PushAllAsync(string accountId, string taskListName = "ChronoIsle",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return new(0, 0, 0, "请先完成 Microsoft 登录。");
        var token = await authentication.AcquireAccessTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            syncStore.UpsertAccount(accountId, SyncProvider.MicrosoftToDo, SyncAccountStatus.ReauthRequired);
            return new(0, 0, 0, "登录已过期，请先重新登录 Microsoft。 ");
        }

        try
        {
            var listId = await EnsureTaskListAsync(token, taskListName, cancellationToken);
            var created = 0; var updated = 0; var skipped = 0;
            foreach (var item in ReadPushableTodos())
            {
                var mapping = syncStore.FindActiveMapping(accountId, SyncProvider.MicrosoftToDo, item.Id);
                if (mapping?.IsReadOnly == true) { skipped++; continue; }
                var payload = BuildTaskPayload(item);
                if (mapping is null)
                {
                    var remoteId = await CreateTaskAsync(token, listId, payload, cancellationToken);
                    syncStore.AddMapping(Guid.NewGuid().ToString("N"), accountId, SyncProvider.MicrosoftToDo, item.Id,
                        remoteId, ExternalMappingResult.Editable());
                    created++;
                }
                else
                {
                    await UpdateTaskAsync(token, listId, mapping.RemoteResourceId, payload, cancellationToken);
                    updated++;
                }
            }
            syncStore.UpsertAccount(accountId, SyncProvider.MicrosoftToDo, SyncAccountStatus.Connected);
            return new(created, updated, skipped, null);
        }
        catch (HttpRequestException exception)
        {
            return new(0, 0, 0, $"Microsoft To Do 推送失败：{exception.Message}");
        }
        catch (JsonException exception)
        {
            return new(0, 0, 0, $"Microsoft To Do 返回无法识别的数据：{exception.Message}");
        }
    }

    async Task<string> EnsureTaskListAsync(string token, string name, CancellationToken cancellationToken)
    {
        using var listResponse = await SendAsync(HttpMethod.Get, "me/todo/lists", token, null, cancellationToken);
        using var document = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync(cancellationToken));
        if (document.RootElement.TryGetProperty("value", out var values))
        {
            foreach (var list in values.EnumerateArray())
                if (list.TryGetProperty("displayName", out var display) && string.Equals(display.GetString(), name, StringComparison.Ordinal) &&
                    list.TryGetProperty("id", out var id)) return id.GetString()!;
        }
        using var create = await SendAsync(HttpMethod.Post, "me/todo/lists", token, new { displayName = name }, cancellationToken);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync(cancellationToken));
        return created.RootElement.GetProperty("id").GetString() ?? throw new JsonException("Task list response has no id.");
    }

    async Task<string> CreateTaskAsync(string token, string listId, object payload, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, $"me/todo/lists/{Uri.EscapeDataString(listId)}/tasks", token, payload, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetProperty("id").GetString() ?? throw new JsonException("Task response has no id.");
    }

    async Task UpdateTaskAsync(string token, string listId, string remoteId, object payload, CancellationToken cancellationToken)
    {
        using var _ = await SendAsync(HttpMethod.Patch,
            $"me/todo/lists/{Uri.EscapeDataString(listId)}/tasks/{Uri.EscapeDataString(remoteId)}", token, payload, cancellationToken);
    }

    async Task<HttpResponseMessage> SendAsync(HttpMethod method, string relativeUri, string token, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, relativeUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            response.Dispose();
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {responseText}");
        }
        return response;
    }

    IReadOnlyList<PushableTodo> ReadPushableTodos()
    {
        using var connection = connections.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,title,notes,status,priority,due_local_datetime,due_utc_instant,due_iana_time_zone_id,
                   due_windows_time_zone_id_cache,due_time_semantics
            FROM life_items
            WHERE deleted_at IS NULL AND is_readonly=0 AND kind='Todo' AND origin_type='Local'
            ORDER BY updated_at,id
            """;
        using var reader = command.ExecuteReader();
        var result = new List<PushableTodo>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), Read(reader, 2), reader.GetString(3), reader.GetString(4),
            Read(reader, 5), Read(reader, 6), Read(reader, 7), Read(reader, 8), Read(reader, 9)));
        return result;
    }

    static object BuildTaskPayload(PushableTodo item)
    {
        var payload = new Dictionary<string, object?>
        {
            ["title"] = item.Title,
            ["status"] = string.Equals(item.Status, "Completed", StringComparison.OrdinalIgnoreCase) ? "completed" : "notStarted",
            ["importance"] = item.Priority is "Urgent" or "High" ? "high" : item.Priority == "Low" ? "low" : "normal"
        };
        if (!string.IsNullOrWhiteSpace(item.Notes)) payload["body"] = new { contentType = "text", content = item.Notes };
        if (item.DueSemantics == "ZonedWallClock" && item.DueLocal is not null)
            payload["dueDateTime"] = new { dateTime = DateTime.Parse(item.DueLocal, CultureInfo.InvariantCulture).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
                timeZone = item.DueWindowsZone ?? item.DueIanaZone };
        else if (item.DueUtc is not null)
            payload["dueDateTime"] = new { dateTime = DateTimeOffset.Parse(item.DueUtc, CultureInfo.InvariantCulture).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture), timeZone = "UTC" };
        return payload;
    }

    static string? Read(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    sealed record PushableTodo(string Id, string Title, string? Notes, string Status, string Priority, string? DueLocal,
        string? DueUtc, string? DueIanaZone, string? DueWindowsZone, string? DueSemantics);
}
