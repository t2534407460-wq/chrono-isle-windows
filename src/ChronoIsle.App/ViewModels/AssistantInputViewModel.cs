using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ChronoIsle.App.Services.Commanding;
using ChronoIsle.App.Services.Domain;

namespace ChronoIsle.App.ViewModels;

public partial class AssistantInputViewModel : ObservableObject
{
    public string Key { get; }
    public string Label { get; }
    public string? Help { get; }
    public bool Required { get; }
    public string? DependsOn { get; }
    public string? DependsValue { get; }
    public IReadOnlyList<AssistantInputOption> Options { get; }
    public IReadOnlyList<AssistantOptionViewModel> Choices { get; }
    public bool IsChoice { get; }
    public bool IsText { get; }
    public bool IsDuration { get; }
    public bool IsRange { get; }
    public bool IsMultiChoice { get; }
    public bool HasSuggestions { get; }
    public bool HasDate { get; }
    public bool HasTime { get; }
    public IReadOnlyList<string> Hours { get; } = Enumerable.Range(0, 24).Select(n => n.ToString("00")).ToArray();
    public IReadOnlyList<string> Minutes { get; } = Enumerable.Range(0, 60).Select(n => n.ToString("00")).ToArray();
    [ObservableProperty] bool isVisible = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] string? error;
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    [ObservableProperty] string? value;
    [ObservableProperty] string? suggestion;
    [ObservableProperty] string? rangeStart;
    [ObservableProperty] string? rangeEnd;
    [ObservableProperty] DateTime? date;
    [ObservableProperty] string? hour;
    [ObservableProperty] string? minute;

    public AssistantInputViewModel(AssistantInputField field)
    {
        Key = field.Key; Label = field.Label; Options = field.Options.ToArray(); Help = field.Help;
        Required = field.Required; DependsOn = field.DependsOn; DependsValue = field.DependsValue;
        IsChoice = field.Kind == "choice"; IsText = field.Kind is "text" or "time_list";
        IsDuration = field.Kind == "duration"; IsRange = field.Kind == "time_range";
        IsMultiChoice = field.Kind == "multichoice";
        HasSuggestions = (IsDuration || IsRange) && Options.Count > 0;
        HasDate = field.Kind is "datetime" or "date"; HasTime = field.Kind is "time" or "datetime";
        Value = field.Value;
        if (HasDate && DateTime.TryParseExact(field.Value, HasTime ? "yyyy-MM-dd HH:mm" : "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var existing))
        { Date = existing.Date; Hour = existing.ToString("HH"); Minute = existing.ToString("mm"); }
        else if (HasTime && TimeOnly.TryParseExact(field.Value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        { Hour = time.ToString("HH"); Minute = time.ToString("mm"); }
        if (IsRange) SetRange(field.Value);
        Choices = Options.Select(o => new AssistantOptionViewModel(o, (field.Value ?? "").Split(',').Contains(o.Value))).ToArray();
        foreach (var choice in Choices) choice.PropertyChanged += (_, _) => OnPropertyChanged(nameof(InputValue));
        Error = field.Error;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Value) or nameof(Date) or nameof(Hour) or nameof(Minute) or nameof(RangeStart) or nameof(RangeEnd))
                OnPropertyChanged(nameof(InputValue));
            if (e.PropertyName == nameof(InputValue)) Error = null;
        };
    }

    public bool Validate()
    {
        Error = !IsVisible ? null
            : Required && string.IsNullOrWhiteSpace(InputValue) ? "请填写或选择此项。"
            : IsRange && !string.IsNullOrWhiteSpace(InputValue) ? ReminderDailySchedule.WindowInputError(InputValue)
            : null;
        return !HasError;
    }

    partial void OnValueChanged(string? value)
    {
        if (Suggestion is not null && Suggestion != value && IsDuration) Suggestion = null;
    }

    partial void OnSuggestionChanged(string? value)
    {
        if (value is null) return;
        if (IsRange) SetRange(value);
        else Value = value;
    }
    void SetRange(string? text)
    {
        var pieces = text?.Split('-');
        if (pieces?.Length == 2) { RangeStart = pieces[0]; RangeEnd = pieces[1]; }
    }

    public string InputValue => IsMultiChoice ? string.Join(",", Choices.Where(c => c.IsSelected).Select(c => c.Value))
        : IsRange ? string.IsNullOrWhiteSpace(RangeStart) && string.IsNullOrWhiteSpace(RangeEnd) ? "" : (RangeStart?.Trim() ?? "") + "-" + (RangeEnd?.Trim() ?? "")
        : HasTime ? Hour is null || Minute is null || HasDate && Date is null ? ""
            : (HasDate ? Date!.Value.ToString("yyyy-MM-dd") + " " : "") + Hour + ":" + Minute
        : HasDate ? Date?.ToString("yyyy-MM-dd") ?? ""
        : Value?.Trim() ?? "";
}

public partial class AssistantOptionViewModel : ObservableObject
{
    public string Label { get; }
    public string Value { get; }
    [ObservableProperty] bool isSelected;
    public AssistantOptionViewModel(AssistantInputOption option, bool selected)
    { Label = option.Label; Value = option.Value; IsSelected = selected; }
}
