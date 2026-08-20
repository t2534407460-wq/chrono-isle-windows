using System.IO;

namespace ChronoIsle.UiTests;

public sealed class TextBoxContextMenuContractTests
{
    [Fact]
    public void EditableTextBoxes_share_themed_editing_context_menu()
    {
        var root = FindRepositoryRoot();
        var controls = File.ReadAllText(Path.Combine(root, "src", "ChronoIsle.App", "Resources", "Controls.xaml"));
        var island = File.ReadAllText(Path.Combine(root, "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml"));

        Assert.Contains("x:Key=\"TextBox.ContextMenu\"", controls, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"TextBox.ContextMenuItem\"", controls, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"ContextMenu\">", controls, StringComparison.Ordinal);
        Assert.Contains("Header=\"剪切\"", controls, StringComparison.Ordinal);
        Assert.Contains("Header=\"复制\"", controls, StringComparison.Ordinal);
        Assert.Contains("Header=\"粘贴\"", controls, StringComparison.Ordinal);
        Assert.Contains("Command=\"ApplicationCommands.Cut\"", controls, StringComparison.Ordinal);
        Assert.Contains("Command=\"ApplicationCommands.Copy\"", controls, StringComparison.Ordinal);
        Assert.Contains("Command=\"ApplicationCommands.Paste\"", controls, StringComparison.Ordinal);
        Assert.Equal(3, controls.Split("CommandTarget=\"{Binding PlacementTarget,RelativeSource={RelativeSource AncestorType={x:Type ContextMenu}}}\"", StringSplitOptions.None).Length - 1);

        Assert.Contains("<Style x:Key=\"IslandTextInput\" TargetType=\"TextBox\" BasedOn=\"{StaticResource {x:Type TextBox}}\">", island, StringComparison.Ordinal);
        Assert.Contains("<TextBox x:Name=\"MeaningInput\"", island, StringComparison.Ordinal);
        Assert.Contains("<TextBox x:Name=\"CaseConverterInput\"", island, StringComparison.Ordinal);
        Assert.Contains("<TextBox x:Name=\"QuickAddInput\"", island, StringComparison.Ordinal);
    }

    static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ChronoIsle.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("ChronoIsle.sln was not found from the UI test host.");
    }
}
