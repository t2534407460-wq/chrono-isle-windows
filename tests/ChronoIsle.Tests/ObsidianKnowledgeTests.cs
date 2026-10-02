using System.Text;
using System.Text.Json;
using ChronoIsle.App;
using ChronoIsle.App.Services;
using ChronoIsle.App.Services.Knowledge;

namespace ChronoIsle.Tests;

public sealed class ObsidianKnowledgeTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "chronoisle-knowledge-tests", Guid.NewGuid().ToString("N"));
    readonly ObsidianKnowledgeIndex index = new();
    readonly KnowledgeBaseSettingsService settings;
    readonly string vault;
    static readonly ProviderSettings Provider = new("https://unused.invalid", "fake", "test-key");

    public ObsidianKnowledgeTests()
    {
        vault = Path.Combine(directory, "本地知识库");
        Directory.CreateDirectory(vault);
        settings = new(directory);
        settings.SavePath(vault);
    }

    [Fact]
    public void Auto_detection_uses_the_only_open_vault_and_explicit_path_wins()
    {
        Directory.CreateDirectory(Path.Combine(directory, "obsidian"));
        File.WriteAllText(Path.Combine(directory, "obsidian", "obsidian.json"), JsonSerializer.Serialize(new
        {
            vaults = new { first = new { path = vault, open = true }, second = new { path = directory, open = false } }
        }));
        settings.SavePath("");
        Assert.Equal(vault, settings.ResolveVaultPath());
        settings.SavePath(directory);
        Assert.Equal(directory, settings.ResolveVaultPath());
    }

    [Fact]
    public void Ambiguous_auto_detection_requires_an_explicit_selection()
    {
        Directory.CreateDirectory(Path.Combine(directory, "obsidian"));
        File.WriteAllText(Path.Combine(directory, "obsidian", "obsidian.json"), JsonSerializer.Serialize(new
        {
            vaults = new { first = new { path = vault }, second = new { path = directory } }
        }));
        settings.SavePath("");
        Assert.Contains("多个", Assert.Throws<KnowledgeBaseException>(settings.ResolveVaultPath).Message);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    public void Corrupt_settings_do_not_silently_switch_to_another_vault(string content)
    {
        File.WriteAllText(Path.Combine(directory, "ChronoIsle", "knowledge-base.json"), content);
        Assert.Throws<KnowledgeBaseException>(settings.ResolveVaultPath);
        settings.SavePath(vault);
        Assert.Equal(vault, settings.ResolveVaultPath());
    }

    [Fact]
    public void Unchanged_offline_vault_does_not_block_saving_other_settings()
    {
        Directory.Move(vault, vault + "-offline");
        settings.SavePath(vault);
        Assert.Equal(vault, settings.LoadPath());
        Assert.Throws<KnowledgeBaseException>(settings.ResolveVaultPath);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData(@"\\server\vault")]
    public void Invalid_or_remote_roots_are_rejected(string path) => Assert.Throws<KnowledgeBaseException>(() => settings.SavePath(path));

    [Fact]
    public async Task Chinese_and_code_queries_return_original_lines_metadata_and_heading()
    {
        Write("项目/备份.md", "---\nstatus: 已修改待测试\n---\n# 备份恢复\n恢复数据库前需要停止应用。\n调用 RestoreDatabaseAsync 执行恢复。\n");
        Write("其他.md", "# 天气\n今天有阵雨。\n");
        var chinese = await index.SearchAsync(vault, "数据库如何恢复");
        var source = Assert.Single(chinese.Sources);
        Assert.Equal(4, source.StartLine);
        Assert.Equal("备份恢复", source.Heading);
        Assert.Contains("已修改待测试", source.Metadata);
        Assert.Contains("恢复数据库前需要停止应用。", source.Text);
        Assert.True(source.EndLine >= 6);
        var code = await index.SearchAsync(vault, "RestoreDatabaseAsync");
        Assert.Equal(source.RelativePath, Assert.Single(code.Sources).RelativePath);
    }

    [Fact]
    public async Task Refresh_updates_additions_edits_deletions_and_renames_without_restart()
    {
        var path = Write("备份.md", "恢复数据库前需要停止应用。");
        Assert.Contains("停止应用", Assert.Single((await index.SearchAsync(vault, "数据库恢复")).Sources).Text);
        File.WriteAllText(path, "恢复数据库现在需要先导出备份。", Encoding.UTF8);
        Assert.Contains("先导出备份", Assert.Single((await index.SearchAsync(vault, "数据库恢复")).Sources).Text);
        File.Move(path, Path.Combine(vault, "恢复流程.md"));
        Assert.Equal("恢复流程.md", Assert.Single((await index.SearchAsync(vault, "数据库恢复")).Sources).RelativePath);
        File.Delete(Path.Combine(vault, "恢复流程.md"));
        Assert.Empty((await index.SearchAsync(vault, "数据库恢复")).Sources);
    }

    [Fact]
    public async Task Unreadable_or_oversized_notes_are_removed_from_cached_results()
    {
        var path = Write("备份.md", "恢复数据库前需要停止应用。");
        await index.SearchAsync(vault, "数据库恢复");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // A changed timestamp forces a re-read of the cache entry.
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            var unavailable = await index.SearchAsync(vault, "数据库恢复");
            Assert.Empty(unavailable.Sources);
            Assert.Equal(1, unavailable.SkippedCount);
        }
        File.WriteAllText(path, new string('x', 2 * 1024 * 1024 + 1));
        var oversized = await index.SearchAsync(vault, "数据库恢复");
        Assert.Empty(oversized.Sources);
        Assert.Equal(1, oversized.SkippedCount);
    }

    [Fact]
    public async Task Hidden_metadata_trash_attachments_and_invalid_utf8_are_not_sent()
    {
        Write(".obsidian/config.md", "数据库恢复 SECRET_CONFIG");
        Write(".trash/old.md", "数据库恢复 SECRET_TRASH");
        Write(".claudian/history.md", "数据库恢复 SECRET_HISTORY");
        Write("attachment.pdf", "数据库恢复 SECRET_PDF");
        File.WriteAllBytes(Path.Combine(vault, "invalid.md"), [0xff, 0xff, 0x61]);
        Write("正文.md", "恢复数据库前需要停止应用。");
        var result = await index.SearchAsync(vault, "数据库恢复");
        Assert.Equal(1, result.NoteCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.DoesNotContain("SECRET", Assert.Single(result.Sources).Text);
    }

    [Fact]
    public async Task Switching_scope_cannot_reuse_previous_vault_notes()
    {
        Write("备份.md", "恢复数据库前需要停止应用。");
        var subdirectory = Path.Combine(vault, "空目录");
        Directory.CreateDirectory(subdirectory);
        Assert.NotEmpty((await index.SearchAsync(vault, "数据库恢复")).Sources);
        Assert.Empty((await index.SearchAsync(subdirectory, "数据库恢复")).Sources);
    }

    [Fact]
    public async Task Long_lines_are_bounded_and_sources_are_diversified()
    {
        for (var i = 0; i < 8; i++) Write($"备份{i}.md", string.Concat(Enumerable.Repeat("数据库恢复要停止应用。", 800)));
        var result = await index.SearchAsync(vault, "数据库恢复");
        Assert.Equal(6, result.Sources.Count);
        Assert.All(result.Sources, s => Assert.True(s.Text.Length <= 1800));
        Assert.All(result.Sources.GroupBy(s => s.RelativePath), g => Assert.True(g.Count() <= 2));
    }

    [Fact]
    public async Task Very_long_headings_do_not_multiply_unbounded_metadata_per_chunk()
    {
        Write("备份.md", "# 数据库恢复" + new string('a', 200000) + "\n恢复数据库前需要停止应用。");
        var result = await index.SearchAsync(vault, "数据库恢复");
        Assert.NotEmpty(result.Sources);
        Assert.All(result.Sources, source => Assert.True(source.Heading.Length <= 200));
    }

    [Fact]
    public async Task Concurrent_queries_and_cancellation_leave_the_index_usable()
    {
        Write("备份.md", "恢复数据库前需要停止应用。");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => index.SearchAsync(vault, "数据库恢复", cancellation.Token));
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => index.SearchAsync(vault, "数据库恢复")));
        Assert.All(results, r => Assert.Single(r.Sources));
    }

    [Fact]
    public async Task No_hits_missing_directory_and_missing_key_do_not_call_the_model()
    {
        var model = new FakeModel(_ => throw new Exception("Must not call the model."));
        var service = new KnowledgeQuestionService(settings, index, model);
        Assert.Contains("未检索到", (await service.AskAsync(Provider, "夸克振荡")).Reply);
        Write("备份.md", "恢复数据库前需要停止应用。");
        Assert.Contains("API Key", (await service.AskAsync(Provider with { ApiKey = "" }, "数据库恢复")).Reply);
        Directory.Move(vault, vault + "-moved");
        Assert.Contains("目录不存在", (await service.AskAsync(Provider, "数据库恢复")).Reply);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task Accepted_answer_has_app_generated_source_lines_and_verbatim_evidence()
    {
        Write("备份.md", "# 备份\n恢复数据库前需要停止应用。");
        var model = new FakeModel(ValidAnswer);
        var result = await new KnowledgeQuestionService(settings, index, model).AskAsync(Provider, "数据库恢复");
        Assert.False(result.IsFailure);
        Assert.False(result.RefreshReminders);
        Assert.Null(result.PendingAction);
        Assert.Contains("先停止应用，再恢复数据库。", result.Reply);
        Assert.DoesNotContain("原文 [S1]", result.Reply);
        Assert.Contains(new Uri(Path.Combine(vault, "备份.md")).AbsoluteUri + "#L1", result.Reply);
        Assert.Contains("第 1–2 行", result.Reply);
        Assert.DoesNotContain(vault, model.Messages!.Last().Content); // Send relative paths only.
        Assert.True(model.JsonObject);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"insufficient\":false,\"points\":[{\"text\":\"[伪造链接](file:///C:/secret.md)\",\"evidence\":[{\"sourceId\":\"S1\",\"quote\":\"恢复数据库前需要停止应用。\"}]}]}")]
    [InlineData("{\"insufficient\":false,\"points\":[{\"text\":\"UNVERIFIED_CLAIM\",\"evidence\":[{\"sourceId\":\"S999\",\"quote\":\"恢复数据库前需要停止应用。\"}]}]}")]
    [InlineData("{\"insufficient\":false,\"points\":[{\"text\":\"UNVERIFIED_CLAIM\",\"evidence\":[{\"sourceId\":\"S1\",\"quote\":\"伪造的原文不能接受\"}]}]}")]
    [InlineData("{\"insufficient\":false,\"insufficient\":false,\"points\":[]}")]
    [InlineData("{\"insufficient\":false,\"points\":[{\"text\":\"UNVERIFIED_CLAIM\",\"evidence\":[]}]}")]
    public async Task Malformed_or_fabricated_evidence_hides_all_model_claims(string reply)
    {
        Write("备份.md", "恢复数据库前需要停止应用。");
        var result = await new KnowledgeQuestionService(settings, index, new FakeModel(_ => reply)).AskAsync(Provider, "数据库恢复");
        Assert.Contains("未通过引用核对", result.Reply);
        Assert.DoesNotContain("UNVERIFIED_CLAIM", result.Reply);
        Assert.Contains("恢复数据库前需要停止应用。", result.Reply);
    }

    [Fact]
    public async Task Insufficient_evidence_is_explicit_and_cannot_become_a_positive_answer()
    {
        Write("备份.md", "恢复数据库前需要停止应用。");
        var result = await new KnowledgeQuestionService(settings, index, new FakeModel(_ => "{\"insufficient\":true,\"points\":[]}"))
            .AskAsync(Provider, "数据库恢复");
        Assert.Contains("不足以支持可靠结论", result.Reply);
        Assert.Contains("仅供核对", result.Reply);
    }

    [Fact]
    public async Task Edits_during_generation_discard_the_answer_even_with_unchanged_size_and_timestamp()
    {
        var path = Write("备份.md", "恢复数据库前需要停止应用。");
        var modified = File.GetLastWriteTimeUtc(path);
        var model = new FakeModel(messages =>
        {
            File.WriteAllText(path, "恢复数据库前需要启动应用。", new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(path, modified);
            return ValidAnswer(messages);
        });
        var result = await new KnowledgeQuestionService(settings, index, model).AskAsync(Provider, "数据库恢复");
        Assert.True(result.IsFailure);
        Assert.Contains("本次回答已丢弃", result.Reply);
        Assert.DoesNotContain("先停止应用", result.Reply);
        Assert.Contains("启动应用", Assert.Single((await index.SearchAsync(vault, "数据库恢复")).Sources).Text);
    }

    [Fact]
    public async Task Source_freshness_check_rejects_a_deleted_file()
    {
        var path = Write("备份.md", "恢复数据库前需要停止应用。");
        var result = await index.SearchAsync(vault, "数据库恢复");
        File.Delete(path);
        Assert.False(await index.SourcesUnchangedAsync(result));
    }

    [Fact]
    public async Task Cancellation_reaches_the_model_and_is_not_converted_to_a_failure_reply()
    {
        Write("备份.md", "恢复数据库前需要停止应用。");
        using var cancellation = new CancellationTokenSource();
        var model = new BlockingModel();
        var task = new KnowledgeQuestionService(settings, index, model).AskAsync(Provider, "数据库恢复", cancellation.Token);
        await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(model.Token.IsCancellationRequested);
    }

    string Write(string relative, string text)
    {
        var path = Path.Combine(vault, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    static string ValidAnswer(IReadOnlyList<ModelMessage> messages)
    {
        using var request = JsonDocument.Parse(messages.Last().Content);
        var source = request.RootElement.GetProperty("sources")[0].GetProperty("id").GetString();
        return JsonSerializer.Serialize(new { insufficient = false, points = new[] { new { text = "先停止应用，再恢复数据库。",
            evidence = new[] { new { sourceId = source, quote = "恢复数据库前需要停止应用。" } } } } });
    }

    sealed class FakeModel(Func<IReadOnlyList<ModelMessage>, string> reply) : IChatCompletionClient
    {
        public int Calls;
        public IReadOnlyList<ModelMessage>? Messages;
        public bool JsonObject;
        public Task<string> Complete(ProviderSettings provider, IEnumerable<ModelMessage> messages, bool jsonObject = false)
        { Calls++; Messages = messages.ToArray(); JsonObject = jsonObject; return Task.FromResult(reply(Messages)); }
        public Task<string> Reply(ProviderSettings provider, IEnumerable<ChatMessage> history, string input) => throw new NotSupportedException();
        public Task Test(ProviderSettings provider) => throw new NotSupportedException();
    }

    sealed class BlockingModel : IChatCompletionClient
    {
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token;
        public async Task<string> Complete(ProviderSettings provider, IEnumerable<ModelMessage> messages, bool jsonObject, CancellationToken cancellationToken)
        { Token = cancellationToken; Started.SetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); return ""; }
        public Task<string> Complete(ProviderSettings provider, IEnumerable<ModelMessage> messages, bool jsonObject = false) => throw new NotSupportedException();
        public Task<string> Reply(ProviderSettings provider, IEnumerable<ChatMessage> history, string input) => throw new NotSupportedException();
        public Task Test(ProviderSettings provider) => throw new NotSupportedException();
    }

    public void Dispose()
    {
        // Only this fixture's generated temporary directory is removed.
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
