using System.IO;
using System.Text.Json;

namespace ChronoIsle.App.Services;

/// <summary>
/// 工作区设置：用户在控制中心选定的目录列表，控制 Overview 仅统计 cwd 落在
/// 这些目录下的 session（项目级筛选）。空列表 = 不筛选，等同于全量统计。
/// 持久化到 %APPDATA%\ChronoIsle\settings.json。
/// </summary>
public class WorkspaceSettings
{
    private readonly string _path;

    public List<string> Workspaces { get; private set; } = new();

    /// <summary>
    /// 灵动岛提示音总开关（任务完成 / 需关注 / 提醒的"叮"）。默认 true（开）。
    /// false = 全局静音，SoundService 据此 no-op。json key = "soundEnabled"。
    /// Master mute for the island's completion / attention / reminder chimes. Default true.
    /// </summary>
    public bool SoundEnabled { get; private set; } = true;

    /// <summary>界面语言： "auto"（跟随 Windows 系统语言）/ "zh" / "en"。默认 auto。json key = "language"。</summary>
    public string Language { get; private set; } = "auto";

    /// <summary>区域截图全局快捷键（如 "Ctrl+Q"）。默认 Ctrl+Q。json key = "screenshotHotkey"。
    /// 由 HotkeyService 注册为系统级热键；在设置中心可改。</summary>
    public string ScreenshotHotkey { get; private set; } = "Ctrl+Q";

    public event EventHandler? Changed;

    public WorkspaceSettings()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _path = Path.Combine(appData, "ChronoIsle", "settings.json");
        Load();
    }

    /// <summary>整理 + 持久化 + 通知监听者。</summary>
    public void SetWorkspaces(IEnumerable<string> paths)
    {
        Workspaces = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.TrimEnd('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>设置提示音总开关 + 持久化 + 通知监听者。</summary>
    public void SetSoundEnabled(bool v)
    {
        SoundEnabled = v;
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>设置界面语言（"auto"/"zh"/"en"）+ 持久化 + 通知监听者。</summary>
    public void SetLanguage(string lang)
    {
        Language = lang is "zh" or "en" ? lang : "auto";
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>设置区域截图快捷键（如 "Ctrl+Q"）+ 持久化 + 通知监听者（HotkeyService 据此重新注册）。</summary>
    public void SetScreenshotHotkey(string hotkey)
    {
        ScreenshotHotkey = string.IsNullOrWhiteSpace(hotkey) ? "Ctrl+Q" : hotkey.Trim();
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// session.cwd 是否命中任一工作区。Workspaces 空 → 永远 true（不筛选）。
    /// </summary>
    public bool Matches(string? cwd)
    {
        if (Workspaces.Count == 0) return true;
        if (string.IsNullOrEmpty(cwd)) return false;
        var c = cwd.TrimEnd('\\', '/');
        foreach (var w in Workspaces)
        {
            if (c.StartsWith(w, StringComparison.OrdinalIgnoreCase)
                && (c.Length == w.Length || c[w.Length] is '\\' or '/'))
                return true;
        }
        return false;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("workspaces", out var ws)
                && ws.ValueKind == JsonValueKind.Array)
            {
                Workspaces = ws.EnumerateArray()
                    .Select(x => x.GetString())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s!.TrimEnd('\\', '/'))
                    .ToList();
            }
            // soundEnabled 缺失 / 解析失败 → 保持默认 true（旧 settings.json 没这个 key
            // 时不静音，符合"默认开"语义）
            if (doc.RootElement.TryGetProperty("soundEnabled", out var snd)
                && (snd.ValueKind == JsonValueKind.True || snd.ValueKind == JsonValueKind.False))
            {
                SoundEnabled = snd.GetBoolean();
            }
            if (doc.RootElement.TryGetProperty("language", out var lng)
                && lng.ValueKind == JsonValueKind.String)
            {
                var v = lng.GetString();
                Language = v is "zh" or "en" ? v : "auto";
            }
            if (doc.RootElement.TryGetProperty("screenshotHotkey", out var hk)
                && hk.ValueKind == JsonValueKind.String)
            {
                var v = hk.GetString();
                if (!string.IsNullOrWhiteSpace(v)) ScreenshotHotkey = v!.Trim();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WorkspaceSettings.Load failed: {ex.Message}");
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // 所有 key 一起序列化 —— 写任一项都不丢其它项 (workspaces / soundEnabled / language / screenshotHotkey)。
            var payload = JsonSerializer.Serialize(
                new
                {
                    workspaces = Workspaces,
                    soundEnabled = SoundEnabled,
                    language = Language,
                    screenshotHotkey = ScreenshotHotkey
                },
                new JsonSerializerOptions { WriteIndented = true });
            // 原子写：先写 tmp 再替换，避免写到一半崩溃/断电截断文件。
            var tmp = _path + ".tmp." + System.Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, payload);
            if (File.Exists(_path)) File.Replace(tmp, _path, null);
            else File.Move(tmp, _path);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WorkspaceSettings.Save failed: {ex.Message}");
        }
    }
}
