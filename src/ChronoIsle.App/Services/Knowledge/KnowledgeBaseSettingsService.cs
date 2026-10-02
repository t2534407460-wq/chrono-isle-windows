using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace ChronoIsle.App.Services.Knowledge;

public sealed record RemoteKnowledgeSettings(string BaseUrl, string ApiKey);

public sealed class KnowledgeBaseSettingsService
{
    readonly string settingsPath;
    readonly string obsidianPath;
    readonly string remotePath;

    public KnowledgeBaseSettingsService() : this(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)) { }

    public KnowledgeBaseSettingsService(string applicationData)
    {
        settingsPath = Path.Combine(applicationData, "ChronoIsle", "knowledge-base.json");
        obsidianPath = Path.Combine(applicationData, "obsidian", "obsidian.json");
        remotePath = Path.Combine(applicationData, "ChronoIsle", "knowledge-remote.json");
    }

    public string LoadPath()
    {
        if (!File.Exists(settingsPath)) return "";
        try
        {
            var saved = JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsPath));
            return saved?.VaultPath ?? throw new KnowledgeBaseException("知识库设置不完整，请在设置中重新选择目录并保存。");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { throw new KnowledgeBaseException("知识库设置无法读取，请在设置中重新选择目录并保存。"); }
    }

    public void SavePath(string value)
    {
        // An unplugged vault must not prevent saving unrelated model/application preferences.
        try { if (string.Equals(value.Trim(), LoadPath(), StringComparison.Ordinal)) return; }
        catch (KnowledgeBaseException) { } // Saving a replacement repairs an unreadable settings file.
        var path = string.IsNullOrWhiteSpace(value) ? "" : ValidatePath(value.Trim());
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        var temporary = settingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Settings(path)));
        File.Move(temporary, settingsPath, true);
    }

    public string ResolveVaultPath()
    {
        var configured = LoadPath();
        if (!string.IsNullOrWhiteSpace(configured)) return ValidatePath(configured);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(obsidianPath));
            var vaults = document.RootElement.GetProperty("vaults").EnumerateObject()
                .Select(v => v.Value)
                .Where(v => v.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String)
                .ToArray();
            var opened = vaults.Where(v => v.TryGetProperty("open", out var o) && o.ValueKind == JsonValueKind.True).ToArray();
            var candidates = opened.Length == 1 ? opened : vaults;
            if (candidates.Length == 1) return ValidatePath(candidates[0].GetProperty("path").GetString()!);
            if (candidates.Length > 1)
                throw new KnowledgeBaseException("检测到多个 Obsidian 知识库，请在设置中选择本次使用的目录。");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException) { }
        throw new KnowledgeBaseException("未找到本地 Obsidian 知识库，请在设置中选择知识库目录。");
    }

    public RemoteKnowledgeSettings? LoadRemote()
    {
        if (!File.Exists(remotePath)) return null;
        try
        {
            var saved = JsonSerializer.Deserialize<RemoteSaved>(File.ReadAllText(remotePath));
            if (saved is null || saved.BaseUrl is null || saved.ProtectedKey is null) throw new JsonException();
            if (!saved.Enabled) return null;
            var key = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(saved.ProtectedKey), null, DataProtectionScope.CurrentUser));
            return ValidateRemote(saved.BaseUrl, key);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException or CryptographicException)
        { throw new KnowledgeBaseException("远程知识库设置无法读取，请重新填写接口地址和访问密钥。未切换到本地资料。"); }
    }

    public void SaveRemote(bool enabled, string url, string key)
    {
        var value = enabled ? ValidateRemote(url, key) : null;
        var encrypted = value is null ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value.ApiKey), null, DataProtectionScope.CurrentUser));
        Directory.CreateDirectory(Path.GetDirectoryName(remotePath)!);
        File.WriteAllText(remotePath + ".tmp", JsonSerializer.Serialize(new RemoteSaved(enabled, value?.BaseUrl ?? "", encrypted)));
        File.Move(remotePath + ".tmp", remotePath, true);
    }

    public static RemoteKnowledgeSettings ValidateRemote(string url, string key)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new KnowledgeBaseException("请填写不含账号、查询参数或片段的 HTTPS 知识库接口地址。");
        if (key.Length is < 32 or > 256 || key.Any(c => c <= ' ' || c > '~'))
            throw new KnowledgeBaseException("请填写服务器为此项目签发的访问密钥（至少 32 位）。");
        return new(uri.AbsoluteUri.TrimEnd('/') + "/", key);
    }

    public static string ValidatePath(string value) => VaultPaths.ValidateRoot(value);
    sealed record RemoteSaved(bool Enabled, string BaseUrl, string ProtectedKey);
    sealed record Settings(string VaultPath);
}
