using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace OpenIsland.App.Services;

public sealed class LifeTrayService : IDisposable
{
    Forms.NotifyIcon? icon;

    public event EventHandler? OpenRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;

    public void Initialize()
    {
        if (icon is not null) return;
        var menu = new Forms.ContextMenuStrip();
        var settings = new Forms.ToolStripMenuItem("设置");
        settings.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        var exit = new Forms.ToolStripMenuItem("退出");
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(settings);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exit);

        icon = new Forms.NotifyIcon
        {
            Icon = CurrentIcon(),
            Text = "Island",
            ContextMenuStrip = menu,
            Visible = true
        };
        icon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
                OpenRequested?.Invoke(this, EventArgs.Empty);
        };
    }

    static Drawing.Icon CurrentIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/OpenIsland;component/Assets/face.ico", UriKind.Absolute);
            using var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
            if (stream is not null) return new Drawing.Icon(stream);
        }
        catch { }
        return Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        if (icon is null) return;
        icon.Visible = false;
        icon.Dispose();
        icon = null;
    }
}