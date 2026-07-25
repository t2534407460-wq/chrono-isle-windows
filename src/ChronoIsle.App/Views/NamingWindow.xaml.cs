using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ChronoIsle.App.Services.Domain;

namespace ChronoIsle.App.Views;

public partial class NamingWindow : Window
{
    readonly NamingSuggestionService naming;
    readonly ObservableCollection<FormatRow> formatRows = [];
    NamingResult? result;
    NamingKind? resultKind;
    NamingFormatValue? recommendation;
    int candidateIndex;

    public NamingWindow(NamingSuggestionService naming)
    {
        InitializeComponent();
        this.naming = naming;
        FormatRows.ItemsSource = formatRows;
        Loaded += (_, _) =>
        {
            ContentScroller.ScrollToTop();
            MeaningInput.Focus();
        };
    }

    void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else
            DragMove();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    async void Generate_Click(object sender, RoutedEventArgs e)
    {
        var meaning = MeaningInput.Text.Trim();
        if (meaning.Length == 0)
        {
            ShowStatus("请输入想表达的中文含义。", StatusKind.Error);
            MeaningInput.Focus();
            return;
        }

        SetBusy(true);
        HideStatus();
        try
        {
            var kind = SelectedKind();
            result = await naming.GenerateAsync(kind, meaning);
            resultKind = kind;
            candidateIndex = 0;
            RenderCandidate();
            EmptyState.Visibility = Visibility.Collapsed;
            ResultCard.Visibility = Visibility.Visible;
            ShowStatus("已生成 3 个候选，可使用左右按钮切换。", StatusKind.Success);
        }
        catch (ArgumentException exception)
        {
            ShowStatus(exception.Message, StatusKind.Error);
        }
        catch (NamingServiceException exception)
        {
            ShowStatus(exception.Message, StatusKind.Error);
        }
        catch (TaskCanceledException)
        {
            ShowStatus("模型请求超时，请检查网络后重试。", StatusKind.Error);
        }
        catch (HttpRequestException)
        {
            ShowStatus("无法连接模型服务，请检查网络和模型设置后重试。", StatusKind.Error);
        }
        catch (InvalidOperationException exception)
        {
            var message = exception.Message.Contains("API Key", StringComparison.OrdinalIgnoreCase)
                ? "请先在设置中填写 API Key。"
                : "模型请求失败，请检查模型地址、模型名称和 API Key 后重试。";
            ShowStatus(message, StatusKind.Error);
        }
        catch
        {
            ShowStatus("生成命名时发生错误，请重试。", StatusKind.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (result is null) return;
        candidateIndex = (candidateIndex - 1 + result.Candidates.Count) % result.Candidates.Count;
        RenderCandidate();
        HideStatus();
    }

    void Next_Click(object sender, RoutedEventArgs e)
    {
        if (result is null) return;
        candidateIndex = (candidateIndex + 1) % result.Candidates.Count;
        RenderCandidate();
        HideStatus();
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: FormatRow row }) Copy(row.Label, row.Value);
    }

    void CopyRecommended_Click(object sender, RoutedEventArgs e)
    {
        if (recommendation is not null) Copy(recommendation.Label, recommendation.Value);
    }

    void Copy(string label, string value)
    {
        try
        {
            System.Windows.Clipboard.SetText(value);
            ShowStatus($"已复制“{label}”：{value}", StatusKind.Success);
        }
        catch
        {
            ShowStatus("剪贴板暂时被其他程序占用，请稍后重试。", StatusKind.Error);
        }
    }

    NamingKind SelectedKind() =>
        KindSelector.SelectedItem is ComboBoxItem { Tag: NamingKind kind }
            ? kind
            : NamingKind.Variable;

    void RenderCandidate()
    {
        if (result is null || result.Candidates.Count == 0) return;
        var candidate = result.Candidates[candidateIndex];
        CandidatePhrase.Text = candidate.Phrase;
        CandidateDescription.Text = candidate.Description;
        CandidateCounter.Text = $"{candidateIndex + 1} / {result.Candidates.Count}";
        recommendation = NamingFormatter.RecommendedName(resultKind ?? SelectedKind(), candidate.Words);
        RecommendedNameLabel.Text = recommendation.Label;
        RecommendedNameValue.Text = recommendation.Value;
        RecommendedHint.Text = resultKind == NamingKind.DatabaseTable
            ? "数据库表名按规范固定使用 TBLCUS_ 前缀"
            : "按当前名称类型推荐的默认写法";
        formatRows.Clear();
        foreach (var format in candidate.Formats)
            formatRows.Add(new FormatRow(
                format.Label,
                format.Value,
                $"NamingCopy{format.Kind}",
                $"复制{format.Label}命名"));
    }

    void SetBusy(bool busy)
    {
        MeaningInput.IsEnabled = !busy;
        KindSelector.IsEnabled = !busy;
        GenerateButton.IsEnabled = !busy;
        PreviousButton.IsEnabled = !busy;
        NextButton.IsEnabled = !busy;
        LoadingPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
        {
            var animation = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(.85))
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            LoadingRotation.BeginAnimation(RotateTransform.AngleProperty, animation);
        }
        else
        {
            LoadingRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        }
    }

    void ShowStatus(string message, StatusKind kind)
    {
        StatusText.Text = message;
        StatusNotice.Style = (Style)FindResource(kind == StatusKind.Success ? "Notice.Success" : "Notice.Error");
        StatusIcon.Stroke = (System.Windows.Media.Brush)FindResource(
            kind == StatusKind.Success ? "Brush.Success" : "Brush.Danger");
        StatusIcon.Data = Geometry.Parse(kind == StatusKind.Success
            ? "M2,7 L6,11 L14,2"
            : "M12,3 L22,21 L2,21 Z M12,9 L12,14 M12,17 L12,18");
        StatusNotice.Visibility = Visibility.Visible;
    }

    void HideStatus() => StatusNotice.Visibility = Visibility.Collapsed;

    enum StatusKind { Success, Error }

    sealed record FormatRow(
        string Label,
        string Value,
        string AutomationId,
        string CopyAccessibleName);
}
