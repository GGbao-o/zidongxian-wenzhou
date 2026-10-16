using System.Net;
using System.Net.Sockets;
using AutomatedLine.Infrastructure.Drivers;
using AutomatedLine.Tests.Fakes;

namespace AutomatedLine.Tests;

/// <summary>用本机TCP桩核对双头镗三参数的真实FC06地址、Word值和中点舍入，不访问现场PLC。</summary>
public class BoringRegisterEncodingTests
{
    /// <summary>真实Modbus采集只读取三个请求，避免向已删除的卡钳地址发送请求。</summary>
    /// <returns>异步验证任务。</returns>
    [Fact]
    public async Task ReadInputs_UsesOnlyThreeRequestAddresses()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var driver = new ModbusDeviceDriver(1, "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
            1, DeviceModbusMaps.Boring(), new TestLogger());
        var server = ServeInputsAsync(listener, guard.Token);
        try
        {
            var signals = await driver.ReadAllSignalsAsync(guard.Token);
            Assert.Equal(new[] { 12203, 12207, 12215 }, await server);
            Assert.Equal(new[] { "RequestData", "RequestLoad", "RequestUnload" }, signals.Keys.OrderBy(k => k));
            Assert.All(signals.Values, value => Assert.Equal(true, value));
        }
        finally
        {
            guard.Cancel();
            await driver.DisconnectAsync();
            listener.Stop();
            try { await server; }
            catch (OperationCanceledException) { }
        }
    }

    // 用真实FC03帧返回1，并记录实际地址；三次后关闭连接，使意外的第四次读取无法蒙混通过。
    private static async Task<int[]> ServeInputsAsync(TcpListener listener, CancellationToken token)
    {
        using var peer = await listener.AcceptTcpClientAsync(token);
        var stream = peer.GetStream();
        var addresses = new int[3];
        for (var i = 0; i < addresses.Length; i++)
        {
            var request = new byte[12];
            await stream.ReadExactlyAsync(request, token);
            Assert.Equal(3, request[7]);
            Assert.Equal(1, (request[10] << 8) | request[11]);
            addresses[i] = (request[8] << 8) | request[9];
            var response = new byte[] { request[0], request[1], 0, 0, 0, 5, request[6], 3, 2, 0, 1 };
            await stream.WriteAsync(response, token);
        }
        return addresses;
    }

    /// <summary>长度/堵厚舍入为整数，外径乘10；.5沿用Convert.ToInt32的偶数舍入。</summary>
    /// <param name="point">逻辑点名。</param>
    /// <param name="address">期望PDU地址。</param>
    /// <param name="value">界面录入的原始数值。</param>
    /// <param name="word">期望写入的无符号Word。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData("Length", 301, 1200.4, 1200)]
    [InlineData("Diameter", 303, 180.6, 1806)]
    [InlineData("PlugThickness", 309, 12.7, 13)]
    [InlineData("Length", 301, 1200.5, 1200)]
    [InlineData("Diameter", 303, 180.65, 1806)]
    [InlineData("PlugThickness", 309, 12.5, 12)]
    [InlineData("PlugThickness", 309, 13.5, 14)]
    public async Task WriteParameter_UsesConfirmedWordProtocol(string point, int address, double value, int word)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var driver = new ModbusDeviceDriver(1, "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
            1, DeviceModbusMaps.Boring(), new TestLogger());
        var server = EchoAsync(listener, guard.Token);
        try
        {
            await driver.WriteSignalAsync(point, value, guard.Token);
            var request = await server;
            Assert.Equal(6, request[7]);
            Assert.Equal(address, (request[8] << 8) | request[9]);
            Assert.Equal(word, (request[10] << 8) | request[11]);
        }
        finally
        {
            guard.Cancel();
            await driver.DisconnectAsync();
            listener.Stop();
            try { await server; }
            catch (OperationCanceledException) { }
        }
    }

    // FC06正常响应原样回显地址和值；返回整帧供调用方核对，避免在通信桩中失败而触发请求超时。
    private static async Task<byte[]> EchoAsync(TcpListener listener, CancellationToken token)
    {
        using var peer = await listener.AcceptTcpClientAsync(token);
        var request = new byte[12];
        await peer.GetStream().ReadExactlyAsync(request, token);
        await peer.GetStream().WriteAsync(request, token);
        return request;
    }
}
