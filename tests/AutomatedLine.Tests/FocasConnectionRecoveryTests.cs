using System.Collections.Concurrent;
using AutomatedLine.Infrastructure.Communication;
using FanucFocas;

namespace AutomatedLine.Tests;

/// <summary>使用真实FOCAS通道和脚本化会话验证断线后的句柄释放、下一轮重连及同线程约束。</summary>
public sealed class FocasConnectionRecoveryTests
{
    /// <summary>读宏网络错误必须释放旧会话，后续读取建立新连接，并重新应用原3秒超时。</summary>
    /// <returns>异步验证任务。</returns>
    [Fact]
    public async Task SocketFailure_ReleasesSessionAndReconnectsOnNextRead()
    {
        var session = new ScriptedSession();
        var logger = new TestLogger();
        var failure = new FocasException(-16);
        session.OnRead = _ => throw failure;
        using var channel = new FocasLatheChannel("192.0.2.4", logger, session);

        var error = await Assert.ThrowsAsync<FocasException>(() => channel.ReadMacroAsync(1001));

        Assert.Same(failure, error);
        Assert.False(channel.IsConnected);
        Assert.Equal(1, session.ConnectCount);
        Assert.Equal(new[] { 1001 }, session.Reads);
        Assert.Contains(logger.Messages, message => message.Contains("操作=读取宏#1001") && message.Contains("下一次请求重新连接"));
        session.OnRead = _ => 7;

        Assert.Equal(7d, await channel.ReadMacroAsync(1001));
        Assert.True(channel.IsConnected);
        Assert.Equal(2, session.ConnectCount);
        Assert.Equal(new[] { 3, 3 }, session.Timeouts);
        Assert.Equal(1, session.DisconnectCount);
        AssertSingleWorker(session);
    }

    /// <summary>写入失败不能当场重连重发，避免CNC已接受的握手或参数被重复下发。</summary>
    /// <returns>异步验证任务。</returns>
    [Fact]
    public async Task FailedWrite_IsNotReplayedWhenNextRequestReconnects()
    {
        var session = new ScriptedSession { OnWrite = (_, _) => throw new FocasException(-16) };
        using var channel = new FocasLatheChannel("192.0.2.4", new TestLogger(), session);

        await Assert.ThrowsAsync<FocasException>(() => channel.WriteMacroAsync(1103, 1));
        Assert.False(channel.IsConnected);
        Assert.Equal(new[] { (1103, 1d) }, session.Writes);
        Assert.Equal(1, session.ConnectCount);

        await channel.ReadMacroAsync(1001);

        Assert.Equal(2, session.ConnectCount);
        Assert.Equal(new[] { (1103, 1d) }, session.Writes);
        AssertSingleWorker(session);
    }

    /// <summary>批次中途断线不能返回半份快照或续读；下一批次从头读取且保留失败宏号日志。</summary>
    /// <returns>异步验证任务。</returns>
    [Fact]
    public async Task BatchFailure_StopsRemainingReadsAndRestartsNextBatch()
    {
        var session = new ScriptedSession { OnRead = macro => macro == 1002 ? throw new FocasException(-16) : 1 };
        var logger = new TestLogger();
        using var channel = new FocasLatheChannel("192.0.2.4", logger, session);

        await Assert.ThrowsAsync<FocasException>(() => channel.ReadMacrosAsync(new[] { 1001, 1002, 1003 }));

        Assert.Equal(new[] { 1001, 1002 }, session.Reads);
        Assert.Contains(logger.Messages, message => message.Contains("宏=#1002") && message.Contains("进度=2/3"));
        session.OnRead = macro => macro;
        Assert.Equal(new[] { 1001d, 1002d, 1003d }, await channel.ReadMacrosAsync(new[] { 1001, 1002, 1003 }));
        Assert.Equal(new[] { 1001, 1002, 1001, 1002, 1003 }, session.Reads);
        Assert.Equal(2, session.ConnectCount);
    }

    /// <summary>宏号/数据等非Socket错误保留原失败语义，不能误断开正常会话。</summary>
    /// <param name="code">原生非Socket错误码。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData(5)]
    [InlineData(-8)]
    public async Task NonSocketFailure_DoesNotInvalidateConnection(short code)
    {
        var session = new ScriptedSession { OnRead = _ => throw new FocasException(code) };
        using var channel = new FocasLatheChannel("192.0.2.4", new TestLogger(), session);

        var error = await Assert.ThrowsAsync<FocasException>(() => channel.ReadMacroAsync(1001));

        Assert.Equal(code, error.ErrorCode);
        Assert.True(channel.IsConnected);
        Assert.Equal(0, session.DisconnectCount);
        session.OnRead = _ => 1;
        Assert.Equal(1d, await channel.ReadMacroAsync(1001));
        Assert.Equal(1, session.ConnectCount);
    }

