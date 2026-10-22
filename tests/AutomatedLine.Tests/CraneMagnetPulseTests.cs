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

    /// <summary>充退磁脉冲固定500ms，清0后默认等1000ms才单读对应反馈。</summary>
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
            var feedbackWait = new Stopwatch();
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
                    Assert.InRange(pulse.ElapsedMilliseconds, 490, 1200);
                    feedbackWait.Start();
                }
                await stream.WriteAsync(request, deadline.Token);
                if (address == trigger && value == 2)
                {
                    pulse.Start();
                }
            }

            var read = new byte[12];
            await stream.ReadExactlyAsync(read, deadline.Token);
            Assert.InRange(feedbackWait.ElapsedMilliseconds, 990, 1900);
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

    /// <summary>每次动作读取新配置；当前动作即使配置再次变化，仍使用开始时固定的反馈等待。</summary>
    /// <returns>异步通信验证任务。</returns>
    [Fact]
    public async Task FeedbackDelay_UsesLatestConfig_PerAction()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new ModbusTcpClient("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port);
        var config = new FakeConfigService();
        var logger = new TestLogger();
        var service = new CraneService(1, "测试天车", "127.0.0.1", logger, config);
        var field = typeof(CraneService).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((ModbusTcpClient)field.GetValue(service)!).Dispose();
        field.SetValue(service, client);
        TcpClient? peer = null;
        try
        {
            foreach (var (on, delay) in new[] { (true, 80), (false, 150), (true, 0) })
            {
                // 使用新对象模拟配置系统重载，证明驱动不依赖构造时保存的参数对象。
                config.Options = new() { Crane = new() { MagnetFeedbackDelayMs = delay } };
                var needsMode = peer is null;
                var command = on ? service.MagnetOnAsync(deadline.Token) : service.MagnetOffAsync(deadline.Token);
                peer ??= await listener.AcceptTcpClientAsync(deadline.Token);
                var stream = peer.GetStream();
                var trigger = on ? CraneAddress.ManualMagnetOn : CraneAddress.ManualMagnetOff;
                var writes = needsMode
                    ? new[] { (CraneAddress.ModeCmd, 2), (CraneAddress.ManualMode, 2), (trigger, 2), (trigger, 0) }
                    : new[] { (trigger, 2), (trigger, 0) };
                var pulse = new Stopwatch();
                var feedbackWait = new Stopwatch();
                foreach (var (address, value) in writes)
                {
                    var request = new byte[12];
                    await stream.ReadExactlyAsync(request, deadline.Token);
                    Assert.Equal(6, request[7]);
                    Assert.Equal(address, (request[8] << 8) | request[9]);
                    Assert.Equal(value, (request[10] << 8) | request[11]);
                    if (address == trigger && value == 2)
                    {
                        pulse.Start();
                        config.Options = new() { Crane = new() { MagnetFeedbackDelayMs = 5000 } };
                    }
                    if (address == trigger && value == 0)
                    {
                        Assert.InRange(pulse.ElapsedMilliseconds, 490, 1200);
                        feedbackWait.Start();
                    }
                    await stream.WriteAsync(request, deadline.Token);
                }
                var read = new byte[12];
                await stream.ReadExactlyAsync(read, deadline.Token);
                Assert.InRange(feedbackWait.ElapsedMilliseconds, delay - 10, 700);
                Assert.Equal(1, read[7]);
                Assert.Equal(on ? CraneAddress.X6_MagnetizeOk : CraneAddress.X7_DemagnetizeOk, (read[8] << 8) | read[9]);
                await stream.WriteAsync(new byte[] { read[0], read[1], 0, 0, 0, 4, read[6], 1, 1, 1 }, deadline.Token);
                Assert.True((await command).IsSuccess);
                Assert.Contains(logger.Messages, m => m.Contains($"复位后等待{delay}ms"));
            }
        }
        finally
        {
            peer?.Dispose();
            listener.Stop();
        }
    }

    /// <summary>非法等待时间在任何PLC写入前拒绝，避免Task.Delay负数引入异常或无限等待。</summary>
    /// <param name="on">是否充磁，否则退磁。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NegativeFeedbackDelay_DoesNotSendCommand(bool on)
    {
        var config = new FakeConfigService { Options = new() { Crane = new() { MagnetFeedbackDelayMs = -1 } } };
        var service = new CraneService(1, "测试天车", "127.0.0.1", new TestLogger(), config);
        var result = on ? await service.MagnetOnAsync() : await service.MagnetOffAsync();
        Assert.Equal(CraneCommandCode.Rejected, result.Code);
        Assert.Contains("MagnetFeedbackDelayMs=-1", result.Message);
        Assert.False(service.IsConnected);
    }

    /// <summary>清0后的反馈等待支持当前任务取消，取消后不能再发送反馈读取。</summary>
    /// <param name="on">是否充磁，否则退磁。</param>
    /// <returns>异步通信验证任务。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationDuringFeedbackWait_DoesNotReadCoil(bool on)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var currentTask = new CancellationTokenSource();
        using var client = new ModbusTcpClient("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port);
        var service = new CraneService(1, "测试天车", "127.0.0.1", new TestLogger(), new FakeConfigService());
        var field = typeof(CraneService).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((ModbusTcpClient)field.GetValue(service)!).Dispose();
        field.SetValue(service, client);
        try
        {
            var command = on ? service.MagnetOnAsync(currentTask.Token) : service.MagnetOffAsync(currentTask.Token);
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            var stream = peer.GetStream();
            var trigger = on ? CraneAddress.ManualMagnetOn : CraneAddress.ManualMagnetOff;
            foreach (var (address, value) in new[] { (CraneAddress.ModeCmd, 2), (CraneAddress.ManualMode, 2), (trigger, 2), (trigger, 0) })
            {
                var request = new byte[12];
                await stream.ReadExactlyAsync(request, deadline.Token);
                Assert.Equal(6, request[7]);
                Assert.Equal(address, (request[8] << 8) | request[9]);
                Assert.Equal(value, (request[10] << 8) | request[11]);
                await stream.WriteAsync(request, deadline.Token);
            }
            currentTask.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.WaitAsync(deadline.Token));
            Assert.Equal(0, peer.Available);
        }
        finally { listener.Stop(); }
    }
}
