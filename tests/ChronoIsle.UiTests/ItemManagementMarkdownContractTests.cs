using System.IO;

namespace ChronoIsle.UiTests;

public sealed class ItemManagementMarkdownContractTests
{
    [Fact]
    public void Item_management_exposes_template_import_and_export_actions()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root, "src", "ChronoIsle.App", "Views", "LifeManagementWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(
            root, "src", "ChronoIsle.App", "Views", "LifeManagementWindow.xaml.cs"));

        Assert.Contains("Click=\"DownloadTemplate_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"ImportMarkdown_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"ExportMarkdown_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MarkdownItemTransferService.Parse", code, StringComparison.Ordinal);
        Assert.Contains("markdownTransfer.Import(preview)", code, StringComparison.Ordinal);
    }

    static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "ChronoIsle.App")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
