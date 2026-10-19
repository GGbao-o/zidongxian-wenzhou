using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Diagnostics;
using AutomatedLine.Domain.Workpieces;
using Microsoft.Extensions.Hosting;

namespace AutomatedLine.Application.Erp;

/// <summary>ERP批次接收宿主服务；默认持续监听，合法工件按文件顺序自动入队，线路开关仍独立控制。</summary>
public sealed class ErpImportService : BackgroundService, IErpImportService
{
    private readonly IErpJobFile _file;
    private readonly IRuntimeTaskManager _tasks;
    private readonly IConfigService _config;
    private readonly IAppLogger _logger;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private ErpImportStatus _status = new(true, "等待接收", 0);
    // 清理失败时保留原批次与已接收行索引，暂停/继续也不丢回执；只在本进程有效。
    private readonly Dictionary<string, Receipt> _receipts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _warnings = new();
    private string? _lastSource;

    /// <summary>创建接收服务，实际磁盘读写通过抽象注入。</summary>
    /// <param name="file">批次文件读写</param>
    /// <param name="tasks">全线内存任务表</param>
    /// <param name="config">最新程序配置</param>
    /// <param name="logger">操作及任务日志</param>
    public ErpImportService(IErpJobFile file, IRuntimeTaskManager tasks, IConfigService config, IAppLogger logger)
    {
        _file = file;
        _tasks = tasks;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public ErpImportStatus Status { get { lock (_sync) return _status; } }

    /// <inheritdoc />
    public event EventHandler? StateChanged;

    /// <inheritdoc />
    public void SetListening(bool isListening)
    {
        lock (_sync)
        {
            if (_status.IsListening == isListening) return;
            _status = _status with { IsListening = isListening };
        }
        _logger.Info(LogCategory.Operation, $"ERP接收：{(isListening ? "开启持续监听" : "暂停监听，当前批次完成后停止接收")}");
        Notify();
        // 信号仅用于唤醒单循环；按钮重复点击不会创建另一个监听任务。
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    /// <summary>暂停时不读文件；运行时一批结束后再等待节拍，下一轮获取最新配置。</summary>
    /// <param name="stoppingToken">宿主关闭令牌</param>
    /// <returns>宿主后台循环</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.Info(LogCategory.Operation,
                $"ERP接收服务启动：{(Status.IsListening ? "持续监听已开启" : "监听已暂停")}，合法工件按文件行序自动启动入队");
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!Status.IsListening)
                {
                    await _wake.WaitAsync(stoppingToken).ConfigureAwait(false);
                    continue;
                }
                var interval = 500;
                try
                {
                    var options = _config.GetAppOptions().ErpImport;
                    if (options.PollIntervalMs <= 0) throw new InvalidOperationException("ERP检查间隔必须大于0");
                    interval = options.PollIntervalMs;
                    if (string.IsNullOrWhiteSpace(options.FilePath)) throw new InvalidOperationException("ERP文件路径不能为空");
                    var path = Path.GetFullPath(options.FilePath);
                    var source = $"{path}，间隔={interval}ms，编码={options.Encoding}";
                    if (_lastSource != source)
                    {
                        _lastSource = source;
                        _logger.Info(LogCategory.Operation, $"ERP监听配置：{source}");
                    }
                    await ReceiveBatchAsync(path, options.Encoding, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    WarnOnce("receive", $"ERP接收失败：{ex.Message}");
                    Publish($"接收失败：{ex.Message}");
                }
                await _wake.WaitAsync(interval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            lock (_sync) _status = _status with { IsListening = false };
            Notify();
            _logger.Info(LogCategory.Operation, "ERP接收服务已停止");
        }
    }

    // 人工暂停不打断已经开始的一批；先确认内存已接收，再清对应行，避免只清文件却未创建任务。
    private async Task ReceiveBatchAsync(string path, string encoding, CancellationToken stoppingToken)
    {
        var lines = await _file.ReadAsync(path, encoding, stoppingToken).ConfigureAwait(false);
        if (_warnings.Remove("receive")) _logger.Info(LogCategory.Operation, $"ERP文件读取已恢复：{path}");
        if (lines is null)
        {
            Publish($"等待文件：{path}");
            return;
        }
        if (lines.Length == 0) return; // 空文件不覆盖最近成功批次的结果。

        if (!_receipts.TryGetValue(path, out var receipt) || !receipt.Lines.SequenceEqual(lines))
            _receipts[path] = receipt = new Receipt(lines);
        var remaining = new List<string>();
        var failures = new Dictionary<int, string>();
        var parsed = new List<(int Index, WorkpieceInfo Info)>();
        // 文件已经一次读完；先整批解析，再一次添加和通知页面，避免每个工件都同步刷新整张表。
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (receipt.Accepted.Contains(index) || string.IsNullOrWhiteSpace(line)) continue;
            if (!ErpJobParser.TryParse(line, out var info, out var error))
            {
                failures[index] = $"第{index + 1}行：{error}";
                continue;
            }
            parsed.Add((index, info!));
        }

