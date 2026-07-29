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
