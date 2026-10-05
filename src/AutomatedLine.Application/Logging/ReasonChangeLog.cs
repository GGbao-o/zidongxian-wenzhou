using AutomatedLine.Application.Abstractions;
using AutomatedLine.Domain.Diagnostics;

namespace AutomatedLine.Application.Logging;

/// <summary>
/// "否决原因"日志助手：同一对象的原因**变化时**才写一条，原因消失时写一条"已恢复"。
/// <para>
/// 为什么需要它：候选生成与集中调度每 0.5s 跑一轮，"某设备 / 某天车 / 某任务为什么不行"的原因每轮都会重算，
/// 若每轮都打会刷爆日志；而只按时间抽样（`Sample`）又有两个盲区：
/// ① **部分不行时永远看不到** —— 例如 3 个目标只跳过 2 个、2 台天车只有 1 台可执行时，
/// 被跳过对象的原因不会出现在任何一行里；
/// ② **恢复不可见** —— 问题消失后没有对照行，现场不知道"什么时候好的"。
/// </para>
/// <para>
/// 因此改为**按变化触发**：原因首次出现立即写；原因变化再写；不变则静默（周期汇总行负责体现"还卡着"）；
/// 某个原因在某一轮不再出现后，**再下一轮的** <see cref="BeginCycle"/> 会写一条"已恢复"
/// （恢复要等"没有问题的那一轮"跑完才可判定，故 500ms 周期下比问题消失滞后约 1 轮）。
/// 另外给同一对象设**最小落盘间隔**：原因来回抖动（如信号抖动）时最多每 N 秒一条，避免反而变成刷屏。
/// </para>
/// <para>用法：每轮开头调 <see cref="BeginCycle"/>，然后对**每个仍有问题的对象**调 <see cref="Report"/>（没问题就不调）。</para>
/// </summary>
public sealed class ReasonChangeLog
{
    /// <summary>日志。</summary>
    private readonly IAppLogger _logger;

    /// <summary>日志类别。</summary>
    private readonly LogCategory _category;

    /// <summary>同一对象两次"原因落盘"的最小间隔（防原因抖动变成刷屏）。</summary>
    private readonly TimeSpan _minInterval;

    /// <summary>上一轮仍有问题的对象（对照基线；本轮没再出现的键会随轮次丢弃，内存不随运行时长增长）。</summary>
    private Dictionary<string, ReasonState> _previous = new();

    /// <summary>本轮仍有问题的对象。</summary>
    private Dictionary<string, ReasonState> _current = new();

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="logger">日志</param>
    /// <param name="category">日志类别（候选生成用 Generation、调度用 Scheduling）</param>
    /// <param name="minIntervalSeconds">同一对象两次原因落盘的最小间隔（秒）</param>
    public ReasonChangeLog(IAppLogger logger, LogCategory category, int minIntervalSeconds)
    {
        _logger = logger;
        _category = category;
        _minInterval = TimeSpan.FromSeconds(Math.Max(0, minIntervalSeconds));
    }

    /// <summary>
    /// 开始新一轮：先把"上一轮有问题、本轮已没问题"的对象写一条**已恢复**，再交换缓冲。
    /// 必须在每轮**最开头**调用，且在 <see cref="Report"/> 之前。
    /// </summary>
    public void BeginCycle()
    {
        var now = DateTime.Now;
        foreach (var (key, state) in _previous)
        {
            // 上一轮有问题的键，本轮若没有被 Report 重新登记，说明该对象已恢复正常
            if (!_current.ContainsKey(key))
            {
                // 恢复行不打折：它必然跟在一条已落盘的原因之后，本身就是低频且现场最需要的信息
                _logger.Info(_category, $"{state.Subject} 已恢复（上一轮原因：{state.Reason}）");
            }
        }

        // 交换缓冲并清空：没再出现的键自然被丢弃
        (_previous, _current) = (_current, _previous);
        _current.Clear();
    }

    /// <summary>
    /// 报告某对象**本轮仍有问题**的原因；与上一轮不同（含首次出现）时写一条日志。
    /// 原因未变、或距上次落盘不足最小间隔时静默（抖动时限流，下一轮原因若仍不同会再写）。
    /// </summary>
    /// <param name="key">对象键（同一对象必须每次用同一键，如 "src-{任务号}"）</param>
    /// <param name="subject">对象可读名（如 "工件 P1/1"、"天车1"），"已恢复"行使用</param>
    /// <param name="reason">本轮原因（工整的短句，如 "斜床1:报警"）</param>
    /// <param name="message">原因变化时要写的完整日志文本</param>
    public void Report(string key, string subject, string reason, string message)
    {
        var now = DateTime.Now;
        var previous = _previous.TryGetValue(key, out var prev) ? prev : null;

        // 原因没变 → 静默，并沿用上次落盘时间（保持"还卡着"的基线）
        if (previous is not null && previous.Reason == reason)
        {
            _current[key] = previous;
            return;
        }

        // 原因变了但距上次落盘太近 → 本轮不写、也不推进基线，下一轮再判（避免把这次变化丢掉）
        if (previous is not null && now - previous.LoggedAt < _minInterval)
        {
            _current[key] = previous;
            return;
        }

        _logger.Info(_category, message);
        _current[key] = new ReasonState(subject, reason, now);
    }

    /// <summary>某个对象的原因快照。</summary>
    /// <param name="Subject">对象可读名</param>
    /// <param name="Reason">原因文本</param>
    /// <param name="LoggedAt">该原因的落盘时间</param>
    private sealed record ReasonState(string Subject, string Reason, DateTime LoggedAt);
}
