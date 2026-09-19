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

    [Theory]
    [InlineData(401, "configuration", 1)]
    [InlineData(503, "service", 2)]
    [InlineData(429, "rate_limit", 2)]
    public async Task Transport_errors_are_bounded_and_do_not_expose_provider_body(int status, string code, int calls)
    {
        var handler = new ResponseHandler(() => new((System.Net.HttpStatusCode)status)
            { Content = new StringContent("private-provider-body") });
        var chat = new OpenAiChatService(new HttpClient(handler));
        var error = await Assert.ThrowsAsync<ChronoIsle.App.Services.Commanding.AssistantModelException>(() =>
            chat.Complete(ProviderSettings.Default with { ApiKey = "test-key" }, [new("user", "test")], true));
        Assert.Equal(code, error.Code);
        Assert.Equal(calls, handler.Calls);
        Assert.DoesNotContain("private-provider-body", error.Message);
    }

    [Theory]
    [InlineData("{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"partial\"}}]}", "incomplete")]
    [InlineData("{\"choices\":[{\"message\":{\"refusal\":\"reason\",\"content\":null}}]}", "refused")]
    [InlineData("{\"choices\":[]}", "empty")]
    [InlineData("{\"choices\":{}}", "response")]
    public async Task Incomplete_or_malformed_response_is_not_accepted(string body, string code)
    {
        var handler = new ResponseHandler(() => new(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
        var chat = new OpenAiChatService(new HttpClient(handler));
        var error = await Assert.ThrowsAsync<ChronoIsle.App.Services.Commanding.AssistantModelException>(() =>
            chat.Complete(ProviderSettings.Default with { ApiKey = "test-key" }, [new("user", "test")], true));
        Assert.Equal(code, error.Code);
        Assert.Equal(1, handler.Calls);
    }

    sealed class ResponseHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(response()); }
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
