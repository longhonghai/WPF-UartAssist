using System.Windows;
using WPF_UartAssist.Mvvm;
using WPF_UartAssist.Services;

namespace WPF_UartAssist.ViewModels;

/// <summary>
/// S7 报文构造器。输入区域/DB 号/地址/类型/数量或写入值，
/// 实时生成完整 ISO-on-TCP 帧，可复制或直接发送。
/// </summary>
public sealed class S7ViewModel : ObservableObject
{
    private readonly Action<byte[]> _sendRaw;

    /// <summary>PDU 引用号，逐条递增以便在日志里配对请求与应答。</summary>
    private ushort _pduReference = 1;

    private byte[]? _frameBytes;

    public S7ViewModel(Action<byte[]> sendRaw)
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

    public IReadOnlyList<string> Areas { get; } =
        S7Comm.Areas.Select(a => a.Label).ToArray();

    public IReadOnlyList<string> DataTypes { get; } =
        S7Comm.DataTypes.Select(t => t.Label).ToArray();

    public IReadOnlyList<string> Operations { get; } = ["读变量", "写变量"];

    private string _selectedOperation = "读变量";

    public string SelectedOperation
    {
        get => _selectedOperation;
        set
        {
            if (SetProperty(ref _selectedOperation, value))
            {
                OnPropertyChanged(nameof(IsWrite));
                Regenerate();
            }
        }
    }

    public bool IsWrite => _selectedOperation == "写变量";

    private string _selectedArea = "DB 数据块";

    public string SelectedArea
    {
        get => _selectedArea;
        set
        {
            if (SetProperty(ref _selectedArea, value))
            {
                OnPropertyChanged(nameof(NeedsDbNumber));
                Regenerate();
            }
        }
    }

    /// <summary>只有 DB 区需要填 DB 号。</summary>
    public bool NeedsDbNumber => CurrentArea == 0x84;

    private byte CurrentArea
    {
        get
        {
            foreach (var (label, code) in S7Comm.Areas)
            {
                if (label == _selectedArea)
                {
                    return code;
                }
            }

            return 0x84;
        }
    }

    private string _dbNumberText = "1";

    public string DbNumberText
    {
        get => _dbNumberText;
        set
        {
            if (SetProperty(ref _dbNumberText, value))
            {
                Regenerate();
            }
        }
    }

    private string _byteAddressText = "0";

    /// <summary>字节地址。BOOL 类型时该值无效，实际按位偏移寻址。</summary>
    public string ByteAddressText
    {
        get => _byteAddressText;
        set
        {
            if (SetProperty(ref _byteAddressText, value))
            {
                Regenerate();
            }
        }
    }

    private string _bitAddressText = "0";

    public string BitAddressText
    {
        get => _bitAddressText;
        set
        {
            if (SetProperty(ref _bitAddressText, value))
            {
                Regenerate();
            }
        }
    }

    private string _selectedDataType = "BYTE";

    public string SelectedDataType
    {
        get => _selectedDataType;
        set
        {
            if (SetProperty(ref _selectedDataType, value))
            {
                OnPropertyChanged(nameof(NeedsCount));
                OnPropertyChanged(nameof(IsBoolType));
                Regenerate();
            }
        }
    }

    public bool IsBoolType => CurrentType.ItemCode == 0x01;

    /// <summary>读 BOOL 时数量就是位数，其余按元素个数。</summary>
    public bool NeedsCount => true;

    private S7Comm.DataType CurrentType => S7Comm.FindType(_selectedDataType);

    private string _countText = "1";

    /// <summary>读取的元素个数（BOOL 为位个数）。</summary>
    public string CountText
    {
        get => _countText;
        set
        {
            if (SetProperty(ref _countText, value))
            {
                Regenerate();
            }
        }
    }

    private string _writeDataText = "00";

