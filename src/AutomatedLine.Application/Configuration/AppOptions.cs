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

    /// <summary>天车位置/信号采集周期（毫秒）。</summary>
    public int CraneIntervalMs { get; set; } = 500;

    /// <summary>单次通信请求超时（毫秒）。超时按瞬时故障处理，触发重连重试。</summary>
    public int CommunicationTimeoutMs { get; set; } = 3000;
}

/// <summary>调度参数。决定候选任务重算与派发的节奏。</summary>
public sealed class SchedulingOptions
{
    /// <summary>候选任务生成周期（毫秒）。</summary>
    public int GenerateIntervalMs { get; set; } = 500;

    /// <summary>集中调度周期（毫秒）。</summary>
    public int ScheduleIntervalMs { get; set; } = 500;

    /// <summary>设备预约超时（毫秒）。超时后回收预约，避免死占资源。</summary>
    public int ReservationTimeoutMs { get; set; } = 60000;
}

/// <summary>状态机参数。用超时兜住"PLC 不回信号"的异常场景。</summary>
public sealed class StateMachineOptions
{
    /// <summary>运动到位超时（毫秒）。</summary>
    public int MoveTimeoutMs { get; set; } = 30000;

    /// <summary>取料确认超时（毫秒）。</summary>
    public int PickTimeoutMs { get; set; } = 15000;

    /// <summary>放料确认超时（毫秒）。</summary>
    public int PlaceTimeoutMs { get; set; } = 15000;

    /// <summary>握手信号超时（毫秒）。</summary>
    public int HandshakeTimeoutMs { get; set; } = 10000;

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
