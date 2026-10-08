using System.Net;
using System.Net.Sockets;
using System.Reflection;
using AutomatedLine.Infrastructure.Communication;
using AutomatedLine.Tests.Fakes;

namespace AutomatedLine.Tests;

/// <summary>本机通信桩验证真实充磁驱动的指令边界，区分手动准备失败与触发响应丢失。</summary>
public class CraneServiceTests
{
    /// <summary>切手动失败不通知下发；PLC 收到充磁触发但响应丢失时，通知必须已经发生。</summary>
    /// <param name="failOnTrigger">是否在充磁触发写入处中断响应</param>
    /// <returns>异步验证任务</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MagnetOn_NotifiesAtTriggerWrite_EvenWhenResponseIsLost(bool failOnTrigger)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var client = new ModbusTcpClient("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, timeoutMs: 500);
            var service = new CraneService(1, "测试天车", "127.0.0.1", new TestLogger(), new FakeConfigService());
            // 仅替换测试连接的端口，不引入生产注入接口；执行的命令与协议代码均为真实实现。
            var field = typeof(CraneService).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!;
            ((ModbusTcpClient)field.GetValue(service)!).Dispose();
            field.SetValue(service, client);
            var notifications = 0;
            var command = service.MagnetOnAsync(deadline.Token, () => notifications++);
            using (var peer = await listener.AcceptTcpClientAsync(deadline.Token))
            {
                var stream = peer.GetStream();
                while (true)
                {
                    var request = new byte[12];
                    await stream.ReadExactlyAsync(request, deadline.Token);
                    Assert.Equal(6, request[7]);
                    var address = (request[8] << 8) | request[9];
                    Assert.Equal(2, (request[10] << 8) | request[11]);
                    if (!failOnTrigger || address == CraneAddress.ManualMagnetOn)
                    {
                        Assert.Equal(failOnTrigger ? 1 : 0, notifications);
                        // 不回复并断开：PLC 可能已执行，驱动不能等待写入成功才记录充磁边界。
                        break;
                    }
                    Assert.Equal(0, notifications);
                    await stream.WriteAsync(request, deadline.Token);
                }
            }
            var result = await command.WaitAsync(deadline.Token);
            Assert.False(result.IsSuccess);
            Assert.Equal(failOnTrigger ? 1 : 0, notifications);
        }
        finally
        {
            listener.Stop();
        }
    }
}
