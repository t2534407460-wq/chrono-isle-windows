using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ChronoIsle.App.Services.Commanding;

namespace ChronoIsle.App.ViewModels;

public partial class AssistantInputViewModel : ObservableObject
{
    public string Key { get; }
    public string Label { get; }
    public IReadOnlyList<AssistantInputOption> Options { get; }
    public bool IsChoice { get; }
    public bool IsText { get; }
    public bool HasDate { get; }
    public bool HasTime { get; }
    public IReadOnlyList<string> Hours { get; } = Enumerable.Range(0, 24).Select(n => n.ToString("00")).ToArray();
    public IReadOnlyList<string> Minutes { get; } = Enumerable.Range(0, 60).Select(n => n.ToString("00")).ToArray();
    [ObservableProperty] string? value;
    [ObservableProperty] DateTime? date;
    [ObservableProperty] string? hour;
    [ObservableProperty] string? minute;

    public AssistantInputViewModel(AssistantInputField field)
    {
        Key = field.Key; Label = field.Label; Options = field.Options;
        IsChoice = field.Kind == "choice"; IsText = field.Kind == "text";
        HasDate = field.Kind == "datetime"; HasTime = field.Kind is "time" or "datetime";
        Value = field.Value;
        if (HasDate && DateTime.TryParseExact(field.Value, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var existing))
        { Date = existing.Date; Hour = existing.ToString("HH"); Minute = existing.ToString("mm"); }
        else if (HasTime && TimeOnly.TryParseExact(field.Value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        { Hour = time.ToString("HH"); Minute = time.ToString("mm"); }
    }

    public string InputValue => HasTime
        ? Hour is null || Minute is null || HasDate && Date is null ? ""
            : (HasDate ? Date!.Value.ToString("yyyy-MM-dd") + " " : "") + Hour + ":" + Minute
        : Value?.Trim() ?? "";
}
