using System.Net;
using System.Net.Sockets;
using AutomatedLine.Infrastructure.Communication;

namespace AutomatedLine.Tests;

/// <summary>
/// 使用本机 TCP 服务验证真实客户端的超时分类、外部取消及失败后重连，不访问现场设备。
/// </summary>
public class ModbusTcpClientTests
{
    private static readonly TimeSpan GuardTimeout = TimeSpan.FromSeconds(5);

    /// <summary>自动或显式连接后的内部超时都必须归为超时，并允许下一请求重新连接。</summary>
    /// <param name="connectFirst">是否在发送请求前显式建立连接</param>
    /// <returns>异步验证任务</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InternalTimeout_ThrowsTimeout_AndNextRequestReconnects(bool connectFirst)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var logger = new TestLogger();
            using var client = new ModbusTcpClient("127.0.0.1", port, timeoutMs: 500, logger: logger);
            var accepted = listener.AcceptTcpClientAsync();
            if (connectFirst)
            {
                await client.ConnectAsync().WaitAsync(GuardTimeout);
            }

            var firstRead = client.ReadRegistersAsync(0, 1);
            using var firstPeer = await accepted.WaitAsync(GuardTimeout);
            var request = new byte[12];
            await firstPeer.GetStream().ReadExactlyAsync(request).AsTask().WaitAsync(GuardTimeout);

            // 已收到完整请求但故意不回复，确保测到请求超时而非连接失败。
            var exception = await Assert.ThrowsAsync<TimeoutException>(() => firstRead.WaitAsync(GuardTimeout));
            Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
            Assert.Contains("超时", exception.Message);
            Assert.False(client.IsConnected);
            var diagnostic = Assert.Single(logger.Messages);
            Assert.Contains("阶段=读取响应头", diagnostic);
            Assert.Contains("响应头=无（已读0/7字节）", diagnostic);
            Assert.Contains("Length=未取得", diagnostic);
            Assert.Contains("超时", diagnostic);

            var reconnected = listener.AcceptTcpClientAsync();
            var secondRead = client.ReadRegistersAsync(0, 1);
            using var secondPeer = await reconnected.WaitAsync(GuardTimeout);
            await secondPeer.GetStream().ReadExactlyAsync(request).AsTask().WaitAsync(GuardTimeout);
            var response = new byte[]
            {
                request[0], request[1], 0, 0, 0, 5, request[6], 3, 2, 0, 123
            };
            await secondPeer.GetStream().WriteAsync(response).AsTask().WaitAsync(GuardTimeout);

            Assert.Equal(new ushort[] { 123 }, await secondRead.WaitAsync(GuardTimeout));
            Assert.True(client.IsConnected);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>调用者取消等待响应时保留取消异常，不能误转换成内部超时。</summary>
    /// <returns>异步验证任务</returns>
    [Fact]
    public async Task CallerCancellation_RemainsCancellation_AndDisconnects()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var logger = new TestLogger();
            using var client = new ModbusTcpClient("127.0.0.1", port, timeoutMs: 10000, logger: logger);
            using var cancellation = new CancellationTokenSource();
            var accepted = listener.AcceptTcpClientAsync();
            var read = client.ReadRegistersAsync(0, 1, cancellation.Token);
            using var peer = await accepted.WaitAsync(GuardTimeout);
            var request = new byte[12];
            await peer.GetStream().ReadExactlyAsync(request).AsTask().WaitAsync(GuardTimeout);

