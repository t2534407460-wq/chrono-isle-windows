using ChronoIsle.App;
using ChronoIsle.App.Services;

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
}
