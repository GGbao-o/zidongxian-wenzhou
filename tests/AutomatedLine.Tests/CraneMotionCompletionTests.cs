using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using AutomatedLine.Application.Abstractions;
using AutomatedLine.Infrastructure.Communication;
using AutomatedLine.Tests.Fakes;

namespace AutomatedLine.Tests;

/// <summary>通过本机Modbus服务核对真实运动轮询的位置容差、Busy和分阶段触发，不访问现场PLC。</summary>
public sealed class CraneMotionCompletionTests
{
    /// <summary>位置、速度区或任一Busy读取被对端重置时，重连后重新完整读取且不写运动命令。</summary>
    /// <param name="failedAddress">首次断连的读取地址。</param>
    /// <param name="encoder">是否使用绝对编码器判定；false使用显示坐标。</param>
    /// <returns>异步回环通信验证任务。</returns>
    [Theory]
    [InlineData(5000, true)]
    [InlineData(2501, true)]
    [InlineData(12819, true)]
    [InlineData(12824, true)]
    [InlineData(12829, true)]
    [InlineData(5000, false)]
    [InlineData(12819, false)]
    public async Task Poll_ReadDisconnectRestartsCompleteCycle(int failedAddress, bool encoder)
    {
        await using var h = new MotionPeer(timeoutMs: 3000);
        var disconnected = false;
        h.DisconnectRequest = (fc, address) => fc is 1 or 3 && address == failedAddress && !disconnected && (disconnected = true);
        var result = await h.Poll(1000, 2000, 3000, encoder: encoder);
        Assert.Equal(CraneCommandCode.InPosition, result.Code);
        Assert.True(disconnected);
        Assert.Equal(2, h.Requests.Count(r => r == (3, 5000)));
        Assert.Empty(h.Writes);
        Assert.Empty(h.TargetWrites);
        Assert.Contains(h.Logger.Messages, m => m.Contains("读取恢复") && m.Contains("连续失败1次"));
    }

    /// <summary>运动触发后的读取断连只重读反馈，不会重写目标或重复发送触发脉冲。</summary>
    /// <param name="relative">是否使用相对运动入口。</param>
    /// <returns>异步运动验证任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Movement_ReadDisconnectDoesNotRetrigger(bool relative)
    {
        await using var h = new MotionPeer();
        var disconnected = false;
        h.DisconnectRequest = (fc, address) => fc == 1 && address == 12819 && !disconnected && (disconnected = true);
        var result = relative ? await h.Service.MoveRelativeAsync(30, 0, 0)
            : await h.Service.MoveAbsoluteAsync(1030, 2000, 3000);
        Assert.Equal(CraneCommandCode.InPosition, result.Code);
        Assert.Equal(relative ? 1 : 3, h.TargetWrites.Count);
        foreach (var trigger in relative ? new[] { 4522 } : new[] { 4522, 4521, 4520 })
            Assert.Single(h.Writes.Where(w => w == (trigger, 2)));
    }

    /// <summary>持续连接重置不能重置运动期限，失败结果保留最后一次读取原因。</summary>
    /// <returns>异步期限验证任务。</returns>
    [Fact]
    public async Task Poll_PersistentDisconnectReturnsTimeout()
    {
        await using var h = new MotionPeer(timeoutMs: 900);
        h.DisconnectRequest = (_, address) => address == 5000;
        var result = await h.Poll(1000, CraneService.SkipAxis, CraneService.SkipAxis);
        Assert.Equal(CraneCommandCode.Timeout, result.Code);
        Assert.True(h.Requests.Count >= 2);
        Assert.Contains("读取失败", result.Message);
        Assert.Empty(h.CoilReads);
    }

