using OpenIsland.App.Services.Reporting;

namespace OpenIsland.App.Views;

public partial class LifeIslandWindow
{
    // Kept as a property so the report panel can be attached after the WPF
    // island has loaded, without moving its existing constructor wiring.
    WeeklyReportScheduler? weeklyReports => new(reports);
}
