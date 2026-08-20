using System.IO;

namespace ChronoIsle.UiTests;

public sealed class QuickAskConversationContractTests
{
    [Fact]
    public void QuickAsk_uses_new_conversation_submission_path()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root, "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml.cs"));

        Assert.Contains("await assistant.SubmitQuickAskAsync(text);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("await assistant.SubmitAsync(text);", QuickAskHandler(source), StringComparison.Ordinal);
    }

    [Fact]
    public void QuickAskAnswer_is_read_only_selectable_text()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml"));

        Assert.Contains("<TextBox x:Name=\"QuickAskAnswer\"", source, StringComparison.Ordinal);
        Assert.Contains("IsReadOnly=\"True\"", source, StringComparison.Ordinal);
        Assert.Contains("TextWrapping=\"Wrap\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void QuickAskInput_TypingResetsMouseLeaveCollapseTimer()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml.cs"));
        var start = source.IndexOf("void QuickAskInput_TextChanged", StringComparison.Ordinal);
        var end = source.IndexOf("void QuickAskInput_KeyDown", start, StringComparison.Ordinal);

        var handler = source[start..end];

        Assert.Contains("if (!pointerHover && expanded) ScheduleMouseLeaveCollapse();", handler, StringComparison.Ordinal);
        Assert.Contains("else Touch();", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void QuickAskAnswer_uses_themed_copy_context_menu()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml"));
        var start = source.IndexOf("<TextBox x:Name=\"QuickAskAnswer\"", StringComparison.Ordinal);
        var end = source.IndexOf("<TextBox x:Name=\"QuickAskInput\"", start, StringComparison.Ordinal);
        var answer = source[start..end];

        Assert.Contains("<ContextMenu Style=\"{StaticResource IslandContextMenu}\">", answer, StringComparison.Ordinal);
        Assert.Contains("Header=\"复制\"", answer, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource IslandContextMenuItem}\"", answer, StringComparison.Ordinal);
        Assert.Contains("Command=\"ApplicationCommands.Copy\"", answer, StringComparison.Ordinal);
        Assert.Contains("CommandTarget=\"{Binding PlacementTarget,RelativeSource={RelativeSource AncestorType={x:Type ContextMenu}}}\"", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void QuickAskInput_uses_themed_editing_context_menu()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml"));
        var start = source.IndexOf("<TextBox x:Name=\"QuickAskInput\"", StringComparison.Ordinal);
        var end = source.IndexOf("<StackPanel Orientation=\"Horizontal\" HorizontalAlignment=\"Right\">", start, StringComparison.Ordinal);
        var input = source[start..end];

        Assert.Contains("<ContextMenu Style=\"{StaticResource IslandContextMenu}\">", input, StringComparison.Ordinal);
        Assert.Contains("Header=\"剪切\"", input, StringComparison.Ordinal);
        Assert.Contains("Header=\"复制\"", input, StringComparison.Ordinal);
        Assert.Contains("Header=\"粘贴\"", input, StringComparison.Ordinal);
        Assert.Contains("Command=\"ApplicationCommands.Cut\"", input, StringComparison.Ordinal);
        Assert.Contains("Command=\"ApplicationCommands.Copy\"", input, StringComparison.Ordinal);
        Assert.Contains("Command=\"ApplicationCommands.Paste\"", input, StringComparison.Ordinal);
        Assert.Equal(3, input.Split("CommandTarget=\"{Binding PlacementTarget,RelativeSource={RelativeSource AncestorType={x:Type ContextMenu}}}\"", StringSplitOptions.None).Length - 1);
    }

    static string QuickAskHandler(string source)
    {
        var start = source.IndexOf("async void QuickAskSend_Click", StringComparison.Ordinal);
        var end = source.IndexOf("void QuickAskStop_Click", start, StringComparison.Ordinal);
        return source[start..end];
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
