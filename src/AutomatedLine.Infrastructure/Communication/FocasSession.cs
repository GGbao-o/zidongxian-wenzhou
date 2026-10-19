using FanucFocas;

namespace AutomatedLine.Infrastructure.Communication;

/// <summary>隔离原生SDK入口，供真实通信通道和无需现场设备的断线恢复测试共用。</summary>
internal interface IFocasSession
{
    /// <summary>托管会话是否仍持有已建立的连接。</summary>
    bool IsConnected { get; }

    /// <summary>建立会话；调用方负责在专用线程执行。</summary>
    /// <param name="ip">CNC地址。</param>
    /// <param name="port">FOCAS端口。</param>
    /// <param name="timeoutSeconds">连接超时秒数。</param>
    /// <returns>连接建立成功时为true。</returns>
    bool Connect(string ip, ushort port, int timeoutSeconds);

    /// <summary>释放句柄并清除托管连接状态；释放异常也必须清除状态。</summary>
    void Disconnect();

    /// <summary>设置当前会话的原生操作超时。</summary>
    /// <param name="seconds">超时秒数。</param>
    void SetTimeout(int seconds);

    /// <summary>读取指定宏变量。</summary>
    /// <param name="macro">宏号。</param>
    /// <returns>宏变量数值。</returns>
    double GetMacro(int macro);

    /// <summary>写入指定宏变量，不自动重发。</summary>
    /// <param name="macro">宏号。</param>
    /// <param name="value">写入值。</param>
    void SetMacro(int macro, double value);
}

/// <summary>生产会话适配器，保留FanucSdk原有连接、读写及超时调用。</summary>
internal sealed class FocasSession : IFocasSession
{
    /// <summary>仅由所属通道的专用线程调用原生方法，保持句柄线程归属。</summary>
    private readonly FanucSdk _sdk = new();

    /// <inheritdoc />
    public bool IsConnected => _sdk.IsConnected;

    /// <inheritdoc />
    public bool Connect(string ip, ushort port, int timeoutSeconds) => _sdk.Connect(ip, port, timeoutSeconds);

    /// <inheritdoc />
    public void Disconnect() => _sdk.Disconnect();

    /// <inheritdoc />
    public void SetTimeout(int seconds) => Focas1.cnc_settimeout(_sdk.Handle, seconds);

    /// <inheritdoc />
    public double GetMacro(int macro) => _sdk.GetMacro(macro);

    /// <inheritdoc />
    public void SetMacro(int macro, double value) => _sdk.SetMacro(macro, value);
}
