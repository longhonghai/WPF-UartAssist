using System.Windows;

namespace WPF_UartAssist.Services;

/// <summary>
/// 运行时换肤。做法是替换 Application.Resources 里的「调色板字典」，
/// 样式字典只加载一次、内部用 DynamicResource 引用颜色，
/// 所以替换后所有控件立即变色，不需要重建窗口。
/// </summary>
public static class ThemeManager
{
    public const string Dark = "Dark";
    public const string Light = "Light";

    private static readonly string[] PaletteFiles = [Dark + ".xaml", Light + ".xaml"];

    /// <summary>当前主题，取值 <see cref="Dark"/> 或 <see cref="Light"/>。</summary>
    public static string Current { get; private set; } = Dark;

    public static bool IsDark => Current == Dark;

    /// <summary>规范化主题名，非法值一律回落到暗色。</summary>
    public static string Normalize(string? theme)
        => string.Equals(theme, Light, StringComparison.OrdinalIgnoreCase) ? Light : Dark;

    public static string Toggle(string? theme)
        => Normalize(theme) == Dark ? Light : Dark;

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
