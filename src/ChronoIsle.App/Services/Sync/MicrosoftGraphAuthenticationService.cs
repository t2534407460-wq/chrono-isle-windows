using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;

namespace ChronoIsle.App.Services.Sync;

public enum GraphAuthenticationMode { WindowsBroker, SystemBrowser }

public sealed record MicrosoftGraphSignInOptions(string ClientId, string? TenantId = null)
{
    public string AuthorityTenant => string.IsNullOrWhiteSpace(TenantId) ? "common" : TenantId.Trim();
}

public sealed record MicrosoftGraphSignedInAccount(string HomeAccountId, string Username, GraphAuthenticationMode Mode);
public sealed record MicrosoftGraphAuthenticationResult(MicrosoftGraphSignedInAccount? Account, string? Error)
{
    public bool Succeeded => Account is not null;
}

public interface IMicrosoftGraphAuthenticationService
{
    Task<MicrosoftGraphAuthenticationResult> SignInAsync(MicrosoftGraphSignInOptions options,
        IntPtr parentWindowHandle = default, CancellationToken cancellationToken = default);
    Task<string?> AcquireAccessTokenAsync(CancellationToken cancellationToken = default);
    Task SignOutAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns only the delegated token hand-off. It tries Windows WAM first and falls back to the system browser.
/// A caller must provide its own public-client app registration; this application never ships a secret.
/// </summary>
public sealed class MicrosoftGraphAuthenticationService : IMicrosoftGraphAuthenticationService
{
    static readonly string[] Scopes = ["Tasks.ReadWrite", "Calendars.ReadWrite", "offline_access"];
    IPublicClientApplication? client;
    IAccount? account;

    public async Task<MicrosoftGraphAuthenticationResult> SignInAsync(MicrosoftGraphSignInOptions options,
        IntPtr parentWindowHandle = default, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(options.ClientId, out _))
            return new(null, "请输入 Azure 应用注册的有效 Client ID；ChronoIsle 不保存或提供应用密钥。");

        try
        {
            client = BuildWamClient(options);
            var result = await AcquireInteractiveAsync(client, parentWindowHandle, cancellationToken);
            account = result.Account;
            return new(new(account.HomeAccountId.Identifier, account.Username, GraphAuthenticationMode.WindowsBroker), null);
        }
        catch (Exception wamFailure) when (IsInteractiveFailure(wamFailure))
        {
            try
            {
                client = BuildBrowserClient(options);
                var result = await AcquireInteractiveAsync(client, parentWindowHandle, cancellationToken);
                account = result.Account;
                return new(new(account.HomeAccountId.Identifier, account.Username, GraphAuthenticationMode.SystemBrowser), null);
            }
            catch (Exception browserFailure) when (IsInteractiveFailure(browserFailure))
            {
                return new(null, $"Microsoft 登录未完成：{browserFailure.Message}");
            }
        }
    }

    public async Task<string?> AcquireAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (client is null || account is null) return null;
        try
        {
            return (await client.AcquireTokenSilent(Scopes, account).ExecuteAsync(cancellationToken)).AccessToken;
        }
        catch (MsalUiRequiredException) { return null; }
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        if (client is not null && account is not null) await client.RemoveAsync(account);
        account = null;
        client = null;
    }

    static IPublicClientApplication BuildWamClient(MicrosoftGraphSignInOptions options) =>
        PublicClientApplicationBuilder.Create(options.ClientId.Trim())
            .WithAuthority(AzureCloudInstance.AzurePublic, options.AuthorityTenant)
            .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows))
            .Build();

    static IPublicClientApplication BuildBrowserClient(MicrosoftGraphSignInOptions options) =>
        PublicClientApplicationBuilder.Create(options.ClientId.Trim())
            .WithAuthority(AzureCloudInstance.AzurePublic, options.AuthorityTenant)
            .WithRedirectUri("http://localhost")
            .Build();

    static Task<AuthenticationResult> AcquireInteractiveAsync(IPublicClientApplication application,
        IntPtr parentWindowHandle, CancellationToken cancellationToken) =>
        application.AcquireTokenInteractive(Scopes)
            .WithPrompt(Prompt.SelectAccount)
            .WithParentActivityOrWindow(parentWindowHandle)
            .ExecuteAsync(cancellationToken);

    static bool IsInteractiveFailure(Exception exception) => exception is MsalException or PlatformNotSupportedException;
}
