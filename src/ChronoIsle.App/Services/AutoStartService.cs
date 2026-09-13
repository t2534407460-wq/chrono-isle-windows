using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;
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
    public string? Read(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return key?.GetValue(name) as string;
    }
    public void Write(string name, string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        key.SetValue(name, command, RegistryValueKind.String);
    }
    public void Remove(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        key?.DeleteValue(name, false);
    }
}

public interface IAutoStartTask
{
    string? ReadDefinition();
    void Register(string definition);
    void Remove();
}

public sealed class CurrentUserLogonTask : IAutoStartTask
{
    private readonly string userId = WindowsIdentity.GetCurrent().User!.Value;
    public string TaskName => $"ChronoIsle-Startup-{userId}";

    public string? ReadDefinition() => WithFolder<string?>(folder =>
    {
        object? task = null;
        try
        {
            task = folder.GetTask(TaskName);
            return (string)((dynamic)task).Xml;
        }
        catch (COMException exception) when (exception.HResult == unchecked((int)0x80070002))
        {
            return null;
        }
        finally { Release(task); }
    });

    public void Register(string definition) => WithFolder(folder =>
    {
        // InteractiveToken uses the signed-in user's desktop, without storing a password.
        object task = folder.RegisterTask(TaskName, definition, 6, userId, null, 3, null);
        Release(task);
        return true;
    });

    public void Remove() => WithFolder(folder =>
    {
        try { folder.DeleteTask(TaskName, 0); }
        catch (COMException exception) when (exception.HResult == unchecked((int)0x80070002)) { }
        return true;
    });

    private static T WithFolder<T>(Func<dynamic, T> action)
    {
        object service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!)!;
        object? folder = null;
        try
        {
            ((dynamic)service).Connect();
            folder = ((dynamic)service).GetFolder(@"\");
            return action((dynamic)folder);
        }
        finally
        {
            Release(folder);
            Release(service);
        }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.FinalReleaseComObject(value);
    }
}

public sealed class AutoStartService
{
    public const string ValueName = "ChronoIsle";
    internal const string LegacyValueName = "OpenIsland";
    private static readonly XNamespace TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private readonly IAutoStartRegistry registry;
    private readonly IAutoStartTask task;
    private readonly Func<string?> executablePath;
    private readonly Func<string> userId;
    private string? migrationError;

    public AutoStartService(IAutoStartRegistry registry, IAutoStartTask task,
        Func<string?>? executablePath = null, Func<string>? userId = null)
    {
        this.registry = registry;
        this.task = task;
        this.executablePath = executablePath ?? (() => Environment.ProcessPath);
        this.userId = userId ?? (() => WindowsIdentity.GetCurrent().User!.Value);
    }

    public bool IsSupported => IsExecutable(executablePath());
    public bool IsEnabled
    {
        get
        {
            try { return IsCurrentTaskEnabled(); }
            catch { return false; }
        }
    }

    public string Status
    {
        get
        {
            if (!IsSupported) return "仅从已构建的 ChronoIsle.exe 启动时可设置开机自启。";
            try
            {
                if (IsCurrentTaskEnabled()) return "已开启：登录 Windows 后自动启动。";
                return migrationError is null
                    ? "关闭：登录后不会自动启动。"
                    : $"无法迁移原自启设置：{migrationError}";
            }
            catch (Exception exception) { return $"无法读取开机自启状态：{exception.Message}"; }
        }
    }

