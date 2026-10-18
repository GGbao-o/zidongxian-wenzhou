using System.Net;
using System.Net.Sockets;
using System.Reflection;
using AutomatedLine.Infrastructure.Communication;
using AutomatedLine.Tests.Fakes;

namespace AutomatedLine.Tests;

/// <summary>使用本机 Modbus 服务验证天车坐标的符号解析，避免负坐标被误判为大正数。</summary>
public class CraneSignedCoordinateTests
{
    /// <summary>四项 Y/Z 反馈均按单 Word 有符号 INT 解码，X 坐标及其他 Word 保持原规则。</summary>
    /// <param name="yEncoder">Y 编码器期望值。</param>
    /// <param name="yPosition">Y 显示值期望值。</param>
    /// <param name="zEncoder">Z 编码器期望值。</param>
    /// <param name="zPosition">Z 显示值期望值。</param>
    /// <returns>异步通信验证任务。</returns>
    [Theory]
    [InlineData(-1, -100, -32768, 32767)]
    [InlineData(0, 1, 123, 30000)]
    [InlineData(32767, -32768, -1, 0)]
    public async Task ReadStatus_PreservesSignedCoordinates(int yEncoder, int yPosition, int zEncoder, int zPosition)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = new ModbusTcpClient("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port);
        var service = new CraneService(1, "坐标测试车", "127.0.0.1", new TestLogger(), new FakeConfigService());
        // 仅替换通信端口，状态读取和解析走真实驱动；不访问现场 PLC。
        var field = typeof(CraneService).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((ModbusTcpClient)field.GetValue(service)!).Dispose();
        field.SetValue(service, client);
        var words = new ushort[30];
        words[11] = 65535;
        words[14] = unchecked((ushort)-100000);
        words[15] = unchecked((ushort)(-100000 >> 16));
        words[18] = unchecked((ushort)100000);
        words[19] = (ushort)(100000 >> 16);
        words[20] = unchecked((ushort)yEncoder);
        words[22] = unchecked((ushort)yPosition);
        words[23] = unchecked((ushort)zEncoder);
        words[25] = unchecked((ushort)zPosition);
        // 相邻未使用字非零，确保反馈没有被错误组合为双 Word。
        words[21] = 0x3456;
        words[24] = 0x7890;
        words[26] = 0xABCD;
        try
        {
            var read = service.ReadStatusAsync(deadline.Token);
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            var stream = peer.GetStream();
            for (var i = 0; i < 11; i++)
            {
                var request = new byte[12];
                await stream.ReadExactlyAsync(request, deadline.Token);
                var address = (request[8] << 8) | request[9];
                var count = (request[10] << 8) | request[11];
                byte[] body;
                if (i < 2)
                {
                    Assert.Equal(3, request[7]);
                    Assert.Equal(i == 0 ? 5000 : 2501, address);
                    Assert.Equal(i == 0 ? 30 : 21, count);
                    body = new byte[2 + count * 2];
                    body[0] = 3;
                    body[1] = (byte)(count * 2);
                    for (var j = 0; j < count; j++)
                    {
                        var value = i == 0 ? words[j] : (ushort)40000;
                        body[2 + j * 2] = (byte)(value >> 8);
                        body[3 + j * 2] = (byte)value;
                    }
                }
                else
                {
                    Assert.Equal(1, request[7]);
                    Assert.Equal(1, count);
                    body = new byte[] { 1, 1, 0 };
                }
                var response = new byte[7 + body.Length];
                request.AsSpan(0, 7).CopyTo(response);
                response[4] = 0;
                response[5] = (byte)(1 + body.Length);
                body.CopyTo(response, 7);
                await stream.WriteAsync(response, deadline.Token);
            }
            var status = await read.WaitAsync(deadline.Token);
            Assert.NotNull(status);
            Assert.Equal(yEncoder, status.YEncoder);
            Assert.Equal(yPosition, status.YDisplay);
            Assert.Equal(zEncoder, status.ZEncoder);
            Assert.Equal(zPosition, status.ZDisplay);
            Assert.Equal(-100000, status.XEncoder);
            Assert.Equal(100000, status.XDisplay);
            Assert.Equal(65535, status.ServoAlarm);
            Assert.Equal(40000, status.YSpeed);
            Assert.Equal(40000, status.ZSpeed);
        }
        finally
        {
            listener.Stop();
        }
    }
}