    /// <summary>写入的原始字节（HEX）。写 BOOL 时填 00/01。写多字节时按类型对齐。</summary>
    public string WriteDataText
    {
        get => _writeDataText;
        set
        {
            if (SetProperty(ref _writeDataText, value))
            {
                Regenerate();
            }
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

    /// <summary>输入合法时给出的一句说明，方便确认地址怎么被解释。</summary>
    public string FrameHint => _frameHint;

    private string _frameHint = string.Empty;

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
        SelectedOperation = "读变量";
        SelectedArea = "DB 数据块";
        DbNumberText = "1";
        ByteAddressText = "0";
        BitAddressText = "0";
        SelectedDataType = "BYTE";
        CountText = "1";
        WriteDataText = "00";
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
        _frameHint = string.Empty;

        var type = CurrentType;

        if (type.Size == 0)
        {
            FrameError = "请选择数据类型。";
            FramePreview = string.Empty;
            return;
        }

        if (IsWrite)
        {
            RegenerateWrite(type);
        }
        else
        {
            RegenerateRead(type);
        }
    }

    private void RegenerateRead(S7Comm.DataType type)
    {
        if (!TryResolveAddress(type, out int byteAddress, out int bitAddress, out string? error))
        {
            FrameError = error!;
            FramePreview = string.Empty;
            return;
        }

        if (!ModbusRtu.TryParseUshort(CountText, out ushort countUshort) || countUshort < 1)
        {
            FrameError = "数量应为正整数（最多 4 位十六进制）。";
            FramePreview = string.Empty;
            return;
        }

        int count = countUshort;

        // BOOL 的地址字段本身就是位地址，数量为位数；其余为位地址 + 元素个数
        byte[] pdu = S7Comm.BuildReadRequest(
            _pduReference, CurrentArea, ResolveDb(), byteAddress, bitAddress, type, count);

        _frameBytes = S7Comm.WrapCotp(pdu);
        FramePreview = HexConverter.ToHexLines(_frameBytes);
        FrameError = string.Empty;
        _frameHint = DescribeHint(byteAddress, bitAddress, type, count, isWrite: false);
        AdvanceReference();
    }

    private void RegenerateWrite(S7Comm.DataType type)
    {
        if (!TryResolveAddress(type, out int byteAddress, out int bitAddress, out string? error))
        {
            FrameError = error!;
            FramePreview = string.Empty;
            return;
        }

        if (!HexConverter.TryParse(WriteDataText, out byte[] writeData, out int errorIndex))
        {
            FrameError = $"第 {errorIndex + 1} 个字符处不是合法 HEX 数据。";
            FramePreview = string.Empty;
            return;
        }

        if (writeData.Length == 0)
        {
            FrameError = "请填写要写入的数据。";
            FramePreview = string.Empty;
            return;
        }

        if (type.ItemCode != 0x01 && writeData.Length % type.Size != 0)
        {
            FrameError = $"写入数据必须是 {type.Size} 字节的整数倍（当前 {writeData.Length} 字节）。";
            FramePreview = string.Empty;
            return;
        }

        byte[] pdu = S7Comm.BuildWriteRequest(
            _pduReference, CurrentArea, ResolveDb(), byteAddress, bitAddress, type, writeData);

        _frameBytes = S7Comm.WrapCotp(pdu);
        FramePreview = HexConverter.ToHexLines(_frameBytes);
        FrameError = string.Empty;
        _frameHint = DescribeHint(byteAddress, bitAddress, type, writeData.Length / type.Size, isWrite: true);
        AdvanceReference();
    }

    private string DescribeHint(int byteAddress, int bitAddress, S7Comm.DataType type, int count, bool isWrite)
    {
        string action = isWrite ? "写入" : "读取";
        string area = S7Comm.DescribeArea(CurrentArea);
        string prefix = NeedsDbNumber ? $"{area}{ResolveDb()}" : area;

        return type.ItemCode == 0x01
            ? $"将{action} {prefix} 的第 {bitAddress} 位起共 {count} 位"
            : $"将{action} {prefix} 字节 {byteAddress} 起 {count} 个 {type.Label}（共 {count * type.Size} 字节）";
    }

    private ushort ResolveDb()
        => ushort.TryParse(DbNumberText?.Trim(), out ushort db) ? db : (ushort)0;

    /// <summary>校验并解析地址输入。BOOL 用位偏移，其余用字节地址。</summary>
    private bool TryResolveAddress(
        S7Comm.DataType type, out int byteAddress, out int bitAddress, out string? error)
    {
        byteAddress = 0;
        bitAddress = 0;
        error = null;

        if (type.ItemCode == 0x01)
        {
            // BOOL：地址字段是位地址，用位偏移表示
            if (!ModbusRtu.TryParseUshort(BitAddressText, out ushort bit) || bit > 7)
            {
                error = "BOOL 的位偏移应为 0–7。";
                return false;
            }

            bitAddress = bit;
            return true;
        }

        if (!ModbusRtu.TryParseUshort(ByteAddressText, out ushort addrUshort))
        {
            error = "字节地址应为 0x0000–0xFFFF 或十进制。";
            return false;
        }

        byteAddress = addrUshort;

        // 非 BOOL 时位偏移固定为 0，地址字段按位地址编码
        return true;
    }

    /// <summary>PDU 引用号自增，避免与 PLC 的请求编号冲突。</summary>
    private void AdvanceReference()
    {
        if (_pduReference < 0xFFFF)
        {
            _pduReference++;
        }
    }
}