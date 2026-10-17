using System.Net;
using System.Net.Sockets;
using AutomatedLine.Infrastructure.Communication;
using AutomatedLine.Infrastructure.Drivers;

namespace AutomatedLine.Tests;

/// <summary>使用本机TCP桩验证打号机四状态同批读取及拆包接收，不访问现场设备。</summary>
public class MarkerInputMappingTests
{
    /// <summary>一次FC04读取0～3，按地址映射四个布尔状态；拆分响应仍须收齐，不额外读取输出点。</summary>
    /// <param name="fragmented">是否分段发送响应以覆盖TCP分段读取。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadSignals_UsesOneFourRegisterRequest(bool fragmented)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var logger = new TestLogger();
        var driver = new ModbusDeviceDriver(1, "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
            MarkerMachineAddress.UnitId, DeviceModbusMaps.Marker(), logger,
            inputRegisterBlock: (MarkerMachineAddress.InputStart, MarkerMachineAddress.InputCount),
            expectMarkerReadTrailingZero: true);
        try
        {
            var server = ReplyAsync();
            var values = await driver.ReadAllSignalsAsync(guard.Token);
            var request = await server;

            Assert.Equal((byte)1, request[6]);
            Assert.Equal((byte)4, request[7]);
            Assert.Equal(0, (request[8] << 8) | request[9]);
            Assert.Equal(4, (request[10] << 8) | request[11]);
            Assert.Equal(4, values.Count);
            Assert.Equal(false, values["Busy"]);
            Assert.Equal(true, values["Running"]);
            Assert.Equal(true, values["ProcessDone"]);
            Assert.Equal(false, values["NewPrintContent"]);
            Assert.Contains("FC04尾字节适配采集完成", Assert.Single(logger.Messages));
            Assert.True(driver.IsConnected);
        }
        finally
        {
            await driver.DisconnectAsync();
            listener.Stop();
        }

