using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChronoIsle.App.Services.Domain;

public enum NamingKind
{
    Variable,
    Function,
    Type,
    DatabaseTable,
    DatabaseColumn,
    FileOrDirectory,
    Constant,
    CharacterOrGeneral
}

public enum NamingFormatKind
{
    Phrase,
    Lowercase,
    Uppercase,
    SnakeCase,
    UpperSnakeCase,
    CamelCase,
    PascalCase,
    KebabCase
}

public sealed record NamingFormatValue(
    NamingFormatKind Kind,
    string Label,
    string Value,
    bool IsRecommended);

public sealed record NamingCandidate(
    IReadOnlyList<string> Words,
    string Description,
    IReadOnlyList<NamingFormatValue> Formats)
{
    public string Phrase => string.Join(' ', Words);
}

public sealed record NamingResult(IReadOnlyList<NamingCandidate> Candidates);

public sealed class NamingServiceException(string message) : Exception(message);

public static class NamingFormatter
{
    public static IReadOnlyList<NamingFormatValue> Format(
        NamingKind kind,
        IEnumerable<string> sourceWords)
    {
        var words = sourceWords.Select(word => word.ToLowerInvariant()).ToArray();
        if (words.Length == 0) throw new ArgumentException("至少需要一个英文词元。", nameof(sourceWords));

        var recommended = RecommendedFormat(kind);
        var prefix = kind == NamingKind.DatabaseTable ? "TBLCUS" : string.Empty;
        var phrase = WithPrefix(prefix, " ", string.Join(' ', words));
        var lowercase = WithPrefix(prefix, string.Empty, string.Concat(words));
        var snakeCase = WithPrefix(prefix, "_", string.Join('_', words));
        var kebabCase = WithPrefix(prefix, "-", string.Join('-', words));
        var pascalCase = string.Concat(words.Select(Capitalize));
        var camelCase = prefix.Length == 0
            ? words[0] + string.Concat(words.Skip(1).Select(Capitalize))
            : prefix + pascalCase;
        pascalCase = WithPrefix(prefix, string.Empty, pascalCase);

        return
        [
            Value(NamingFormatKind.Phrase, "英文短语", phrase, recommended),
            Value(NamingFormatKind.Lowercase, "全小写", lowercase, recommended),
            Value(NamingFormatKind.Uppercase, "全大写", lowercase.ToUpperInvariant(), recommended),
            Value(NamingFormatKind.SnakeCase, "下划线", snakeCase, recommended),
            Value(NamingFormatKind.UpperSnakeCase, "大写下划线", snakeCase.ToUpperInvariant(), recommended),
            Value(NamingFormatKind.CamelCase, "小驼峰", camelCase, recommended),
            Value(NamingFormatKind.PascalCase, "大驼峰", pascalCase, recommended),
            Value(NamingFormatKind.KebabCase, "短横线", kebabCase, recommended)
        ];
    }

