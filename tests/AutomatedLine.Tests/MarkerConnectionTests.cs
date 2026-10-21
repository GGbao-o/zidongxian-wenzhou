using System.Net;
using System.Net.Sockets;
using AutomatedLine.Infrastructure.Communication;
using System.Text;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Infrastructure.Drivers;

namespace AutomatedLine.Tests;

/// <summary>验证仅打号机读响应尾零适配、长连接读写和失败释放；仅使用本机TCP桩。</summary>
public class MarkerConnectionTests
{
    /// <summary>同一连接交错读写，只有正常FC04多一个尾零，FC06/FC16不能额外等待不存在的字节。</summary>
    /// <returns>异步验证任务。</returns>
    [Fact]
    public async Task Marker_MixedReadsAndWrites_ReuseConnectionAndConsumeOnlyReadTail()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var logger = new TestLogger();
        var driver = CreateMarker(listener, logger);
        try
        {
            var server = ServeOneConnectionAsync();
            await driver.WriteSignalAsync("PrintContent", "AB", guard.Token);
            Assert.True(driver.IsConnected);
            await driver.WriteSignalAsync("RollerOuterDiameter", 180, guard.Token);
            Assert.True(driver.IsConnected);
            await driver.WriteSignalAsync("RollerInnerDiameter", 70, guard.Token);
            Assert.True(driver.IsConnected);
            Assert.Equal(4, (await driver.ReadAllSignalsAsync(guard.Token)).Count);
            Assert.True(driver.IsConnected);
            await driver.WriteSignalAsync("LoadDone", 1, guard.Token);
            await driver.WriteSignalAsync("UnloadDone", 1, guard.Token);
            await driver.WriteSignalAsync("LoadDone", 0, guard.Token);
            await driver.WriteSignalAsync("UnloadDone", 0, guard.Token);
            Assert.True(driver.IsConnected);

            var requests = await server;
            Assert.Equal(new byte[] { 0x10, 6, 6, 4, 6, 6, 6, 6 }, requests.Select(frame => frame[7]));
            Assert.Equal(new[] { 9, 6, 7, 0, 4, 5, 4, 5 }, requests.Select(frame => (frame[8] << 8) | frame[9]));
            Assert.Equal(new byte[] { 0x41, 0x42 }, requests[0].AsSpan(13).ToArray());
            Assert.Equal(new[] { 180, 70, 1, 1, 0, 0 }, requests.Where(frame => frame[7] == 6)
                .Select(frame => (frame[10] << 8) | frame[11]));
            Assert.DoesNotContain(logger.Messages, message => message.StartsWith("E:"));
            Assert.All(logger.Messages.Where(message => message.Contains(" 写")),
                message => Assert.Contains("打号机长连接：应答已接收", message));
        }
        finally
        {
            guard.Cancel();
            await driver.DisconnectAsync();
            listener.Stop();
        }

