using System.IO;

namespace ChronoIsle.UiTests;

public sealed class ReleaseVersionContractTests
{
    [Fact]
    public void ReleaseMetadata_IsVersion042()
    {
        var workspace = FindWorkspace();
        var props = File.ReadAllText(Path.Combine(workspace, "Directory.Build.props"));
        var changelog = File.ReadAllText(Path.Combine(workspace, "CHANGELOG.md"));

        Assert.Contains("<Version>0.4.2</Version>", props, StringComparison.Ordinal);
        Assert.Contains("<AssemblyVersion>0.4.2.0</AssemblyVersion>", props, StringComparison.Ordinal);
        Assert.Contains("<FileVersion>0.4.2.0</FileVersion>", props, StringComparison.Ordinal);
        Assert.Contains("## [0.4.2] - 2026-07-27", changelog, StringComparison.Ordinal);
    }

    static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ChronoIsle.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("ChronoIsle.sln was not found from the UI test host.");
    }
}
