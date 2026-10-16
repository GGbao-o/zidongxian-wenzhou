namespace AutomatedLine.Ui.ViewModels;

/// <summary>卡片只读信号；用可空值保留“未知”，避免缺失点位被显示成PLC的0。</summary>
/// <param name="Name">现场可读的信号名称。</param>
/// <param name="Value">true为1、false为0、null为未知。</param>
public sealed record CardSignal(string Name, bool? Value)
{
    /// <summary>同时使用符号和文字表达信号，未知不伪装为未成立。</summary>
    public string Text => $"{Name} {(Value is true ? "● 1" : Value is false ? "○ 0" : "未知")}";
}
