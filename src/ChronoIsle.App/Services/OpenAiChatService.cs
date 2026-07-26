using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace ChronoIsle.App.Services;

public interface IChatCompletionClient
{
    Task<string> Reply(ProviderSettings provider, IEnumerable<ChatMessage> history, string input);
    Task<string> Complete(ProviderSettings provider, IEnumerable<ModelMessage> messages, bool jsonObject = false);
    async IAsyncEnumerable<string> StreamComplete(
        ProviderSettings provider,
        IEnumerable<ModelMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return await Complete(provider, messages);
    }
    Task Test(ProviderSettings provider);
}

public sealed class OpenAiChatService : IChatCompletionClient
{
    readonly HttpClient http;

    public OpenAiChatService() : this(new HttpClient { Timeout = TimeSpan.FromSeconds(90) }) { }
    public OpenAiChatService(HttpClient http) => this.http = http ?? throw new ArgumentNullException(nameof(http));

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

    public async IAsyncEnumerable<string> StreamComplete(
        ProviderSettings provider,
        IEnumerable<ModelMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(provider.ApiKey))
            throw new InvalidOperationException("请先在设置中填写 API Key。");

        var payload = new
        {
            model = provider.Model,
            messages = messages.Select(message => new { role = message.Role, content = message.Content }),
            stream = true
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, provider.BaseUrl.TrimEnd('/') + "/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
        using var response = await http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"模型请求失败：{(int)response.StatusCode} {errorBody}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            var data = line[5..].Trim();
            if (data.Length == 0) continue;
            if (data == "[DONE]") yield break;

            using var json = JsonDocument.Parse(data);
            if (json.RootElement.TryGetProperty("error", out var error))
                throw new InvalidOperationException($"模型请求失败：{error}");
            if (!json.RootElement.TryGetProperty("choices", out var choices) ||
                choices.GetArrayLength() == 0 ||
                !choices[0].TryGetProperty("delta", out var delta) ||
                !delta.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.String)
                continue;

            var text = content.GetString();
            if (!string.IsNullOrEmpty(text)) yield return text;
        }
    }

    public Task Test(ProviderSettings provider) => Complete(provider, [new ModelMessage("user", "请只回复 OK")]);
}
