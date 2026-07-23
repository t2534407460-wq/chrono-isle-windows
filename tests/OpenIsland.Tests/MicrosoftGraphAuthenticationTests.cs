using OpenIsland.App.Services.Sync;

namespace OpenIsland.Tests;

public sealed class MicrosoftGraphAuthenticationTests
{
    [Fact]
    public async Task Invalid_client_id_fails_locally_without_starting_an_interactive_sign_in()
    {
        var service = new MicrosoftGraphAuthenticationService();

        var result = await service.SignInAsync(new MicrosoftGraphSignInOptions("not-a-client-id"));

        Assert.False(result.Succeeded);
        Assert.Contains("Client ID", result.Error, StringComparison.Ordinal);
    }
}
