using System.Net;
using System.Text;
using Cylt.Core;

namespace Cylt.Core.Tests;

public sealed class BoundaryTests
{
    [Theory]
    [InlineData("http://example.com/api/yggdrasil")]
    [InlineData("https://user:password@example.com/")]
    [InlineData("https://example.com/?token=private")]
    [InlineData("https://example.com/#fragment")]
    [InlineData("file:///C:/secret")]
    public void ThirdPartyRejectsUnsafeEndpoints(string url) => Assert.Throws<UserFacingException>(() => AuthenticationService.ValidateServer(url));

    [Fact]
    public void EndpointKeepsProviderBasePath() => Assert.Equal("https://example.com/api/yggdrasil/authserver/authenticate",
        new Uri(AuthenticationService.ValidateServer("https://example.com/api/yggdrasil"), "authserver/authenticate").AbsoluteUri);

    [Fact]
    public void OfflineIdentityIsStableAndValidated()
    {
        var first = LauncherAccount.Offline("TestPlayer");
        Assert.Equal(first.Uuid, LauncherAccount.Offline("TestPlayer").Uuid);
        Assert.NotEqual(first.Uuid, LauncherAccount.Offline("OtherPlayer").Uuid);
        Assert.True(first.ToSession().CheckIsValid());
        Assert.Throws<UserFacingException>(() => LauncherAccount.Offline("ab"));
        Assert.Throws<UserFacingException>(() => LauncherAccount.Offline("name with space"));
    }

