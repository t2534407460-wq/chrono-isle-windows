using ChronoIsle.App.Services;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ChronoIsle.Tests;

public sealed class SystemToastInboxTests
{
    [Fact]
    public void ToastInboxDiagnostics_FormatsSingleLineOperationalMetadata()
    {
        var type = typeof(SystemToastInboxService).Assembly.GetType(
            "ChronoIsle.App.Services.ToastInboxDiagnostics");

        Assert.NotNull(type);
        var format = type.GetMethod("Format", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(format);
        var entry = (string?)format.Invoke(
            null,
            [new DateTimeOffset(2026, 8, 4, 8, 0, 0, TimeSpan.Zero), "event", 42u, "kind=Updated; app=ChatGPT"]);

        Assert.Equal(
            $"2026-08-04T08:00:00.0000000+00:00 | event | id=42 | kind=Updated; app=ChatGPT{Environment.NewLine}",
            entry);
        Assert.DoesNotContain("message body", entry, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToastInboxDiagnostics_DescribesComFailureWithoutItsMessage()
    {
        var type = typeof(SystemToastInboxService).Assembly.GetType(
            "ChronoIsle.App.Services.ToastInboxDiagnostics");

        Assert.NotNull(type);
        var failure = type.GetMethod("Failure", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(failure);
        var detail = (string?)failure.Invoke(
            null,
            [new COMException("private notification text", unchecked((int)0x80004005))]);

        Assert.Equal("exception=COMException; hresult=0x80004005", detail);
        Assert.DoesNotContain("private notification text", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToastInboxThreading_RunsSubscriptionWorkOnMtaThread()
    {
        var type = typeof(SystemToastInboxService).Assembly.GetType(
            "ChronoIsle.App.Services.ToastInboxThreading");

        Assert.NotNull(type);
        var run = type.GetMethod("RunAsync", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(run);
        var apartment = ApartmentState.Unknown;
        var task = (Task?)run.Invoke(null, [new Action(() => apartment = Thread.CurrentThread.GetApartmentState())]);

        Assert.NotNull(task);
        await task;
        Assert.Equal(ApartmentState.MTA, apartment);
    }

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

    [Fact]
    public void ToastTextComposer_UsesLegacyBindingWhenGenericBindingIsMissing()
    {
        var result = ToastTextComposer.Compose(
            preferredValues: null,
            fallbackBindings:
            [
                ["微信", "张三：稍后见"]
            ]);

        Assert.Equal("微信", result.Title);
        Assert.Equal("张三：稍后见", result.Body);
    }
}
