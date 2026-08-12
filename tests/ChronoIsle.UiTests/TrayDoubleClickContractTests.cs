using System.IO;

namespace ChronoIsle.UiTests;

public sealed class TrayDoubleClickContractTests
{
    [Fact]
    public void Left_tray_double_click_restores_the_default_island_and_expands_it()
    {
        var workspace = FindWorkspace();
        var tray = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Services",
            "LifeTrayService.cs"));
        var app = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "App.xaml.cs"));
        var island = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Views",
            "LifeIslandWindow.xaml.cs"));
        var singleClick = ExtractMethodBody(tray, "void Icon_MouseClick(");
        var doubleClick = ExtractMethodBody(tray, "void Icon_MouseDoubleClick(");
        var restore = ExtractMethodBody(island, "public void OpenDefaultExpanded()");

        Assert.Contains("public event EventHandler? RestoreIslandRequested;", tray, StringComparison.Ordinal);
        Assert.Contains("icon.MouseClick += Icon_MouseClick;", tray, StringComparison.Ordinal);
        Assert.Contains("icon.MouseDoubleClick += Icon_MouseDoubleClick;", tray, StringComparison.Ordinal);
        Assert.Contains("args.Button != Forms.MouseButtons.Right", singleClick, StringComparison.Ordinal);
        Assert.Contains("ShowMenu(TrayMenuWindow.CaptureTrayHostAtCursor());", singleClick, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenRequested?.Invoke", singleClick, StringComparison.Ordinal);
        Assert.Contains("args.Button == Forms.MouseButtons.Left", doubleClick, StringComparison.Ordinal);
        Assert.Contains("RestoreIslandRequested?.Invoke(this, EventArgs.Empty);", doubleClick, StringComparison.Ordinal);
        Assert.Contains("tray.RestoreIslandRequested += (_, _) => Dispatcher.BeginInvoke(island.OpenDefaultExpanded);", app, StringComparison.Ordinal);
        Assert.Contains("ResetToDefaultPlacement();", restore, StringComparison.Ordinal);
        Assert.Contains("Show();", restore, StringComparison.Ordinal);
        Assert.Contains("Expand();", restore, StringComparison.Ordinal);
        Assert.Contains("Touch();", restore, StringComparison.Ordinal);
        Assert.DoesNotContain("Activate();", restore, StringComparison.Ordinal);
    }

    static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ChronoIsle.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("ChronoIsle.sln was not found from the UI test host.");
    }

    static string ExtractMethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Method signature was not found: {signature}.");
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
