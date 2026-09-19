using System.Net;
using ChronoIsle.App.Services.Commanding;
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
    Task<string> Complete(
        ProviderSettings provider,
        IEnumerable<ModelMessage> messages,
        bool jsonObject,
        CancellationToken cancellationToken) =>
        Complete(provider, messages, jsonObject).WaitAsync(cancellationToken);
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

    public Task<string> Complete(ProviderSettings provider, IEnumerable<ModelMessage> messages, bool jsonObject = false) =>
        Complete(provider, messages, jsonObject, CancellationToken.None);

    public async Task<string> Complete(
        ProviderSettings provider,
        IEnumerable<ModelMessage> messages,
        bool jsonObject,
        CancellationToken cancellationToken)
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
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, provider.BaseUrl.TrimEnd('/') + "/chat/completions")
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
                using var response = await http.SendAsync(request, deadline.Token);
                if (attempt == 0 && (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500))
                {
                    var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(500);
                    await Task.Delay(delay > TimeSpan.FromSeconds(3) ? TimeSpan.FromSeconds(3) : delay < TimeSpan.Zero ? TimeSpan.Zero : delay, deadline.Token);
                    continue;
                }
                if (!response.IsSuccessStatusCode) throw ResponseFailure(response.StatusCode);
                var body = await response.Content.ReadAsStringAsync(deadline.Token);
                using var json = JsonDocument.Parse(body);
                if (!json.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                    throw new AssistantModelException("empty", "模型没有返回内容，请重试。");
                var choice = choices[0];
                if (choice.TryGetProperty("finish_reason", out var finish) && finish.GetString() is "length" or "content_filter")
                    throw new AssistantModelException("incomplete", "模型回复不完整，任务尚未执行，请重试。");
                var message = choice.GetProperty("message");
                if (message.TryGetProperty("refusal", out var refusal) && refusal.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(refusal.GetString()))
                    throw new AssistantModelException("refused", "模型未能处理这条请求，任务尚未执行。");
                var content = message.TryGetProperty("content", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                if (string.IsNullOrWhiteSpace(content)) throw new AssistantModelException("empty", "模型没有返回内容，请重试。");
                return content;
            }
            catch (HttpRequestException) when (attempt == 0)
            {
                await Task.Delay(500, deadline.Token);
            }
            catch (HttpRequestException)
            {
                throw new AssistantModelException("network", "连接模型服务失败，请检查网络后重试。");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new AssistantModelException("timeout", "模型响应超时，输入已保留，可以重试。");
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException && e is not AssistantModelException)
            {
                throw new AssistantModelException("response", "模型服务返回了无法读取的内容，请重试。");
            }
        }
        throw new AssistantModelException("network", "模型服务暂时不可用，请稍后重试。");
    }

    static AssistantModelException ResponseFailure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new("configuration", "模型服务认证失败，请检查设置中的 API Key 和访问权限。"),
        HttpStatusCode.TooManyRequests => new("rate_limit", "模型服务繁忙，请稍后重试。"),
        HttpStatusCode.BadRequest or HttpStatusCode.NotFound => new("configuration", "模型请求配置不兼容，请检查服务地址和模型名称。"),
        _ => new("service", "模型服务暂时不可用，请稍后重试。")
    };

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
            throw ResponseFailure(response.StatusCode);
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
                throw new AssistantModelException("stream", "模型回复中断，请重试。");
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
