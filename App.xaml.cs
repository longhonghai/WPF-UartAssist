using System.Windows;
using System.Windows.Threading;
using WPF_UartAssist.Services;

namespace WPF_UartAssist;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 未捕获异常统一兜底，避免界面静默消失后无从排查
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        base.OnStartup(e);

        // 窗口创建前先套用上次选择的主题，避免启动时闪一下暗色
        ThemeManager.Apply(SettingsService.Load().Theme);

        // App.xaml 中不设 StartupUri，窗口在此显式创建
        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"发生未处理的异常：\n\n{e.Exception.Message}",
            "WPF 串口助手",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception;
        MessageBox.Show(
            $"发生严重错误：\n\n{exception?.Message ?? "未知错误"}",
            "WPF 串口助手",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}