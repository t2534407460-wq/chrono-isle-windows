using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
namespace OpenIsland.App.Views;
public partial class LifeMainWindow : Window
{
    private readonly IServiceProvider services;
    public LifeMainWindow(OpenIsland.App.ViewModels.LifeViewModel vm, IServiceProvider serviceProvider)
    {
        InitializeComponent(); DataContext = vm; services = serviceProvider; SourceInitialized += (_, _) => UseDarkTitleBar();
    }
    [DllImport("dwmapi.dll", PreserveSig = true)] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    void UseDarkTitleBar(){var value=1;var hwnd=new System.Windows.Interop.WindowInteropHelper(this).Handle;DwmSetWindowAttribute(hwnd,20,ref value,sizeof(int));}
    public void OpenSettings(){var settings=services.GetRequiredService<LifeSettingsWindow>();settings.Owner=this;settings.ShowDialog();}
    private void Settings_Click(object sender, RoutedEventArgs e)=>OpenSettings();
    private void Manage_Click(object sender, RoutedEventArgs e){var window=services.GetRequiredService<LifeManagementWindow>();window.Owner=this;window.ShowDialog();}
    private void ChatInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if(sender is not TextBox box)return;
        var lines=Math.Max(1, box.LineCount); box.Height=Math.Min(120,Math.Max(40,lines*22+12));
    }
}