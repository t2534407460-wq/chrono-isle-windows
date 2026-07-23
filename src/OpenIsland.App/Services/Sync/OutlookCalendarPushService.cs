using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenIsland.App.Services.Persistence;

namespace OpenIsland.App.Services.Sync;

public sealed record OutlookCalendarPushSummary(int Created, int Updated, int Skipped, IReadOnlyList<string> SkippedReasons, string? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>Opt-in, one-way push for only losslessly expressible local Events.</summary>
public sealed class OutlookCalendarPushService
{
    readonly SqliteConnectionFactory connections;
    readonly GraphSyncStore syncStore;
    readonly IMicrosoftGraphAuthenticationService authentication;
    readonly HttpClient http;

    public OutlookCalendarPushService(SqliteConnectionFactory connections, GraphSyncStore syncStore,
        IMicrosoftGraphAuthenticationService authentication, HttpClient? http = null)
    {
        this.connections = connections ?? throw new ArgumentNullException(nameof(connections));
        this.syncStore = syncStore ?? throw new ArgumentNullException(nameof(syncStore));
        this.authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        this.http = http ?? new HttpClient { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
    }

    public async Task<OutlookCalendarPushSummary> PushAllAsync(string accountId, string? calendarId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return new(0, 0, 0, [], "请先完成 Microsoft 登录。");
        var token = await authentication.AcquireAccessTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            syncStore.UpsertAccount(accountId, SyncProvider.OutlookCalendar, SyncAccountStatus.ReauthRequired);
            return new(0, 0, 0, [], "登录已过期，请先重新登录 Microsoft。");
        }

        try
        {
            var created = 0; var updated = 0; var skipped = 0;
            var reasons = new List<string>();
            foreach (var item in ReadPushableEvents())
            {
                if (!TryBuildPayload(item, out var payload, out var reason))
                {
                    skipped++; reasons.Add($"{item.Title}：{reason}"); continue;
                }
                var mapping = syncStore.FindActiveMapping(accountId, SyncProvider.OutlookCalendar, item.Id);
                if (mapping?.IsReadOnly == true) { skipped++; reasons.Add($"{item.Title}：只读外部镜像"); continue; }
                if (mapping is null)
                {
                    var remoteId = await CreateEventAsync(token, calendarId, payload!, cancellationToken);
                    syncStore.AddMapping(Guid.NewGuid().ToString("N"), accountId, SyncProvider.OutlookCalendar, item.Id,
                        remoteId, ExternalMappingResult.Editable());
                    created++;
                }
                else
                {
                    await UpdateEventAsync(token, calendarId, mapping.RemoteResourceId, payload!, cancellationToken);
                    updated++;
                }
            }
            syncStore.UpsertAccount(accountId, SyncProvider.OutlookCalendar, SyncAccountStatus.Connected);
            return new(created, updated, skipped, reasons, null);
        }
        catch (HttpRequestException exception)
        {
            return new(0, 0, 0, [], $"Outlook Calendar 推送失败：{exception.Message}");
        }
        catch (JsonException exception)
        {
            return new(0, 0, 0, [], $"Outlook Calendar 返回无法识别的数据：{exception.Message}");
        }
    }

    async Task<string> CreateEventAsync(string token, string? calendarId, object payload, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, EventsPath(calendarId), token, payload, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetProperty("id").GetString() ?? throw new JsonException("Calendar event response has no id.");
    }

    async Task UpdateEventAsync(string token, string? calendarId, string eventId, object payload, CancellationToken cancellationToken)
    {
        using var _ = await SendAsync(HttpMethod.Patch, EventsPath(calendarId) + "/" + Uri.EscapeDataString(eventId), token, payload, cancellationToken);
    }

    static string EventsPath(string? calendarId) => string.IsNullOrWhiteSpace(calendarId)
        ? "me/calendar/events"
        : "me/calendars/" + Uri.EscapeDataString(calendarId.Trim()) + "/events";

    async Task<HttpResponseMessage> SendAsync(HttpMethod method, string uri, string token, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            response.Dispose();
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {responseText}");
        }
        return response;
    }

    IReadOnlyList<PushableEvent> ReadPushableEvents()
    {
        using var connection = connections.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,title,notes,
              start_local_datetime,start_utc_instant,start_iana_time_zone_id,start_windows_time_zone_id_cache,start_time_semantics,
              end_local_datetime,end_utc_instant,end_iana_time_zone_id,end_windows_time_zone_id_cache,end_time_semantics
            FROM life_items
            WHERE deleted_at IS NULL AND is_readonly=0 AND kind='Event' AND origin_type='Local'
            ORDER BY updated_at,id
            """;
        using var reader = command.ExecuteReader();
        var result = new List<PushableEvent>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), Read(reader, 2),
            Read(reader, 3), Read(reader, 4), Read(reader, 5), Read(reader, 6), Read(reader, 7),
            Read(reader, 8), Read(reader, 9), Read(reader, 10), Read(reader, 11), Read(reader, 12)));
        return result;
    }

    static bool TryBuildPayload(PushableEvent item, out object? payload, out string reason)
    {
        payload = null;
        if (item.StartSemantics != "ZonedWallClock" || item.EndSemantics != "ZonedWallClock" ||
            item.StartLocal is null || item.EndLocal is null || item.StartIanaZone is null || item.EndIanaZone is null)
        {
            reason = "开始和结束时间必须均为固定时区的墙上时间";
            return false;
        }
        if (!string.Equals(item.StartIanaZone, item.EndIanaZone, StringComparison.Ordinal))
        {
            reason = "开始和结束时区不同，无法无损映射";
            return false;
        }
        var windowsZone = item.StartWindowsZone ?? TryWindowsZone(item.StartIanaZone);
        if (string.IsNullOrWhiteSpace(windowsZone))
        {
            reason = "无法映射到 Outlook 时区";
            return false;
        }
        payload = new
        {
            subject = item.Title,
            body = string.IsNullOrWhiteSpace(item.Notes) ? null : new { contentType = "text", content = item.Notes },
            start = new { dateTime = DateTime.Parse(item.StartLocal, CultureInfo.InvariantCulture).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture), timeZone = windowsZone },
            end = new { dateTime = DateTime.Parse(item.EndLocal, CultureInfo.InvariantCulture).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture), timeZone = windowsZone }
        };
        reason = string.Empty;
        return true;
    }

    static string? TryWindowsZone(string iana) => TimeZoneInfo.TryConvertIanaIdToWindowsId(iana, out var windows) ? windows : null;
    static string? Read(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    sealed record PushableEvent(string Id, string Title, string? Notes, string? StartLocal, string? StartUtc, string? StartIanaZone,
        string? StartWindowsZone, string? StartSemantics, string? EndLocal, string? EndUtc, string? EndIanaZone,
        string? EndWindowsZone, string? EndSemantics);
}
