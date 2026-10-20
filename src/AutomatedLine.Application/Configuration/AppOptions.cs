namespace AutomatedLine.Application.Configuration;

/// <summary>
/// 应用程序参数集合。
/// 对应 appsettings.json 的程序参数分区（架构文档 §14.1）。
/// 这些参数属于"可运行时调整"的配置，必须来自配置文件，禁止硬编码在代码里。
/// </summary>
public sealed class AppOptions
{
    /// <summary>新工件首次启动时使用的工艺模板，已启动工件使用独立快照。</summary>
    public ProcessPlanOptions ProcessPlan { get; set; } = new();

    /// <summary>手动操作的确认参数；不在参数管理页面展示。</summary>
    public ManualOperationOptions ManualOperation { get; set; } = new();

    /// <summary>ERP文件接收参数；合法工件按文件行序自动启动入队，不改变线路开关。</summary>
    public ErpImportOptions ErpImport { get; set; } = new();

    /// <summary>采集类参数。</summary>
    public CollectionOptions Collection { get; set; } = new();

    /// <summary>调度类参数。</summary>
    public SchedulingOptions Scheduling { get; set; } = new();

    /// <summary>天车动作参数（运动超时、容差与充退磁反馈等待时间）。</summary>
    public CraneOptions Crane { get; set; } = new();

    /// <summary>搬运流程参数（斜床 Y 二次偏移等）。</summary>
    public TransferOptions Transfer { get; set; } = new();

    /// <summary>状态机类参数。</summary>
    public StateMachineOptions StateMachine { get; set; } = new();

    /// <summary>日志类参数。</summary>
    public LoggingOptions Logging { get; set; } = new();
}

/// <summary>手动操作防误触参数，仅用于页面确认，不改变自动搬运或底层运动。</summary>
public sealed class ManualOperationOptions
{
    /// <summary>绝对移动的确认密码；由JSON配置，未配置或空白时禁止本页绝对移动。</summary>
    public string AbsoluteMovePassword { get; set; } = string.Empty;
}

/// <summary>ERP批量任务文件的路径、检查节拍和文本编码，监听循环每轮读取最新值。</summary>
public sealed class ErpImportOptions
{
    /// <summary>ERP写完一批后等待接收的TXT完整路径。</summary>
    public string FilePath { get; set; } = @"D:\job1\job1.txt";

    /// <summary>一轮处理结束后的等待时间，毫秒；默认500，非正值视为配置错误。</summary>
    public int PollIntervalMs { get; set; } = 500;

    /// <summary>文件编码名称；默认utf-8，也可配置gbk。</summary>
    public string Encoding { get; set; } = "utf-8";
}

/// <summary>采集参数。决定状态刷新的实时性与通信压力。</summary>
public sealed class CollectionOptions
{
    /// <summary>设备 PLC 信号采集周期（毫秒）。默认 500ms（架构文档 §8.2）。</summary>
    public int DeviceIntervalMs { get; set; } = 500;

    /// <summary>每台天车一轮采集结束后的等待时间（毫秒）；跨车独立采集，默认 500ms。</summary>
    public int CraneIntervalMs { get; set; } = 500;

    /// <summary>天车快照从读取开始算起的最大可用年龄（毫秒），过期不展示为有效状态、不参与选车；默认 2000ms。</summary>
    public int CraneSnapshotMaxAgeMs { get; set; } = 2000;

    /// <summary>设备单次通信期限及冷启动等待天车有效快照的上限（毫秒）；天车客户端单次请求仍使用原默认期限。</summary>
    public int CommunicationTimeoutMs { get; set; } = 3000;
}

/// <summary>调度参数。决定候选任务重算与派发的节奏。</summary>
public sealed class SchedulingOptions
{
    /// <summary>候选任务生成周期（毫秒）。</summary>
    public int GenerateIntervalMs { get; set; } = 500;

    /// <summary>集中调度周期（毫秒）。</summary>
    public int ScheduleIntervalMs { get; set; } = 500;
}

/// <summary>
/// 天车运动与磁铁动作参数。
/// 运动超时、到位/下压容差及充退磁反馈等待时间由配置文件提供，手动页与自动引擎共用同一套。
/// </summary>
public sealed class CraneOptions
{
    /// <summary>充退磁写0后到单读反馈前的等待时间（毫秒），须为非负整数；默认1000，每次动作开始读取。</summary>
    public int MagnetFeedbackDelayMs { get; set; } = 1000;

    /// <summary>运动到位超时（毫秒）。超过即判 Timeout。默认 60s。</summary>
    public int MoveTimeoutMs { get; set; } = 60000;

    /// <summary>正常XYZ到位容差（mm），默认30；参与轴还须实时确认对应Busy=0。</summary>
    public int PositionToleranceMm { get; set; } = 30;

    /// <summary>源/目标上方XY精定位容差（mm），默认5；绝对编码器误差严格大于此值才微调一次，必须大于0。</summary>
    public int XyRefineToleranceMm { get; set; } = 5;

    /// <summary>XY微调读取编码器前的一次等待（毫秒），默认500；允许0，不做重复稳定采样。</summary>
    public int XyRefineSettleMs { get; set; } = 500;

    /// <summary>下压容差（mm）：仅当 X2 下压触发时，用它判断 Z 是否真的到位。默认 15。</summary>
    public int PressureToleranceMm { get; set; } = 15;
}

/// <summary>状态机参数。用超时兜住"PLC 不回信号"的异常场景。</summary>
public sealed class StateMachineOptions
{
    /// <summary>
    /// 握手信号超时（毫秒）：给设备/天车下发信号后读反馈，超时即抛异常由程序处理。默认 60s。
    /// </summary>
    public int HandshakeTimeoutMs { get; set; } = 60000;

    /// <summary>天车状态机（Worker）推进周期（毫秒）。</summary>
    public int ExecuteIntervalMs { get; set; } = 500;

    /// <summary>取料位X11明确无板时允许的追加下降次数；不含原位检查，默认3次，0表示不补降。</summary>
    public int PickPlateRetryCount { get; set; } = 3;

    /// <summary>取料无板每次仅Z轴追加下降的指令距离（mm），必须为正整数；默认5mm。</summary>
    public int PickPlateStepMm { get; set; } = 5;
}

/// <summary>日志参数。</summary>
public sealed class LoggingOptions
{
    /// <summary>日志根目录（相对程序运行目录）。</summary>
    public string Directory { get; set; } = "Logs";

    /// <summary>日志保留天数，超期目录自动清理。</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>单个日志文件最大容量（字节），超出后滚动。</summary>
    public long MaxFileSizeBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>抽样日志间隔（秒）。高频循环（如采集）用它控制落盘频率，默认 20s。</summary>
    public int SampleSeconds { get; set; } = 20;
}