    /// <summary>单次请求无响应也受剩余运动期限约束，不能再额外等待客户端原有1秒期限。</summary>
    /// <param name="address">无响应的位置或Busy地址。</param>
    /// <returns>异步期限验证任务。</returns>
    [Theory]
    [InlineData(5000)]
    [InlineData(12819)]
    public async Task Poll_StalledReadHonorsRemainingDeadline(int address)
    {
        await using var h = new MotionPeer(timeoutMs: 350);
        h.BeforeReplyAsync = (_, current, token) => current == address ? Task.Delay(5000, token) : Task.CompletedTask;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await h.Poll(1000, CraneService.SkipAxis, CraneService.SkipAxis);
        Assert.Equal(CraneCommandCode.Timeout, result.Code);
        Assert.True(watch.ElapsedMilliseconds < 1100, $"轮询耗时{watch.ElapsedMilliseconds}ms超出剩余期限");
    }

    /// <summary>单次请求达到客户端期限后仍可重连读取，但不重新计算运动总期限。</summary>
    /// <returns>异步通信期限验证任务。</returns>
    [Fact]
    public async Task Poll_RequestTimeoutCanRecoverWithinMotionDeadline()
    {
        await using var h = new MotionPeer(timeoutMs: 4000);
        var delayed = false;
        h.BeforeReplyAsync = (_, address, token) => address == 5000 && !delayed && (delayed = true)
            ? Task.Delay(1200, token) : Task.CompletedTask;
        var result = await h.Poll(1000, CraneService.SkipAxis, CraneService.SkipAxis);
        Assert.Equal(CraneCommandCode.InPosition, result.Code);
        Assert.Contains(h.Logger.Messages, m => m.Contains("读取恢复"));
        Assert.Empty(h.Writes);
    }

    /// <summary>后台请求持有连接锁时，到位读取排队也必须受本次运动期限限制。</summary>
    /// <returns>异步连接锁期限验证任务。</returns>
    [Fact]
    public async Task Poll_DeadlineAlsoLimitsQueuedRead()
    {
        await using var h = new MotionPeer(timeoutMs: 350);
        using var backgroundCancel = new CancellationTokenSource();
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.BeforeReplyAsync = async (_, address, token) =>
        {
            if (address != 5000) return;
            reading.TrySetResult();
            await Task.Delay(5000, token);
        };
        var background = h.Service.ReadStatusAsync(backgroundCancel.Token);
        try
        {
            await reading.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(CraneCommandCode.Timeout,
                (await h.Poll(1000, CraneService.SkipAxis, CraneService.SkipAxis)).Code);
            Assert.Single(h.Requests); // 轮询只在连接锁上等待，没有挤入第二条请求。
        }
        finally
        {
            backgroundCancel.Cancel();
            await background;
        }
    }

    /// <summary>PLC明确拒绝位置或速度区地址时立即上报协议错误，不能当成网络瞬断循环重试。</summary>
    /// <param name="address">返回异常响应的寄存器地址。</param>
    /// <returns>异步协议边界验证任务。</returns>
    [Theory]
    [InlineData(5000)]
    [InlineData(2501)]
    public async Task Poll_RegisterProtocolErrorIsNotRetried(int address)
    {
        await using var h = new MotionPeer();
        h.OnRegisterReply = current => current == address ? new byte[] { 0x83, 2 } : null;
        await Assert.ThrowsAsync<ModbusProtocolException>(() => h.Poll(1000, CraneService.SkipAxis, CraneService.SkipAxis));
        Assert.Single(h.Requests.Where(r => r.Address == address));
        Assert.DoesNotContain(h.Logger.Messages, m => m.Contains("到位读取失败"));
    }

    /// <summary>触发写入时断连仍返回失败，只尽力复位，不得自动重发写2或进入到位重读。</summary>
    /// <returns>异步写入边界验证任务。</returns>
    [Fact]
    public async Task Movement_TriggerDisconnectIsNotRetried()
    {
        await using var h = new MotionPeer();
        var disconnected = false;
        h.DisconnectRequest = (fc, address) => fc == 6 && address == 4522 && !disconnected && (disconnected = true);
        var result = await h.Service.MoveAsync(1000, CraneService.SkipAxis, CraneService.SkipAxis,
            1000, CraneService.SkipAxis, CraneService.SkipAxis, true, 0);
        Assert.Equal(CraneCommandCode.NotConnected, result.Code);
        Assert.Equal(2, h.Requests.Count(r => r == (6, 4522))); // 一次触发失败、一次finally写0。
        Assert.DoesNotContain((4522, 2), h.Writes);
        Assert.Contains((4522, 0), h.Writes);
        Assert.Empty(h.CoilReads);
    }