    [Fact]
    public void SecretStoreDoesNotWritePlaintext()
    {
        var root = Path.Combine(Path.GetTempPath(), "cylt-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalStore(root);
            const string fake = "synthetic-test-token-not-a-real-secret";
            store.SaveSecret("account", fake);
            Assert.Equal(fake, store.LoadSecret("account"));
            Assert.DoesNotContain(fake, Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root, "account.secrets"))));
            store.ForgetSecret("account");
            Assert.Null(store.LoadSecret("account"));
            Assert.Throws<ArgumentException>(() => store.LoadSecret("../escape"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CurseForgeMissingKeyDoesNotMakeNetworkRequest()
    {
        using var http = new HttpClient(new FakeHandler((Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>)((_, _) => throw new InvalidOperationException("Must not send"))));
        var error = await Assert.ThrowsAsync<UserFacingException>(() => new ResourceService(http).SearchAsync("test", true, null, default));
        Assert.Contains("尚未配置", error.Message);
    }

    [Fact]
    public async Task ResourceKeyOnlyGoesToCurseForge()
    {
        using var http = new HttpClient(new FakeHandler((req, _) =>
        {
            if (req.RequestUri!.Host == "api.curseforge.com")
            {
                Assert.Equal("synthetic", req.Headers.GetValues("x-api-key").Single());
                return Reply("{\"data\":[]}");
            }
            Assert.Equal("api.modrinth.com", req.RequestUri.Host);
            Assert.False(req.Headers.Contains("x-api-key"));
            return Reply("{\"hits\":[]}");
        }));
        var service = new ResourceService(http);
        await service.SearchAsync("a&b", true, "synthetic", default);
        await service.SearchAsync("a&b", false, "synthetic", default);
    }

    [Fact]
    public async Task InvalidAppRegistrationIsNotReportedAsIpFailure()
    {
        using var http = new HttpClient(new FakeHandler((req, _) => req.RequestUri!.Host switch
        {
            "login.microsoftonline.com" => Reply("{\"access_token\":\"fake-oauth\",\"refresh_token\":\"fake-refresh\"}"),
            "user.auth.xboxlive.com" => Reply("{\"Token\":\"fake-xbl\"}"),
            "xsts.auth.xboxlive.com" => Reply("{\"Token\":\"fake-xsts\",\"DisplayClaims\":{\"xui\":[{\"uhs\":\"123\"}]}}"),
            _ => Reply("{\"error\":\"Invalid app registration, see https://aka.ms/AppRegInfo for more information\"}", HttpStatusCode.Forbidden)
        }));
        var error = await Assert.ThrowsAsync<UserFacingException>(() => new AuthenticationService(http).RefreshMicrosoftAsync(
            new LauncherAccount { RefreshToken = "fake" }, Guid.NewGuid().ToString(), new Progress<WorkProgress>(), default));
        Assert.Contains("尚未获准", error.Message);
        Assert.DoesNotContain("IP", error.Message);
        Assert.DoesNotContain("fake", error.Message);
    }

    [Fact]
    public async Task MicrosoftSessionRequiresOwnership()
    {
        using var http = new HttpClient(new FakeHandler((req, _) => req.RequestUri!.Host switch
        {
            "login.microsoftonline.com" => Reply("{\"access_token\":\"fake-oauth\",\"refresh_token\":\"fake-refresh\"}"),
            "user.auth.xboxlive.com" => Reply("{\"Token\":\"fake-xbl\"}"),
            "xsts.auth.xboxlive.com" => Reply("{\"Token\":\"fake-xsts\",\"DisplayClaims\":{\"xui\":[{\"uhs\":\"123\"}]}}"),
            _ when req.RequestUri.AbsolutePath.Contains("login_with_xbox") => Reply("{\"access_token\":\"fake-mc\",\"expires_in\":3600}"),
            _ when req.RequestUri.AbsolutePath.Contains("entitlements") => Reply("{\"items\":[]}"),
            _ => throw new InvalidOperationException("Must not request a profile without ownership")
        }));
        var error = await Assert.ThrowsAsync<UserFacingException>(() => new AuthenticationService(http).RefreshMicrosoftAsync(
            new LauncherAccount { RefreshToken = "fake" }, Guid.NewGuid().ToString(), new Progress<WorkProgress>(), default));
        Assert.Contains("没有可用", error.Message);
    }

    [Fact]
    public async Task ThirdPartyAuthenticationUsesOnlyConfiguredServiceAndExplicitProfile()
    {
        using var http = new HttpClient(new FakeHandler(async (req, ct) =>
        {
            Assert.Equal("auth.example.com", req.RequestUri!.Host);
            var body = await req.Content!.ReadAsStringAsync(ct);
            if (req.RequestUri.AbsolutePath.EndsWith("authenticate"))
                return Reply("{\"accessToken\":\"fake-token\",\"clientToken\":\"fake-client\",\"availableProfiles\":[{\"id\":\"11111111111111111111111111111111\",\"name\":\"One\"},{\"id\":\"22222222222222222222222222222222\",\"name\":\"Two\"}]}");
            Assert.Contains("22222222222222222222222222222222", body);
            Assert.DoesNotContain("fake-password", body);
            return Reply("{\"accessToken\":\"refreshed-fake\"}");
        }));
        var service = new AuthenticationService(http);
        var login = await service.ThirdPartyLoginAsync("https://auth.example.com/api/yggdrasil", "test", "fake-password", default);
        var account = await service.SelectThirdPartyAsync(login, login.Profiles[1], default);
        Assert.Equal("Two", account.Name);
        Assert.Equal("refreshed-fake", account.AccessToken);
    }

    private static HttpResponseMessage Reply(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task InstalledOfflineGameBuildsWithoutNetwork()
    {
        var root = Path.Combine(Path.GetTempPath(), "cylt-offline-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "versions", "fixture"));
            File.WriteAllText(Path.Combine(root, "versions", "fixture", "fixture.json"),
                "{\"id\":\"fixture\",\"type\":\"release\",\"mainClass\":\"Example\",\"minecraftArguments\":\"--username ${auth_player_name} --uuid ${auth_uuid}\",\"libraries\":[]}");
            using var http = new HttpClient(new FakeHandler((Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>)((_, _) => throw new InvalidOperationException("Offline launch must not contact a server"))));
            var account = LauncherAccount.Offline("CyltTest");
            using var process = await new GameService(http).BuildProcessAsync(new LauncherSettings { GameDirectory = root, JavaPath = Environment.ProcessPath! },
                "fixture", account, false, new Progress<WorkProgress>(), default);
            Assert.Contains("CyltTest", process.StartInfo.Arguments);
            Assert.Contains(account.Uuid, process.StartInfo.Arguments);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send;
        public FakeHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler) => send = (r, c) => Task.FromResult(handler(r, c));
        public FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) => send = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
