using System.IO;

namespace ChronoIsle.UiTests;

public sealed class IslandStartupVisibilityContractTests
{
    [Fact]
    public void Startup_uses_a_visible_top_center_placement_instead_of_restoring_taskbar_docking()
    {
        var source = ReadFile("src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml.cs");
        var restoreInitialPlacement = ExtractMethodBody(source, "void RestoreInitialPlacement()");

        Assert.Contains("PositionAtTopCenter();", restoreInitialPlacement, StringComparison.Ordinal);
        Assert.DoesNotContain("placement = IslandPlacement.Taskbar;", restoreInitialPlacement, StringComparison.Ordinal);
    }

    [Fact]
    public void Restart_script_writes_the_shortcut_description_without_source_encoding_dependence()
    {
        var script = ReadFile("scripts", "dev-restart.ps1");

        Assert.Contains(
            "$shortcut.Description = [string]::Concat([char]0x65F6, [char]0x5C7F, ' ChronoIsle')",
            script,
            StringComparison.Ordinal);
    }

    static string ReadFile(params string[] relativePath)
        => File.ReadAllText(Path.Combine([FindWorkspace(), .. relativePath]));

    static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ChronoIsle.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("ChronoIsle.sln was not found from the UI test host.");
    }

    static string ExtractMethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Method signature was not found for {signature}.");
        var openingBrace = source.IndexOf('{', start + signature.Length);
        Assert.True(openingBrace >= 0, $"Opening brace was not found for {signature}.");

        var depth = 0;
        for (var index = openingBrace; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            if (source[index] != '}') continue;
            if (--depth == 0) return source[start..(index + 1)];
        }

        throw new InvalidOperationException($"Closing brace was not found for {signature}.");
    }
}
