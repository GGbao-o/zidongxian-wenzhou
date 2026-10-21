using System.Net;
using System.Net.Sockets;
using AutomatedLine.Application.Generation;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Infrastructure.Communication;
using AutomatedLine.Infrastructure.Drivers;
using AutomatedLine.Tests.Fakes;

namespace AutomatedLine.Tests;

/// <summary>用本机TCP通信桩验证真实料架驱动的地址、输入极性及候选就绪判断，不访问现场设备。</summary>
public class RackInputMappingTests
{
    /// <summary>上下料架的允许信号都为1，但下料架必须反转为业务HasMaterial，不能反转上料架。</summary>
    /// <param name="type">料架类型。</param>
    /// <param name="address">现场确认的PDU地址。</param>
    /// <param name="rawValue">通信桩返回的原始允许信号。</param>
    /// <param name="hasMaterial">预期的业务有料状态。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData(DeviceType.LoadRack, 0, false, false)]
    [InlineData(DeviceType.LoadRack, 0, true, true)]
    [InlineData(DeviceType.UnloadRack, 2, false, true)]
    [InlineData(DeviceType.UnloadRack, 2, true, false)]
    public async Task ReadInputs_UsesConfirmedAddressAndNormalizesPresence(
        DeviceType type, int address, bool rawValue, bool hasMaterial)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var driver = new ModbusDeviceDriver(1, "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
            RackAddress.UnitId, DeviceModbusMaps.Rack(type), new TestLogger());
        try
        {
            var server = ReplyAsync(listener, rawValue, false, guard.Token);
            var values = await driver.ReadAllSignalsAsync(guard.Token);
            var request = await server;

            Assert.Equal((byte)254, request[6]);
            Assert.Equal((byte)2, request[7]);
            Assert.Equal(address, (request[8] << 8) | request[9]);
            Assert.Equal(1, (request[10] << 8) | request[11]);
            Assert.Single(values);
            Assert.Equal(hasMaterial, Assert.IsType<bool>(values["HasMaterial"]));

            DeviceBase rack = type == DeviceType.LoadRack ? new LoadRack(1, "上料架", 1000) : new UnloadRack(1, "下料架", 2000);
            rack.Signals = TestSignals.Of(("HasMaterial", values["HasMaterial"]));
            Assert.Equal(rawValue, type == DeviceType.LoadRack
                ? DeviceHandshakeRules.CanProvide(rack) : DeviceHandshakeRules.CanReceive(rack));
        }
        finally
        {
            await driver.DisconnectAsync();
            listener.Stop();
        }
    }

    /// <summary>读取异常不能经极性转换被包装成有板或无板，必须继续交给采集层发布失败快照。</summary>
    /// <param name="type">料架类型。</param>
    /// <returns>异步验证任务。</returns>
    [Theory]
    [InlineData(DeviceType.LoadRack)]
    [InlineData(DeviceType.UnloadRack)]
    public async Task ReadFailure_DoesNotReturnPresence(DeviceType type)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var driver = new ModbusDeviceDriver(1, "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
            RackAddress.UnitId, DeviceModbusMaps.Rack(type), new TestLogger());
        try
        {
            var server = ReplyAsync(listener, false, true, guard.Token);
            await Assert.ThrowsAsync<ModbusProtocolException>(() => driver.ReadAllSignalsAsync(guard.Token));
            await server;
        }
        finally
        {
            await driver.DisconnectAsync();
            listener.Stop();
        }
    }

    // 先响应再由调用方断言请求，避免地址不符时通信桩提前退出，让驱动等待超时掩盖真实差异。
    private static async Task<byte[]> ReplyAsync(TcpListener listener, bool rawValue, bool fail, CancellationToken token)
    {
        using var peer = await listener.AcceptTcpClientAsync(token);
        var stream = peer.GetStream();
        var request = new byte[12];
        await stream.ReadExactlyAsync(request, token);
        byte[] response = fail
            ? new byte[] { request[0], request[1], 0, 0, 0, 3, request[6], 0x82, 2 }
            : new byte[] { request[0], request[1], 0, 0, 0, 4, request[6], 2, 1, (byte)(rawValue ? 1 : 0) };
        await stream.WriteAsync(response, token);
        return request;
    }
}
