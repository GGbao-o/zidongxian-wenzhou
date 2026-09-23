using System.Net.Sockets;

namespace AutomatedLine.Infrastructure.Communication;

/// <summary>
/// 自研最小 Modbus TCP 客户端（汇川 PLC 用，端口默认 502）。
/// 支持 FC03 读保持寄存器、FC01 读线圈、FC06 写单寄存器、FC16 写多寄存器（DINT 低字在前）。
/// 不依赖第三方库；内部用 SemaphoreSlim 串行化请求，保证同一连接上不会交错收发。
/// </summary>
public sealed class ModbusTcpClient : IDisposable
{
    /// <summary>PLC IP。</summary>
    private readonly string _ip;

    /// <summary>端口（默认 502）。</summary>
    private readonly int _port;

    /// <summary>站号。</summary>
    private readonly byte _unitId;

    /// <summary>单次请求超时（毫秒）。</summary>
    private readonly int _timeoutMs;

    /// <summary>串行化收发。</summary>
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    /// <summary>底层 TCP 连接。</summary>
    private TcpClient? _tcp;

    /// <summary>网络流。</summary>
    private NetworkStream? _stream;

    /// <summary>事务号，自增。</summary>
    private int _transactionId;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="ip">PLC IP</param>
    /// <param name="port">端口，默认 502</param>
    /// <param name="unitId">站号，默认 1</param>
    /// <param name="timeoutMs">单次请求超时（毫秒），默认 3000</param>
    public ModbusTcpClient(string ip, int port = 502, byte unitId = 1, int timeoutMs = 3000)
    {
        _ip = ip;
        _port = port;
        _unitId = unitId;
        _timeoutMs = timeoutMs;
    }

    /// <summary>当前是否已连接。</summary>
    public bool IsConnected => _tcp is { Connected: true } && _stream is not null;

    /// <summary>
    /// 建立连接（幂等）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected)
        {
            return;
        }

        await DisconnectAsync().ConfigureAwait(false);

