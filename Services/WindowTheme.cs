using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WPF_UartAssist.Services;

/// <summary>
/// 窗口级的系统外观设置。
/// 目前只有一件事：让系统标题栏跟随暗色/玻璃主题，否则深色界面顶着一根白条很割裂。
/// </summary>
internal static class WindowTheme
{
    /// <summary>DWMWA_USE_IMMERSIVE_DARK_MODE（Windows 10 20H1 起）。</summary>
    private const int DwmwaUseImmersiveDarkMode = 20;

    /// <summary>同一属性在 Windows 10 1809–1909 上的旧编号，失败时补一次。</summary>
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// 设置标题栏明暗。仅在窗口句柄已创建后调用（SourceInitialized 之后）。
    /// 系统不支持时静默忽略，不影响其余功能。
    /// </summary>
    public static void SetDarkTitleBar(Window window, bool dark)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;

        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        int value = dark ? 1 : 0;

        try
        {
            if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeLegacy, ref value, sizeof(int));
            }
        }
        catch (DllNotFoundException)
        {
            // 极老系统没有 dwmapi，保持默认标题栏
        }
        catch (EntryPointNotFoundException)
        {
        }
    }
}
