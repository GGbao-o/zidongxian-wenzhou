namespace AutomatedLine.Application.Configuration;

/// <summary>
/// 应用程序参数集合。
/// 对应 appsettings.json 的程序参数分区（架构文档 §14.1）。
/// 这些参数属于"可运行时调整"的配置，必须来自配置文件，禁止硬编码在代码里。
/// </summary>
public sealed class AppOptions
{
    /// <summary>采集类参数。</summary>
    public CollectionOptions Collection { get; set; } = new();

    /// <summary>调度类参数。</summary>
    public SchedulingOptions Scheduling { get; set; } = new();

    /// <summary>天车运动类参数（超时与容差）。</summary>
    public CraneOptions Crane { get; set; } = new();

    /// <summary>搬运流程参数（斜床 Y 二次偏移等）。</summary>
    public TransferOptions Transfer { get; set; } = new();

    /// <summary>状态机类参数。</summary>
    public StateMachineOptions StateMachine { get; set; } = new();

    /// <summary>日志类参数。</summary>
    public LoggingOptions Logging { get; set; } = new();
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
/// 天车运动参数。
/// 运动超时与到位/下压容差都由配置文件提供（不再放数据库），手动页与自动引擎共用同一套。
/// </summary>
public sealed class CraneOptions
{
    /// <summary>运动到位超时（毫秒）。超过即判 Timeout。默认 60s。</summary>
    public int MoveTimeoutMs { get; set; } = 60000;

    /// <summary>正常 XYZ 到位容差（mm）。默认 5。</summary>
    public int PositionToleranceMm { get; set; } = 5;

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
