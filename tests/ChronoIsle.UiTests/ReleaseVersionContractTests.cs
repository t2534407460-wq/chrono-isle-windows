using System.IO;

namespace ChronoIsle.UiTests;

public sealed class ReleaseVersionContractTests
{
    [Fact]
    public void ReleaseMetadata_IsVersion044()
    {
        var workspace = FindWorkspace();
        var props = File.ReadAllText(Path.Combine(workspace, "Directory.Build.props"));
        var changelog = File.ReadAllText(Path.Combine(workspace, "CHANGELOG.md"));

        Assert.Contains("<Version>0.4.4</Version>", props, StringComparison.Ordinal);
        Assert.Contains("<AssemblyVersion>0.4.4.0</AssemblyVersion>", props, StringComparison.Ordinal);
        Assert.Contains("<FileVersion>0.4.4.0</FileVersion>", props, StringComparison.Ordinal);
        Assert.Contains("## [0.4.4] - 2026-10-02", changelog, StringComparison.Ordinal);
    }

    static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ChronoIsle.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("ChronoIsle.sln was not found from the UI test host.");
    }
}
