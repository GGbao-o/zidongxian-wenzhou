using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using AutomatedLine.Application.Abstractions;
using AutomatedLine.Infrastructure.Communication;
using AutomatedLine.Tests.Fakes;

namespace AutomatedLine.Tests;

/// <summary>通过本机 Modbus 服务验证真实充退磁脉冲顺序、宽度及单次反馈结果。</summary>
public class CraneMagnetPulseTests
{
    /// <summary>取料有板入口只发送一次 X11 线圈读取，不读取完整状态或执行动作。</summary>
    /// <param name="feedback">PLC 有板反馈</param>
    /// <returns>异步验证任务</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadHasPlateReadsOnlyX11(bool feedback)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var client = new ModbusTcpClient("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, timeoutMs: 500);
            var service = new CraneService(1, "测试天车", "127.0.0.1", new TestLogger(), new FakeConfigService());
            // 注入本机端口，实际读取仍经过生产驱动及 Modbus 客户端。
            var field = typeof(CraneService).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!;
            ((ModbusTcpClient)field.GetValue(service)!).Dispose();
            field.SetValue(service, client);
            var command = service.ReadHasPlateAsync(deadline.Token);
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            var stream = peer.GetStream();
            var read = new byte[12];
            await stream.ReadExactlyAsync(read, deadline.Token);
            Assert.Equal(1, read[7]);
            Assert.Equal(CraneAddress.X11_HasPlate, (read[8] << 8) | read[9]);
            Assert.Equal(1, (read[10] << 8) | read[11]);
            await stream.WriteAsync(new byte[] { read[0], read[1], 0, 0, 0, 4, read[6], 1, 1, feedback ? (byte)1 : (byte)0 }, deadline.Token);
            Assert.Equal(feedback, await command.WaitAsync(deadline.Token));
            Assert.False(stream.DataAvailable);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>写 2 后至少保持 200ms，写 0 后只读对应反馈，直接返回成功或失败。</summary>
    /// <param name="magnetOn">是否验证充磁，否则验证退磁</param>
    /// <param name="feedback">PLC 返回的对应反馈值</param>
    /// <returns>异步验证任务</returns>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task PulseResetsBeforeSingleFeedbackRead(bool magnetOn, bool feedback)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var client = new ModbusTcpClient("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, timeoutMs: 500);
            var service = new CraneService(1, "测试天车", "127.0.0.1", new TestLogger(), new FakeConfigService());
            // 只替换本机测试端口，命令和通信协议使用真实生产实现。
            var field = typeof(CraneService).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!;
            ((ModbusTcpClient)field.GetValue(service)!).Dispose();
            field.SetValue(service, client);
            var trigger = magnetOn ? CraneAddress.ManualMagnetOn : CraneAddress.ManualMagnetOff;
            var coil = magnetOn ? CraneAddress.X6_MagnetizeOk : CraneAddress.X7_DemagnetizeOk;
            var command = magnetOn ? service.MagnetOnAsync(deadline.Token) : service.MagnetOffAsync(deadline.Token);
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            var stream = peer.GetStream();
            var pulse = new Stopwatch();
            var writes = new[] { (CraneAddress.ModeCmd, 2), (CraneAddress.ManualMode, 2), (trigger, 2), (trigger, 0) };
            foreach (var (address, value) in writes)
            {
                var request = new byte[12];
                await stream.ReadExactlyAsync(request, deadline.Token);
                Assert.Equal(6, request[7]);
                Assert.Equal(address, (request[8] << 8) | request[9]);
                Assert.Equal(value, (request[10] << 8) | request[11]);
                if (address == trigger && value == 0)
                {
                    Assert.True(pulse.ElapsedMilliseconds >= 190, $"脉冲过短：{pulse.ElapsedMilliseconds}ms");
                }
                await stream.WriteAsync(request, deadline.Token);
                if (address == trigger && value == 2)
                {
                    pulse.Start();
                }
            }

            var read = new byte[12];
            await stream.ReadExactlyAsync(read, deadline.Token);
            Assert.Equal(1, read[7]);
            Assert.Equal(coil, (read[8] << 8) | read[9]);
            Assert.Equal(1, (read[10] << 8) | read[11]);
            var response = new byte[] { read[0], read[1], 0, 0, 0, 4, read[6], 1, 1, feedback ? (byte)1 : (byte)0 };
            await stream.WriteAsync(response, deadline.Token);

            // 服务端不再回应任何后续读取，旧版轮询/重试无法通过此检查。
            var result = await command.WaitAsync(deadline.Token);
            Assert.Equal(feedback, result.IsSuccess);
            Assert.Equal(feedback ? CraneCommandCode.Completed : CraneCommandCode.Rejected, result.Code);
            if (!feedback)
            {
                Assert.Contains(magnetOn ? "X6=0" : "X7=0", result.Message);
            }
            Assert.False(stream.DataAvailable);
        }
        finally
        {
            listener.Stop();
        }
    }
}