        var tcp = new TcpClient
        {
            ReceiveTimeout = _timeoutMs,
            SendTimeout = _timeoutMs
        };

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(_timeoutMs);
        await tcp.ConnectAsync(_ip, _port, linked.Token).ConfigureAwait(false);

        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    /// <summary>断开连接（幂等）。</summary>
    public Task DisconnectAsync()
    {
        try
        {
            _stream?.Dispose();
        }
        catch
        {
            // 忽略关闭异常
        }

        try
        {
            _tcp?.Dispose();
        }
        catch
        {
            // 忽略关闭异常
        }

        _stream = null;
        _tcp = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// 读保持寄存器（FC03）。
    /// </summary>
    /// <param name="startAddress">起始地址（0 基，如 D5000 → 5000）</param>
    /// <param name="count">寄存器数量</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>寄存器值数组</returns>
    public async Task<ushort[]> ReadRegistersAsync(int startAddress, int count, CancellationToken cancellationToken = default)
    {
        var pdu = new byte[5];
        pdu[0] = 0x03;
        pdu[1] = (byte)(startAddress >> 8);
        pdu[2] = (byte)(startAddress & 0xFF);
        pdu[3] = (byte)(count >> 8);
        pdu[4] = (byte)(count & 0xFF);

        var resp = await ExchangeAsync(pdu, cancellationToken).ConfigureAwait(false);
        EnsureFunction(resp, 0x03);

        var result = new ushort[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = (ushort)((resp[2 + i * 2] << 8) | resp[3 + i * 2]);
        }

        return result;
    }

    /// <summary>
    /// 读单个线圈（FC01）。
    /// </summary>
    /// <param name="address">线圈地址（0 基，如 X6 → 63494）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>ON 返回 true</returns>
    public async Task<bool> ReadCoilAsync(int address, CancellationToken cancellationToken = default)
    {
        var pdu = new byte[5];
        pdu[0] = 0x01;
        pdu[1] = (byte)(address >> 8);
        pdu[2] = (byte)(address & 0xFF);
        pdu[3] = 0x00;
        pdu[4] = 0x01;

        var resp = await ExchangeAsync(pdu, cancellationToken).ConfigureAwait(false);
        EnsureFunction(resp, 0x01);
        return (resp[2] & 0x01) != 0;
    }

    /// <summary>
    /// 写单个寄存器（FC06）。
    /// </summary>
    /// <param name="address">地址</param>
    /// <param name="value">值</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task WriteRegisterAsync(int address, ushort value, CancellationToken cancellationToken = default)
    {
        var pdu = new byte[5];
        pdu[0] = 0x06;
        pdu[1] = (byte)(address >> 8);
        pdu[2] = (byte)(address & 0xFF);
        pdu[3] = (byte)(value >> 8);
        pdu[4] = (byte)(value & 0xFF);

        var resp = await ExchangeAsync(pdu, cancellationToken).ConfigureAwait(false);
        EnsureFunction(resp, 0x06);
    }

    /// <summary>
    /// 写多个连续寄存器（FC16）。
    /// </summary>
    /// <param name="startAddress">起始地址</param>
    /// <param name="values">值数组</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task WriteRegistersAsync(int startAddress, IReadOnlyList<ushort> values, CancellationToken cancellationToken = default)
    {
        var pdu = new byte[5 + 1 + values.Count * 2];
        pdu[0] = 0x10;
        pdu[1] = (byte)(startAddress >> 8);
        pdu[2] = (byte)(startAddress & 0xFF);
        pdu[3] = (byte)(values.Count >> 8);
        pdu[4] = (byte)(values.Count & 0xFF);
        pdu[5] = (byte)(values.Count * 2);
        for (var i = 0; i < values.Count; i++)
        {
            pdu[6 + i * 2] = (byte)(values[i] >> 8);
            pdu[7 + i * 2] = (byte)(values[i] & 0xFF);
        }

        var resp = await ExchangeAsync(pdu, cancellationToken).ConfigureAwait(false);
        EnsureFunction(resp, 0x10);
    }

    /// <summary>
    /// 读 32 位有符号整数（DINT，小端低字在前）。
    /// </summary>
    /// <param name="startAddress">低字地址</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>组合后的 32 位值</returns>
    public async Task<int> ReadInt32Async(int startAddress, CancellationToken cancellationToken = default)
    {
        var regs = await ReadRegistersAsync(startAddress, 2, cancellationToken).ConfigureAwait(false);
        return unchecked((int)(((uint)regs[1] << 16) | regs[0]));
    }

    /// <summary>
    /// 写 32 位有符号整数（DINT，小端低字在前）。
    /// </summary>
    /// <param name="startAddress">低字地址</param>
    /// <param name="value">值</param>
    /// <param name="cancellationToken">取消令牌</param>
    public Task WriteInt32Async(int startAddress, int value, CancellationToken cancellationToken = default)
    {
        ushort low = (ushort)(value & 0xFFFF);
        ushort high = (ushort)((value >> 16) & 0xFFFF);
        return WriteRegistersAsync(startAddress, new[] { low, high }, cancellationToken);
    }

    /// <summary>
    /// 发送一次请求并读取响应（自动连接、超时后断开以便下次重连）。
    /// </summary>
    /// <param name="pdu">PDU 数据</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>响应 PDU（不含 MBAP 头）</returns>
    private async Task<byte[]> ExchangeAsync(byte[] pdu, CancellationToken cancellationToken)
    {
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(_timeoutMs);

            if (!IsConnected)
            {
                await ConnectAsync(linked.Token).ConfigureAwait(false);
            }

            var stream = _stream!;
            var txId = unchecked((ushort)Interlocked.Increment(ref _transactionId));

            var frame = new byte[7 + pdu.Length];
            frame[0] = (byte)(txId >> 8);
            frame[1] = (byte)(txId & 0xFF);
            frame[2] = 0x00; // 协议标识
            frame[3] = 0x00;
            var length = 1 + pdu.Length;
            frame[4] = (byte)(length >> 8);
            frame[5] = (byte)(length & 0xFF);
            frame[6] = _unitId;
            Array.Copy(pdu, 0, frame, 7, pdu.Length);

            try
            {
                await stream.WriteAsync(frame, 0, frame.Length, linked.Token).ConfigureAwait(false);
                await stream.FlushAsync(linked.Token).ConfigureAwait(false);

                var header = await ReadExactAsync(stream, 6, linked.Token).ConfigureAwait(false);
                var respLength = (header[4] << 8) | header[5];   // 含 unitId
                var unit = await ReadExactAsync(stream, 1, linked.Token).ConfigureAwait(false);
                _ = unit;
                var bodyLength = respLength - 1;
                if (bodyLength < 0)
                {
                    throw new IOException("Modbus 响应长度非法");
                }

                var body = await ReadExactAsync(stream, bodyLength, linked.Token).ConfigureAwait(false);
                return body;
            }
            catch
            {
                // 通信失败：断开连接，下一次调用会重连
                await DisconnectAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>
    /// 严格读取指定字节数。
    /// </summary>
    /// <param name="stream">网络流</param>
    /// <param name="length">字节数</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>读到的字节数组</returns>
    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int length, CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                throw new IOException("Modbus 连接已关闭");
            }

            offset += read;
        }

        return buffer;
    }

    /// <summary>
    /// 校验响应功能码；异常响应（最高位为 1）时抛出带异常码的异常。
    /// </summary>
    /// <param name="response">响应 PDU</param>
    /// <param name="expectedFunction">期望功能码</param>
    private static void EnsureFunction(byte[] response, byte expectedFunction)
    {
        if (response.Length == 0)
        {
            throw new IOException("Modbus 空响应");
        }

        if ((response[0] & 0x80) != 0)
        {
            var code = response.Length > 1 ? response[1] : (byte)0;
            throw new IOException($"Modbus 异常响应，功能码 0x{response[0]:X2}，异常码 {code}");
        }

        if (response[0] != expectedFunction)
        {
            throw new IOException($"Modbus 功能码不匹配，期望 0x{expectedFunction:X2}，实际 0x{response[0]:X2}");
        }
    }

    /// <summary>释放资源。</summary>
    public void Dispose()
    {
        _ = DisconnectAsync();
        _ioLock.Dispose();
    }
}
