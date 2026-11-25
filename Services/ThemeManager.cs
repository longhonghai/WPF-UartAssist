using System.Windows;

namespace WPF_UartAssist.Services;

/// <summary>
/// 运行时换肤。做法是替换 Application.Resources 里的「调色板字典」，
/// 样式字典只加载一次、内部用 DynamicResource 引用颜色，
/// 所以替换后所有控件立即变色，不需要重建窗口。
/// 目前支持 亮色 / 暗色 / 液态玻璃 三套主题，按此顺序循环切换。
/// </summary>
public static class ThemeManager
{
    public const string Light = "Light";
    public const string Dark = "Dark";
    public const string Glass = "Glass";

    private static readonly string[] PaletteFiles = [Light + ".xaml", Dark + ".xaml", Glass + ".xaml"];

    /// <summary>当前主题，取值 Light / Dark / Glass。默认亮色。</summary>
    public static string Current { get; private set; } = Light;

    public static bool IsDark => Current == Dark;

    /// <summary>是否处于液态玻璃主题（界面据此显示背景光斑层）。</summary>
    public static bool IsGlass => Current == Glass;

    /// <summary>规范化主题名，非法值一律回落到亮色（默认主题）。</summary>
    public static string Normalize(string? theme)
    {
        if (string.Equals(theme, Dark, StringComparison.OrdinalIgnoreCase))
        {
            return Dark;
        }

        return string.Equals(theme, Glass, StringComparison.OrdinalIgnoreCase) ? Glass : Light;
    }

    /// <summary>下一个主题：亮色 → 暗色 → 液态玻璃 → 亮色。</summary>
    public static string Next(string? theme) => Normalize(theme) switch
    {
        Dark => Glass,
        Glass => Light,
        _ => Dark
    };

    /// <summary>沿主题环切换一格（保留旧名称，等价于 <see cref="Next"/>）。</summary>
    public static string Toggle(string? theme) => Next(theme);

    /// <summary>给界面显示的中文名。</summary>
    public static string DisplayName(string? theme) => Normalize(theme) switch
    {
        Dark => "暗色",
        Glass => "液态玻璃",
        _ => "亮色"
    };

    /// <summary>应用主题。可在窗口创建前调用，也可运行时调用。</summary>
    public static void Apply(string? theme)
    {
        string target = Normalize(theme);
        Current = target;

        ResourceDictionary? resources = Application.Current?.Resources;
        if (resources is null)
        {
            // 设计时/无 Application 场景，仅记录选择
            return;
        }

        var palette = new ResourceDictionary
        {
            Source = new Uri($"Themes/{target}.xaml", UriKind.Relative)
        };

        var merged = resources.MergedDictionaries;
        int index = FindPaletteIndex(merged);

        if (index >= 0)
        {
            merged[index] = palette;
        }
        else
        {
            // 调色板必须在样式之前，否则样式里的引用解析不到
            merged.Insert(0, palette);
        }
    }

    private static int FindPaletteIndex(IList<ResourceDictionary> merged)
    {
        for (int i = 0; i < merged.Count; i++)
        {
            string? source = merged[i].Source?.OriginalString;
            if (string.IsNullOrEmpty(source))
            {
                continue;
            }

            foreach (string file in PaletteFiles)
            {
                if (source.EndsWith(file, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        return -1;
    }
}
