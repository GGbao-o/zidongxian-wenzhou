using AutomatedLine.Domain.Devices;

namespace AutomatedLine.Application.Transfer;

/// <summary>
/// 按设备类型分派 <see cref="ITransferHandler"/>。
/// </summary>
public sealed class TransferHandlerRegistry
{
    /// <summary>类型 → handler。</summary>
    private readonly Dictionary<DeviceType, ITransferHandler> _map = new();

    /// <summary>构造函数：把所有 handler 按类型建索引。</summary>
    /// <param name="handlers">全部 handler</param>
    public TransferHandlerRegistry(IEnumerable<ITransferHandler> handlers)
    {
        foreach (var handler in handlers)
        {
            foreach (var type in Enum.GetValues<DeviceType>())
            {
                if (handler.CanHandle(type))
                {
                    _map[type] = handler;
                }
            }
        }
    }

    /// <summary>取某类型的 handler。</summary>
    /// <param name="type">设备类型</param>
    /// <returns>handler</returns>
    /// <exception cref="NotSupportedException">类型无 handler</exception>
    public ITransferHandler Get(DeviceType type)
        => _map.TryGetValue(type, out var handler)
            ? handler
            : throw new NotSupportedException($"设备类型 {type} 无取放料策略");
}
