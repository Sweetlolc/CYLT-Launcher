using System.Text.Json.Nodes;

namespace Cylt.Core;

public sealed class ResourceService(HttpClient http)
{
    public async Task<IReadOnlyList<ModResult>> SearchAsync(string query, bool curseForge, string? apiKey, CancellationToken ct)
    {
        if (curseForge && string.IsNullOrWhiteSpace(apiKey))
            throw new UserFacingException("CurseForge 尚未配置 API Key。可先使用 Modrinth，或前往设置填写获批的 Key。");
        string url = curseForge
            ? "https://api.curseforge.com/v1/mods/search?gameId=432&classId=6&pageSize=20&searchFilter=" + Uri.EscapeDataString(query)
            : "https://api.modrinth.com/v2/search?limit=20&facets=%5B%5B%22project_type%3Amod%22%5D%5D&query=" + Uri.EscapeDataString(query);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("CYLTLauncher/0.1.0");
        if (curseForge) request.Headers.Add("x-api-key", apiKey!.Trim());
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new UserFacingException($"资源服务请求失败（HTTP {(int)response.StatusCode}）。" + (curseForge ? "请检查 API Key 是否获批且有效。" : "请稍后重试。"));
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
        var list = (JsonArray?)json[curseForge ? "data" : "hits"] ?? [];
        return list.OfType<JsonObject>().Select(item => curseForge
            ? new ModResult(Value(item, "name"), Value(item, "summary"), item["links"]?["websiteUrl"]?.GetValue<string>() ?? "", item["authors"]?[0]?["name"]?.GetValue<string>() ?? "")
            : new ModResult(Value(item, "title"), Value(item, "description"), "https://modrinth.com/mod/" + Uri.EscapeDataString(Value(item, "slug")), Value(item, "author"))).ToArray();
    }
    private static string Value(JsonNode node, string key) => node[key]?.GetValue<string>() ?? "";
}
