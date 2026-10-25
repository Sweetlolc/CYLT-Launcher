using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Cylt.Core;

public sealed class AuthenticationService(HttpClient http)
{
    private const string LoginBase = "https://login.microsoftonline.com/consumers/oauth2/v2.0/";
    private const string Scope = "XboxLive.signin offline_access";

    public async Task<LauncherAccount> MicrosoftLoginAsync(string clientId, IProgress<DeviceCodeInfo> prompt,
        IProgress<WorkProgress> progress, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(clientId, out _)) throw new UserFacingException("请先在设置中填写有效的微软应用 Client ID。");
        var device = await FormAsync("devicecode", new() { ["client_id"] = clientId, ["scope"] = Scope }, cancellationToken);
        prompt.Report(new(Text(device, "user_code"), Text(device, "verification_uri")));
        var expires = DateTimeOffset.UtcNow.AddSeconds(device["expires_in"]?.GetValue<int>() ?? 900);
        var interval = Math.Max(5, device["interval"]?.GetValue<int>() ?? 5);
        while (DateTimeOffset.UtcNow < expires)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Post, LoginBase + "token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["client_id"] = clientId, ["device_code"] = Text(device, "device_code")
                })
            };
            using var response = await http.SendAsync(request, cancellationToken);
            var data = await ParseAsync(response, cancellationToken);
            if (response.IsSuccessStatusCode)
                return await CompleteMicrosoftAsync(Text(data, "access_token"), Text(data, "refresh_token"), progress, cancellationToken);
            switch (Text(data, "error"))
            {
                case "authorization_pending": continue;
                case "slow_down": interval += 5; continue;
                case "access_denied": throw new UserFacingException("微软登录已取消授权。");
                case "expired_token": throw new UserFacingException("登录代码已过期，请重新登录。");
                default: throw new UserFacingException("微软登录未完成，请检查应用配置或重新获取登录代码。");
            }
        }
        throw new UserFacingException("登录代码已过期，请重新登录。");
    }

    public async Task<LauncherAccount> RefreshMicrosoftAsync(LauncherAccount account, string clientId,
        IProgress<WorkProgress> progress, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(account.RefreshToken)) throw new UserFacingException("账号已过期，请重新登录微软账号。");
        var token = await FormAsync("token", new() { ["client_id"] = clientId, ["grant_type"] = "refresh_token",
            ["refresh_token"] = account.RefreshToken, ["scope"] = Scope }, ct);
        return await CompleteMicrosoftAsync(Text(token, "access_token"), Text(token, "refresh_token"), progress, ct);
    }

    private async Task<LauncherAccount> CompleteMicrosoftAsync(string oauthToken, string refreshToken,
        IProgress<WorkProgress> progress, CancellationToken ct)
    {
        progress.Report(new("正在验证 Xbox 账号…"));
        var xbox = await PostAsync("https://user.auth.xboxlive.com/user/authenticate", new
        {
            Properties = new { AuthMethod = "RPS", SiteName = "user.auth.xboxlive.com", RpsTicket = "d=" + oauthToken },
            RelyingParty = "http://auth.xboxlive.com", TokenType = "JWT"
        }, "Xbox 账号验证", ct);
        var xsts = await PostAsync("https://xsts.auth.xboxlive.com/xsts/authorize", new
        {
            Properties = new { SandboxId = "RETAIL", UserTokens = new[] { Text(xbox, "Token") } },
            RelyingParty = "rp://api.minecraftservices.com/", TokenType = "JWT"
        }, "Xbox 游戏权限验证", ct);
        var claims = xsts["DisplayClaims"]?["xui"]?[0];
        var hash = claims?["uhs"]?.GetValue<string>() ?? throw new UserFacingException("Xbox 未返回有效身份信息。");
        progress.Report(new("正在验证 Minecraft 游戏所有权…"));
        var token = await PostAsync("https://api.minecraftservices.com/authentication/login_with_xbox",
            new { identityToken = $"XBL3.0 x={hash};{Text(xsts, "Token")}" }, "Minecraft 登录", ct);
        var access = Text(token, "access_token");
        var owns = await GetBearerAsync("https://api.minecraftservices.com/entitlements/mcstore", access, ct);
        if (owns["items"] is not JsonArray { Count: > 0 })
            throw new UserFacingException("该微软账号没有可用的 Minecraft Java 版许可。请使用已购买游戏的账号。");
        var profile = await GetBearerAsync("https://api.minecraftservices.com/minecraft/profile", access, ct);
        return new()
        {
            Kind = AccountKind.Microsoft, Name = Text(profile, "name"), Uuid = Text(profile, "id"),
            AccessToken = access, RefreshToken = refreshToken, Xuid = claims?["xid"]?.GetValue<string>() ?? "",
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(token["expires_in"]?.GetValue<int>() ?? 3600)
        };
    }

    public static Uri ValidateServer(string text)
    {
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new UserFacingException("请输入 HTTPS 认证服务器根地址，不能含账号、密码、查询参数或片段。");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }

    public async Task<ThirdPartyResult> ThirdPartyLoginAsync(string server, string username, string password, CancellationToken ct)
    {
        var root = ValidateServer(server);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            throw new UserFacingException("请输入第三方账号和密码。");
        var clientToken = Guid.NewGuid().ToString("N");
        var result = await PostAsync(new Uri(root, "authserver/authenticate").AbsoluteUri, new
        {
            agent = new { name = "Minecraft", version = 1 }, username, password, clientToken, requestUser = true
        }, "第三方账号登录", ct);
        var profiles = (result["availableProfiles"] as JsonArray ?? [])
            .OfType<JsonObject>().Select(p => new ThirdPartyProfile(Text(p, "id"), Text(p, "name"))).ToArray();
        if (profiles.Length == 0 && result["selectedProfile"] is JsonObject profile)
            profiles = [new(Text(profile, "id"), Text(profile, "name"))];
        if (profiles.Length == 0) throw new UserFacingException("认证服务器未返回角色，请先在该服务器创建角色。");
        return new(Text(result, "accessToken"), Text(result, "clientToken"), root.AbsoluteUri, profiles);
    }

    public async Task<LauncherAccount> SelectThirdPartyAsync(ThirdPartyResult login, ThirdPartyProfile profile, CancellationToken ct)
    {
        var account = new LauncherAccount { Kind = AccountKind.ThirdParty, Name = profile.Name, Uuid = profile.Id,
            AccessToken = login.AccessToken, ClientToken = login.ClientToken, Server = login.Server };
        return await RefreshThirdPartyAsync(account, ct);
    }

    public async Task<LauncherAccount> RefreshThirdPartyAsync(LauncherAccount account, CancellationToken ct)
    {
        var root = ValidateServer(account.Server);
        var response = await PostAsync(new Uri(root, "authserver/refresh").AbsoluteUri, new
        {
            accessToken = account.AccessToken, clientToken = account.ClientToken,
            selectedProfile = new { id = account.Uuid, name = account.Name }, requestUser = true
        }, "第三方会话刷新", ct);
        return account with { AccessToken = Text(response, "accessToken"), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
    }

    private async Task<JsonObject> FormAsync(string endpoint, Dictionary<string, string> values, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, LoginBase + endpoint) { Content = new FormUrlEncodedContent(values) };
        using var response = await http.SendAsync(req, ct);
        var data = await ParseAsync(response, ct);
        if (!response.IsSuccessStatusCode) throw new UserFacingException("微软身份验证请求失败，请检查 Client ID 和公共客户端设置，或重新登录。");
        return data;
    }

    private async Task<JsonObject> PostAsync(string url, object body, string stage, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(url, body, ct);
        var data = await ParseAsync(response, ct);
        if (!response.IsSuccessStatusCode)
        {
            if (url.StartsWith("https://api.minecraftservices.com/", StringComparison.Ordinal) &&
                (Text(data, "error") + Text(data, "errorMessage")).Contains("Invalid app registration", StringComparison.OrdinalIgnoreCase))
                throw new UserFacingException("此启动器的微软应用尚未获准访问 Minecraft 服务。微软网页登录成功不代表游戏登录已完成；请等待应用接入获批。");
            if (url.StartsWith("https://xsts.auth.xboxlive.com/", StringComparison.Ordinal))
                throw new UserFacingException("Xbox 游戏权限验证失败。请检查 Xbox 个人资料、家庭设置和账号所在地区是否满足服务要求。");
            throw new UserFacingException($"{stage}失败（HTTP {(int)response.StatusCode}）。请检查账号或服务配置后重试。");
        }
        return data;
    }

    private async Task<JsonObject> GetBearerAsync(string url, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new UserFacingException($"Minecraft 所有权或角色验证失败（HTTP {(int)response.StatusCode}）。请确认已拥有 Java 版并创建游戏角色。");
        return await ParseAsync(response, ct);
    }

    private static async Task<JsonObject> ParseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject ?? new(); }
        catch (System.Text.Json.JsonException) { throw new UserFacingException("服务返回了无法识别的响应，请稍后重试。"); }
    }
    private static string Text(JsonNode data, string key) => data[key]?.GetValue<string>() ?? "";
}
