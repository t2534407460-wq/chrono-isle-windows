using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Net.Mail;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ChronoIsle.App.Services.Sync;

namespace ChronoIsle.App.Views;

public partial class CloudAccountWindow : Window
{
    HwndSource? windowSource;
    readonly CloudAccountClient client;
    readonly CloudSyncService sync;
    readonly Func<bool>? hasUnsavedSettings;
    readonly CancellationTokenSource lifetime = new();
    CloudEmailChallenge? challenge;
    int mode;
    string challengeEmail = "";
    bool busy;
    public CloudAccountWindow(CloudAccountClient client, CloudSyncService sync, Func<bool>? hasUnsavedSettings = null)
    { this.client = client; this.sync = sync; this.hasUnsavedSettings = hasUnsavedSettings; InitializeComponent(); Refresh(); }
    void Refresh()
    {
        var account = client.Account;
        AuthPanel.Visibility = account is null ? Visibility.Visible : Visibility.Collapsed;
        ProfilePanel.Visibility = account is null ? Visibility.Collapsed : Visibility.Visible;
        if (account is null) return;
        ProfileEmail.Text = account.Email; Avatar.Text = account.Email[..1].ToUpperInvariant();
        AccountStatus.Text = "已登录 · 邮箱已验证";
        LastSync.Text = sync.LastSuccess is { } at ? $"上次成功：{at.ToLocalTime():yyyy-MM-dd HH:mm}" : "上次成功：暂无";
        SyncStatus.Text = sync.LastSuccess is null ? "准备好后，点击立即同步" : "可随时同步最新变更";
        RefreshConflicts();
    }
    void RefreshConflicts()
    {
        var conflicts = sync.Conflicts;
        ConflictPanel.Visibility = conflicts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ConflictList.ItemsSource = conflicts; if (conflicts.Count > 0) ConflictList.SelectedIndex = 0;
    }
    void Mode_Click(object sender, RoutedEventArgs e)
    { if (sender is Button { Tag: string value } && int.TryParse(value, out var selected)) SetMode(selected); }
    void SetMode(int selected)
    {
        mode = selected; challenge = null; Code.Clear(); Password.Clear(); Message.Text = "";
        Verification.Visibility = Visibility.Collapsed; Credentials.Visibility = Visibility.Visible;
        AuthTabs.Visibility = mode == 2 ? Visibility.Collapsed : Visibility.Visible;
        BackToLogin.Visibility = mode == 2 ? Visibility.Visible : Visibility.Collapsed;
        ForgotPassword.Visibility = mode == 0 ? Visibility.Visible : Visibility.Collapsed;
        PasswordHint.Visibility = mode == 0 ? Visibility.Collapsed : Visibility.Visible;
        PasswordLabel.Text = mode == 0 ? "密码" : mode == 1 ? "设置密码" : "新密码";
        AuthHeading.Text = mode == 0 ? "欢迎回到时屿" : mode == 1 ? "创建你的账号" : "找回密码";
        AuthDescription.Text = mode == 0 ? "登录后，管理账号并同步你的时屿数据。" : mode == 1 ? "用邮箱注册，验证后即可登录。" : "验证邮箱，重新设置账号密码。";
        Submit.Content = mode == 0 ? "登录" : "发送验证码";
        LoginTab.Style = (Style)FindResource(mode == 0 ? "Button.Primary" : "Button.Secondary");
        RegisterTab.Style = (Style)FindResource(mode == 1 ? "Button.Primary" : "Button.Secondary");
    }
    async void Submit_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var email = Email.Text.Trim(); var password = Password.Password;
        if (!MailAddress.TryCreate(email, out var parsed) || parsed.Address != email || email.Length > 254)
        { Email.Focus(); throw new InvalidOperationException("请输入完整的邮箱地址。"); }
        if (password.Length == 0) { Password.Focus(); throw new InvalidOperationException("请输入密码。"); }
        if (mode != 0 && (password.Length is < 9 or > 128 || !password.Any(char.IsLetter) || !password.Any(char.IsDigit) || !password.Any(c => char.IsPunctuation(c) || char.IsSymbol(c))))
        { Password.Focus(); throw new InvalidOperationException("密码至少 9 个字符，需包含字母、数字和符号。"); }
        if (mode == 0) { await client.LoginAsync(email, password, lifetime.Token); Password.Clear(); Refresh(); Message.Text = "登录成功，可立即同步数据。"; }
        else
        {
            challengeEmail = email;
            challenge = mode == 1 ? await client.RegisterAsync(email, password, lifetime.Token) : await client.ResetAsync(email, password, lifetime.Token);
            Password.Clear(); Credentials.Visibility = Visibility.Collapsed; Verification.Visibility = Visibility.Visible; AuthTabs.Visibility = Visibility.Collapsed;
            VerificationEmail.Text = $"验证码已发送至 {email}";
            CodeExpiry.Text = $"请在 {challenge.ExpiresAt.ToLocalTime():HH:mm} 前完成验证；未收到时请检查垃圾邮件。";
            Verify.Content = mode == 1 ? "验证并登录" : "验证并重置密码";
            Message.Text = ""; Code.Focus();
        }
    });
    async void Verify_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (challenge is null) throw new InvalidOperationException("请先获取验证码。");
        var code = Code.Text.Trim();
        if (code.Length != 6 || code.Any(c => c is < '0' or > '9')) { Code.Focus(); throw new InvalidOperationException("请输入 6 位数字验证码。"); }
        if (challenge.ExpiresAt <= DateTimeOffset.UtcNow) throw new InvalidOperationException("验证码已过期，请重新获取。");
        if (mode == 1) { await client.ConfirmAsync(challenge.ChallengeId, code, challengeEmail, lifetime.Token); Message.Text = "邮箱验证成功，账号已注册并登录。"; }
        else { await client.ConfirmResetAsync(challenge.ChallengeId, code, lifetime.Token); SetMode(0); Message.Text = "密码已重置，请使用新密码登录。"; }
        challenge = null; Code.Clear(); Verification.Visibility = Visibility.Collapsed; Refresh();
    });
    void BackToCredentials_Click(object sender, RoutedEventArgs e) => SetMode(mode);
    void Credentials_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; Submit_Click(sender, e); } }
    void Code_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; Verify_Click(sender, e); } }
    async void Logout_Click(object sender, RoutedEventArgs e) => await Run(async () => { await client.LogoutAsync(lifetime.Token); SetMode(0); Refresh(); Message.Text = "已退出登录，本机数据继续保留。"; });
    async void Sync_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (hasUnsavedSettings?.Invoke() == true) throw new InvalidOperationException("设置页有未保存的修改，请先保存设置，再进入账号中心同步。");
        var progress = new Progress<string>(text => Dispatcher.BeginInvoke(() => { if (busy) SyncStatus.Text = text; }));
        var result = await sync.SyncAsync(progress, lifetime.Token);
        Refresh(); SyncStatus.Text = result.Message; Message.Text = result.Complete ? "本机数据已与时屿云端同步。" : "本机修改已保留，请处理冲突或重试。";
    });
    void Conflict_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (KeepLocal is null) return;
        KeepLocal.IsEnabled = ConflictList.SelectedItem is CloudSyncConflict { CloudDeleted: false };
        UseCloud.IsEnabled = ConflictList.SelectedItem is CloudSyncConflict;
        ConflictDetails.Text = ConflictList.SelectedItem is CloudSyncConflict conflict ? $"本机：{conflict.LocalSummary}\n云端：{conflict.CloudSummary}" : "";
    }
    async void Resolve_Click(object sender, RoutedEventArgs e) => await Run(() =>
    {
        if (hasUnsavedSettings?.Invoke() == true) throw new InvalidOperationException("设置页有未保存的修改，请先保存设置，再处理同步冲突。");
        if (ConflictList.SelectedItem is not CloudSyncConflict conflict) throw new InvalidOperationException("请先选择需要处理的冲突。");
        var local = sender is Button { Tag: "local" };
        sync.Resolve(conflict, local); RefreshConflicts(); Message.Text = local ? "已保留本机版本，请再次同步上传。" : "已采用云端版本；原本机数据保存在同步前备份中。";
        return Task.CompletedTask;
    });
    async Task Run(Func<Task> action)
    {
        if (busy) return;
        busy = true; AuthPanel.IsEnabled = false; ProfilePanel.IsEnabled = false; BusyProgress.Visibility = Visibility.Visible; Message.Text = "正在处理…";
        Message.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        try { await action(); }
        catch (InvalidOperationException e) { ShowError(e.Message); }
        catch (HttpRequestException) { ShowError("无法连接服务，请检查网络；本机数据和待同步队列已保留。"); }
        catch (OperationCanceledException) { if (!lifetime.IsCancellationRequested) ShowError("请求超时，请稍后重试；本机数据已保留。"); }
        catch (IOException) { ShowError("无法保存本机数据或账号配置，请检查文件权限。"); }
        catch (Exception e) when (e is SqliteException or JsonException or System.Security.Cryptography.CryptographicException or UnauthorizedAccessException)
        { ShowError("本机数据或服务响应无法处理，请保留数据并重试。"); }
        finally
        {
            busy = false; AuthPanel.IsEnabled = true; ProfilePanel.IsEnabled = true; BusyProgress.Visibility = Visibility.Collapsed;
            if (Message.Text.Length > 0) Message.BringIntoView();
            if (challenge is not null) Code.Focus();
        }
    }
    void ShowError(string text)
    {
        Message.Text = text; Message.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");
        if (SyncStatus.Text.StartsWith("正在", StringComparison.Ordinal)) SyncStatus.Text = "本次同步未完成，可以重试。";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        windowSource?.AddHook(WindowWorkArea.ConstrainMaximizedBounds);
    }
    protected override void OnClosed(EventArgs e)
    {
        lifetime.Cancel();
        if (windowSource is { IsDisposed: false }) windowSource.RemoveHook(WindowWorkArea.ConstrainMaximizedBounds);
        windowSource = null;
        base.OnClosed(e);
    }
    void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2) ToggleMaximize();
        else DragMove();
    }
    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    void Close_Click(object sender, RoutedEventArgs e) => Close();
    void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