        async Task<byte[]> ReplyAsync()
        {
            using var peer = await listener.AcceptTcpClientAsync(guard.Token);
            var stream = peer.GetStream();
            var request = new byte[12];
            await stream.ReadExactlyAsync(request, guard.Token);
            var response = new byte[] { request[0], request[1], 0, 0, 0, 11, request[6], 4, 8, 0, 0, 0, 1, 0, 7, 0, 0, 0 };
            if (fragmented)
            {
                // 头和数据均分多次写入，防止未来误改为单次ReadAsync后直接解析。
                foreach (var value in response)
                {
                    await stream.WriteAsync(new[] { value }, guard.Token);
                    await Task.Delay(2, guard.Token);
                }
            }
            else
            {
                await stream.WriteAsync(response, guard.Token);
            }
            return request;
        }
    }

    /// <summary>连续三轮读取复用同一连接，额外尾零即使延迟单独到达也必须读齐。</summary>
    /// <param name="splitTail">尾零是否与标准响应分开发送。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LongReads_ReuseConnection_AndConsumeDelayedTail(bool splitTail)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var logger = new TestLogger();
        var driver = new ModbusDeviceDriver(1, "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
            MarkerMachineAddress.UnitId, DeviceModbusMaps.Marker(), logger,
            inputRegisterBlock: (MarkerMachineAddress.InputStart, MarkerMachineAddress.InputCount),
            expectMarkerReadTrailingZero: true);
        try
        {
            var server = ServeOneConnectionAsync();
            for (var i = 0; i < 3; i++)
            {
                var values = await driver.ReadAllSignalsAsync(guard.Token);
                Assert.Equal(4, values.Count);
                Assert.Equal(true, values["ProcessDone"]);
                Assert.True(driver.IsConnected);
            }
            await server;
            Assert.DoesNotContain(logger.Messages, message => message.StartsWith("E:"));
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
            for (var i = 0; i < 3; i++)
            {
                var request = new byte[12];
                await stream.ReadExactlyAsync(request, guard.Token);
                Assert.Equal((byte)4, request[7]);
                Assert.Equal((byte)4, request[11]);
                var response = new byte[] { request[0], request[1], 0, 0, 0, 11, 1, 4, 8, 0, 0, 0, 0, 0, 1, 0, 0 };
                if (splitTail)
                {
                    await stream.WriteAsync(response, guard.Token);
                    // 不能以DataAvailable=false判定不存在尾字节；它可能在TCP下一段才到达。
                    await Task.Delay(25, guard.Token);
                    await stream.WriteAsync(new byte[] { 0 }, guard.Token);
                }
                else
                {
                    await stream.WriteAsync(response.Concat(new byte[] { 0 }).ToArray(), guard.Token);
                }
            }
        }
    }

    /// <summary>适配采集等待同一通信锁，收到FC06应答后在同一连接读取状态。</summary>
    /// <returns>异步验证任务。</returns>
    [Fact]
    public async Task AdaptedRead_DoesNotInterruptPendingWrite()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var driver = new ModbusDeviceDriver(1, "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
            MarkerMachineAddress.UnitId, DeviceModbusMaps.Marker(), new TestLogger(),
            inputRegisterBlock: (MarkerMachineAddress.InputStart, MarkerMachineAddress.InputCount),
            expectMarkerReadTrailingZero: true);
        try
        {
            var accepted = listener.AcceptTcpClientAsync(guard.Token);
            var write = driver.WriteSignalAsync("LoadDone", 1, guard.Token);
            using var writePeer = await accepted;
            var request = new byte[12];
            await writePeer.GetStream().ReadExactlyAsync(request, guard.Token);
            Assert.Equal((byte)6, request[7]);

            // 请求尚未收到写应答，此时让采集排队；随后写应答仍必须能正常完成。
            var read = driver.ReadAllSignalsAsync(guard.Token);
            await writePeer.GetStream().WriteAsync(request, guard.Token);
            await write;
            await writePeer.GetStream().ReadExactlyAsync(request, guard.Token);
            Assert.Equal((byte)4, request[7]);
            await writePeer.GetStream().WriteAsync(new byte[]
                { request[0], request[1], 0, 0, 0, 11, 1, 4, 8, 0, 0, 0, 0, 0, 1, 0, 0, 0 }, guard.Token);
            Assert.Equal(4, (await read).Count);
            Assert.True(driver.IsConnected);
        }
        finally
        {
            guard.Cancel();
            await driver.DisconnectAsync();
            listener.Stop();
        }
    }

    /// <summary>读响应的尾零未到时不能放行下一次写入，尾零处理必须仍在完整收发锁内。</summary>
    /// <returns>异步验证任务。</returns>
    [Fact]
    public async Task PendingWrite_WaitsUntilReadTrailerIsConsumed()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var driver = new ModbusDeviceDriver(1, "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
            MarkerMachineAddress.UnitId, DeviceModbusMaps.Marker(), new TestLogger(),
            inputRegisterBlock: (0, 4), expectMarkerReadTrailingZero: true);
        try
        {
            var accepted = listener.AcceptTcpClientAsync(guard.Token);
            var read = driver.ReadAllSignalsAsync(guard.Token);
            using var peer = await accepted;
            var stream = peer.GetStream();
            var request = new byte[12];
            await stream.ReadExactlyAsync(request, guard.Token);
            await stream.WriteAsync(new byte[]
                { request[0], request[1], 0, 0, 0, 11, 1, 4, 8, 0, 0, 0, 0, 0, 1, 0, 0 }, guard.Token);
            var write = driver.WriteSignalAsync("LoadDone", 1, guard.Token);
            Assert.False(write.IsCompleted);
            await stream.WriteAsync(new byte[] { 0 }, guard.Token);
            Assert.Equal(4, (await read).Count);
            await stream.ReadExactlyAsync(request, guard.Token);
            Assert.Equal((byte)6, request[7]);
            await stream.WriteAsync(request, guard.Token);
            await write;
            Assert.True(driver.IsConnected);
        }
        finally
        {
            guard.Cancel();
            await driver.DisconnectAsync();
            listener.Stop();
        }
    }
}
