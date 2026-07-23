using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace OpenIsland.App.Services;

public interface IChatCompletionClient
{
    Task<string> Reply(ProviderSettings provider, IEnumerable<ChatMessage> history, string input);
    Task<string> Complete(ProviderSettings provider, IEnumerable<ModelMessage> messages, bool jsonObject = false);
    Task Test(ProviderSettings provider);
}

public sealed class OpenAiChatService : IChatCompletionClient
{
    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(90) };

    public Task<string> Reply(ProviderSettings provider, IEnumerable<ChatMessage> history, string input) =>
        Complete(provider, history.Select(x => new ModelMessage(x.Role, x.Content)).Append(new ModelMessage("user", input)));

    public async Task<string> Complete(ProviderSettings provider, IEnumerable<ModelMessage> messages, bool jsonObject = false)
    {
        if (string.IsNullOrWhiteSpace(provider.ApiKey))
            throw new InvalidOperationException("请先在设置中填写 API Key。");

        var payload = new Dictionary<string, object?>
        {
            ["model"] = provider.Model,
            ["messages"] = messages.Select(x => new { role = x.Role, content = x.Content }),
            ["stream"] = false
        };
        if (jsonObject) payload["response_format"] = new { type = "json_object" };
        using var request = new HttpRequestMessage(HttpMethod.Post, provider.BaseUrl.TrimEnd('/') + "/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"模型请求失败：{(int)response.StatusCode} {body}");

        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    public Task Test(ProviderSettings provider) => Complete(provider, [new ModelMessage("user", "请只回复 OK")]);
}