        var results = _tasks.TryAddImportedBatch(parsed.Select(entry => entry.Info).ToArray());
        var added = 0;
        for (var index = 0; index < parsed.Count; index++)
        {
            var entry = parsed[index];
            var task = results[index];
            if (task is null)
            {
                failures[entry.Index] = $"第{entry.Index + 1}行：工件{entry.Info}版号和序号重复，未覆盖原任务";
                continue;
            }
            receipt.Accepted.Add(entry.Index);
            added++;
            _logger.Info(LogCategory.Operation,
                $"ERP接收 {path} 第{entry.Index + 1}行：工件{entry.Info}，任务={task.Id}，入队状态=Waiting（已自动启动），入线顺序={task.Runtime.QueueOrder}");
        }
        // 校验错误和重复身份可能交错出现，回写和提示都按原文件行号排序，不改变剩余行顺序。
        var problems = failures.OrderBy(entry => entry.Key).Select(entry => entry.Value).ToList();
        for (var index = 0; index < lines.Length; index++)
            if (!receipt.Accepted.Contains(index) && !string.IsNullOrWhiteSpace(lines[index])) remaining.Add(lines[index]);
        if (added > 0) Publish($"本批新增并自动启动{added}件，正在清理已接收行", added);
        if (problems.Count > 0) WarnOnce("rows:" + path, $"ERP文件 {path} 保留{problems.Count}行：{string.Join("；", problems)}");

        if (remaining.Count == lines.Length)
        {
            Publish($"新增0件，保留{remaining.Count}行；{problems.FirstOrDefault()}");
            return;
        }
        // 添加后即使宿主关闭也要完成本批清理；手动暂停只影响下一批，不取消这次回写。
        try
        {
            await _file.WriteRemainingAsync(path, remaining, encoding, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            WarnOnce("cleanup:" + path, $"ERP文件清理失败：{path}，已接收{receipt.Accepted.Count}件，保留回执待重试：{ex.Message}");
            Publish($"已接收{receipt.Accepted.Count}件，文件清理失败待重试：{ex.Message}");
            return;
        }
        _receipts.Remove(path);
        _warnings.Remove("cleanup:" + path);
        _logger.Info(LogCategory.Operation,
            $"ERP文件清理完成：{path}，已接收{receipt.Accepted.Count}行，保留{remaining.Count}行，忽略空行{lines.Count(string.IsNullOrWhiteSpace)}");
        if (problems.Count == 0) _warnings.Remove("rows:" + path);
        Publish($"已接收{receipt.Accepted.Count}件（已自动启动入队），保留{remaining.Count}行"
            + (problems.Count > 0 ? $"；{problems[0]}" : ""));
    }

    private void Publish(string message, int added = 0)
    {
        lock (_sync)
        {
            if (_status.Message == message && added == 0) return;
            _status = _status with { Message = message, ImportedCount = _status.ImportedCount + added };
        }
        Notify();
    }

    private void WarnOnce(string key, string message)
    {
        if (_warnings.GetValueOrDefault(key) == message) return;
        _warnings[key] = message;
        _logger.Warn(LogCategory.Operation, message);
    }

    // 页面订阅者失败不能让宿主循环退出，也不能把已接收的批次重新导入。
    private void Notify()
    {
        try { StateChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { _logger.Warn(LogCategory.Operation, $"ERP状态通知失败：{ex.Message}"); }
    }

    private sealed class Receipt(string[] lines)
    {
        /// <summary>清理失败时的原始批次，确保回执不会应用到另一份文件内容。</summary>
        public string[] Lines { get; } = lines;
        /// <summary>已经提交到内存的原始行索引，下次仅清理而不重新创建。</summary>
        public HashSet<int> Accepted { get; } = new();
    }
}
