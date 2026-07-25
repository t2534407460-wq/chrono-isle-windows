using ChronoIsle.App;
using ChronoIsle.App.Services;
using ChronoIsle.App.Services.Domain;

namespace ChronoIsle.Tests;

public sealed class NamingSuggestionServiceTests
{
    [Fact]
    public void Format_builds_all_eight_deterministic_variants()
    {
        var formats = NamingFormatter.Format(
            NamingKind.Variable,
            ["api", "response", "v2"]);

        Assert.Equal(
            [
                "api response v2",
                "apiresponsev2",
                "APIRESPONSEV2",
                "api_response_v2",
                "API_RESPONSE_V2",
                "apiResponseV2",
                "ApiResponseV2",
                "api-response-v2"
            ],
            formats.Select(format => format.Value));
        Assert.Equal(8, formats.Select(format => format.Kind).Distinct().Count());
        Assert.Single(formats, format => format.IsRecommended);
        Assert.True(formats.Single(format => format.Kind == NamingFormatKind.CamelCase).IsRecommended);
    }

    [Theory]
    [InlineData(NamingKind.Variable, NamingFormatKind.CamelCase)]
    [InlineData(NamingKind.Function, NamingFormatKind.CamelCase)]
    [InlineData(NamingKind.Type, NamingFormatKind.PascalCase)]
    [InlineData(NamingKind.DatabaseTable, NamingFormatKind.SnakeCase)]
    [InlineData(NamingKind.DatabaseColumn, NamingFormatKind.SnakeCase)]
    [InlineData(NamingKind.FileOrDirectory, NamingFormatKind.KebabCase)]
    [InlineData(NamingKind.Constant, NamingFormatKind.UpperSnakeCase)]
    [InlineData(NamingKind.CharacterOrGeneral, NamingFormatKind.PascalCase)]
    public void Recommended_format_matches_the_selected_kind(
        NamingKind kind,
        NamingFormatKind expected)
    {
        Assert.Equal(expected, NamingFormatter.RecommendedFormat(kind));
    }

    [Fact]
    public void Database_table_formats_keep_the_TBLCUS_prefix()
    {
        var formats = NamingFormatter.Format(
            NamingKind.DatabaseTable,
            ["auto", "billing", "status"]);

        Assert.Equal(
            [
                "TBLCUS auto billing status",
                "TBLCUSautobillingstatus",
                "TBLCUSAUTOBILLINGSTATUS",
                "TBLCUS_auto_billing_status",
                "TBLCUS_AUTO_BILLING_STATUS",
                "TBLCUSAutoBillingStatus",
                "TBLCUSAutoBillingStatus",
                "TBLCUS-auto-billing-status"
            ],
            formats.Select(format => format.Value));
    }

    [Fact]
    public void Database_table_recommendation_uses_TBLCUS_prefix()
    {
        var recommendation = NamingFormatter.RecommendedName(
            NamingKind.DatabaseTable,
            ["inventory", "transactions"]);

        Assert.Equal("建议表名", recommendation.Label);
        Assert.Equal("TBLCUS_inventory_transactions", recommendation.Value);
    }

    [Fact]
    public void Parse_preserves_three_ranked_candidates()
    {
        var result = NamingSuggestionService.Parse(
            NamingKind.DatabaseColumn,
            """
            {
              "candidates": [
                {"words":["last","active","time"],"description":"最后活跃时间"},
                {"words":["last","seen","at"],"description":"最后一次出现时间"},
                {"words":["recent","activity","time"],"description":"最近活动时间"}
              ]
            }
            """);

        Assert.Equal(3, result.Candidates.Count);
        Assert.Equal("last active time", result.Candidates[0].Phrase);
        Assert.Equal("last_seen_at", result.Candidates[1].Formats
            .Single(format => format.Kind == NamingFormatKind.SnakeCase).Value);
        Assert.All(result.Candidates, candidate =>
            Assert.True(candidate.Formats.Single(format => format.Kind == NamingFormatKind.SnakeCase).IsRecommended));
    }

    [Theory]
    [InlineData("""{"candidates":[]}""")]
    [InlineData("""{"candidates":[{"words":["valid"],"description":"一"},{"words":["valid"],"description":"重复"},{"words":["third"],"description":"三"}]}""")]
    [InlineData("""{"candidates":[{"words":["not-valid"],"description":"一"},{"words":["second"],"description":"二"},{"words":["third"],"description":"三"}]}""")]
    [InlineData("""{"candidates":[{"words":["valid"],"description":"一"},{"words":["second"],"description":"二"},{"words":["third"]}]}""")]
    [InlineData("""not json""")]
    public void Parse_rejects_malformed_or_unsafe_model_output(string response)
    {
        var error = Assert.Throws<NamingServiceException>(() =>
            NamingSuggestionService.Parse(NamingKind.Variable, response));

        Assert.Equal("模型返回的命名格式无法识别，请重试。", error.Message);
    }

    [Fact]
    public async Task Generate_requests_json_and_includes_kind_and_meaning()
    {
        var chat = new RecordingChatClient
        {
            Response =
                """
                {"candidates":[
                  {"words":["load","user"],"description":"加载用户"},
                  {"words":["fetch","user"],"description":"获取用户"},
                  {"words":["get","user"],"description":"读取用户"}
                ]}
                """
        };
        var service = new NamingSuggestionService(chat, new ProviderSettingsService());

        var result = await service.GenerateAsync(NamingKind.Function, "加载当前用户");

        Assert.Equal(3, result.Candidates.Count);
        Assert.True(chat.JsonObject);
        Assert.Contains(chat.Messages, message =>
            message.Role == "user" &&
            message.Content.Contains("函数 / 方法", StringComparison.Ordinal) &&
            message.Content.Contains("加载当前用户", StringComparison.Ordinal));
        Assert.Contains(chat.Messages, message =>
            message.Role == "system" &&
            message.Content.Contains("JSON", StringComparison.Ordinal));
    }

    sealed class RecordingChatClient : IChatCompletionClient
    {
        public string Response { get; init; } = "";
        public bool JsonObject { get; private set; }
        public IReadOnlyList<ModelMessage> Messages { get; private set; } = [];

        public Task<string> Reply(
            ProviderSettings provider,
            IEnumerable<ChatMessage> history,
            string input) =>
            throw new NotSupportedException();

        public Task<string> Complete(
            ProviderSettings provider,
            IEnumerable<ModelMessage> messages,
            bool jsonObject = false)
        {
            JsonObject = jsonObject;
            Messages = messages.ToArray();
            return Task.FromResult(Response);
        }

        public Task Test(ProviderSettings provider) => throw new NotSupportedException();
    }
}
