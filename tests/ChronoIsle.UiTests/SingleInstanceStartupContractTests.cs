using System.IO;

namespace ChronoIsle.UiTests;

public sealed class SingleInstanceStartupContractTests
{
    [Fact]
    public void AppStartup_rejects_a_second_instance_in_the_same_user_session()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "ChronoIsle.App", "App.xaml.cs"));
        var startup = ExtractMethodBody(source, "protected override void OnStartup");
        var exit = ExtractMethodBody(source, "protected override void OnExit");

        Assert.Contains("InstanceMutexName = @\"Local\\ChronoIsle.App\"", source, StringComparison.Ordinal);
        Assert.Contains("static Mutex? instanceMutex;", source, StringComparison.Ordinal);
        Assert.Contains("new Mutex(true, InstanceMutexName, out var isFirstInstance)", startup, StringComparison.Ordinal);
        Assert.Contains("if (!isFirstInstance)", startup, StringComparison.Ordinal);
        Assert.Contains("Shutdown();", startup, StringComparison.Ordinal);
        Assert.True(startup.IndexOf("new Mutex", StringComparison.Ordinal) < startup.IndexOf("new ServiceCollection", StringComparison.Ordinal));
        Assert.Contains("finally", exit, StringComparison.Ordinal);
        Assert.True(exit.IndexOf("(services as IDisposable)?.Dispose();", StringComparison.Ordinal) < exit.IndexOf("instanceMutex?.Dispose();", StringComparison.Ordinal));
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
