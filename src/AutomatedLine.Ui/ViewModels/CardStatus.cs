namespace AutomatedLine.Ui.ViewModels;

/// <summary>
/// 大屏卡片边框状态（设备/天车共用）。
/// 颜色优先级：<see cref="Faulted"/>（红） &gt; <see cref="Reserved"/>（黄） &gt; <see cref="Normal"/>（绿）。
/// 由各 ViewModel 计算出一个最终值，XAML 据值设边框色；PLC 点位的"报警点"不参与颜色。
/// </summary>
public enum CardStatus
{
    /// <summary>正常（绿）：已连接且无任务/预约。</summary>
    Normal = 0,

    /// <summary>已预约/有任务（黄）：设备被任务预约，或天车正在执行任务。</summary>
    Reserved = 1,

    /// <summary>故障（红）：连接失败/采集超时/断联/报警。</summary>
    Faulted = 2
}
