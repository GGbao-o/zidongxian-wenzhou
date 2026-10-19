using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Configuration;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Application.Transfer;
using AutomatedLine.Application.Transfer.Handlers;
using AutomatedLine.Domain.Cranes;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Domain.Workpieces;
using AutomatedLine.Infrastructure.Communication;
using AutomatedLine.Infrastructure.Drivers;
using AutomatedLine.Tests.Fakes;

namespace AutomatedLine.Tests;

/// <summary>验证下料架完成脉冲的真实线圈地址、回升顺序、应答计时及异常复位，不访问现场设备。</summary>
public sealed class UnloadRackPlaceDoneTests
{
    /// <summary>仅下料架通过FC05写PDU1，上下料架的FC02输入映射保持原样。</summary>
    /// <returns>异步协议验证任务。</returns>
    [Fact]
    public async Task Driver_WritesFc05AtPduOneAndPreservesInputMapping()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var driver = new ModbusDeviceDriver(9, "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
            RackAddress.UnitId, DeviceModbusMaps.Rack(DeviceType.UnloadRack), new TestLogger());
        var server = ReplyAsync();
        try
        {
            await driver.WriteSignalAsync("PlaceDone", 1, guard.Token);
            await driver.WriteSignalAsync("PlaceDone", 0, guard.Token);
            var requests = await server;
            Assert.All(requests, frame =>
            {
                Assert.Equal((byte)254, frame[6]);
                Assert.Equal((byte)5, frame[7]);
                Assert.Equal(1, (frame[8] << 8) | frame[9]);
            });
            Assert.Equal(new[] { 0xFF00, 0 }, requests.Select(frame => (frame[10] << 8) | frame[11]));
            Assert.DoesNotContain(DeviceModbusMaps.Rack(DeviceType.LoadRack), point => point.Writable);
            var input = Assert.Single(DeviceModbusMaps.Rack(DeviceType.UnloadRack).Where(point => !point.Writable));
            Assert.Equal(2, input.Address);
            Assert.True(input.InvertDiscreteInput);
        }
        finally
        {
            guard.Cancel();
            await driver.DisconnectAsync();
            listener.Stop();
            try { await server; } catch (OperationCanceledException) when (guard.IsCancellationRequested) { }
        }

