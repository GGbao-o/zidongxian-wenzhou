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
            using var client = new ModbusTcpClient("127.0.0.1", port, timeoutMs: 500);
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
            using var client = new ModbusTcpClient("127.0.0.1", port, timeoutMs: 10000);
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
        }
        finally
        {
            listener.Stop();
        }
    }
}
