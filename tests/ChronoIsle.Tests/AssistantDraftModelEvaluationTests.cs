using ChronoIsle.App;
using ChronoIsle.App.Services;
using ChronoIsle.App.Services.Commanding;

namespace ChronoIsle.Tests;

public sealed class AssistantDraftModelEvaluationTests
{
    [ModelEvaluationFact]
    public async Task Configured_provider_understands_public_examples_without_business_writes()
    {
        var provider = new ProviderSettingsService().Load();
        Assert.False(string.IsNullOrWhiteSpace(provider.ApiKey), "No configured model credentials.");
        var interpreter = new AssistantDraftInterpreter(new OpenAiChatService());
        var examples = new (string Input, string Kind, string? Operation, string? Title)[]
        {
            ("每天的凌晨12点提醒我睡觉", "tasks", "create_reminder", "睡觉"),
            ("明早九点提醒我开会", "tasks", "create_reminder", "开会"),
            ("十分钟后提醒我喝水", "tasks", "create_reminder", "喝水"),
            ("提醒我交电费", "tasks", "create_reminder", "交电费"),
            ("添加待办买牛奶", "tasks", "create_todo", "买牛奶"),
            ("每周五下午六点提醒我写周报", "tasks", "create_reminder", "写周报"),
            ("明天14:00到15:00安排一个项目评审日程", "tasks", "create_event", "项目评审"),
            ("把买牛奶这个待办标为完成", "tasks", "complete_todo", null),
            ("删除名为买牛奶的待办", "tasks", "delete_todo", null),
            ("把项目评审改到明天15:00", "tasks", "reschedule_item", null),
            ("查看明天的日程", "query", "list_items", null),
            ("查看本周的待办", "query", "list_items", null),
            ("如何创建每天重复的提醒？", "chat", null, null),
            ("不要创建提醒，我只想知道怎样改善睡眠", "chat", null, null),
            ("添加待办买牛奶，再在明天08:00提醒我晨跑", "tasks", "create_todo", "买牛奶")
        };
        var failures = new List<string>();
        for (var i = 0; i < examples.Length; i++)
        {
            var sample = examples[i];
            var turn = new AssistantDraftTurn(Guid.NewGuid().ToString("N"), "evaluation", 1, "Understanding",
                sample.Input, new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.FromHours(8)), "Asia/Shanghai",
                DateTimeOffset.UtcNow.AddMinutes(15), [], [], new Dictionary<int, AssistantPlanCandidateBindingV2>(),
                new Dictionary<string, AssistantPlanCandidateBindingV2>());
            try
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                var result = await interpreter.UnderstandAsync(provider, sample.Input, turn, [], cancellation.Token);
                Assert.Equal(sample.Kind, result.Kind);
                if (sample.Operation is not null) Assert.Contains(result.Tasks, t => t.Operation == sample.Operation && (sample.Title is null || t.Title == sample.Title));
                if (i == 14) Assert.Equal(2, result.Tasks.Count);
                if (i == 0)
                {
                    var compiled = AssistantDraftCompiler.Compile(turn with { Tasks = result.Tasks });
                    Assert.Empty(compiled.Fields);
                    Assert.Equal(AssistantCommandName.CreateRecurringTask, Assert.Single(compiled.Commands).Command);
                }
            }
            catch (Exception e) { failures.Add($"Example {i + 1}: {e.GetType().Name}: {e.Message}"); }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
    [ModelEvaluationFact]
    public async Task Configured_provider_retains_multiscenario_constraints_in_plannable_drafts()
    {
        var provider = new ProviderSettingsService().Load();
        var interpreter = new AssistantDraftInterpreter(new OpenAiChatService());
        var examples = new[]
        {
            "每天工作时间间隔2小时提醒我站起来活动，除午休时间外",
            "每天09:00到18:00每隔1小时提醒我喝水，排除12:00到13:00午休",
            "每天上午9点和下午3点提醒我喝水",
            "明天下午三点安排项目评审，持续90分钟",
            "工作日上午9点到下午6点每隔2小时提醒我活动，持续到2026-10-30"
        };
        var failures = new List<string>();
        for (var i = 0; i < examples.Length; i++)
        {
            var input = examples[i];
            var turn = new AssistantDraftTurn(Guid.NewGuid().ToString("N"), "evaluation", 1, "Understanding",
                input, new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.FromHours(8)), "Asia/Shanghai",
                DateTimeOffset.UtcNow.AddMinutes(15), [], [], new Dictionary<int, AssistantPlanCandidateBindingV2>(),
                new Dictionary<string, AssistantPlanCandidateBindingV2>());
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                var result = await interpreter.UnderstandAsync(provider, input, turn, [], timeout.Token);
                var draft = AssistantScenarioPlanner.Enrich(Assert.Single(result.Tasks), input);
                if (i == 3) Assert.Contains("90", draft.DurationText);
                else
                {
                    Assert.NotNull(draft.Schedule);
                    if (i == 0) Assert.Contains("午休", draft.Schedule!.ExclusionText);
                    if (i == 1) Assert.Contains("12:00", draft.Schedule!.ExclusionText);
                    if (i == 4) Assert.Contains("2026-10-30", draft.Schedule!.UntilText);
                    var plan = AssistantDraftCompiler.Compile(turn with { Tasks = [draft] });
                    Assert.True(plan.Fields.Any(f => f.Key.Contains("schedule")) || plan.Commands.Any(c =>
                        c.Arguments is CreateRecurringTaskArgumentsV1 { DailySchedule: not null }), plan.Summary);
                    Assert.DoesNotContain(plan.Fields, f => f.Kind == "time");
                }
            }
            catch (Exception e) { failures.Add($"Scenario {i + 1}: {e.GetType().Name}: {e.Message}"); }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

}

public sealed class ModelEvaluationFactAttribute : FactAttribute
{
    public ModelEvaluationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CHRONOISLE_RUN_MODEL_EVAL") != "1")
            Skip = "Requires explicit model-evaluation opt-in; uses the configured provider with public synthetic inputs only.";
    }
}