        async Task<List<byte[]>> ReplyAsync()
        {
            using var peer = await listener.AcceptTcpClientAsync(guard.Token);
            var frames = new List<byte[]>();
            for (var i = 0; i < 2; i++)
            {
                var frame = new byte[12];
                await peer.GetStream().ReadExactlyAsync(frame, guard.Token);
                frames.Add(frame);
                await peer.GetStream().WriteAsync(frame, guard.Token);
            }
            return frames;
        }
    }

    /// <summary>写1只能发生在回升成功之后，1秒保持从写1应答完成后计时。</summary>
    /// <returns>异步时序验证任务。</returns>
    [Fact]
    public async Task Place_WaitsForSafeRiseAndHighAcknowledgementBeforeHolding()
    {
        var (ctx, target, crane, driver, _) = Build();
        var highSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledgeHigh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long highAcknowledgedAt = 0;
        long resetStartedAt = 0;
        driver.OnWrite = async (_, value, _) =>
        {
            Assert.Equal(new[] { "MOVE(2000,0,390)", "MAGNET_OFF", "MOVE(2000,0,500)" }, crane.Actions);
            if (Equals(value, 1))
            {
                highSeen.TrySetResult();
                await acknowledgeHigh.Task;
            }
            else resetStartedAt = Stopwatch.GetTimestamp();
        };
        var place = new UnloadRackHandler().PlaceAsync(ctx, target, CancellationToken.None);
        try
        {
            await highSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(1100);
            Assert.Equal(new[] { "PlaceDone=1" }, driver.Writes);
            Assert.False(place.IsCompleted);
            highAcknowledgedAt = Stopwatch.GetTimestamp();
        }
        finally { acknowledgeHigh.TrySetResult(); }
        await place.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { "PlaceDone=1", "PlaceDone=0" }, driver.Writes);
        Assert.True(Stopwatch.GetElapsedTime(highAcknowledgedAt, resetStartedAt).TotalMilliseconds >= 990);
    }

    /// <summary>下降或回升失败时不能发送架体转动通知。</summary>
    /// <param name="failedMove">模拟失败的绝对运动次序。</param>
    /// <returns>异步流程验证任务。</returns>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task MoveFailure_DoesNotWriteCompletion(int failedMove)
    {
        var (ctx, target, crane, driver, _) = Build();
        var moves = 0;
        crane.OnAbsoluteMove = (_, _) => Task.FromResult(++moves == failedMove
            ? CraneCommandResult.Fail(CraneCommandCode.Timeout, "模拟运动失败")
            : CraneCommandResult.Ok(CraneCommandCode.InPosition, "到位"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new UnloadRackHandler().PlaceAsync(ctx, target, CancellationToken.None));
        Assert.Empty(driver.Writes);
    }

    /// <summary>退磁失败时板可能仍被磁铁吸住，不能回升后通知架体转动。</summary>
    /// <returns>异步流程验证任务。</returns>
    [Fact]
    public async Task MagnetOffFailure_DoesNotRiseOrWriteCompletion()
    {
        var (ctx, target, crane, driver, _) = Build();
        crane.MagnetOffException = new IOException("模拟退磁失败");

        await Assert.ThrowsAsync<IOException>(() => new UnloadRackHandler().PlaceAsync(ctx, target, CancellationToken.None));

        Assert.Equal(new[] { "MOVE(2000,0,390)", "MAGNET_OFF" }, crane.Actions);
        Assert.Empty(driver.Writes);
    }

    /// <summary>回升刚返回时若搬运已取消，不应开始写1或无条件复位一个尚未触发的完成位。</summary>
    /// <returns>异步取消验证任务。</returns>
    [Fact]
    public async Task CanceledBeforePulse_DoesNotWriteCompletion()
    {
        var (ctx, target, crane, driver, _) = Build();
        using var execution = new CancellationTokenSource();
        var moves = 0;
        crane.OnAbsoluteMove = (_, _) =>
        {
            if (++moves == 2) execution.Cancel();
            return Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.InPosition, "到位"));
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new UnloadRackHandler().PlaceAsync(ctx, target, execution.Token));

        Assert.Empty(driver.Writes);
    }

    /// <summary>写1失败可能已被PLC接收，仍单次复位；复位失败不能替换最初的写入异常。</summary>
    /// <param name="resetFails">是否同时模拟写0失败。</param>
    /// <returns>异步异常验证任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HighFailure_StillResetsAndPreservesOriginalException(bool resetFails)
    {
        var (ctx, target, _, driver, logger) = Build();
        var failure = new IOException("写1应答丢失");
        driver.OnWrite = (_, value, _) => Equals(value, 1) ? Task.FromException(failure)
            : resetFails ? Task.FromException(new IOException("写0失败")) : Task.CompletedTask;

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => new UnloadRackHandler().PlaceAsync(ctx, target, CancellationToken.None)));
        Assert.Equal(new[] { "PlaceDone=1", "PlaceDone=0" }, driver.Writes);
        if (resetFails) Assert.Contains(logger.Messages, message => message.Contains("复位失败"));
    }

    /// <summary>保持期间取消时仍使用独立令牌复位，保留原取消结果。</summary>
    /// <returns>异步取消验证任务。</returns>
    [Fact]
    public async Task CanceledHold_ResetsWithIndependentToken()
    {
        var (ctx, target, _, driver, _) = Build();
        using var execution = new CancellationTokenSource();
        driver.OnWrite = (_, value, token) =>
        {
            if (Equals(value, 1)) execution.Cancel();
            else
            {
                Assert.NotEqual(execution.Token, token);
                Assert.False(token.IsCancellationRequested);
                Assert.True(token.CanBeCanceled);
            }
            return Task.CompletedTask;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new UnloadRackHandler().PlaceAsync(ctx, target, execution.Token));
        Assert.Equal(new[] { "PlaceDone=1", "PlaceDone=0" }, driver.Writes);
    }

    /// <summary>正常保持完成后写0失败或超过独立期限必须报错，不能记Place完成。</summary>
    /// <param name="timeout">是否模拟复位等待超过独立超时。</param>
    /// <returns>异步复位验证任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetFailure_DoesNotCompletePlace(bool timeout)
    {
        var (ctx, target, _, driver, logger) = Build();
        ctx.Options.Collection.CommunicationTimeoutMs = 100;
        driver.OnWrite = (_, value, token) => Equals(value, 1) ? Task.CompletedTask
            : timeout ? Task.Delay(Timeout.Infinite, token) : Task.FromException(new IOException("复位通信失败"));

        var place = new UnloadRackHandler().PlaceAsync(ctx, target, CancellationToken.None);
        if (timeout) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => place.WaitAsync(TimeSpan.FromSeconds(5)));
        else await Assert.ThrowsAsync<IOException>(() => place);
        Assert.Equal(new[] { "PlaceDone=1", "PlaceDone=0" }, driver.Writes);
        Assert.DoesNotContain(logger.Messages, message => message.Contains("放料：完成"));
        Assert.Contains(logger.Messages, message => message.Contains("复位失败"));
    }

    // 原有驱动桩记录全部动作；测试不建立任何现场连接。
    private static (TransferContext Context, UnloadRack Target, FakeCraneDriver Crane, FakeDeviceDriver Driver, TestLogger Logger) Build()
    {
        var crane = new Crane(1, 1, "天车1") { SafeX = 0, SafeZ = 500 };
        var target = new UnloadRack(9, "下料架", 2000) { Signals = TestSignals.Of(("HasMaterial", false)) };
        var craneDriver = new FakeCraneDriver();
        var driver = new FakeDeviceDriver(target.Id);
        var drivers = new DriverRegistry();
        drivers.RegisterCrane(craneDriver);
        drivers.RegisterDevice(driver);
        var store = new FakeConfigurationStore();
        store.Devices.Add(new DeviceConfigDto { Id = target.Id, PositionX = 2000, PositionY = 0, PositionZ = 500 });
        var logger = new TestLogger();
        var ctx = new TransferContext(crane, new WorkpieceTask(new WorkpieceInfo { Diameter = 100, Length = 700 }),
            drivers, store, new AppOptions(), logger);
        return (ctx, target, craneDriver, driver, logger);
    }
}