    /// <summary>断连后读取到Busy仍忙必须继续等，不能沿用上一轮停止反馈误判到位。</summary>
    /// <returns>异步反馈完整性验证任务。</returns>
    [Fact]
    public async Task Poll_ReconnectedBusyStillRequiresStopped()
    {
        await using var h = new MotionPeer(timeoutMs: 900);
        var disconnected = false;
        h.DisconnectRequest = (_, address) => address == 12824 && !disconnected && (disconnected = true);
        h.OnCoil = address => new byte[] { 1, 1, address == 12819 && disconnected ? (byte)1 : (byte)0 };
        var result = await h.Poll(1000, 2000, CraneService.SkipAxis);
        Assert.Equal(CraneCommandCode.Timeout, result.Code);
        Assert.Contains("Busy X=1", result.Message);
    }

    /// <summary>调用者取消不能被断连重读吞掉。</summary>
    /// <returns>异步取消验证任务。</returns>
    [Fact]
    public async Task Poll_CancellationAfterDisconnectPropagates()
    {
        await using var h = new MotionPeer();
        using var cancel = new CancellationTokenSource();
        h.DisconnectRequest = (_, _) => { cancel.Cancel(); return true; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Poll(1000, CraneService.SkipAxis, CraneService.SkipAxis, cancel.Token));
        Assert.Single(h.Requests);
    }

    /// <summary>X2读取异常仍按false处理；下压收尾中的写失败必须直接传播，不能当读取失败重试。</summary>
    /// <param name="stopWriteFails">是否让下压急停写入被PLC拒绝。</param>
    /// <returns>异步下压边界验证任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Poll_PreservesPressureReadAndStopWriteBehavior(bool stopWriteFails)
    {
        await using var h = new MotionPeer();
        h.OnCoil = address => address == 63490
            ? stopWriteFails ? new byte[] { 1, 1, 1 } : new byte[] { 0x81, 2 }
            : new byte[] { 1, 1, 0 };
        h.OnWrite = (_, _) => new byte[] { 0x86, 2 };
        if (stopWriteFails)
            await Assert.ThrowsAnyAsync<IOException>(() => h.Poll(CraneService.SkipAxis, CraneService.SkipAxis, 3000, watchPressure: true));
        else
            Assert.Equal(CraneCommandCode.InPosition, (await h.Poll(CraneService.SkipAxis, CraneService.SkipAxis, 3000, watchPressure: true)).Code);
        Assert.Equal(1, h.Requests.Count(r => r == (3, 5000)));
    }

    /// <summary>绝对目标-1必须写为DINT、触发运动并读取该轴Busy，不能再当作跳过轴。</summary>
    /// <param name="axis">目标为-1的轴：0为X、1为Y、2为Z。</param>
    /// <returns>异步本机通信验证任务。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AbsoluteNegativeOne_IsWrittenTriggeredAndChecked(int axis)
    {
        await using var h = new MotionPeer();
        h.FollowTargetWrites = true;
        var target = new[] { 1000, 2000, 3000 };
        target[axis] = -1;
        var result = await h.Service.MoveAbsoluteAsync(target[0], target[1], target[2]);
        Assert.Equal(CraneCommandCode.InPosition, result.Code);
        Assert.Contains((3102 + axis * 2, -1), h.TargetWrites);
        Assert.Contains((4522 - axis, 2), h.Writes);
        Assert.Contains((4522 - axis, 0), h.Writes);
        Assert.Contains(new[] { 12819, 12824, 12829 }[axis], h.CoilReads);
    }