    /// <summary>断线后仍离线的连接失败应继续报错；再下一轮网络恢复后仍能建立会话。</summary>
    /// <returns>异步验证任务。</returns>
    [Fact]
    public async Task ReconnectFailure_DoesNotPreventLaterRecovery()
    {
        var session = new ScriptedSession { OnRead = _ => throw new FocasException(-16) };
        using var channel = new FocasLatheChannel("192.0.2.4", new TestLogger(), session);
        await Assert.ThrowsAsync<FocasException>(() => channel.ReadMacroAsync(1001));
        session.AllowConnect = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => channel.ReadMacroAsync(1001));

        Assert.False(channel.IsConnected);
        Assert.Single(session.Reads);
        session.AllowConnect = true;
        session.OnRead = _ => 1;
        Assert.Equal(1d, await channel.ReadMacroAsync(1001));
        Assert.Equal(3, session.ConnectCount);
        AssertSingleWorker(session);
    }

    /// <summary>释放原生句柄异常不能覆盖首次Socket错误，托管状态已清除后仍可重新连接。</summary>
    /// <returns>异步验证任务。</returns>
    [Fact]
    public async Task CleanupFailure_PreservesOriginalSocketErrorAndRecovery()
    {
        var failure = new FocasException(-16);
        var session = new ScriptedSession
        {
            OnRead = _ => throw failure,
            OnDisconnect = () => throw new InvalidOperationException("模拟原生释放异常")
        };
        var logger = new TestLogger();
        using var channel = new FocasLatheChannel("192.0.2.4", logger, session);

        Assert.Same(failure, await Assert.ThrowsAsync<FocasException>(() => channel.ReadMacroAsync(1001)));
        Assert.False(channel.IsConnected);
        Assert.Contains(logger.Messages, message => message.Contains("失效句柄释放异常") && message.Contains("本次仍传播原EW_SOCKET"));
        session.OnRead = _ => 1;
        session.OnDisconnect = null;
        Assert.Equal(1d, await channel.ReadMacroAsync(1001));
        Assert.Equal(2, session.ConnectCount);
    }

    /// <summary>取消等待不会中断已开始的原生调用，迟到的Socket错误仍须在原线程清理后供下一请求重连。</summary>
    /// <returns>异步验证任务。</returns>
    [Fact]
    public async Task CanceledWaiter_StillInvalidatesSessionWhenNativeCallFails()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var session = new ScriptedSession();
        session.OnRead = _ =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("测试未释放原生调用");
            throw new FocasException(-16);
        };
        using var channel = new FocasLatheChannel("192.0.2.4", new TestLogger(), session);
        using var canceled = new CancellationTokenSource();
        try
        {
            var pending = channel.ReadMacroAsync(1001, canceled.Token);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        finally
        {
            release.Set();
        }

        // Connect请求排在迟到错误之后，必须先清理旧会话再建立新连接。
        await channel.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(channel.IsConnected);
        Assert.Equal(2, session.ConnectCount);
        Assert.Equal(1, session.DisconnectCount);
        AssertSingleWorker(session);
    }

    // 线程记录只包含原生会话操作，不含只读托管IsConnected查询。
    private static void AssertSingleWorker(ScriptedSession session)
        => Assert.Single(session.ThreadIds.Distinct());

    private sealed class ScriptedSession : IFocasSession
    {
        private int _connected;
        public bool AllowConnect { get; set; } = true;
        public int ConnectCount { get; private set; }
        public int DisconnectCount { get; private set; }
        public List<int> Reads { get; } = new();
        public List<(int Macro, double Value)> Writes { get; } = new();
        public List<int> Timeouts { get; } = new();
        public ConcurrentQueue<int> ThreadIds { get; } = new();
        public Func<int, double>? OnRead { get; set; }
        public Action<int, double>? OnWrite { get; set; }
        public Action? OnDisconnect { get; set; }

        /// <inheritdoc />
        public bool IsConnected => Volatile.Read(ref _connected) != 0;

        /// <inheritdoc />
        public bool Connect(string ip, ushort port, int timeoutSeconds)
        {
            RecordThread();
            Assert.Equal((ushort)8193, port);
            Assert.Equal(3, timeoutSeconds);
            ConnectCount++;
            Volatile.Write(ref _connected, AllowConnect ? 1 : 0);
            return IsConnected;
        }

        /// <inheritdoc />
        public void Disconnect()
        {
            RecordThread();
            DisconnectCount++;
            try { OnDisconnect?.Invoke(); }
            finally { Volatile.Write(ref _connected, 0); }
        }

        /// <inheritdoc />
        public void SetTimeout(int seconds)
        {
            RecordThread();
            Timeouts.Add(seconds);
        }

        /// <inheritdoc />
        public double GetMacro(int macro)
        {
            RecordThread();
            Reads.Add(macro);
            return OnRead?.Invoke(macro) ?? 1;
        }

        /// <inheritdoc />
        public void SetMacro(int macro, double value)
        {
            RecordThread();
            Writes.Add((macro, value));
            OnWrite?.Invoke(macro, value);
        }

        private void RecordThread() => ThreadIds.Enqueue(Environment.CurrentManagedThreadId);
    }
}
