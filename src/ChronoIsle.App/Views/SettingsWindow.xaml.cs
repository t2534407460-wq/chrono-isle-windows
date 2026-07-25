using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using ChronoIsle.App.Services;

namespace ChronoIsle.App.Views;

public partial class SettingsWindow : Window
{
    private readonly WorkspaceSettings _settings;
    private readonly ObservableCollection<string> _draft = new();
    private bool _initializing = true; // 构造期设 LanguageCombo 选中项不触发切换
    private bool _recordingHotkey;     // 正在录制截图快捷键

    public SettingsWindow(WorkspaceSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        foreach (var w in _settings.Workspaces) _draft.Add(w);
        WorkspacesList.ItemsSource = _draft;

        // 语言下拉对齐当前设置（auto=0 / zh=1 / en=2）
        LanguageCombo.SelectedIndex = _settings.Language switch { "zh" => 1, "en" => 2, _ => 0 };
        HotkeyBox.Content = _settings.ScreenshotHotkey;
        _initializing = false;
    }

    // ── 区域截图快捷键录制 ──

    /// <summary>点击后进入录制模式：下一组"修饰键 + 主键"会被记录为新快捷键。</summary>
    private void HotkeyBox_Click(object sender, RoutedEventArgs e)
    {
        _recordingHotkey = true;
        HotkeyBox.Content = Loc.Get("Settings_Screenshot_Press");
        HotkeyBox.Focus();
    }

    /// <summary>录制中捕获按键：等到按下非修饰主键时合成 "Ctrl+Q" 形式并持久化（需至少一个修饰键）。</summary>
    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_recordingHotkey) return;
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Esc 取消录制，恢复原值
        if (key == Key.Escape)
        {
            _recordingHotkey = false;
            HotkeyBox.Content = _settings.ScreenshotHotkey;
            return;
        }
        // 仅按下修饰键时继续等待主键
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        var keyName = KeyName(key);
        if (keyName == null) return; // 不支持的键，继续等

        var parts = new List<string>();
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (parts.Count == 0)
        {
            // 必须含至少一个修饰键（纯字母键会和打字冲突）
            HotkeyBox.Content = Loc.Get("Settings_Screenshot_NeedMod");
            return;
        }
        parts.Add(keyName);

        var combo = string.Join("+", parts);
        _settings.SetScreenshotHotkey(combo); // 持久化 → HotkeyService 自动重绑
        HotkeyBox.Content = combo;
        _recordingHotkey = false;
    }

    /// <summary>WPF Key → 快捷键字符串里的主键名（A-Z / 0-9 / F1-F24 / 少量常用键）。</summary>
    private static string? KeyName(Key k)
    {
        if (k >= Key.A && k <= Key.Z) return k.ToString();
        if (k >= Key.D0 && k <= Key.D9) return ((char)('0' + (k - Key.D0))).ToString();
        if (k >= Key.NumPad0 && k <= Key.NumPad9) return ((char)('0' + (k - Key.NumPad0))).ToString();
        if (k >= Key.F1 && k <= Key.F24) return k.ToString();
        return k switch
        {
            Key.Space => "Space",
            Key.Enter => "Enter",
            Key.Tab => "Tab",
            _ => null
        };
    }

    /// <summary>语言下拉改变：持久化 + 立即切换界面语言（本窗口与灵动岛、托盘同步刷新）。</summary>
    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        var value = (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        _settings.SetLanguage(value);
        Loc.Instance.Apply(value);
    }

    private void AddWorkspace_Click(object sender, RoutedEventArgs e)
    {
        // .NET 8 内置的 OpenFolderDialog（无须 WinForms 依赖）
        var dlg = new OpenFolderDialog
        {
            Title = Loc.Get("Settings_AddDir"),
            Multiselect = false
        };
        if (dlg.ShowDialog(this) != true) return;

        var path = dlg.FolderName?.TrimEnd('\\', '/');
        if (string.IsNullOrEmpty(path)) return;
        if (_draft.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        _draft.Add(path);
    }

    private void RemoveWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (WorkspacesList.SelectedItem is string s) _draft.Remove(s);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // 工作区走草稿，保存时落盘。
        _settings.SetWorkspaces(_draft);
        TrySetDialogResult(true);
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        TrySetDialogResult(false);
        Close();
    }

    /// <summary>
    /// 设置 DialogResult 仅当窗口是用 ShowDialog() 打开的（IsModal）。Show() 模式下设
    /// DialogResult 会抛 InvalidOperationException，旧版导致用户点保存整个 app 崩。
    /// </summary>
    private void TrySetDialogResult(bool value)
    {
        try { DialogResult = value; } catch (InvalidOperationException) { /* 非 dialog 模式 */ }
    }
}
