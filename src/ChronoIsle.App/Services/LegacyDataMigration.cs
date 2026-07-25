using System.IO;

namespace ChronoIsle.App.Services;

internal static class LegacyDataMigration
{
    internal const string LegacyDirectoryName = "OpenIsland";
    internal const string CurrentDirectoryName = "ChronoIsle";

    internal static void Run() => Run(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    internal static void Run(string roamingRoot, string localRoot)
    {
        try
        {
            CopyMissingFiles(roamingRoot);
            CopyMissingFiles(localRoot);
        }
        catch
        {
            // 品牌迁移不能阻止应用启动；旧目录保持不变，可供用户手动恢复。
        }
    }

    static void CopyMissingFiles(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return;
        var source = Path.Combine(root, LegacyDirectoryName);
        if (!Directory.Exists(source)) return;

        var destination = Path.Combine(root, CurrentDirectoryName);
        foreach (var sourceFile in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destinationFile = Path.Combine(destination, Path.GetRelativePath(source, sourceFile));
            if (File.Exists(destinationFile)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(sourceFile, destinationFile);
        }
    }
}
