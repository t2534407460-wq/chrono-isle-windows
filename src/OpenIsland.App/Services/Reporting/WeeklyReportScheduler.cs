namespace OpenIsland.App.Services.Reporting;

/// <summary>
/// Generates one immutable weekly fact snapshot on Sunday. Generation is
/// idempotent, so a restart or repeated timer tick cannot duplicate a report.
/// </summary>
public sealed class WeeklyReportScheduler
{
    readonly IReportService reports;

    public WeeklyReportScheduler(IReportService reports) =>
        this.reports = reports ?? throw new ArgumentNullException(nameof(reports));

    public ReportSnapshot? GenerateIfDue(DateTimeOffset localNow, string queryVersion = "facts-v1")
    {
        if (localNow.DayOfWeek != DayOfWeek.Sunday) return null;
        var localDay = localNow.Date;
        var start = localDay.AddDays(-6);
        var end = localDay.AddDays(1);
        var offset = localNow.Offset;
        return reports.Generate(new ReportPeriod(
            ReportPeriodKind.Weekly,
            new DateTimeOffset(start, offset),
            new DateTimeOffset(end, offset)),
            queryVersion);
    }

    public static IReadOnlyList<string> BuildNextWeekPlan(ReportFacts facts)
    {
        var plan = new List<string>();
        if (facts.OverdueCount > 0)
            plan.Add($"周一先处理 {facts.OverdueCount} 项逾期事项");
        if (facts.HighPriorityCount > 0)
            plan.Add($"为 {facts.HighPriorityCount} 项高优先级事项预留专注时段");
        if (facts.DeferredCount > 0)
            plan.Add($"复核 {facts.DeferredCount} 项延期事项是否需要重新安排");
        if (plan.Count == 0)
            plan.Add("下周保持当前节奏，先安排最重要的一项任务");
        return plan;
    }
}
