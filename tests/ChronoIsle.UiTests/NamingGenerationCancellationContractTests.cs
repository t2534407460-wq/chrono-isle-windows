using System.IO;

namespace ChronoIsle.UiTests;

public sealed class NamingGenerationCancellationContractTests
{
    [Fact]
    public void Naming_generation_button_cancels_the_active_request()
    {
        var root = FindRepositoryRoot();
        var code = File.ReadAllText(Path.Combine(root, "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml.cs"));
        var generate = ExtractMethodBody(code, "async void Generate_Click");
        var setBusy = ExtractMethodBody(code, "void SetBusy");

        Assert.Contains("CancellationTokenSource? namingRequestCancellation", code, StringComparison.Ordinal);
        Assert.Contains("if (namingRequestCancellation is not null)", generate, StringComparison.Ordinal);
        Assert.Contains("namingRequestCancellation.Cancel();", generate, StringComparison.Ordinal);
        Assert.Contains("await naming.GenerateAsync(kind, meaning, cancellation.Token);", generate, StringComparison.Ordinal);
        Assert.Contains("GenerateButton.IsEnabled = true;", setBusy, StringComparison.Ordinal);
        Assert.Contains("GenerateButton.Content = busy ? \"取消生成\" : \"生成命名\";", setBusy, StringComparison.Ordinal);
        Assert.Contains("ShowStatus(\"已取消生成，可修改内容后重新生成。\", StatusKind.Success);", code, StringComparison.Ordinal);
        Assert.Contains("if (ReferenceEquals(namingRequestCancellation, cancellation))", code, StringComparison.Ordinal);
        Assert.Contains("namingRequestCancellation?.Cancel();", code, StringComparison.Ordinal);
    }

    static string FindRepositoryRoot()
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