    /// <summary>相对运动算出显示目标-1时仍执行，零增量的其余轴使用SkipAxis且不写PLC或读取Busy。</summary>
    /// <param name="axis">唯一参与运动的轴。</param>
    /// <returns>异步本机通信验证任务。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RelativeComputedNegativeOne_MovesOnlyRequestedAxis(int axis)
    {
        await using var h = new MotionPeer();
        h.FollowTargetWrites = true;
        var delta = new[] { 0, 0, 0 };
        delta[axis] = -1 - (axis + 1) * 1000;
        var result = await h.Service.MoveRelativeAsync(delta[0], delta[1], delta[2]);
        Assert.Equal(CraneCommandCode.InPosition, result.Code);
        Assert.Equal((3102 + axis * 2, -1), Assert.Single(h.TargetWrites));
        Assert.Contains((4522 - axis, 2), h.Writes);
        Assert.Contains((4522 - axis, 0), h.Writes);
        Assert.NotEmpty(h.CoilReads);
        Assert.All(h.CoilReads, address => Assert.Equal(new[] { 12819, 12824, 12829 }[axis], address));
    }

    /// <summary>真实到位目标-1仍须通过位置判定；反馈相差较大时不能因跳过该轴而误报到位。</summary>
    /// <param name="axis">唯一参与到位检查的轴。</param>
    /// <returns>异步轮询验证任务。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Poll_NegativeOneTargetCannotBypassPositionCheck(int axis)
    {
        await using var h = new MotionPeer(timeoutMs: 350);
        var target = new[] { CraneService.SkipAxis, CraneService.SkipAxis, CraneService.SkipAxis };
        target[axis] = -1;
        var result = await h.Poll(target[0], target[1], target[2]);
        Assert.Equal(CraneCommandCode.Timeout, result.Code);
        Assert.NotEmpty(h.CoilReads);
        Assert.All(h.CoilReads, address => Assert.Equal(new[] { 12819, 12824, 12829 }[axis], address));
    }

