namespace AutomatedLine.Application.Erp;

/// <summary>ERP接收状态快照，供页面展示；累计件数只包括本进程实际新增的任务。</summary>
/// <param name="IsListening">是否持续监听，暂停只阻止下一批</param>
/// <param name="Message">最近处理结果或等待原因</param>
/// <param name="ImportedCount">本进程累计成功新增件数</param>
public sealed record ErpImportStatus(bool IsListening, string Message, int ImportedCount);

/// <summary>ERP监听控制与查询入口；页面不直接操作文件或创建工件。</summary>
public interface IErpImportService
{
    /// <summary>当前监听状态和最近接收结果。</summary>
    ErpImportStatus Status { get; }

    /// <summary>监听状态变化通知，可能来自后台线程。</summary>
    event EventHandler? StateChanged;

    /// <summary>开启持续监听或暂停后续批次；当前批次完成添加与文件清理。</summary>
    /// <param name="isListening">true开启，false暂停</param>
    void SetListening(bool isListening);
}

/// <summary>ERP文件读写抽象，保持应用层不依赖具体磁盘实现。</summary>
public interface IErpJobFile
{
    /// <summary>读取整批行；不存在返回null，不主动创建ERP文件。</summary>
    /// <param name="path">完整文件路径</param>
    /// <param name="encoding">文本编码名称</param>
    /// <param name="cancellationToken">宿主取消令牌</param>
    /// <returns>文件原始行；不存在返回null</returns>
    Task<string[]?> ReadAsync(string path, string encoding, CancellationToken cancellationToken);

    /// <summary>保存未接收的原始行；全部接收时写空内容，保留文件本身。</summary>
    /// <param name="path">接收时读取的同一文件路径</param>
    /// <param name="lines">应保留的原始行</param>
    /// <param name="encoding">本批使用的文本编码</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>文件更新任务</returns>
    Task WriteRemainingAsync(string path, IReadOnlyList<string> lines, string encoding, CancellationToken cancellationToken);
}
