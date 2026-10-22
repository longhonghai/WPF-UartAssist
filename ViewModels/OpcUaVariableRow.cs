using WPF_UartAssist.Mvvm;
using WPF_UartAssist.Services;

namespace WPF_UartAssist.ViewModels;

/// <summary>
/// 服务端变量在界面上的一行。值随服务端变化刷新，
/// 也支持从界面直接改值来模拟数据变化。
/// </summary>
public sealed class OpcUaVariableRow : ObservableObject
{
    private readonly OpcUaVariable _variable;

    public OpcUaVariableRow(OpcUaVariable variable)
    {
        _variable = variable;
    }

    public string Name => _variable.Name;

    public string NodeId => _variable.NodeId;

    public string DataType => _variable.DataType;

    public string Value => _variable.Value;

    /// <summary>服务端被写入后刷新显示。</summary>
    public void Refresh() => OnPropertyChanged(nameof(Value));

    /// <summary>本地改值。</summary>
    public void SetValue(object value)
    {
        _variable.SetValue(value);
        OnPropertyChanged(nameof(Value));
    }

    /// <summary>下拉框显示文字。</summary>
    public string Display => $"{Name}（{DataType}）";
}