    /// <summary>按钮不受旧手动缓存影响，连续点击始终写四帧，也不改变运动前置缓存。</summary>
    /// <param name="cached">原运动前置是否已缓存成功。</param>
    /// <returns>异步通信验证任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetManualMode_AlwaysWritesBothWordsWithoutChangingMotionCache(bool cached)
    {
        await using var h = new MotionPeer();
        var field = typeof(CraneService).GetField("_manualModeSet", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(h.Service, cached);
        Assert.True((await h.Service.SetManualModeAsync()).IsSuccess);
        Assert.True((await h.Service.SetManualModeAsync()).IsSuccess);
        Assert.Equal(new[] { (4001, 2), (4500, 2), (4001, 2), (4500, 2) }, h.Writes.ToArray());
        Assert.Equal(cached, (bool)field.GetValue(h.Service)!);
        Assert.Empty(h.CoilReads);
    }

    /// <summary>任一模式写入被PLC拒绝都不能报告成功，首项失败时禁止继续写使能。</summary>
    /// <param name="failedAddress">PLC返回异常的模式地址。</param>
    /// <returns>异步通信验证任务。</returns>
    [Theory]
    [InlineData(4001)]
    [InlineData(4500)]
    public async Task SetManualMode_PartialFailureIsNotSuccess(int failedAddress)
    {
        await using var h = new MotionPeer();
        h.OnWrite = (address, _) => address == failedAddress ? new byte[] { 0x86, 2 } : null;
        var result = await h.Service.SetManualModeAsync();
        Assert.False(result.IsSuccess);
        Assert.Equal(failedAddress == 4001 ? new[] { (4001, 2) } : new[] { (4001, 2), (4500, 2) }, h.Writes.ToArray());
        Assert.Empty(h.CoilReads);
    }

    /// <summary>模式单点异常只将该项置未知，另一项可重连后读回，坐标和其他状态仍有效。</summary>
    /// <param name="failedAddress">失败的模式地址，-1表示均成功。</param>
    /// <returns>异步通信验证任务。</returns>
    [Theory]
    [InlineData(-1)]
    [InlineData(4001)]
    [InlineData(4500)]
    public async Task ReadStatus_ModeWordFailurePreservesOtherFeedback(int failedAddress)
    {
        await using var h = new MotionPeer();
        h.OnModeRead = address => address == failedAddress ? new byte[] { 0x83, 2 } : new byte[] { 3, 2, 0, 2 };
        var status = await h.Service.ReadStatusAsync();
        Assert.NotNull(status);
        Assert.Equal(1000, status.XEncoder);
        Assert.Equal(failedAddress == 4001 ? null : (int?)2, status.ModeCommand);
        Assert.Equal(failedAddress == 4500 ? null : (int?)2, status.ManualEnable);
        Assert.Equal(new[] { 5000, 2501, 4001, 4500 }, h.RegisterReads.ToArray());
        Assert.Empty(h.Writes);
    }

    /// <summary>默认30mm边界包含正负30，31mm即使已停止也不能到位；Busy=1同样不能到位。</summary>
    /// <param name="offset">X坐标与目标的差值。</param>
    /// <param name="busy">X轴是否一直运动中。</param>
    /// <param name="expected">预期命令结果。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData(-30, false, CraneCommandCode.InPosition)]
    [InlineData(30, false, CraneCommandCode.InPosition)]
    [InlineData(-31, false, CraneCommandCode.Timeout)]
    [InlineData(31, false, CraneCommandCode.Timeout)]
    [InlineData(0, true, CraneCommandCode.Timeout)]
    public async Task Poll_RequiresBothPositionAndStopped(int offset, bool busy, CraneCommandCode expected)
    {
        await using var h = new MotionPeer(timeoutMs: 350);
        h.OnCoil = _ => new byte[] { 1, 1, busy ? (byte)1 : (byte)0 };
        var result = await h.Poll(1000 + offset, CraneService.SkipAxis, CraneService.SkipAxis);
        Assert.Equal(expected, result.Code);
        Assert.NotEmpty(h.CoilReads);
        Assert.All(h.CoilReads, address => Assert.Equal(12819, address));
        Assert.Contains("Busy X=", result.Message);
    }

    /// <summary>逐轴读取Busy直到停止；未参与轴完全不读，非法容差仍按新的30mm兜底。</summary>
    /// <param name="axis">唯一参与运动的轴。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Poll_ReadsOnlyParticipatingAxisAndWaitsForBusyZero(int axis)
    {
        await using var h = new MotionPeer(timeoutMs: 3000, tolerance: 0);
        var reads = 0;
        h.OnCoil = _ => new byte[] { 1, 1, ++reads < 3 ? (byte)1 : (byte)0 };
        var targets = new[] { CraneService.SkipAxis, CraneService.SkipAxis, CraneService.SkipAxis };
        targets[axis] = (axis + 1) * 1000 + 30;
        var result = await h.Poll(targets[0], targets[1], targets[2]);
        Assert.Equal(CraneCommandCode.InPosition, result.Code);
        Assert.Equal(3, reads);
        Assert.All(h.CoilReads, address => Assert.Equal(new[] { 12819, 12824, 12829 }[axis], address));
    }

    /// <summary>绝对与相对入口共用新判据；反馈失败仍返回通信失败，不能当Busy=0。</summary>
    /// <param name="relative">是否从相对移动入口调用。</param>
    /// <param name="failBusy">是否让Busy读取返回Modbus异常。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task MovementEntrypoints_UseLiveBusyAndPropagateReadFailure(bool relative, bool failBusy)
    {
        await using var h = new MotionPeer();
        h.OnCoil = _ => failBusy ? new byte[] { 0x81, 2 } : new byte[] { 1, 1, 0 };
        var result = relative
            ? await h.Service.MoveRelativeAsync(30, 0, 0)
            : await h.Service.MoveAbsoluteAsync(1030, 2000, 3000);
        // 客户端对异常报文断线，沿用原FailCommunication映射为NotConnected。
        Assert.Equal(failBusy ? CraneCommandCode.NotConnected : CraneCommandCode.InPosition, result.Code);
        Assert.Contains(12819, h.CoilReads);
        if (relative) Assert.All(h.CoilReads, address => Assert.Equal(12819, address));
        else if (!failBusy) Assert.Equal(new[] { 12819, 12824, 12829 }, h.CoilReads.ToArray());
        // 运动结束或失败仍执行原触发位复位。
        Assert.Contains((4522, 0), h.Writes);
    }

    /// <summary>上升/下降阶段先确认第一组轴Busy=0，才允许触发后一组。</summary>
    /// <param name="direction">1先XY后Z，2先Z后XY。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task MovementStages_WaitForFirstStageStopped(int direction)
    {
        await using var h = new MotionPeer();
        var firstCoil = direction == 1 ? 12819 : 12829;
        var laterTrigger = direction == 1 ? 4520 : 4522;
        var firstReads = 0;
        h.OnCoil = address =>
        {
            if (address == firstCoil && ++firstReads == 1)
            {
                Assert.DoesNotContain((laterTrigger, 2), h.Writes);
                return new byte[] { 1, 1, 1 };
            }
            return new byte[] { 1, 1, 0 };
        };
        var result = await h.Service.MoveAsync(1000, 2000, 3000, 1000, 2000, 3000, true, direction);
        Assert.Equal(CraneCommandCode.InPosition, result.Code);
        Assert.True(firstReads >= 2);
        Assert.Contains((laterTrigger, 2), h.Writes);
        Assert.Contains(direction == 1 ? 12824 : 12829, h.CoilReads);
    }

    /// <summary>完整状态采集包含三个Busy；单点失败仅变为未知，其余状态继续发布。</summary>
    /// <param name="failedAddress">模拟返回异常的Busy地址，-1表示全部读取成功。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData(-1)]
    [InlineData(12819)]
    [InlineData(12824)]
    [InlineData(12829)]
    public async Task ReadStatus_CollectsBusyAndPreservesUnknownPerPoint(int failedAddress)
    {
        await using var h = new MotionPeer();
        h.OnCoil = address => address == failedAddress ? new byte[] { 0x81, 2 }
            : new byte[] { 1, 1, address == 12824 ? (byte)0 : (byte)1 };
        var status = await h.Service.ReadStatusAsync();
        Assert.NotNull(status);
        Assert.Equal(failedAddress == 12819 ? null : (bool?)true, status.XBusy);
        Assert.Equal(failedAddress == 12824 ? null : (bool?)false, status.YBusy);
        Assert.Equal(failedAddress == 12829 ? null : (bool?)true, status.ZBusy);
        Assert.Equal(1000, status.XEncoder);
        Assert.True(status.HasPlate);
        Assert.Equal(12, h.CoilReads.Count);
        Assert.Equal(new[] { 12819, 12824, 12829 }, h.CoilReads.TakeLast(3));
    }

    /// <summary>等待Busy停止时仍响应当前任务取消。</summary>
    /// <returns>异步验证任务。</returns>
    [Fact]
    public async Task Poll_CancellationStillPropagates()
    {
        await using var h = new MotionPeer();
        using var cancel = new CancellationTokenSource();
        h.OnCoil = _ => { cancel.Cancel(); return new byte[] { 1, 1, 1 }; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Poll(1000, CraneService.SkipAxis, CraneService.SkipAxis, cancel.Token));
    }

    /// <summary>X2特殊下压判据保留15mm容差，优先于普通Busy联合到位。</summary>
    /// <param name="offset">下压坐标与目标的差值。</param>
    /// <param name="expected">预期下压结果。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData(15, CraneCommandCode.PressureReached)]
    [InlineData(16, CraneCommandCode.PressureOutOfRange)]
    public async Task PressureStop_PreservesExistingTolerance(int offset, CraneCommandCode expected)
    {
        await using var h = new MotionPeer();
        h.OnCoil = _ => new byte[] { 1, 1, 1 };
        var result = await h.Poll(CraneService.SkipAxis, CraneService.SkipAxis, 3000 + offset, watchPressure: true);
        Assert.Equal(expected, result.Code);
        Assert.Equal(new[] { 63490 }, h.CoilReads.ToArray());
        Assert.Contains((4518, 2), h.Writes);
        Assert.Contains((4514, 2), h.Writes);
    }

    /// <summary>只模拟本机请求应答；驱动、坐标解析和轮询均使用生产代码。</summary>
    private sealed class MotionPeer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _lifetime = new(TimeSpan.FromSeconds(15));
        private readonly ModbusTcpClient _client;
        private readonly Task _server;
        /// <summary>连接本机服务的生产天车驱动。</summary>
        public CraneService Service { get; }
        /// <summary>运动读取失败及恢复的业务日志。</summary>
        public TestLogger Logger { get; } = new();
        /// <summary>包含失败请求的完整顺序，用于核对重读和触发次数。</summary>
        public ConcurrentQueue<(byte Function, int Address)> Requests { get; } = new();
        /// <summary>指定请求由服务端重置连接，模拟PLC主动断连。</summary>
        public Func<byte, int, bool> DisconnectRequest { get; set; } = (_, _) => false;
        /// <summary>应答前注入可取消等待，模拟PLC无响应。</summary>
        public Func<byte, int, CancellationToken, Task> BeforeReplyAsync { get; set; } = (_, _, _) => Task.CompletedTask;
        /// <summary>实际收到的线圈读取地址。</summary>
        public ConcurrentQueue<int> CoilReads { get; } = new();
        /// <summary>实际收到的寄存器读取地址，区分模式显示与运动轮询。</summary>
        public ConcurrentQueue<int> RegisterReads { get; } = new();
        /// <summary>实际收到的单寄存器写入。</summary>
        public ConcurrentQueue<(int Address, int Value)> Writes { get; } = new();
        /// <summary>记录FC16实际DINT目标，核对负坐标没有被跳过或误编码。</summary>
        public ConcurrentQueue<(int Address, int Value)> TargetWrites { get; } = new();
        /// <summary>仅指定测试随目标写入更新坐标反馈，模拟实际负坐标到位。</summary>
        public bool FollowTargetWrites { get; set; }
        private readonly int[] _positions = { 1000, 2000, 3000 };
        /// <summary>按线圈地址返回正常或异常PDU，模拟现场运动反馈。</summary>
        public Func<int, byte[]> OnCoil { get; set; } = _ => new byte[] { 1, 1, 0 };
        /// <summary>按模式地址返回正常或异常Word应答，模拟单点未知。</summary>
        public Func<int, byte[]> OnModeRead { get; set; } = _ => new byte[] { 3, 2, 0, 2 };
        /// <summary>覆盖寄存器响应，区分明确的协议拒绝和网络断连。</summary>
        public Func<int, byte[]?> OnRegisterReply { get; set; } = _ => null;
        /// <summary>按写地址和值覆盖应答；null表示正常回显。</summary>
        public Func<int, int, byte[]?> OnWrite { get; set; } = (_, _) => null;

        /// <summary>装配真实驱动与随机回环端口，不启动业务宿主。</summary>
        /// <param name="timeoutMs">单次运动轮询期限。</param>
        /// <param name="tolerance">覆盖容差；null使用类型默认值，0验证非法配置兜底。</param>
        public MotionPeer(int timeoutMs = 5000, int? tolerance = null)
        {
            _listener.Start();
            _client = new ModbusTcpClient("127.0.0.1", ((IPEndPoint)_listener.LocalEndpoint).Port, timeoutMs: 1000);
            var config = new FakeConfigService();
            config.Options.Crane.MoveTimeoutMs = timeoutMs;
            if (tolerance.HasValue) config.Options.Crane.PositionToleranceMm = tolerance.Value;
            Service = new CraneService(1, "运动测试车", "127.0.0.1", Logger, config);
            var field = typeof(CraneService).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!;
            ((ModbusTcpClient)field.GetValue(Service)!).Dispose();
            field.SetValue(Service, _client);
            _server = RespondAsync();
        }

        /// <summary>直接验证生产轮询的判定边界，省去无关运动脉冲耗时。</summary>
        /// <param name="x">X目标，CraneService.SkipAxis跳过。</param>
        /// <param name="y">Y目标，CraneService.SkipAxis跳过。</param>
        /// <param name="z">Z目标，CraneService.SkipAxis跳过。</param>
        /// <param name="token">当前执行令牌。</param>
        /// <param name="watchPressure">是否保留X2下压监视。</param>
        /// <param name="encoder">是否使用编码器，否则使用显示坐标。</param>
        /// <returns>生产方法的轮询结果。</returns>
        public Task<CraneCommandResult> Poll(int x, int y, int z, CancellationToken token = default, bool watchPressure = false, bool encoder = true)
            => (Task<CraneCommandResult>)typeof(CraneService)
                .GetMethod("PollAxesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Service, new object[] { x, y, z, encoder, watchPressure, token })!;

        private async Task RespondAsync()
        {
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    using var peer = await _listener.AcceptTcpClientAsync(_lifetime.Token);
                    try { await RespondOnConnectionAsync(peer); }
                    catch (IOException) when (!_lifetime.IsCancellationRequested)
                    {
                        // 异常应答会让生产客户端断线；继续接受运动finally复位触发位的新连接。
                    }
                }
            }
            catch (Exception ex) when (_lifetime.IsCancellationRequested && ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
        }

        private async Task RespondOnConnectionAsync(TcpClient peer)
        {
            var stream = peer.GetStream();
            while (!_lifetime.IsCancellationRequested)
            {
                var header = new byte[7];
                await stream.ReadExactlyAsync(header, _lifetime.Token);
                var pdu = new byte[(header[4] << 8 | header[5]) - 1];
                await stream.ReadExactlyAsync(pdu, _lifetime.Token);
                var address = pdu[1] << 8 | pdu[2];
                Requests.Enqueue((pdu[0], address));
                if (DisconnectRequest(pdu[0], address))
                {
                    peer.Client.LingerState = new LingerOption(true, 0);
                    return;
                }
                await BeforeReplyAsync(pdu[0], address, _lifetime.Token);
                byte[] body;
                if (pdu[0] == 1)
                {
                    Assert.Equal(1, pdu[3] << 8 | pdu[4]);
                    CoilReads.Enqueue(address);
                    body = OnCoil(address);
                }
                else if (pdu[0] == 3)
                {
                    RegisterReads.Enqueue(address);
                    var count = pdu[3] << 8 | pdu[4];
                    body = new byte[2 + count * 2];
                    body[0] = 3;
                    body[1] = (byte)(count * 2);
                    if (address == 5000)
                    {
                        // X是DINT，负数反馈必须同时填高Word；Y/Z仍按现场协议只占一个有符号Word。
                        foreach (var (offset, value) in new[] { (14, _positions[0]), (15, _positions[0] >> 16), (18, _positions[0]), (19, _positions[0] >> 16), (20, _positions[1]), (22, _positions[1]), (23, _positions[2]), (25, _positions[2]) })
                        {
                            body[2 + offset * 2] = (byte)(value >> 8);
                            body[3 + offset * 2] = (byte)value;
                        }
                    }
                    if (address is 4001 or 4500)
                    {
                        Assert.Equal(1, count);
                        body = OnModeRead(address);
                    }
                    body = OnRegisterReply(address) ?? body;
                }
                else
                {
                    Assert.Contains(pdu[0], new byte[] { 6, 16 });
                    body = pdu.Take(5).ToArray();
                    if (pdu[0] == 6)
                    {
                        var value = pdu[3] << 8 | pdu[4];
                        Writes.Enqueue((address, value));
                        body = OnWrite(address, value) ?? body;
                    }
                    else
                    {
                        Assert.Equal(2, pdu[3] << 8 | pdu[4]);
                        var low = pdu[6] << 8 | pdu[7];
                        var high = pdu[8] << 8 | pdu[9];
                        var value = unchecked((int)((uint)low | (uint)high << 16));
                        TargetWrites.Enqueue((address, value));
                        if (FollowTargetWrites) _positions[(address - 3102) / 2] = value;
                    }
                }
                header[4] = 0;
                header[5] = (byte)(body.Length + 1);
                await stream.WriteAsync(header.Concat(body).ToArray(), _lifetime.Token);
            }
        }

        /// <summary>关闭本机通信并等待应答循环退出，防止测试遗留任务。</summary>
        /// <returns>异步释放任务。</returns>
        public async ValueTask DisposeAsync()
        {
            await _lifetime.CancelAsync();
            _client.Dispose();
            _listener.Stop();
            try { await _server.WaitAsync(TimeSpan.FromSeconds(3)); }
            finally { _lifetime.Dispose(); }
        }
    }
}
