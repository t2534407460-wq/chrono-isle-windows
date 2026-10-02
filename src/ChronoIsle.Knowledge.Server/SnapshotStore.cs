using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using ChronoIsle.App.Services.Knowledge;

namespace ChronoIsle.Knowledge.Server;

public sealed class SnapshotStore
{
    public const long MaxArchiveBytes = 2L * 1024 * 1024 * 1024;
    public const long MaxFileBytes = 128L * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly string root;
    readonly SemaphoreSlim writer = new(1, 1);

    public SnapshotStore(string root)
    {
        Directory.CreateDirectory(root);
        this.root = VaultPaths.ValidateRoot(root);
        Directory.CreateDirectory(Path.Combine(this.root, "snapshots"));
    }

    public static bool IsId(string? id) => id is { Length: 32 } && id.All(c => char.IsAsciiHexDigit(c));
    public static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(c => char.IsAsciiHexDigit(c));

    public string? CurrentId()
    {
        var file = Path.Combine(root, "current.txt");
        if (!File.Exists(file)) return null;
        using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        var id = reader.ReadLine();
        if (!IsId(id)) throw new IOException("Invalid current snapshot.");
        return id;
    }

    public string VaultRoot(string id)
    {
        if (!IsId(id)) throw new KnowledgeBaseException("资料版本无效。");
        return VaultPaths.ValidateRoot(Path.Combine(root, "snapshots", id, "vault"));
    }

    public PublishedVault ReadManifest(string id)
    {
        VaultRoot(id);
        return JsonSerializer.Deserialize<PublishedVault>(File.ReadAllText(Path.Combine(root, "snapshots", id, "manifest.json")), Json)
            ?? throw new IOException("Invalid snapshot manifest.");
    }

    public async Task<PublishedVault> PublishAsync(Stream upload, CancellationToken token)
    {
        if (!await writer.WaitAsync(0, token)) throw new SyncConflictException("已有同步正在进行，请稍后重试。");
        var staging = Path.Combine(root, "upload-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            var zipPath = Path.Combine(staging, "bundle.zip");
            await using (var file = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await CopyBounded(upload, file, MaxArchiveBytes, token);
            using var zip = ZipFile.OpenRead(zipPath);
            if (zip.Entries.Count > 20001 || zip.Entries.Select(e => e.FullName).Distinct(StringComparer.Ordinal).Count() != zip.Entries.Count)
                throw new InvalidDataException("同步包条目过多或存在重复项。");
            var entry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("同步包缺少清单。");
            if (entry.Length > 16 * 1024 * 1024) throw new InvalidDataException("同步清单过大。");
            await using var manifestStream = entry.Open();
            using var manifestBytes = new MemoryStream();
            await CopyBounded(manifestStream, manifestBytes, 16 * 1024 * 1024, token);
            var manifest = JsonSerializer.Deserialize<VaultManifest>(manifestBytes.ToArray(), Json) ?? throw new InvalidDataException("同步清单无效。");
            ValidateManifest(manifest);
            var active = CurrentId();
            if (manifest.BaseSnapshotId != active) throw new SyncConflictException("远程资料版本已变化，请重新同步。");
            var previous = active is null ? new Dictionary<string, VaultFile>(StringComparer.Ordinal) :
                ReadManifest(active).Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
            var id = Guid.NewGuid().ToString("N");
            var prepared = Path.Combine(staging, id);
            var vault = Path.Combine(prepared, "vault");
            Directory.CreateDirectory(vault);
            var expectedEntries = new HashSet<string>(StringComparer.Ordinal) { "manifest.json" };
            foreach (var file in manifest.Files)
            {
                token.ThrowIfCancellationRequested();
                var destination = Path.Combine(vault, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (active is not null && previous.TryGetValue(file.Path, out var old) && old.Hash == file.Hash && old.Length == file.Length && old.ModifiedUtc == file.ModifiedUtc)
                {
                    var source = VaultPaths.ResolveFile(VaultRoot(active), file.Path);
                    // Published files are immutable. Linux hard links avoid copying unchanged attachments.
                    if (OperatingSystem.IsLinux())
                    {
                        if (link(source, destination) != 0) throw new IOException("无法建立资料快照。");
                    }
                    else File.Copy(source, destination);
                    continue;
                }
                var name = "files/" + file.Hash;
                expectedEntries.Add(name);
                var payload = zip.GetEntry(name) ?? throw new InvalidDataException("同步包缺少文件内容。");
                if (payload.Length != file.Length) throw new InvalidDataException("文件长度与清单不一致。");
                await using (var input = payload.Open())
                await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    await CopyBounded(input, output, file.Length, token);
                await using (var input = File.OpenRead(destination))
                {
                    if (input.Length != file.Length || Convert.ToHexString(await SHA256.HashDataAsync(input, token)) != file.Hash)
                        throw new InvalidDataException("文件校验失败，未发布本次同步。");
                }
                File.SetLastWriteTimeUtc(destination, file.ModifiedUtc);
            }
            if (zip.Entries.Any(e => !expectedEntries.Contains(e.FullName))) throw new InvalidDataException("同步包包含清单之外的内容。");
            var published = new PublishedVault(id, manifest.Files);
            await File.WriteAllTextAsync(Path.Combine(prepared, "manifest.json"), JsonSerializer.Serialize(published, Json), token);
            // All files and hashes are complete before readers can observe this version.
            token.ThrowIfCancellationRequested();
            Directory.Move(prepared, Path.Combine(root, "snapshots", id));
            var pointer = Path.Combine(staging, "current.txt");
            await File.WriteAllTextAsync(pointer, id, token);
            File.Move(pointer, Path.Combine(root, "current.txt"), true);
            return published;
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { Console.Error.WriteLine("Temporary upload cleanup failed; inspect the data directory."); }
            finally { writer.Release(); }
        }
    }

    static void ValidateManifest(VaultManifest manifest)
    {
        if (manifest.Files is null || manifest.Files.Count is < 1 or > 20000 ||
            (manifest.BaseSnapshotId is not null && !IsId(manifest.BaseSnapshotId))) throw new InvalidDataException("同步清单无效或为空。");
        long total = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (file is null || !VaultPaths.IsRelativeDocumentPath(file.Path) || !names.Add(file.Path) ||
                !IsHash(file.Hash) || file.Hash != file.Hash.ToUpperInvariant() || file.Length < 0 || file.Length > MaxFileBytes ||
                file.ModifiedUtc.Kind != DateTimeKind.Utc || file.ModifiedUtc.Year < 1980 || file.ModifiedUtc.Year > 2200)
                throw new InvalidDataException("同步清单包含无效路径、大小、哈希或时间。");
            total += file.Length;
            if (total > 8L * 1024 * 1024 * 1024) throw new InvalidDataException("知识库超过 8 GiB 同步上限。");
        }
    }

    public static async Task CopyBounded(Stream input, Stream output, long maximum, CancellationToken token)
    {
        var buffer = new byte[81920];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            total += count;
            if (total > maximum) throw new InvalidDataException("内容超过读取上限。");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
    }

    [DllImport("libc", SetLastError = true)]
    static extern int link(string oldpath, string newpath);
}

public sealed class SyncConflictException(string message) : Exception(message);
