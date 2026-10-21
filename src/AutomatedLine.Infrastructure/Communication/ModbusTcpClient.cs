using System.Diagnostics;
using System.Net.Sockets;
using AutomatedLine.Application.Abstractions;
using AutomatedLine.Domain.Diagnostics;

namespace AutomatedLine.Infrastructure.Communication;

/// <summary>
/// 自研最小 Modbus TCP 客户端（汇川 PLC / 地面设备用）。
/// 支持 FC01 读线圈、FC02 读离散输入、FC03 读保持寄存器、FC04 读输入寄存器、
/// FC05 写单线圈、FC06 写单寄存器、FC16 写多寄存器（DINT 低字在前）。
/// 不依赖第三方库；内部用 SemaphoreSlim 串行化请求，保证同一连接上不会交错收发。
/// 地址一律 0 基（PDU 地址）；文档里的 1 基编号（如 0001 / 10001）需先减 1 / 去掉 1x 前缀。
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

    /// <summary>收发失败时记录报文现场；正常轮询不逐次落盘。</summary>
    private readonly IAppLogger? _logger;

    /// <summary>仅兼容旧打号机FC04正常响应末尾额外的一个00；标准设备不启用。</summary>
    private readonly bool _expectMarkerReadTrailingZero;

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
    /// <param name="logger">可选的应用日志，用于记录失败阶段、请求和实际收到的字节</param>
    /// <param name="expectMarkerReadTrailingZero">是否消费并校验FC04正常响应末尾额外的一个00，默认false，仅旧打号机启用</param>
    public ModbusTcpClient(string ip, int port = 502, byte unitId = 1, int timeoutMs = 3000, IAppLogger? logger = null,
        bool expectMarkerReadTrailingZero = false)
    {
        _ip = ip;
        _port = port;
        _unitId = unitId;
        _timeoutMs = timeoutMs;
        _logger = logger;
        _expectMarkerReadTrailingZero = expectMarkerReadTrailingZero;
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
        try
        {
            await tcp.ConnectAsync(_ip, _port, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 自己的超时触发的取消：转成明确的超时异常，避免被误读为“外部取消”
            tcp.Dispose();
            throw new TimeoutException($"连接 {_ip}:{_port} 超时（{_timeoutMs}ms）");
        }
        catch
        {
            tcp.Dispose();
            throw;
        }

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

        var result = new ushort[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = (ushort)((resp[2 + i * 2] << 8) | resp[3 + i * 2]);
        }

        return result;
    }

    /// <summary>
    /// 读输入寄存器（FC04）。用于打号机等"输入寄存器"型设备（只读）。
    /// </summary>
    /// <param name="startAddress">起始地址（0 基）</param>
    /// <param name="count">寄存器数量</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>寄存器值数组</returns>
    public async Task<ushort[]> ReadInputRegistersAsync(int startAddress, int count, CancellationToken cancellationToken = default)
    {
        var pdu = new byte[5];
        pdu[0] = 0x04;
        pdu[1] = (byte)(startAddress >> 8);
        pdu[2] = (byte)(startAddress & 0xFF);
        pdu[3] = (byte)(count >> 8);
        pdu[4] = (byte)(count & 0xFF);

        var resp = await ExchangeAsync(pdu, cancellationToken).ConfigureAwait(false);

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
        return (resp[2] & 0x01) != 0;
    }

    /// <summary>
    /// 读多个离散输入（FC02）。用于料架"有板"信号等只读输入线圈。
    /// </summary>
    /// <param name="startAddress">起始地址（0 基）</param>
    /// <param name="count">位数</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>每一位的布尔值数组（与地址顺序一致）</returns>
    public async Task<bool[]> ReadDiscreteInputsAsync(int startAddress, int count, CancellationToken cancellationToken = default)
    {
        var pdu = new byte[5];
        pdu[0] = 0x02;
        pdu[1] = (byte)(startAddress >> 8);
        pdu[2] = (byte)(startAddress & 0xFF);
        pdu[3] = (byte)(count >> 8);
        pdu[4] = (byte)(count & 0xFF);

        var resp = await ExchangeAsync(pdu, cancellationToken).ConfigureAwait(false);

        var result = new bool[count];
        for (var i = 0; i < count; i++)
        {
            // 每 1 字节 8 位，字节内低位在前（bit0 对应该字节最低位）
            result[i] = (resp[2 + (i / 8)] & (1 << (i % 8))) != 0;
        }

        return result;
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

        await ExchangeAsync(pdu, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 写单个线圈（FC05）。
    /// </summary>
    /// <param name="address">线圈地址（0 基）</param>
    /// <param name="value">true=ON(0xFF00)，false=OFF(0x0000)</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task WriteCoilAsync(int address, bool value, CancellationToken cancellationToken = default)
    {
        var pdu = new byte[5];
        pdu[0] = 0x05;
        pdu[1] = (byte)(address >> 8);
        pdu[2] = (byte)(address & 0xFF);
        pdu[3] = value ? (byte)0xFF : (byte)0x00;
        pdu[4] = 0x00;

        await ExchangeAsync(pdu, cancellationToken).ConfigureAwait(false);
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

        await ExchangeAsync(pdu, cancellationToken).ConfigureAwait(false);
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
    /// 发送一次请求并读取响应（自动连接、失败后断开以便下次重连）。
    /// 内部期限到达抛出超时异常，只有调用者主动取消才传播取消异常。
    /// </summary>
    /// <param name="pdu">PDU 数据</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>响应 PDU（不含 MBAP 头）</returns>
    /// <exception cref="TimeoutException">连接、发送或读取超过单次请求期限。</exception>
    /// <exception cref="OperationCanceledException">调用者取消请求。</exception>
    private async Task<byte[]> ExchangeAsync(byte[] pdu, CancellationToken cancellationToken)
    {
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        var phase = "连接";
        byte[]? frame = null;
        var header = new byte[7];
        var headerRead = 0;
        var body = Array.Empty<byte>();
        var bodyRead = 0;
        var trailer = Array.Empty<byte>();
        var trailerRead = 0;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(_timeoutMs);

            try
            {
                // 自动连接也使用请求期限，必须纳入同一异常边界，避免嵌套令牌把超时误报为外部取消。
                if (!IsConnected)
                {
                    await ConnectAsync(linked.Token).ConfigureAwait(false);
                }

                var stream = _stream!;
                var txId = unchecked((ushort)Interlocked.Increment(ref _transactionId));

                frame = new byte[7 + pdu.Length];
                frame[0] = (byte)(txId >> 8);
                frame[1] = (byte)(txId & 0xFF);
                frame[2] = 0x00; // 协议标识
                frame[3] = 0x00;
                var length = 1 + pdu.Length;
                frame[4] = (byte)(length >> 8);
                frame[5] = (byte)(length & 0xFF);
                frame[6] = _unitId;
                Array.Copy(pdu, 0, frame, 7, pdu.Length);

                phase = "发送请求";
                await stream.WriteAsync(frame, 0, frame.Length, linked.Token).ConfigureAwait(false);
                await stream.FlushAsync(linked.Token).ConfigureAwait(false);

                phase = "读取响应头";
                await ReadExactAsync(stream, header, linked.Token, read => headerRead = read).ConfigureAwait(false);
                phase = "解析响应头";
                var respLength = (header[4] << 8) | header[5];   // 含 unitId
                var bodyLength = respLength - 1;
                if (bodyLength < 0)
                {
                    throw new ModbusProtocolException("Modbus 响应长度非法");
                }

                phase = "读取响应体";
                body = new byte[bodyLength];
                await ReadExactAsync(stream, body, linked.Token, read => bodyRead = read).ConfigureAwait(false);
                // 放在收发边界内校验，设备异常响应也能留下对应请求和原始响应，便于区分地址错误与报文头错误。
                phase = "校验功能码";
                EnsureFunction(body, pdu[0]);
                if (_expectMarkerReadTrailingZero && pdu[0] == 0x04)
                {
                    // 旧VB服务端数组上界多1，正常FC04响应后固定追加00。必须在本次锁内读齐，不能等到下一头再丢前导0。
                    // 先校验功能码，异常响应直接沿用断开清理其额外尾字节；正常FC06/FC16不应多读。
                    phase = "读取打号机FC04尾字节";
                    trailer = new byte[1];
                    await ReadExactAsync(stream, trailer, linked.Token, read => trailerRead = read).ConfigureAwait(false);
                    phase = "校验打号机FC04尾字节";
                    if (trailer[0] != 0)
                    {
                        throw new ModbusProtocolException($"打号机FC04响应尾字节非法：期望00，实际{trailer[0]:X2}");
                    }
                }
                return body;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // 内部期限只终止当前请求，不能让上层把它当作停止天车执行器的命令。
                await DisconnectAsync().ConfigureAwait(false);
                var timeout = new TimeoutException($"Modbus 请求 {_ip}:{_port} 超时（{_timeoutMs}ms）", ex);
                LogFailure(timeout);
                throw timeout;
            }
            catch (Exception ex)
            {
                // 通信失败：断开连接，下一次调用会重连
                await DisconnectAsync().ConfigureAwait(false);
                // 主动取消属于流程控制；保留取消异常，但不把正常停止记录为通信故障。
                if (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    LogFailure(ex);
                }
                throw;
            }
        }
        finally
        {
            _ioLock.Release();
        }

        // 只输出实际读取的前缀，避免把未填充的默认0误当成对端返回；诊断失败不能替换原通信异常。
        void LogFailure(Exception exception)
        {
            if (_logger is null) return;
            try
            {
                var responseLength = headerRead >= 6 ? ((header[4] << 8) | header[5]).ToString() : "未取得";
                _logger.Error(LogCategory.Communication,
                    $"Modbus通信失败：端点={_ip}:{_port}，UnitId={_unitId}，FC=0x{pdu[0]:X2}，阶段={phase}，" +
                    $"耗时={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}ms，连接模式=复用连接，" +
                    $"协议适配={(_expectMarkerReadTrailingZero ? "打号机FC04尾零" : "标准")}，原因={exception.Message}；" +
                    $"请求={Hex(frame, frame?.Length ?? 0)}；" +
                    $"响应头={Hex(header, headerRead)}（已读{headerRead}/7字节），Length={responseLength}；" +
                    $"响应体={Hex(body, bodyRead)}（已读{bodyRead}/{body.Length}字节）；" +
                    $"适配尾字节={Hex(trailer, trailerRead)}（已读{trailerRead}/{trailer.Length}字节）",
                    exception);
            }
            catch
            {
                // 日志出口不可用时仍向上抛出原始通信异常，保持断开和释放锁的语义。
            }
        }
    }

    /// <summary>
    /// 严格读取指定字节数。
    /// </summary>
    /// <param name="stream">网络流</param>
    /// <param name="buffer">需要完整填充的缓冲区</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <param name="reportProgress">记录实际读入长度，异常时用于区分真实字节与未填充缓冲区</param>
    /// <returns>缓冲区填充完成后的异步任务</returns>
    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken, Action<int> reportProgress)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                throw new IOException("Modbus 连接已关闭");
            }

            offset += read;
            reportProgress(offset);
        }
    }

    // 请求和响应各最多保留64字节，避免长打印内容或畸形响应把一条诊断膨胀为大段日志。
    private static string Hex(byte[]? buffer, int count)
    {
        if (buffer is null || count == 0) return "无";
        var shown = Math.Min(count, 64);
        return Convert.ToHexString(buffer.AsSpan(0, shown)) + (count > shown ? "…（仅前64字节）" : string.Empty);
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
            throw new ModbusProtocolException("Modbus 空响应");
        }

        if ((response[0] & 0x80) != 0)
        {
            var code = response.Length > 1 ? response[1] : (byte)0;
            throw new ModbusProtocolException($"Modbus 异常响应，功能码 0x{response[0]:X2}，异常码 {code}");
        }

        if (response[0] != expectedFunction)
        {
            throw new ModbusProtocolException($"Modbus 功能码不匹配，期望 0x{expectedFunction:X2}，实际 0x{response[0]:X2}");
        }
    }

    /// <summary>释放资源。</summary>
    public void Dispose()
    {
        _ = DisconnectAsync();
        _ioLock.Dispose();
    }
}

/// <summary>已收到响应但协议或设备明确拒绝的错误，供运动读取排除重试；仍兼容现有IOException捕获。</summary>
internal sealed class ModbusProtocolException : IOException
{
    /// <summary>保留原始诊断内容，区分重连可恢复的传输故障和不可盲目重试的协议错误。</summary>
    /// <param name="message">原始协议或设备异常说明。</param>
    internal ModbusProtocolException(string message) : base(message) { }
}