    public static NamingFormatKind RecommendedFormat(NamingKind kind) => kind switch
    {
        NamingKind.Variable or NamingKind.Function => NamingFormatKind.CamelCase,
        NamingKind.Type or NamingKind.CharacterOrGeneral => NamingFormatKind.PascalCase,
        NamingKind.DatabaseTable or NamingKind.DatabaseColumn => NamingFormatKind.SnakeCase,
        NamingKind.FileOrDirectory => NamingFormatKind.KebabCase,
        NamingKind.Constant => NamingFormatKind.UpperSnakeCase,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static NamingFormatValue RecommendedName(
        NamingKind kind,
        IEnumerable<string> sourceWords)
    {
        var recommended = Format(kind, sourceWords)
            .Single(format => format.IsRecommended);

        return kind == NamingKind.DatabaseTable
            ? recommended with { Label = "建议表名" }
            : recommended with { Label = $"建议{recommended.Label}" };
    }

    static string WithPrefix(string prefix, string separator, string value) =>
        prefix.Length == 0 ? value : $"{prefix}{separator}{value}";

    static NamingFormatValue Value(
        NamingFormatKind kind,
        string label,
        string value,
        NamingFormatKind recommended) =>
        new(kind, label, value, kind == recommended);

    static string Capitalize(string word) =>
        char.ToUpper(word[0], CultureInfo.InvariantCulture) + word[1..];
}

public sealed class NamingSuggestionService(
    IChatCompletionClient chat,
    ProviderSettingsService providerSettings)
{
    const int CandidateCount = 3;
    const int RequestTimeoutSeconds = 30;
    static readonly Regex WordPattern = new(
        "^[a-z][a-z0-9]*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    const string SystemPrompt = """
        你是专业的软件命名助手。根据用户给出的中文含义和标识符类型，推荐最自然、准确、简洁的英文命名词元。
        只返回 JSON 对象，不要 Markdown、代码围栏或额外文字。
        JSON 格式必须为：
        {"candidates":[{"words":["user","profile"],"description":"用户资料"}]}

        规则：
        1. candidates 必须恰好有 3 项，最推荐的排在最前，三项的语义或措辞应有实际差异。
        2. words 必须包含 1 到 4 个小写 ASCII 英文词元；每个词元以字母开头，之后只能包含小写字母或数字。
        3. description 使用简短中文解释该候选的语义或适用侧重，不超过 40 个汉字。
        4. 函数/方法优先使用动词短语；变量、类、表、字段和常量优先使用清晰的名词短语。
        5. 不要输出大小写、下划线、驼峰或短横线变体，客户端会在本地生成。
        6. 不要无依据添加 I 前缀、类型后缀或单复数变化。
        """;

    public async Task<NamingResult> GenerateAsync(
        NamingKind kind,
        string chineseMeaning,
        CancellationToken cancellationToken = default)
    {
        var meaning = chineseMeaning.Trim();
        if (meaning.Length == 0) throw new ArgumentException("请输入想表达的中文含义。", nameof(chineseMeaning));
        if (meaning.Length > 500) throw new ArgumentException("中文含义不能超过 500 个字符。", nameof(chineseMeaning));

        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestCancellation.CancelAfter(TimeSpan.FromSeconds(RequestTimeoutSeconds));
        string response;
        try
        {
            response = await chat.Complete(
                providerSettings.Load(),
                [
                    new ModelMessage("system", SystemPrompt),
                    new ModelMessage("user", $"名称类型：{KindLabel(kind)}\n中文含义：{meaning}")
                ],
                jsonObject: true,
                requestCancellation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new NamingServiceException("生成命名超时，请检查网络和模型设置后重试。");
        }

        return Parse(kind, response);
    }

    public static NamingResult Parse(NamingKind kind, string response)
    {
        try
        {
            using var document = JsonDocument.Parse(response);
            if (!document.RootElement.TryGetProperty("candidates", out var candidatesElement) ||
                candidatesElement.ValueKind != JsonValueKind.Array ||
                candidatesElement.GetArrayLength() != CandidateCount)
                throw InvalidResponse();

            var candidates = new List<NamingCandidate>(CandidateCount);
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var candidateElement in candidatesElement.EnumerateArray())
            {
                if (!candidateElement.TryGetProperty("words", out var wordsElement) ||
                    wordsElement.ValueKind != JsonValueKind.Array ||
                    wordsElement.GetArrayLength() is < 1 or > 4)
                    throw InvalidResponse();

                var words = wordsElement.EnumerateArray()
                    .Select(element => element.ValueKind == JsonValueKind.String ? element.GetString() : null)
                    .ToArray();
                if (words.Any(word => string.IsNullOrWhiteSpace(word) || !WordPattern.IsMatch(word!)))
                    throw InvalidResponse();

                if (!candidateElement.TryGetProperty("description", out var descriptionElement) ||
                    descriptionElement.ValueKind != JsonValueKind.String)
                    throw InvalidResponse();
                var description = descriptionElement.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(description) || description.Length > 80)
                    throw InvalidResponse();

                var safeWords = words.Select(word => word!).ToArray();
                if (!unique.Add(string.Join('_', safeWords))) throw InvalidResponse();
                candidates.Add(new NamingCandidate(
                    safeWords,
                    description,
                    NamingFormatter.Format(kind, safeWords)));
            }
            return new NamingResult(candidates);
        }
        catch (NamingServiceException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw InvalidResponse();
        }
        catch (InvalidOperationException)
        {
            throw InvalidResponse();
        }
    }

    public static string KindLabel(NamingKind kind) => kind switch
    {
        NamingKind.Variable => "变量",
        NamingKind.Function => "函数 / 方法",
        NamingKind.Type => "类 / 接口",
        NamingKind.DatabaseTable => "数据库表",
        NamingKind.DatabaseColumn => "数据库字段 / 列",
        NamingKind.FileOrDirectory => "文件 / 目录",
        NamingKind.Constant => "常量",
        NamingKind.CharacterOrGeneral => "角色 / 通用名称",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    static NamingServiceException InvalidResponse() =>
        new("模型返回的命名格式无法识别，请重试。");
}
