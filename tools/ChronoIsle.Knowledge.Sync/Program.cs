using System.IO.Compression;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChronoIsle.App.Services.Knowledge;

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
try
{
    if (args is ["--pack", var vault, var output])
    {
        var files = Scan(vault);
        Pack(vault, files, null, output);
        Console.WriteLine($"已生成同步包：{files.Count} 个文件，{files.Sum(f => f.Length)} 字节。");
        return 0;
    }
    if (args is not ["--config", var configPath]) throw new ArgumentException("用法：--pack <项目知识库目录> <ZIP> 或 --config <加密配置文件>");
    var config = JsonSerializer.Deserialize<SyncSettings>(File.ReadAllText(configPath), json) ?? throw new InvalidDataException("配置无效。");
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("自动同步配置使用 Windows 当前用户加密。");
    if (!Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https" ||
        endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0) throw new InvalidDataException("同步需要有效的 HTTPS 地址。");
    endpoint = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/");
    var key = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(config.ProtectedKey), null, DataProtectionScope.CurrentUser));
    using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(30), MaxResponseContentBufferSize = 16 * 1024 * 1024 };
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    var timer = Stopwatch.StartNew();
    Console.WriteLine("正在读取远程资料清单…");
    using var manifestBudget = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    using var response = await client.GetAsync(new Uri(endpoint, "v1/sync/manifest"), manifestBudget.Token);
    response.EnsureSuccessStatusCode();
    var previous = JsonSerializer.Deserialize<PublishedVault>(await response.Content.ReadAsStringAsync(), json) ?? throw new InvalidDataException("远程清单无效。");
    Console.WriteLine($"远程清单已读取（{timer.Elapsed.TotalSeconds:F1} 秒），正在核对本地文件哈希…");
    var current = Scan(config.VaultPath);
    Console.WriteLine($"已核对 {current.Count} 个文件（累计 {timer.Elapsed.TotalSeconds:F1} 秒）。");
    if (previous.Files.OrderBy(f => f.Path, StringComparer.Ordinal).SequenceEqual(current))
    { Console.WriteLine("资料未变化，无需上传。"); return 0; }
    var zip = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, "sync-" + Guid.NewGuid().ToString("N") + ".zip");
    try
    {
        Pack(config.VaultPath, current, previous, zip);
        Console.WriteLine($"正在上传变化内容：{new FileInfo(zip).Length} 字节…");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "v1/sync"));
        request.Content = new StreamContent(File.OpenRead(zip));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        using var result = await client.SendAsync(request);
        result.EnsureSuccessStatusCode();
        var published = JsonSerializer.Deserialize<PublishedVault>(await result.Content.ReadAsStringAsync(), json) ?? throw new InvalidDataException("发布结果无效。");
        if (published.Files.Count != current.Count || !published.Files.OrderBy(f => f.Path, StringComparer.Ordinal).SequenceEqual(current))
            throw new InvalidDataException("远程发布清单与本地不一致。");
        Console.WriteLine($"同步完成：{current.Count} 个文件，版本 {published.SnapshotId}。");
    }
    finally { if (File.Exists(zip)) File.Delete(zip); }
    return 0;
}
catch (Exception error)
{
    // No key, request body, note text or potentially credential-bearing exception is logged.
    Console.Error.WriteLine($"同步未完成（{error.GetType().Name}）。远程已发布资料保留；请检查网络、密钥、目录和同步包，下一周期重试。");
    return 1;
}

List<VaultFile> Scan(string path)
{
    path = VaultPaths.ValidateRoot(path);
    if (new DirectoryInfo(path).Name != "项目知识库") throw new InvalidDataException("同步范围必须为项目知识库目录，不能选整个 Obsidian Vault。");
    var directories = new Stack<string>(); directories.Push(path);
    var result = new List<VaultFile>();
    while (directories.TryPop(out var directory))
    foreach (var item in Directory.EnumerateFileSystemEntries(directory))
    {
        var name = Path.GetFileName(item);
        var attributes = File.GetAttributes(item);
        if (name.StartsWith('.') || name is "node_modules" or "CodexDebug" || (attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0) continue;
        if ((attributes & FileAttributes.Directory) != 0) { directories.Push(item); continue; }
        var relative = Path.GetRelativePath(path, item).Replace(Path.DirectorySeparatorChar, '/');
        if (!VaultPaths.IsRelativeDocumentPath(relative)) throw new InvalidDataException("不支持的资料路径。");
        var before = new FileInfo(item);
        var length = before.Length; var modified = before.LastWriteTimeUtc;
        if (length > 128 * 1024 * 1024) throw new InvalidDataException("单文件超过 128 MiB。");
        using var stream = new FileStream(item, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        var after = new FileInfo(item);
        if (after.Length != length || after.LastWriteTimeUtc != modified) throw new IOException("资料在扫描时变化。");
        result.Add(new(relative, length, hash, modified));
        if (result.Count > 20000) throw new InvalidDataException("文件数量超限。");
    }
    if (result.Count == 0 || result.Sum(f => f.Length) > 8L * 1024 * 1024 * 1024) throw new InvalidDataException("资料为空或超出 8 GiB。");
    return result.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
}

void Pack(string root, IReadOnlyList<VaultFile> files, PublishedVault? previous, string output)
{
    var before = previous?.Files.ToDictionary(f => f.Path, StringComparer.Ordinal) ?? new();
    using var zip = ZipFile.Open(output, ZipArchiveMode.Create);
    using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open(), new UTF8Encoding(false)))
        writer.Write(JsonSerializer.Serialize(new VaultManifest(string.IsNullOrEmpty(previous?.SnapshotId) ? null : previous.SnapshotId, files), json));
    var hashes = new HashSet<string>(StringComparer.Ordinal);
    foreach (var file in files)
    {
        if (before.TryGetValue(file.Path, out var old) && old == file) continue;
        if (!hashes.Add(file.Hash)) continue;
        var path = VaultPaths.ResolveFile(root, file.Path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var outputStream = zip.CreateEntry("files/" + file.Hash, CompressionLevel.Fastest).Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int count; long total = 0;
        while ((count = input.Read(buffer)) > 0)
        {
            total += count;
            if (total > file.Length) throw new IOException("打包时文件发生变化。");
            hash.AppendData(buffer.AsSpan(0, count)); outputStream.Write(buffer.AsSpan(0, count));
        }
        if (total != file.Length || Convert.ToHexString(hash.GetHashAndReset()) != file.Hash) throw new IOException("打包校验失败。");
    }
}

sealed record SyncSettings(string VaultPath, string BaseUrl, string ProtectedKey);