    public void Initialize()
    {
        if (!IsSupported) return;
        try
        {
            if (registry.Read(ValueName) is not null || registry.Read(LegacyValueName) is not null)
                SetEnabled(true);
        }
        catch (Exception exception)
        {
            // A registration failure must not prevent the application from opening.
            migrationError = exception.Message;
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (!IsSupported) throw new InvalidOperationException("当前运行方式不支持开机自启。");
        if (enabled)
        {
            task.Register(CreateTaskDefinition(executablePath()!, userId()));
            if (!IsCurrentTaskEnabled())
                throw new InvalidOperationException("登录启动任务未生效，请重新开启自启。");
        }
        else task.Remove();

        // Keep legacy entries until the replacement has been registered and verified.
        registry.Remove(ValueName);
        registry.Remove(LegacyValueName);
        migrationError = null;
    }

    private bool IsCurrentTaskEnabled()
    {
        var path = executablePath();
        if (!IsExecutable(path) || task.ReadDefinition() is not { } definition) return false;
        var root = XDocument.Parse(definition).Root;
        var principal = root?.Element(TaskNamespace + "Principals")?.Element(TaskNamespace + "Principal");
        var settings = root?.Element(TaskNamespace + "Settings");
        var actions = root?.Element(TaskNamespace + "Actions");
        var action = actions?.Element(TaskNamespace + "Exec");
        var currentUser = userId();
        return root?.Name == TaskNamespace + "Task" &&
            settings?.Element(TaskNamespace + "Enabled")?.Value != "false" &&
            IsSameUser(principal?.Element(TaskNamespace + "UserId")?.Value, currentUser) &&
            principal?.Element(TaskNamespace + "LogonType")?.Value == "InteractiveToken" &&
            principal?.Element(TaskNamespace + "RunLevel")?.Value == "HighestAvailable" &&
            root.Element(TaskNamespace + "Triggers")?.Elements(TaskNamespace + "LogonTrigger").Any(trigger =>
                IsSameUser(trigger.Element(TaskNamespace + "UserId")?.Value, currentUser) &&
                trigger.Element(TaskNamespace + "Enabled")?.Value != "false") == true &&
            actions?.Elements().Count() == 1 &&
            string.Equals(action?.Element(TaskNamespace + "Command")?.Value, path, StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(action?.Element(TaskNamespace + "Arguments")?.Value) &&
            string.Equals(action?.Element(TaskNamespace + "WorkingDirectory")?.Value, Path.GetDirectoryName(path), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameUser(string? taskUser, string currentUser)
    {
        if (string.IsNullOrWhiteSpace(taskUser)) return false;
        if (string.Equals(taskUser, currentUser, StringComparison.OrdinalIgnoreCase)) return true;
        if (taskUser.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)) return false;
        // Task Scheduler can normalize a logon trigger's SID to DOMAIN\user in saved XML.
        try { return new NTAccount(taskUser).Translate(typeof(SecurityIdentifier)).Value == currentUser; }
        catch (IdentityNotMappedException) { return false; }
    }

    internal static string CreateTaskDefinition(string path, string userId)
    {
        var ns = TaskNamespace;
        return new XDocument(
            new XElement(ns + "Task", new XAttribute("version", "1.2"),
                new XElement(ns + "RegistrationInfo",
                    new XElement(ns + "Description", "登录 Windows 后启动时屿。可在时屿设置中关闭。")),
                new XElement(ns + "Triggers",
                    new XElement(ns + "LogonTrigger",
                        new XElement(ns + "Enabled", true),
                        new XElement(ns + "UserId", userId),
                        new XElement(ns + "Delay", "PT10S"))),
                new XElement(ns + "Principals",
                    new XElement(ns + "Principal", new XAttribute("id", "CurrentUser"),
                        new XElement(ns + "UserId", userId),
                        new XElement(ns + "LogonType", "InteractiveToken"),
                        new XElement(ns + "RunLevel", "HighestAvailable"))),
                new XElement(ns + "Settings",
                    new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(ns + "DisallowStartIfOnBatteries", false),
                    new XElement(ns + "StopIfGoingOnBatteries", false),
                    new XElement(ns + "StartWhenAvailable", true),
                    new XElement(ns + "RunOnlyIfNetworkAvailable", false),
                    new XElement(ns + "AllowStartOnDemand", true),
                    new XElement(ns + "Enabled", true),
                    new XElement(ns + "RunOnlyIfIdle", false),
                    new XElement(ns + "ExecutionTimeLimit", "PT0S")),
                new XElement(ns + "Actions", new XAttribute("Context", "CurrentUser"),
                    new XElement(ns + "Exec",
                        new XElement(ns + "Command", path),
                        new XElement(ns + "WorkingDirectory", Path.GetDirectoryName(path)))))).ToString();
    }

    private static bool IsExecutable(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.GetFileName(path).Equals("ChronoIsle.exe", StringComparison.OrdinalIgnoreCase) &&
        File.Exists(path);
}
