namespace ChronoIsle.App.Services.Knowledge;

public sealed class KnowledgeBaseException(string message) : Exception(message);

public static class VaultPaths
{
    public static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static string ValidateRoot(string value)
    {
        try
        {
            if (!Path.IsPathFullyQualified(value) || value.StartsWith(@"\\", StringComparison.Ordinal))
                throw new KnowledgeBaseException("请选择本机知识库文件夹的完整路径。");
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            if (!Directory.Exists(path)) throw new KnowledgeBaseException("知识库目录不存在或暂时无法访问，请检查磁盘和目录设置。");
            for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new KnowledgeBaseException("知识库目录包含符号链接或目录联接，请选择实际文件夹。");
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new KnowledgeBaseException("知识库路径无法访问，请重新选择本机文件夹。"); }
    }

    public static bool IsRelativeDocumentPath(string? path) => !string.IsNullOrWhiteSpace(path) && path.Length <= 4096 &&
        !path.Contains('\\') && !path.Contains(':') && !Path.IsPathRooted(path) &&
        path.Split('/').All(part => part.Length > 0 && !part.StartsWith('.') && part != "node_modules" && !part.Any(char.IsControl));

    public static string ResolveFile(string root, string relative)
    {
        if (!IsRelativeDocumentPath(relative)) throw new KnowledgeBaseException("资料路径无效。");
        root = ValidateRoot(root);
        var path = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, Comparison)) throw new KnowledgeBaseException("资料路径超出知识库范围。");
        ValidateRoot(Path.GetDirectoryName(path)!);
        if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) != 0)
            throw new KnowledgeBaseException("无法读取此资料。");
        return path;
    }
}
