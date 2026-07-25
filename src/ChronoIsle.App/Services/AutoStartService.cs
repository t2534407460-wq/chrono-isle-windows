using System.IO;
using Microsoft.Win32;
namespace ChronoIsle.App.Services;
public interface IAutoStartRegistry
{
    string? Read(string name);
    void Write(string name, string command);
    void Remove(string name);
}
public sealed class CurrentUserRunRegistry : IAutoStartRegistry
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public string? Read(string name) => Registry.CurrentUser.OpenSubKey(RunKey, false)?.GetValue(name) as string;
    public void Write(string name, string command) => Registry.CurrentUser.CreateSubKey(RunKey, true).SetValue(name, command, RegistryValueKind.String);
    public void Remove(string name) => Registry.CurrentUser.OpenSubKey(RunKey, true)?.DeleteValue(name, false);
}
public sealed class AutoStartService
{
    public const string ValueName = "ChronoIsle";
    internal const string LegacyValueName = "OpenIsland";
    private readonly IAutoStartRegistry registry;
    private readonly Func<string?> executablePath;
    public AutoStartService(IAutoStartRegistry registry, Func<string?>? executablePath = null)
    {
        this.registry = registry;
        this.executablePath = executablePath ?? (() => Environment.ProcessPath);
        MigrateLegacyEntry();
    }
    public bool IsSupported => IsExecutable(executablePath());
    public bool IsEnabled => string.Equals(registry.Read(ValueName), CommandFor(executablePath()), StringComparison.OrdinalIgnoreCase);
    public string? Status => IsSupported ? (IsEnabled ? "已在登录时自动启动。" : "关闭：登录后不会自动启动。") : "仅从已构建的 ChronoIsle.exe 启动时可设置开机自启。";
    public void SetEnabled(bool enabled)
    {
        if (!IsSupported) throw new InvalidOperationException("当前运行方式不支持开机自启。");
        if (enabled) registry.Write(ValueName, CommandFor(executablePath())!);
        else registry.Remove(ValueName);
        registry.Remove(LegacyValueName);
    }
    void MigrateLegacyEntry()
    {
        if (registry.Read(ValueName) is null && registry.Read(LegacyValueName) is not null && IsSupported)
            registry.Write(ValueName, CommandFor(executablePath())!);
        registry.Remove(LegacyValueName);
    }
    private static bool IsExecutable(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.GetFileName(path).Equals("ChronoIsle.exe", StringComparison.OrdinalIgnoreCase) &&
        File.Exists(path);
    private static string? CommandFor(string? path) => IsExecutable(path) ? $"\"{path}\"" : null;
}