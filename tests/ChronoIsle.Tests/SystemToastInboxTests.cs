using ChronoIsle.App.Services;

namespace ChronoIsle.Tests;

public sealed class SystemToastInboxTests
{
    [Fact]
    public void ToastTextComposer_UsesFirstLineAsTitle()
    {
        var result = ToastTextComposer.Compose(["  应用标题 ", "第一行", "", "第二行"]);

        Assert.Equal("应用标题", result.Title);
        Assert.Equal("第一行 · 第二行", result.Body);
    }

    [Fact]
    public void ToastTextComposer_ProvidesSafeFallback()
    {
        var result = ToastTextComposer.Compose([null, " "]);

        Assert.Equal("新通知", result.Title);
        Assert.Empty(result.Body);
    }
}