        async Task<List<byte[]>> ServeOneConnectionAsync()
        {
            var requests = new List<byte[]>();
            using var peer = await listener.AcceptTcpClientAsync(guard.Token);
            var stream = peer.GetStream();
            for (var i = 0; i < 8; i++)
            {
                var frame = await ReadFrameAsync(stream, guard.Token);
                requests.Add(frame);
                // 按已确认的VB服务端行为发送：FC04多1字节，正常写响应严格12字节。
                var response = ResponseFor(frame);
                if (frame[7] == 4) response = response.Concat(new byte[] { 0 }).ToArray();
                await stream.WriteAsync(response, guard.Token);
            }
            return requests;
        }
    }

    /// <summary>写入被设备拒绝、内部超时或外部取消均关闭连接，下一次独立请求可以重新连接。</summary>
    /// <param name="failure">模拟的失败类型。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData("设备异常")]
    [InlineData("超时")]
    [InlineData("取消")]
    public async Task Marker_WriteFailure_ClosesConnectionAndAllowsNextRequest(string failure)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(guard.Token);
        var logger = new TestLogger();
        var driver = CreateMarker(listener, logger, failure == "超时" ? 500 : 3000);
        try
        {
            var accepted = listener.AcceptTcpClientAsync(guard.Token);
            var write = driver.WriteSignalAsync("LoadDone", 1, execution.Token);
            using var peer = await accepted;
            var stream = peer.GetStream();
            var request = await ReadFrameAsync(stream, guard.Token);
            if (failure == "设备异常")
            {
                // 异常声明9字节但实际12字节；必须保留设备异常码并断开，不能把异常误当正常响应适配。
                await stream.WriteAsync(new byte[] { request[0], request[1], 0, 0, 0, 3, 1, 0x86, 2, 0, 0, 0 }, guard.Token);
                await Assert.ThrowsAsync<ModbusProtocolException>(() => write);
            }
            else if (failure == "超时")
            {
                await Assert.ThrowsAsync<TimeoutException>(() => write);
            }
            else
            {
                execution.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
            }
            Assert.False(driver.IsConnected);
            try { Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token)); }
            catch (IOException) when (failure == "设备异常") { /* 未读的异常尾零在关闭时可触发RST。 */ }
            // 新增写入开始日志后，只对故障日志计数；主动取消仍不能误记通信错误或写入完成。
            var errors = logger.Messages.Where(message => message.StartsWith("E:Communication:")).ToArray();
            if (failure != "取消") Assert.Contains("协议适配=打号机FC04尾零", Assert.Single(errors));
            else Assert.Empty(errors);
            Assert.Contains(logger.Messages, message => message.Contains("FC06写入开始：LoadDone"));
            Assert.DoesNotContain(logger.Messages, message => message.Contains("FC06写入完成：LoadDone"));

            var nextAccepted = listener.AcceptTcpClientAsync(guard.Token);
            var nextWrite = driver.WriteSignalAsync("LoadDone", 0, guard.Token);
            using var nextPeer = await nextAccepted;
            var nextRequest = await ReadFrameAsync(nextPeer.GetStream(), guard.Token);
            await nextPeer.GetStream().WriteAsync(ResponseFor(nextRequest), guard.Token);
            await nextWrite;
            Assert.True(driver.IsConnected);
        }
        finally
        {
            guard.Cancel();
            await driver.DisconnectAsync();
            listener.Stop();
        }
    }

    /// <summary>尾字节非零、缺失超时或等待期间取消都必须终止本次请求并断开，不能盲目保留未知边界。</summary>
    /// <param name="failure">模拟的尾字节失败类型。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData("非零")]
    [InlineData("缺失超时")]
    [InlineData("取消")]
    public async Task Marker_InvalidOrMissingReadTail_DisconnectsAndReconnects(string failure)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(guard.Token);
        var logger = new TestLogger();
        var driver = CreateMarker(listener, logger, failure == "缺失超时" ? 500 : 3000);
        try
        {
            var accepted = listener.AcceptTcpClientAsync(guard.Token);
            var read = driver.ReadAllSignalsAsync(execution.Token);
            using var peer = await accepted;
            var request = await ReadFrameAsync(peer.GetStream(), guard.Token);
            await peer.GetStream().WriteAsync(ResponseFor(request), guard.Token);
            if (failure == "非零")
            {
                await peer.GetStream().WriteAsync(new byte[] { 0x7F }, guard.Token);
                var exception = await Assert.ThrowsAsync<ModbusProtocolException>(() => read);
                Assert.Contains("期望00，实际7F", exception.Message);
                var message = Assert.Single(logger.Messages);
                Assert.Contains("阶段=校验打号机FC04尾字节", message);
                Assert.Contains("适配尾字节=7F（已读1/1字节）", message);
            }
            else if (failure == "缺失超时")
            {
                await Assert.ThrowsAsync<TimeoutException>(() => read);
                var message = Assert.Single(logger.Messages);
                Assert.Contains("阶段=读取打号机FC04尾字节", message);
                Assert.Contains("适配尾字节=无（已读0/1字节）", message);
            }
            else
            {
                execution.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
                Assert.Empty(logger.Messages);
            }
            Assert.False(driver.IsConnected);
            var nextAccepted = listener.AcceptTcpClientAsync(guard.Token);
            var nextRead = driver.ReadAllSignalsAsync(guard.Token);
            using var nextPeer = await nextAccepted;
            var nextRequest = await ReadFrameAsync(nextPeer.GetStream(), guard.Token);
            await nextPeer.GetStream().WriteAsync(ResponseFor(nextRequest).Concat(new byte[] { 0 }).ToArray(), guard.Token);
            Assert.Equal(4, (await nextRead).Count);
            Assert.True(driver.IsConnected);
        }
        finally
        {
            guard.Cancel();
            await driver.DisconnectAsync();
            listener.Stop();
        }
    }

    /// <summary>FC04异常响应先保留异常码并断开，多出的三个零随连接清除，不进入正常读尾零处理。</summary>
    /// <returns>异步验证任务。</returns>
    [Fact]
    public async Task Marker_ReadException_PreservesCodeAndDisconnects()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var logger = new TestLogger();
        var driver = CreateMarker(listener, logger);
        try
        {
            var accepted = listener.AcceptTcpClientAsync(guard.Token);
            var read = driver.ReadAllSignalsAsync(guard.Token);
            using var peer = await accepted;
            var request = await ReadFrameAsync(peer.GetStream(), guard.Token);
            await peer.GetStream().WriteAsync(new byte[]
                { request[0], request[1], 0, 0, 0, 3, 1, 0x84, 3, 0, 0, 0 }, guard.Token);
            var exception = await Assert.ThrowsAsync<ModbusProtocolException>(() => read);
            Assert.Contains("异常码 3", exception.Message);
            Assert.False(driver.IsConnected);
            var message = Assert.Single(logger.Messages);
            Assert.Contains("阶段=校验功能码", message);
            Assert.Contains("适配尾字节=无（已读0/0字节）", message);
        }
        finally
        {
            guard.Cancel();
            await driver.DisconnectAsync();
            listener.Stop();
        }
    }

    /// <summary>其他设备维持标准响应解析，不额外读取尾字节；连续两轮采集始终复用连接。</summary>
    /// <param name="type">具有真实Modbus驱动的其他地面设备类型。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData(DeviceType.BoringMachine)]
    [InlineData(DeviceType.GrinderMachine)]
    [InlineData(DeviceType.LoadRack)]
    [InlineData(DeviceType.UnloadRack)]
    public async Task OtherDevices_DefaultPolicy_ReusesConnection(DeviceType type)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var points = type switch
        {
            DeviceType.BoringMachine => DeviceModbusMaps.Boring(),
            DeviceType.GrinderMachine => DeviceModbusMaps.Grinder(),
            _ => DeviceModbusMaps.Rack(type)
        };
        var driver = new ModbusDeviceDriver(1, "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, 1, points, new TestLogger());
        var inputs = points.Count(point => !point.Writable);
        try
        {
            var server = ServeOneConnectionAsync();
            Assert.Equal(inputs, (await driver.ReadAllSignalsAsync(guard.Token)).Count);
            Assert.True(driver.IsConnected);
            Assert.Equal(inputs, (await driver.ReadAllSignalsAsync(guard.Token)).Count);
            Assert.True(driver.IsConnected);
            await server;
        }
        finally
        {
            guard.Cancel();
            await driver.DisconnectAsync();
            listener.Stop();
        }

        async Task ServeOneConnectionAsync()
        {
            using var peer = await listener.AcceptTcpClientAsync(guard.Token);
            var stream = peer.GetStream();
            for (var i = 0; i < inputs * 2; i++)
            {
                var request = await ReadFrameAsync(stream, guard.Token);
                await stream.WriteAsync(ResponseFor(request), guard.Token);
            }
        }
    }

    /// <summary>打印数据保持UTF8字节原序，奇数末尾补空格；与旧服务端直接解码逻辑一致。</summary>
    /// <param name="text">原始打印字符串。</param>
    /// <param name="expectedHex">协议要求的完整数据字节，含必要填充。</param>
    /// <returns>异步报文验证任务。</returns>
    [Theory]
    [InlineData("ABC123", "414243313233")]
    [InlineData("AB", "4142")]
    [InlineData("ABC", "41424320")]
    [InlineData("A", "4120")]
    [InlineData("打号", "E68993E58FB7")]
    [InlineData("中", "E4B8AD20")]
    [InlineData("😀", "F09F9880")]
    public async Task PrintContent_PreservesUtf8ByteOrderAndWaitsForReply(string text, string expectedHex)
    {
        var request = await CapturePrintRequestAsync(text);
        var payload = Convert.FromHexString(expectedHex);
        AssertPrintFrame(request, payload);
        // 模拟VB服务端直接解码线上字节，不先重排Word，也不去掉空格或零。
        Assert.Equal(text + (Encoding.UTF8.GetByteCount(text) % 2 == 1 ? " " : ""),
            new UTF8Encoding(false, true).GetString(request, 13, payload.Length));
    }

    /// <summary>FC16最大单次文本按字节计数，245字节只补一个空格，246字节完整发送。</summary>
    /// <param name="byteCount">ASCII文本原始字节数。</param>
    /// <returns>异步边界验证任务。</returns>
    [Theory]
    [InlineData(245)]
    [InlineData(246)]
    public async Task PrintContent_MaximumPayload_IsOneFc16Request(int byteCount)
    {
        var text = new string('A', byteCount);
        var request = await CapturePrintRequestAsync(text);
        AssertPrintFrame(request, Encoding.UTF8.GetBytes(text + (byteCount == 245 ? " " : "")));
        Assert.Equal(123, (request[10] << 8) | request[11]);
    }

    /// <summary>空内容及超过单次FC16容量的内容必须在建立连接前拒绝，不能截断或分段触发打印。</summary>
    /// <param name="text">不合法的原始内容，null沿用接口空字符串语义。</param>
    /// <param name="repeat">重复内容次数，覆盖按字节而非字符数的长度限制。</param>
    /// <returns>异步校验验证任务。</returns>
    [Theory]
    [InlineData(null, 1)]
    [InlineData("", 1)]
    [InlineData("A", 247)]
    [InlineData("中", 83)]
    public async Task PrintContent_InvalidLength_DoesNotConnect(string? text, int repeat)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var driver = CreateMarker(listener, new TestLogger());
        try
        {
            var exception = await Assert.ThrowsAsync<ArgumentException>(() => driver.WriteSignalAsync(
                "PrintContent", text is null ? null : string.Concat(Enumerable.Repeat(text, repeat))));
            Assert.Contains("UTF-8", exception.Message);
            Assert.False(driver.IsConnected);
            Assert.False(listener.Pending());
        }
        finally
        {
            await driver.DisconnectAsync();
            listener.Stop();
        }
    }

    // 校验完整MBAP/PDU与原始数据，防止只有Word数组正确而最终线上字节再次交换。
    private static void AssertPrintFrame(byte[] request, byte[] payload)
    {
        Assert.Equal(0, (request[2] << 8) | request[3]);
        Assert.Equal(7 + payload.Length, (request[4] << 8) | request[5]);
        Assert.Equal((byte)1, request[6]);
        Assert.Equal((byte)0x10, request[7]);
        Assert.Equal(9, (request[8] << 8) | request[9]);
        Assert.Equal(payload.Length / 2, (request[10] << 8) | request[11]);
        Assert.Equal(payload.Length, (int)request[12]);
        Assert.Equal(payload, request.AsSpan(13).ToArray());
    }

    // 使用真实驱动与本机服务端捕获一次请求；暂不回包时写入不能被视为成功。
    private static async Task<byte[]> CapturePrintRequestAsync(string text)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var driver = CreateMarker(listener, new TestLogger());
        try
        {
            var accepted = listener.AcceptTcpClientAsync(guard.Token);
            var write = driver.WriteSignalAsync("PrintContent", text, guard.Token);
            using var peer = await accepted;
            var request = await ReadFrameAsync(peer.GetStream(), guard.Token);
            Assert.False(write.IsCompleted);
            await peer.GetStream().WriteAsync(ResponseFor(request), guard.Token);
            await write;
            Assert.False(peer.GetStream().DataAvailable);
            return request;
        }
        finally
        {
            guard.Cancel();
            await driver.DisconnectAsync();
            listener.Stop();
        }
    }

    // 与生产工厂相同，仅打号机显式启用尾零适配；测试使用动态本机端口，不访问502现场服务。
    private static ModbusDeviceDriver CreateMarker(TcpListener listener, TestLogger logger, int timeoutMs = 3000)
        => new(1, "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, 1, DeviceModbusMaps.Marker(), logger, timeoutMs,
            inputRegisterBlock: (0, 4), expectMarkerReadTrailingZero: true);

    // 按请求声明长度读齐，支持长度可变的FC16打印文本，不能把它按固定12字节请求处理。
    private static async Task<byte[]> ReadFrameAsync(NetworkStream stream, CancellationToken token)
    {
        var header = new byte[7];
        await stream.ReadExactlyAsync(header, token);
        var body = new byte[((header[4] << 8) | header[5]) - 1];
        await stream.ReadExactlyAsync(body, token);
        return header.Concat(body).ToArray();
    }

    private static byte[] ResponseFor(byte[] request)
    {
        var pdu = request[7] switch
        {
            2 => new byte[] { 2, 1, 1 },
            3 => new byte[] { 3, 2, 0, 1 },
            4 => new byte[] { 4, 8, 0, 0, 0, 1, 0, 1, 0, 0 },
            _ => request.AsSpan(7, 5).ToArray()
        };
        return new byte[] { request[0], request[1], 0, 0, 0, (byte)(pdu.Length + 1), request[6] }.Concat(pdu).ToArray();
    }
}
