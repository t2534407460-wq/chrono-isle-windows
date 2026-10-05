using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChronoIsle.App.Services.Sync;

public sealed record CloudTokens(string AccessToken, string RefreshToken, int ExpiresIn, Guid UserId);
public sealed record CloudEmailChallenge(Guid ChallengeId, DateTimeOffset ExpiresAt);
public sealed record CloudAccount(Guid UserId, string Email, Guid DeviceId);
public sealed class CloudAccountClient : IDisposable
{
    static readonly Uri Identity = new("https://zhuisu.leadjet.com.cn/identity/v1/");
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly HttpClient http;
    readonly string directory;
    readonly Guid device;
    readonly System.Threading.SemaphoreSlim sessionLock = new(1, 1);
    CloudTokens? tokens;
    DateTimeOffset expires;
    public CloudAccount? Account { get; private set; }
    public CloudAccountClient(string? directory = null, HttpMessageHandler? handler = null)
    {
        this.directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChronoIsle", "Account");
        Directory.CreateDirectory(this.directory);
        http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = Identity, Timeout = TimeSpan.FromSeconds(30) };
        var deviceFile = Path.Combine(this.directory, "device-id");
        if (!Guid.TryParse(File.Exists(deviceFile) ? File.ReadAllText(deviceFile) : null, out device)) { device = Guid.NewGuid(); File.WriteAllText(deviceFile, device.ToString()); }
        var session = ReadProtected<StoredSession>("session.dpapi");
        if (session is not null) { Account = session.Account; tokens = session.Tokens; expires = session.Expires; }
    }
    public Task<CloudEmailChallenge> RegisterAsync(string email, string password, CancellationToken ct = default) =>
        Post<CloudEmailChallenge>("email/register", new { email, password, clientId = "island-windows", deviceId = device }, ct);
    public async Task LoginAsync(string email, string password, CancellationToken ct = default) =>
        await Accept(await Post<CloudTokens>("email/login", new { email, password, clientId = "island-windows", deviceId = device }, ct), email, ct);
    public async Task ConfirmAsync(Guid challenge, string code, string email, CancellationToken ct = default) =>
        await Accept(await Post<CloudTokens>("email/confirm", new { challengeId = challenge, code }, ct), email, ct);
    public Task<CloudEmailChallenge> ResetAsync(string email, string password, CancellationToken ct = default) => Post<CloudEmailChallenge>("email/reset", new { email, newPassword = password }, ct);
    public async Task ConfirmResetAsync(Guid challenge, string code, CancellationToken ct = default)
    { using var response = await http.PostAsJsonAsync("email/confirm", new { challengeId = challenge, code }, Json, ct); await Check(response, ct); }
    public async Task<string> AccessTokenAsync(CancellationToken ct = default)
    {
        await sessionLock.WaitAsync(ct);
        try
        {
            if (tokens is null || Account is null) throw new InvalidOperationException("请先登录账号。");
            if (expires <= DateTimeOffset.UtcNow.AddSeconds(30)) Save(await Post<CloudTokens>("sessions/refresh", new { refreshToken = tokens.RefreshToken }, ct), Account.Email);
            return tokens.AccessToken;
        }
        finally { sessionLock.Release(); }
    }
    public async Task LogoutAsync(CancellationToken ct = default)
    {
        await sessionLock.WaitAsync(ct);
        try
        {
            if (tokens is not null) { using var response = await http.PostAsJsonAsync("sessions/revoke", new { refreshToken = tokens.RefreshToken }, Json, ct); await Check(response, ct); }
            var file = Path.Combine(directory, "session.dpapi"); if (File.Exists(file)) File.Delete(file);
            tokens = null; Account = null;
        }
        finally { sessionLock.Release(); }
    }
    async Task Accept(CloudTokens value, string email, CancellationToken ct)
    {
        await sessionLock.WaitAsync(ct);
        try { Save(value, email); }
        catch (InvalidOperationException)
        { using var response = await http.PostAsJsonAsync("sessions/revoke", new { refreshToken = value.RefreshToken }, Json, ct); throw; }
        finally { sessionLock.Release(); }
    }
    void Save(CloudTokens value, string email)
    {
        if (value.UserId == Guid.Empty || value.ExpiresIn is < 1 or > 3600 || string.IsNullOrWhiteSpace(value.AccessToken) || string.IsNullOrWhiteSpace(value.RefreshToken)) throw new InvalidDataException("账号服务返回了无效会话。");
        // Binding survives logout. A second account must never inherit the first account's local data or queue.
        var owner = ReadProtected<OwnerBinding>("dataset-owner.dpapi");
        if (owner is not null && owner.UserId != value.UserId) throw new InvalidOperationException("当前本地数据已绑定另一个账号。请先使用独立数据空间，避免把两个账号的数据合并。");
        if (owner is null) WriteProtected("dataset-owner.dpapi", new OwnerBinding(value.UserId, Guid.NewGuid()));
        var account = new CloudAccount(value.UserId, email.Trim().ToLowerInvariant(), device);
        var expiration = DateTimeOffset.UtcNow.AddSeconds(value.ExpiresIn);
        WriteProtected("session.dpapi", new StoredSession(account, value, expiration)); Account = account; tokens = value; expires = expiration;
    }
    async Task<T> Post<T>(string path, object body, CancellationToken ct)
    { using var response = await http.PostAsJsonAsync(path, body, Json, ct); await Check(response, ct); return await response.Content.ReadFromJsonAsync<T>(Json, ct) ?? throw new InvalidDataException("账号响应为空。"); }
    static async Task Check(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException("账号服务尚未部署，请稍后再试。");
        if ((int)response.StatusCode is >= 300 and < 400) throw new InvalidOperationException("账号服务返回了重定向，已停止发送登录信息。");
        string? message = null;
        if (response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (json.RootElement.TryGetProperty("message", out var value) && value.ValueKind == JsonValueKind.String) message = value.GetString();
        }
        throw new InvalidOperationException(message is { Length: > 0 and <= 256 } ? message : response.StatusCode switch { HttpStatusCode.Unauthorized => "邮箱、密码或验证码无效。", HttpStatusCode.TooManyRequests => "请求过于频繁，请稍后再试。", _ => "账号服务暂时不可用，请稍后重试。" });
    }
    T? ReadProtected<T>(string file)
    {
        var path = Path.Combine(directory, file); if (!File.Exists(path)) return default;
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<T>(bytes, Json); } finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    void WriteProtected<T>(string file, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        try { var path = Path.Combine(directory, file); var temporary = path + ".tmp"; File.WriteAllBytes(temporary, ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)); File.Move(temporary, path, true); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public void Dispose() => http.Dispose();
    sealed record OwnerBinding(Guid UserId, Guid StoreId);
    sealed record StoredSession(CloudAccount Account, CloudTokens Tokens, DateTimeOffset Expires);
}
