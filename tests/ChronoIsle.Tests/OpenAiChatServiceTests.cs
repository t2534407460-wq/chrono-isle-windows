using ChronoIsle.App;
using ChronoIsle.App.Services;
using System.Net.Http;

namespace ChronoIsle.Tests;

public sealed class OpenAiChatServiceTests
{
    [Fact]
    public async Task MissingApiKey_FailsBeforeAnyNetworkRequest()
    {
        var chat = new OpenAiChatService();
        var provider = ProviderSettings.Default with { ApiKey = "" };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            chat.Complete(provider, [new ModelMessage("user", "test")]));

        Assert.Contains("API Key", error.Message);
    }

    [Fact]
    public async Task Complete_cancels_the_http_request_when_the_caller_cancels()
    {
        var handler = new BlockingHandler();
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var chat = new OpenAiChatService(http);
        using var cancellation = new CancellationTokenSource();
        var provider = ProviderSettings.Default with { ApiKey = "test-key" };

        var request = chat.Complete(provider, [new ModelMessage("user", "test")], false, cancellation.Token);
        var receivedToken = await handler.RequestCancellation.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.True(receivedToken.IsCancellationRequested);
    }

    sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource<CancellationToken> RequestCancellation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCancellation.TrySetResult(cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage();
        }
    }
}
