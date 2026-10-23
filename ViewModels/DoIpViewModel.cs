using System.Windows;
using WPF_UartAssist.Mvvm;
using WPF_UartAssist.Services;

namespace WPF_UartAssist.ViewModels;

/// <summary>
/// DoIP（ISO 13400-2）报文构造器。按所选报文类型拼 8 字节头 + 负载，
/// 可复制或直接通过当前 DoIP 连接发出。
/// </summary>
public sealed class DoIpViewModel : ObservableObject
{
    private readonly Action<byte[]> _sendRaw;
    private byte[]? _frameBytes;

    public DoIpViewModel(Action<byte[]> sendRaw)
    {
        _sendRaw = sendRaw;
        Regenerate();
    }

    /// <summary>面板显示状态。构造器整块比较占地方，收起来可以给日志腾空间。</summary>
    private bool _isVisible;

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (SetProperty(ref _isVisible, value))
            {
                VisibilityChanged?.Invoke();
            }
        }
    }

    public event Action? VisibilityChanged;

    // ===== 地址与内容 =====

    private string _sourceAddressText = "0x0E00";

    /// <summary>测试端（诊断仪）地址。</summary>
    public string SourceAddressText
    {
        get => _sourceAddressText;
        set
        {
            if (SetProperty(ref _sourceAddressText, value))
            {
                Regenerate();
            }
        }
    }

    private string _targetAddressText = "0x1001";

    /// <summary>目标 ECU 地址。</summary>
    public string TargetAddressText
    {
        get => _targetAddressText;
        set
        {
            if (SetProperty(ref _targetAddressText, value))
            {
                Regenerate();
            }
        }
    }

    private string _udsText = "22 F1 90";

    /// <summary>诊断报文里的 UDS 数据（HEX）。</summary>
    public string UdsText
    {
        get => _udsText;
        set
        {
            if (SetProperty(ref _udsText, value))
            {
                Regenerate();
            }
        }
    }

    private string _selectedKind = DoIp.FrameKinds[0].Label;

    /// <summary>报文种类。</summary>
    public string SelectedKind
    {
        get => _selectedKind;
        set
        {
            if (SetProperty(ref _selectedKind, value))
            {
                OnPropertyChanged(nameof(NeedsUds));
                OnPropertyChanged(nameof(NeedsActivation));
                Regenerate();
            }
        }
    }

    public IReadOnlyList<string> Kinds { get; } = DoIp.FrameKinds.Select(k => k.Label).ToArray();

    public IReadOnlyList<string> ActivationTypes { get; } =
        DoIp.ActivationTypes.Select(t => t.Label).ToArray();

    private string _selectedActivation = DoIp.ActivationTypes[0].Label;

    public string SelectedActivation
    {
        get => _selectedActivation;
        set
        {
            if (SetProperty(ref _selectedActivation, value))
            {
                Regenerate();
            }
        }
    }

    /// <summary>只有诊断报文需要 UDS 数据。</summary>
    public bool NeedsUds => CurrentType == DoIp.PayloadTypeDiagnostic;

    /// <summary>只有路由激活请求需要激活类型。</summary>
    public bool NeedsActivation => CurrentType == DoIp.PayloadTypeRoutingActivationRequest;

    private ushort CurrentType
    {
        get
        {
            foreach ((ushort type, string label) in DoIp.FrameKinds)
            {
                if (label == _selectedKind)
                {
                    return type;
                }
            }

            return DoIp.PayloadTypeDiagnostic;
        }
    }

    // ===== 预览与状态 =====

    private string _framePreview = string.Empty;

    public string FramePreview
    {
        get => _framePreview;
        private set => SetProperty(ref _framePreview, value);
    }

    private string _frameError = string.Empty;

    public string FrameError
    {
        get => _frameError;
        private set
        {
            if (SetProperty(ref _frameError, value))
            {
                OnPropertyChanged(nameof(HasFrameError));
            }
        }
    }

    public bool HasFrameError => !string.IsNullOrEmpty(_frameError);

    private string _frameHint = string.Empty;

    public string FrameHint
    {
        get => _frameHint;
        private set => SetProperty(ref _frameHint, value);
    }

    public bool HasFrame => _frameBytes is { Length: > 0 };

    // ===== 命令 =====

    private RelayCommand? _sendFrameCommand;

    public RelayCommand SendFrameCommand => _sendFrameCommand ??= new RelayCommand(
        () =>
        {
            if (_frameBytes is { Length: > 0 })
            {
                _sendRaw(_frameBytes);
            }
        },
        () => HasFrame);

    private RelayCommand? _sendRoutingActivationCommand;

    /// <summary>快捷：不改下拉，直接发路由激活请求（连上后第一步）。</summary>
    public RelayCommand SendRoutingActivationCommand => _sendRoutingActivationCommand ??= new RelayCommand(
        () =>
        {
            if (!ModbusRtu.TryParseUshort(SourceAddressText, out ushort source))
            {
                return;
            }

            byte activation = ParseActivation();
            byte[] payload = DoIp.BuildRoutingActivationPayload(source, activation);
            _sendRaw(DoIp.BuildFrame(DoIp.DefaultVersion, DoIp.PayloadTypeRoutingActivationRequest, payload));
        });

    private RelayCommand? _copyFrameCommand;

    public RelayCommand CopyFrameCommand => _copyFrameCommand ??= new RelayCommand(
        () =>
        {
            if (_frameBytes is { Length: > 0 })
            {
                Clipboard.SetText(FramePreview);
            }
        },
        () => HasFrame);

    private RelayCommand? _clearFieldsCommand;

    public RelayCommand ClearFieldsCommand => _clearFieldsCommand ??= new RelayCommand(() =>
    {
        SourceAddressText = "0x0E00";
        TargetAddressText = "0x1001";
        UdsText = "22 F1 90";
        SelectedKind = DoIp.FrameKinds[0].Label;
        SelectedActivation = DoIp.ActivationTypes[0].Label;
    });

    // ===== 组帧 =====

    /// <summary>按当前输入重新生成报文。</summary>
    private void Regenerate()
    {
        _frameBytes = null;

        if (!ModbusRtu.TryParseUshort(SourceAddressText, out ushort source))
        {
            FrameError = "源地址应为 0x0000–0xFFFF";
            FramePreview = string.Empty;
            FrameHint = string.Empty;
            return;
        }

        ushort type = CurrentType;
        byte[] payload;

        switch (type)
        {
            case DoIp.PayloadTypeDiagnostic:
            {
                if (!ModbusRtu.TryParseUshort(TargetAddressText, out ushort target))
                {
                    FrameError = "目标地址应为 0x0000–0xFFFF";
                    FramePreview = string.Empty;
                    FrameHint = string.Empty;
                    return;
                }

                if (!HexConverter.TryParse(UdsText, out byte[] uds, out _) || uds.Length == 0)
                {
                    FrameError = "UDS 数据不是合法 HEX";
                    FramePreview = string.Empty;
                    FrameHint = string.Empty;
                    return;
                }

                payload = DoIp.BuildDiagnosticPayload(source, target, uds);
                FrameHint = $"诊断报文：SA 0x{source:X4} → TA 0x{target:X4}，UDS {uds.Length} 字节";
                break;
            }

            case DoIp.PayloadTypeRoutingActivationRequest:
            {
                byte activation = ParseActivation();
                payload = DoIp.BuildRoutingActivationPayload(source, activation);
                FrameHint = $"路由激活：SA 0x{source:X4}，类型 0x{activation:X2}";
                break;
            }

            case DoIp.PayloadTypeAliveCheckResponse:
            {
                payload = DoIp.BuildAliveCheckPayload(source);
                FrameHint = $"存活检查响应：SA 0x{source:X4}";
                break;
            }

            default:
            {
                payload = [];
                FrameHint = "该报文没有负载";
                break;
            }
        }

        byte[] frame = DoIp.BuildFrame(DoIp.DefaultVersion, type, payload);
        _frameBytes = frame;
        FramePreview = HexConverter.ToHex(frame);
        FrameError = string.Empty;
    }

    private byte ParseActivation()
    {
        foreach ((byte code, string label) in DoIp.ActivationTypes)
        {
            if (label == _selectedActivation)
            {
                return code;
            }
        }

        return 0x00;
    }
}
