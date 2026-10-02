using System.Threading.Channels;
using AutomatedLine.Domain.Tasks;

namespace AutomatedLine.Application.Runtime;

/// <summary>
/// 每台天车一个任务队列（<see cref="Channel{T}"/>），作为"调度器 → Worker"的**唯一交接通道**。
/// 调度器只 <c>TryWrite</c> 派发，**不再写 <c>Crane.Runtime</c>**；Worker 出队并**独占**写该车 Runtime。
/// 队列容量 1：既有任务占位时再次派发会失败，避免同一台车堆积任务。
/// </summary>
public sealed class CraneTaskChannels
{
    /// <summary>天车号 → 任务队列。</summary>
    private readonly Dictionary<int, Channel<TransportTask>> _channels = new();

    /// <summary>保护锁。</summary>
    private readonly object _sync = new();

    /// <summary>取（或创建）某天车的任务队列。容量 1、单读者、多写者。</summary>
    /// <param name="craneId">天车号</param>
    /// <returns>任务队列</returns>
    public Channel<TransportTask> For(int craneId)
    {
        lock (_sync)
        {
            if (!_channels.TryGetValue(craneId, out var channel))
            {
                channel = Channel.CreateBounded<TransportTask>(new BoundedChannelOptions(1)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.Wait
                });
                _channels[craneId] = channel;
            }

            return channel;
        }
    }
}
