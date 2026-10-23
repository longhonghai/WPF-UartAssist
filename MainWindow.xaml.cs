using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using WPF_UartAssist.Models;
using WPF_UartAssist.ViewModels;

namespace WPF_UartAssist;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    /// <summary>每个页签通道一份文档：串口 / TCP·UDP / S7 / MQTT / OPC UA / DoIP 各自独立记录与显示。</summary>
    private readonly FlowDocument[] _documents = new FlowDocument[6];

    /// <summary>各通道不换行时缓存的最宽一行宽度（像素），用于让页宽贴合内容。</summary>
    private readonly double[] _widestLineWidths = new double[6];

    /// <summary>当前显示的通道，与顶部页签索引一致。</summary>
    private LogChannel CurrentChannel => (LogChannel)_viewModel.SelectedTabIndex;

    // 日志分色随主题变化，切换后重新取资源并重着色已有行
    private Brush _receiveBrush = Brushes.MediumSeaGreen;
    private Brush _sendBrush = Brushes.CornflowerBlue;
    private Brush _systemBrush = Brushes.Gray;
    private Brush _prefixBrush = Brushes.DimGray;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = (MainViewModel)Resources["Vm"];
        DataContext = _viewModel;

        _viewModel.LogAppended += OnLogAppended;
        _viewModel.LogCleared += OnLogCleared;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.ThemeChanged += OnThemeChanged;

        RefreshLogBrushes();

        // 尽早建立文档，避免 Loaded 之前到达的日志丢失
        ResetDocuments();

        Loaded += (_, _) =>
        {
            ApplyWordWrap();

            // 窗口缩放改变可视宽度，需要重新贴合页宽
            SizeChanged += (_, _) => UpdatePageWidthToContent();
        };
    }

    private static Brush CreateBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    /// <summary>从当前调色板取日志分色，取不到时用兜底色。</summary>
    private Brush ResolveBrush(string key, Brush fallback)
        => TryFindResource(key) as Brush ?? fallback;

    private void RefreshLogBrushes()
    {
        _receiveBrush = ResolveBrush("LogReceiveBrush", CreateBrush("#FF4ED6A0"));
        _sendBrush = ResolveBrush("LogSendBrush", CreateBrush("#FF6BA1FF"));
        _systemBrush = ResolveBrush("LogSystemBrush", CreateBrush("#FF8B94A8"));
        _prefixBrush = ResolveBrush("LogPrefixBrush", CreateBrush("#FF5C667A"));
    }

    /// <summary>主题切换：重取日志配色，并把所有通道已有日志行按新配色刷一遍。</summary>
    private void OnThemeChanged()
    {
        RefreshLogBrushes();

        foreach (FlowDocument document in _documents)
        {
            foreach (Block block in document.Blocks)
            {
                if (block is not Paragraph paragraph || paragraph.Tag is not LogKind kind)
                {
                    continue;
                }

                List<Run> runs = paragraph.Inlines.OfType<Run>().ToList();
                if (runs.Count == 0)
                {
                    continue;
                }

                if (runs.Count >= 2)
                {
                    runs[0].Foreground = _prefixBrush;
                }

                runs[^1].Foreground = BrushFor(kind);
            }
        }
    }

    #region 日志渲染

    /// <summary>为三个通道各建一份文档，先显示串口通道的。</summary>
    private void ResetDocuments()
    {
        for (int i = 0; i < _documents.Length; i++)
        {
            _documents[i] = new FlowDocument
            {
                PagePadding = new Thickness(10),
                FontFamily = LogBox.FontFamily,
                FontSize = LogBox.FontSize
            };
        }

        LogBox.Document = _documents[(int)CurrentChannel];
    }

    private Brush BrushFor(LogKind kind) => kind switch
    {
        LogKind.Receive => _receiveBrush,
        LogKind.Send => _sendBrush,
        _ => _systemBrush
    };

    private void OnLogAppended(LogChannel channel, string prefix, string content, LogKind kind)
    {
        FlowDocument document = _documents[(int)channel];

        // 时间戳/字节数用弱化色，正文按收发类型着色
        Brush bodyBrush = BrushFor(kind);

        // 内容里可能带换行（文本模式收到的报文自带 \r\n）。Run 内的换行符在
        // FlowDocument 里不会真正断行，必须拆成多个 Paragraph，否则显示异常。
        string[] lines = content.Replace("\r\n", "\n").Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0), Tag = kind };

            // 只有第一行带前缀，续行留空以便对齐读取
            if (i == 0 && !string.IsNullOrEmpty(prefix))
            {
                paragraph.Inlines.Add(new Run(prefix) { Foreground = _prefixBrush });
            }

            if (lines[i].Length > 0)
            {
                paragraph.Inlines.Add(new Run(lines[i]) { Foreground = bodyBrush });
            }

            // 记录本行宽度，增量维护该通道的最宽值
            NoteLineWidth(document, channel, (i == 0 ? prefix ?? string.Empty : string.Empty) + lines[i]);

            document.Blocks.Add(paragraph);
        }

        TrimDocument(document);

        // 后台通道只记录不渲染；当前通道才需要贴合页宽并滚动到底
        if (channel == CurrentChannel)
        {
            UpdatePageWidthToContent();
            ScrollToBottom();
        }
    }

    /// <summary>控制段落数量，避免长时间运行后富文本渲染变慢。</summary>
    private void TrimDocument(FlowDocument document)
    {
        int limit = Math.Max(200, _viewModel.LogLineLimit);

        while (document.Blocks.Count > limit)
        {
            Block? first = document.Blocks.FirstBlock;
            if (first is null)
            {
                break;
            }

            document.Blocks.Remove(first);
        }
    }

    private void ScrollToBottom()
    {
        if (_viewModel.AutoScroll)
        {
            LogBox.ScrollToEnd();
        }
    }

    private void OnLogCleared(LogChannel channel)
    {
        FlowDocument document = _documents[(int)channel];
        document.Blocks.Clear();

        // 清空后最宽值失效，重新贴合可视宽度
        _widestLineWidths[(int)channel] = 0;
        UpdatePageWidthToContent(document);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.SelectedTabIndex):
                SwitchChannel();
                break;

            case nameof(MainViewModel.WordWrap):
                ApplyWordWrap();
                break;

            case nameof(MainViewModel.LogFontSize):
                foreach (FlowDocument document in _documents)
                {
                    document.FontSize = _viewModel.LogFontSize;
                }

                // 字号变了，之前量出来的宽度全部失效
                Array.Clear(_widestLineWidths);
                UpdatePageWidthToContent(recompute: true);
                break;
        }
    }

    /// <summary>页签切换：把日志框切到对应通道的文档，并重新贴合页宽、滚动到底。</summary>
    private void SwitchChannel()
    {
        FlowDocument document = _documents[(int)CurrentChannel];
        LogBox.Document = document;

        ApplyWordWrap();
        ScrollToBottom();
    }

    /// <summary>
    /// FlowDocument 没有 TextWrapping，靠 PageWidth 模拟自动换行：
    /// 自动换行时设为 NaN（跟随可视宽度折行）；不换行时按内容实测宽度设置，
    /// 这样右侧不会留出一大片空白，也只有在真的超宽时才出现横向滚动条。
    /// </summary>
    private void ApplyWordWrap()
    {
        if (_viewModel.WordWrap)
        {
            foreach (FlowDocument document in _documents)
            {
                document.PageWidth = double.NaN;
            }

            LogBox.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        }
        else
        {
            LogBox.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;

            // 从换行切回不换行，已有内容需要重新量宽
            foreach (FlowDocument document in _documents)
            {
                UpdatePageWidthToContent(document, recompute: true);
            }
        }
    }

    /// <summary>
    /// 不自动换行时，按内容实测宽度设置页宽：右侧不会留出大片空白，
    /// 只有内容真的超出可视区时才出现横向滚动条。
    /// 缓存最宽行宽，避免每条日志都全量遍历（高频率收包下会拖慢渲染）。
    /// </summary>
    private void UpdatePageWidthToContent(FlowDocument? document = null, bool recompute = false)
    {
        if (_viewModel.WordWrap)
        {
            return;
        }

        document ??= LogBox.Document;
        int index = Array.IndexOf(_documents, document);

        double viewport = Math.Max(0, LogBox.ActualWidth - LogBox.BorderThickness.Left - LogBox.BorderThickness.Right);
        double padding = document.PagePadding.Left + document.PagePadding.Right;

        if (recompute)
        {
            _widestLineWidths[index] = 0;

            foreach (Block block in document.Blocks)
            {
                if (block is Paragraph paragraph)
                {
                    string text = string.Concat(paragraph.Inlines.OfType<Run>().Select(r => r.Text));

                    if (text.Length > 0)
                    {
                        _widestLineWidths[index] = Math.Max(_widestLineWidths[index], MeasureWidth(document, text));
                    }
                }
            }
        }

        // 内容放得下就收窄到与内容等宽，放不下才撑开
        double width = _widestLineWidths[index] > 0 ? _widestLineWidths[index] + padding + 4 : viewport;
        document.PageWidth = Math.Max(width, viewport);
    }

    private void UpdatePageWidthToContent() => UpdatePageWidthToContent(LogBox.Document);

    /// <summary>把新追加的一行并入所在通道的最宽宽度统计（比全量遍历便宜）。</summary>
    private void NoteLineWidth(FlowDocument document, LogChannel channel, string text)
    {
        if (text.Length > 0)
        {
            int index = (int)channel;
            _widestLineWidths[index] = Math.Max(_widestLineWidths[index], MeasureWidth(document, text));
        }
    }

    /// <summary>用与段落一致的排版参数估算文本宽度（像素）。</summary>
    private double MeasureWidth(FlowDocument document, string text)
    {
        try
        {
            var formatted = new FormattedText(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                document.FontFamily.GetTypefaces().FirstOrDefault() ?? new Typeface("Consolas"),
                document.FontSize,
                Brushes.Black,
                VisualTreeHelper.GetDpi(LogBox).PixelsPerDip);

            return formatted.WidthIncludingTrailingWhitespace;
        }
        catch
        {
            // 排版参数不可用时退回经验值
            return text.Length * 8.0;
        }
    }

    #endregion

    protected override void OnClosing(CancelEventArgs e)
    {
        // 关闭前落盘配置
        _viewModel.SaveSettings();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.LogAppended -= OnLogAppended;
        _viewModel.LogCleared -= OnLogCleared;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.ThemeChanged -= OnThemeChanged;
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    /// <summary>Ctrl+Enter 在多行输入框内也触发发送，而不是插入换行。</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            _viewModel.HandleSendShortcut();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.O && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            _viewModel.HandleToggleConnectionShortcut();
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }
}
