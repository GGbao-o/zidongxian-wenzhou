namespace AutomatedLine.Application.Abstractions;

/// <summary>
/// PLC 通信驱动的基础抽象。
/// 设备驱动与天车驱动都继承它，统一"连接 / 断开 / 整体读快照 / 按点位写"四类操作。
/// 设计要点：
/// <list type="bullet">
/// <item>上层只依赖本接口，不依赖 Modbus/MC/TCP 等具体协议；换协议时业务层零改动（架构文档 §9.1）。</item>
/// <item>信号点位名（地址、类型、读写权限）定义在设备/天车的代码类中，不进数据库（架构文档 §6.2、§17 不变式 8），
/// 这里只按"点名"读写，不关心地址细节。</item>
/// </list>
/// </summary>
public interface IPlcDriver
{
    /// <summary>当前是否已连接。</summary>
    bool IsConnected { get; }

    /// <summary>
    /// 建立连接。失败时抛出异常，由上层决定重连策略。
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 断开连接。要求幂等：未连接时调用不得抛异常。
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 一次性读取全部信号，返回"点名 → 值"的快照。
    /// 之所以整体读而不是逐点读，是为了保证同一份快照来自同一采集时刻，避免前后不一致（架构文档 §8.2）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>信号点名到值的只读字典</returns>
    Task<IReadOnlyDictionary<string, object?>> ReadAllSignalsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 按点名写入单个信号值（如目标位置、动作字、握手输出）。
    /// </summary>
    /// <param name="pointName">信号点名（在代码类中定义）</param>
    /// <param name="value">待写入的值</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task WriteSignalAsync(string pointName, object? value, CancellationToken cancellationToken = default);
}
