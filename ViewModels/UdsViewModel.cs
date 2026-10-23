using System.Collections.ObjectModel;
using WPF_UartAssist.Mvvm;
using WPF_UartAssist.Services;

namespace WPF_UartAssist.ViewModels;

/// <summary>UDS 服务的一个输入参数（界面上一个输入框）。</summary>
public sealed class UdsParamRow : ObservableObject
{
    private readonly UdsParamSpec _spec;
    private string _text;

    public UdsParamRow(UdsParamSpec spec, Action changed)
    {
        _spec = spec;
        _text = spec.DefaultValue;
        Changed = changed;
    }

    private Action Changed { get; }

    public string Label => _spec.Label;

    public string Tip => _spec.Tip;

    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value))
            {
                Changed();
            }
        }
    }

    /// <summary>变长字段给宽一点。</summary>
    public double Width => _spec.ByteCount switch
    {
        1 => 52,
        2 => 62,
        3 => 76,
        _ => 150
    };
}

/// <summary>
/// UDS 服务构造器。左侧选分类与服务，右侧按服务定义生成参数输入，
/// 实时拼出 UDS 请求，交给 DoIP 通道以 0x8001 诊断报文发出。
/// </summary>
public sealed class UdsViewModel : ObservableObject
{
    private readonly Action<byte[]> _sendUds;
    private byte[]? _payload;

    public UdsViewModel(Action<byte[]> sendUds)
    {
        _sendUds = sendUds;
        Categories = Uds.Categories;
        _selectedCategory = Categories[0];
        RebuildServices();
        RebuildParams();
    }

    public IReadOnlyList<UdsCategory> Categories { get; }

    private UdsCategory _selectedCategory;

    public UdsCategory SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value))
            {
                RebuildServices();
            }
        }
    }

    /// <summary>当前分类下的服务，供第二个下拉使用。</summary>
    public ObservableCollection<UdsService> Services { get; } = [];

    private UdsService? _selectedService;

    public UdsService? SelectedService
    {
        get => _selectedService;
        set
        {
            if (SetProperty(ref _selectedService, value))
            {
                RebuildParams();
            }
        }
    }

    /// <summary>服务参数输入行。</summary>
    public ObservableCollection<UdsParamRow> Params { get; } = [];

    private string _preview = string.Empty;

    /// <summary>UDS 请求的 HEX。</summary>
    public string Preview
    {
        get => _preview;
        private set => SetProperty(ref _preview, value);
    }

    private string _error = string.Empty;

    public string Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    private string _hint = string.Empty;

    public string Hint
    {
        get => _hint;
        private set => SetProperty(ref _hint, value);
    }

    public bool HasPayload => _payload is { Length: > 0 };

    private RelayCommand? _sendCommand;

    public RelayCommand SendCommand => _sendCommand ??= new RelayCommand(
        () =>
        {
            if (_payload is { Length: > 0 })
            {
                _sendUds(_payload);
            }
        },
        () => HasPayload);

    private void RebuildServices()
    {
        Services.Clear();

        foreach (UdsService service in _selectedCategory.Services)
        {
            Services.Add(service);
        }

        SelectedService = Services.Count > 0 ? Services[0] : null;
    }

    private void RebuildParams()
    {
        Params.Clear();

        if (_selectedService is null)
        {
            Preview = string.Empty;
            return;
        }

        foreach (UdsParamSpec spec in _selectedService.Params)
        {
            Params.Add(new UdsParamRow(spec, Regenerate));
        }

        Regenerate();
    }

    /// <summary>按当前参数重新拼 UDS 请求。</summary>
    private void Regenerate()
    {
        _payload = null;

        if (_selectedService is null)
        {
            Preview = string.Empty;
            Hint = string.Empty;
            return;
        }

        var values = Params.Select(p => p.Text).ToList();

        if (!Uds.TryBuild(_selectedService, values, out byte[] payload, out string error))
        {
            Error = error;
            Preview = string.Empty;
            Hint = string.Empty;
            return;
        }

        _payload = payload;
        Preview = HexConverter.ToHex(payload);
        Error = string.Empty;
        Hint = $"{Uds.SidName(payload[0])}，{payload.Length} 字节";
    }
}
