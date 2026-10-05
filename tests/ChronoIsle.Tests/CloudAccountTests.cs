using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using ChronoIsle.App.Services.Sync;

namespace ChronoIsle.Tests;

public sealed class CloudAccountTests
{
    [Fact]
    public async Task CredentialsAreProtectedAndDatasetBindingSurvivesLogout()
    {
        var directory = Path.Combine(Path.GetTempPath(), "island-account-" + Guid.NewGuid().ToString("N"));
        try
        {
            var a = Guid.NewGuid(); var b = Guid.NewGuid(); var handler = new Handler(a);
            using var client = new CloudAccountClient(directory, handler); await client.LoginAsync("A@Gmail.com", "private-password-do-not-store");
            Assert.Equal(a, client.Account!.UserId);
            Assert.DoesNotContain("private-password", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(directory, "session.dpapi"))));
            Assert.DoesNotContain("refresh-test-secret", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(directory, "session.dpapi"))));
            await client.LogoutAsync(); Assert.Null(client.Account); Assert.True(File.Exists(Path.Combine(directory, "dataset-owner.dpapi")));
            handler.User = b;
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.LoginAsync("b@gmail.com", "other-password"));
            Assert.Null(client.Account); Assert.Equal(2, handler.Revocations);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task ConcurrentAccessRequestsRotateRefreshTokenOnce()
    {
        var directory = Path.Combine(Path.GetTempPath(), "island-account-" + Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new Handler(Guid.NewGuid()) { InitialLifetime = 1 };
            using var client = new CloudAccountClient(directory, handler); await client.LoginAsync("a@gmail.com", "a-long-enough-password");
            var tokens = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => client.AccessTokenAsync()));
            Assert.All(tokens, t => Assert.Equal("access-test-refreshed", t)); Assert.Equal(1, handler.Refreshes);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task RedirectIsRejectedAndLoginInformationIsNotSentToAnotherOrigin()
    {
        var directory = Path.Combine(Path.GetTempPath(), "island-account-" + Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new Handler(Guid.NewGuid()) { Redirect = true }; using var client = new CloudAccountClient(directory, handler);
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.LoginAsync("a@gmail.com", "a-long-enough-password")); Assert.Equal(1, handler.Requests);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    sealed class Handler(Guid user) : HttpMessageHandler
    {
        public Guid User { get; set; } = user;
        public int InitialLifetime { get; set; } = 900;
        public bool Redirect { get; set; }
        public int Refreshes, Revocations, Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++; Assert.Equal("zhuisu.leadjet.com.cn", request.RequestUri!.Host); Assert.Equal("https", request.RequestUri.Scheme);
            if (Redirect) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://invalid.test/") } });
            if (request.RequestUri.AbsolutePath.EndsWith("revoke")) { Revocations++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); }
            var refresh = request.RequestUri.AbsolutePath.EndsWith("refresh"); if (refresh) Refreshes++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new CloudTokens(refresh ? "access-test-refreshed" : "access-test", "refresh-test-secret", refresh ? 900 : InitialLifetime, User)) });
        }
    }
}
