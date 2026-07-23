namespace OpenIsland.App.Services.Reporting;

public enum AssistantPersona { Direct, Gentle, Witty, Focused }

/// <summary>Pure wording data; it contains no permission, confirmation, ranking or execution fields.</summary>
public sealed record AssistantWordingProfile(
    string Name,
    string Acknowledgement,
    string SuccessPrefix,
    string FailurePrefix,
    string ConfirmationPrompt);

public static class AssistantPersonaFormatter
{
    public static AssistantWordingProfile GetProfile(AssistantPersona persona) => persona switch
    {
        AssistantPersona.Direct => new("干练型", "收到。", "已完成：", "未完成：", "请确认是否执行。"),
        AssistantPersona.Gentle => new("温柔型", "好的，我来帮你。", "已经为你完成：", "这次没能完成：", "确认后我再为你执行，好吗？"),
        AssistantPersona.Witty => new("轻松型", "收到，交给我。", "搞定：", "这次卡住了：", "最后一步，请确认执行。"),
        AssistantPersona.Focused => new("专注型", "已记录。", "完成：", "失败：", "确认执行？"),
        _ => throw new ArgumentOutOfRangeException(nameof(persona))
    };
}