            // 收到请求后主动取消，避免用短计时器与内部超时竞争。
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(GuardTimeout));
            Assert.True(cancellation.IsCancellationRequested);
            Assert.False(client.IsConnected);
            Assert.Empty(logger.Messages);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>零长度响应保留真实请求、完整响应头及失败阶段，断开后下一请求可以重新连接。</summary>
    /// <returns>异步验证任务。</returns>
    [Fact]
    public async Task ZeroLengthHeader_LogsReceivedBytes_AndReconnects()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var guard = new CancellationTokenSource(GuardTimeout);
        var logger = new TestLogger();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new ModbusTcpClient("127.0.0.1", port, logger: logger);
        try
        {
            var accepted = listener.AcceptTcpClientAsync(guard.Token);
            var read = client.ReadInputRegistersAsync(0, 4, guard.Token);
            using var peer = await accepted;
            var request = new byte[12];
            await peer.GetStream().ReadExactlyAsync(request, guard.Token);
            var header = new byte[] { request[0], request[1], 0, 0, 0, 0, 1 };
            await peer.GetStream().WriteAsync(header, guard.Token);
            var exception = await Assert.ThrowsAsync<ModbusProtocolException>(() => read);
            Assert.Equal("Modbus 响应长度非法", exception.Message);
            Assert.False(client.IsConnected);
            var diagnostic = Assert.Single(logger.Messages);
            Assert.Contains($"端点=127.0.0.1:{port}", diagnostic);
            Assert.Contains("UnitId=1，FC=0x04，阶段=解析响应头", diagnostic);
            Assert.Contains($"请求={Convert.ToHexString(request)}", diagnostic);
            Assert.Contains($"响应头={Convert.ToHexString(header)}（已读7/7字节），Length=0", diagnostic);

            var reconnected = listener.AcceptTcpClientAsync(guard.Token);
            var nextRead = client.ReadInputRegistersAsync(0, 4, guard.Token);
            using var nextPeer = await reconnected;
            await nextPeer.GetStream().ReadExactlyAsync(request, guard.Token);
            await nextPeer.GetStream().WriteAsync(new byte[]
                { request[0], request[1], 0, 0, 0, 11, 1, 4, 8, 0, 1, 0, 0, 0, 1, 0, 0 }, guard.Token);
            Assert.Equal(new ushort[] { 1, 0, 1, 0 }, await nextRead);
            Assert.Single(logger.Messages);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>接收中途断开时，日志只能输出已收到的字节，不能把未填充的0误报为设备返回。</summary>
    /// <param name="partialBody">是否在收到完整头之后截断响应体，否则截断响应头。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TruncatedResponse_LogsOnlyReceivedPrefix(bool partialBody)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var guard = new CancellationTokenSource(GuardTimeout);
        var logger = new TestLogger();
        using var client = new ModbusTcpClient("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, logger: logger);
        try
        {
            var accepted = listener.AcceptTcpClientAsync(guard.Token);
            var read = client.ReadInputRegistersAsync(0, 4, guard.Token);
            using (var peer = await accepted)
            {
                var request = new byte[12];
                await peer.GetStream().ReadExactlyAsync(request, guard.Token);
                var response = partialBody
                    ? new byte[] { request[0], request[1], 0, 0, 0, 11, 1, 4, 8, 0 }
                    : new byte[] { 0, 1, 0 };
                await peer.GetStream().WriteAsync(response, guard.Token);
            }
            await Assert.ThrowsAsync<IOException>(() => read);
            var diagnostic = Assert.Single(logger.Messages);
            Assert.Contains(partialBody ? "阶段=读取响应体" : "阶段=读取响应头", diagnostic);
            Assert.Contains(partialBody ? "响应体=040800（已读3/10字节）" : "响应头=000100（已读3/7字节），Length=未取得", diagnostic);
            Assert.False(client.IsConnected);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>设备返回规范异常响应时保留异常码及请求/响应证据，不能误记为响应头长度错误。</summary>
    /// <returns>异步验证任务。</returns>
    [Fact]
    public async Task DeviceException_LogsRequestAndExceptionPdu()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var guard = new CancellationTokenSource(GuardTimeout);
        var logger = new TestLogger();
        using var client = new ModbusTcpClient("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, logger: logger);
        try
        {
            var accepted = listener.AcceptTcpClientAsync(guard.Token);
            var read = client.ReadInputRegistersAsync(0, 4, guard.Token);
            using var peer = await accepted;
            var request = new byte[12];
            await peer.GetStream().ReadExactlyAsync(request, guard.Token);
            await peer.GetStream().WriteAsync(new byte[] { request[0], request[1], 0, 0, 0, 3, 1, 0x84, 2 }, guard.Token);
            var exception = await Assert.ThrowsAsync<ModbusProtocolException>(() => read);
            Assert.Contains("异常码 2", exception.Message);
            var diagnostic = Assert.Single(logger.Messages);
            Assert.Contains("阶段=校验功能码", diagnostic);
            Assert.Contains("响应体=8402（已读2/2字节）", diagnostic);
            Assert.Contains($"请求={Convert.ToHexString(request)}", diagnostic);
            Assert.False(client.IsConnected);
        }
        finally
        {
            listener.Stop();
        }
    }
}
