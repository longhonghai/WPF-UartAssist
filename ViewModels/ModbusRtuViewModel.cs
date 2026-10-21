using System.Windows;
using WPF_UartAssist.Mvvm;
using WPF_UartAssist.Services;

namespace WPF_UartAssist.ViewModels;

/// <summary>
/// Modbus-RTU 报文构造器。输入从站地址/功能码/寄存器地址与数量，
/// 实时生成带 CRC16 的请求帧，可复制或直接发送。
/// </summary>
public sealed class ModbusRtuViewModel : ObservableObject
{
    private readonly Action<byte[]> _sendRaw;
    private byte[]? _frameBytes;

    public ModbusRtuViewModel(Action<byte[]> sendRaw)
    {
        _sendRaw = sendRaw;
        Regenerate();
    }

    /// <summary>面板显示状态变化（勾选时主界面会自动切到 HEX 收发模式）。</summary>
    public event Action? VisibilityChanged;

    private bool _isVisible;

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (SetProperty(ref _isVisible, value))
            {
                OnPropertyChanged(nameof(IsHidden));
                VisibilityChanged?.Invoke();
            }
        }
    }

    public bool IsHidden => !_isVisible;

    public IReadOnlyList<string> FunctionCodes { get; } =
        ModbusRtu.Functions.Select(f => f.Label).ToArray();

    private string _selectedFunction = "03 读保持寄存器";

    public string SelectedFunction
    {
        get => _selectedFunction;
        set
        {
            if (SetProperty(ref _selectedFunction, value))
            {
                OnPropertyChanged(nameof(NeedsData));
                Regenerate();
            }
        }
    }

    private string _slaveIdText = "0x01";

    public string SlaveIdText
    {
        get => _slaveIdText;
        set
        {
            if (SetProperty(ref _slaveIdText, value))
            {
                Regenerate();
            }
        }
    }

    private string _addressText = "0x0000";

    public string AddressText
    {
        get => _addressText;
        set
        {
            if (SetProperty(ref _addressText, value))
            {
                Regenerate();
            }
        }
    }

    /// <summary>读功能码时是数量，05/06 时是写入值，0F/10 时是写入数量。</summary>
    private string _quantityText = "1";

    public string QuantityText
    {
        get => _quantityText;
        set
        {
            if (SetProperty(ref _quantityText, value))
            {
                Regenerate();
            }
        }
    }

    /// <summary>0F/10 写入的数据（HEX）。</summary>
    private string _dataText = string.Empty;

    public string DataText
    {
        get => _dataText;
        set
        {
            if (SetProperty(ref _dataText, value))
            {
                OnPropertyChanged(nameof(NeedsData));
                Regenerate();
            }
        }
    }

    /// <summary>0F/10 需要额外输入数据。</summary>
    public bool NeedsData
    {
        get
        {
            byte code = ModbusRtu.ExtractFunctionCode(_selectedFunction);
            return code is 0x0F or 0x10;
        }
    }

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

    public bool HasFrame => _frameBytes is { Length: > 0 };

    #region 命令

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
        SlaveIdText = "0x01";
        SelectedFunction = "03 读保持寄存器";
        AddressText = "0x0000";
        QuantityText = "1";
        DataText = string.Empty;
    });

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

    #endregion

    /// <summary>根据当前输入重新生成报文。</summary>
    private void Regenerate()
    {
        _frameBytes = null;

        if (!ModbusRtu.TryParseUshort(SlaveIdText, out ushort slaveId) || slaveId is < 1 or > 247)
        {
            FrameError = "从设备 ID 应为 1–247";
            FramePreview = string.Empty;
            return;
        }

        if (!ModbusRtu.TryParseUshort(AddressText, out ushort address))
        {
            FrameError = "寄存器地址应为 0x0000–0xFFFF 或十进制";
            FramePreview = string.Empty;
            return;
        }

        if (!ModbusRtu.TryParseUshort(QuantityText, out ushort quantity))
        {
            FrameError = "数量/数值应为 0x0000–0xFFFF 或十进制";
            FramePreview = string.Empty;
            return;
        }

        byte function = ModbusRtu.ExtractFunctionCode(_selectedFunction);
        byte[]? writeData = null;

        switch (function)
        {
            case 0x05:
                // 线圈值：1 → FF00（合），0 → 0000（分），也接受直接输入 FF00/0000
                if (quantity == 1)
                {
                    quantity = 0xFF00;
                }
                else if (quantity == 0)
                {
                    quantity = 0x0000;
                }
                else if (quantity != 0xFF00 && quantity != 0x0000)
                {
                    FrameError = "写单个线圈的值应为 0 / 1 或 0xFF00 / 0x0000";
                    FramePreview = string.Empty;
                    return;
                }

                break;

            case 0x06:
                // 写单个寄存器，quantity 即写入值
                break;

            case 0x03:
            case 0x04:
                if (quantity is < 1 or > 125)
                {
                    FrameError = "读寄存器数量应为 1–125";
                    FramePreview = string.Empty;
                    return;
                }

                break;

            case 0x01:
            case 0x02:
                if (quantity is < 1 or > 2000)
                {
                    FrameError = "读线圈数量应为 1–2000";
                    FramePreview = string.Empty;
                    return;
                }

                break;

            case 0x0F:
            case 0x10:
                if (!HexConverter.TryParse(DataText, out writeData, out _))
                {
                    FrameError = "写入数据不是合法 HEX";
                    FramePreview = string.Empty;
                    return;
                }

                if (function == 0x10)
                {
                    if (writeData.Length == 0 || writeData.Length % 2 != 0)
                    {
                        FrameError = "写多个寄存器的数据必须是完整的 2 字节寄存器";
                        FramePreview = string.Empty;
                        return;
                    }

                    if (quantity != writeData.Length / 2)
                    {
                        FrameError = $"寄存器数量与数据不符：{writeData.Length / 2} 个寄存器需要数量 {writeData.Length / 2}";
                        FramePreview = string.Empty;
                        return;
                    }

                    if (quantity is < 1 or > 123)
                    {
                        FrameError = "写寄存器数量应为 1–123";
                        FramePreview = string.Empty;
                        return;
                    }
                }
                else
                {
                    int expectedBytes = (quantity + 7) / 8;

                    if (quantity is < 1 or > 1968 || writeData.Length != expectedBytes)
                    {
                        FrameError = $"写 {quantity} 个线圈需要 {expectedBytes} 字节数据";
                        FramePreview = string.Empty;
                        return;
                    }
                }

                break;
        }

        byte[] frame = ModbusRtu.BuildRequest((byte)slaveId, function, address, quantity, writeData);
        _frameBytes = frame;
        FramePreview = HexConverter.ToHex(frame);
        FrameError = string.Empty;
    }
}
