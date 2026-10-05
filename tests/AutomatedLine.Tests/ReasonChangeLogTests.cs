using AutomatedLine.Application.Logging;
using AutomatedLine.Domain.Diagnostics;
using AutomatedLine.Tests.Fakes;

namespace AutomatedLine.Tests;

/// <summary>
/// "否决原因"变化日志测试（候选生成与调度共用）。
/// 锁住四条约定：**首次立即写**、**原因不变静默**、**原因变化再写**、**恢复写一条且只写一次**，
/// 以及抖动时的**最小间隔限流**（限流不推进基线，间隔过后仍会写出这次变化）。
/// </summary>
public class ReasonChangeLogTests
{
    /// <summary>首次出现原因 → 立即写一条（现场不用等抽样间隔）。</summary>
    [Fact]
    public void FirstProblem_WritesImmediately()
    {
        var logger = new TestLogger();
        var log = new ReasonChangeLog(logger, LogCategory.Generation, minIntervalSeconds: 0);

        log.BeginCycle();
        log.Report("k", "工件 P1/1", "斜床1:报警", "工件 P1/1 无候选：源不可用（斜床1:报警）");

        Assert.Single(logger.Messages);
        Assert.Contains("斜床1:报警", logger.Messages[0]);
    }

    /// <summary>连续多轮原因不变 → 只写第一条，不刷屏（0.5s 一轮也不会爆）。</summary>
    [Fact]
    public void SameReason_DoesNotRepeat()
    {
        var logger = new TestLogger();
        var log = new ReasonChangeLog(logger, LogCategory.Generation, minIntervalSeconds: 0);

        for (var i = 0; i < 5; i++)
        {
            log.BeginCycle();
            log.Report("k", "工件 P1/1", "斜床1:报警", "工件 P1/1 无候选：源不可用（斜床1:报警）");
        }

        Assert.Single(logger.Messages);
    }

    /// <summary>原因变化 → 再写一条（新原因立即可见）。</summary>
    [Fact]
    public void ChangedReason_WritesAgain()
    {
        var logger = new TestLogger();
        var log = new ReasonChangeLog(logger, LogCategory.Scheduling, minIntervalSeconds: 0);

        log.BeginCycle();
        log.Report("k", "候选 P1/1 双头镗→斜床", "天车1:忙", "天车筛选：天车1:忙（无可执行天车）");
        log.BeginCycle();
        log.Report("k", "候选 P1/1 双头镗→斜床", "天车1:报警", "天车筛选：天车1:报警（无可执行天车）");

        Assert.Equal(2, logger.Messages.Count);
        Assert.Contains("天车1:报警", logger.Messages[1]);
    }

    /// <summary>
    /// 本轮不再有问题 → 下一轮结算时写一条"已恢复"（带对象名与上一轮原因），且只写一次。
    /// 注意：恢复要等"没有问题的那一轮"结束才可判定，所以 500ms 周期下比问题消失滞后约 1 轮。
    /// </summary>
    [Fact]
    public void Recovered_WritesRecoveryOnce()
    {
        var logger = new TestLogger();
        var log = new ReasonChangeLog(logger, LogCategory.Generation, minIntervalSeconds: 0);

        log.BeginCycle();   // 第 1 轮开始
        log.Report("k", "工件 P1/1", "上料架 上料架1 未连接", "工件 P1/1 无候选：源不可用（上料架 上料架1 未连接）");

        log.BeginCycle();   // 第 2 轮开始：本轮没有问题（不 Report）
        log.BeginCycle();   // 第 3 轮开始：结算"第 1 轮有、第 2 轮没有" → 写出已恢复
        Assert.Equal(2, logger.Messages.Count);
        Assert.Contains("工件 P1/1 已恢复", logger.Messages[1]);
        Assert.Contains("上料架 上料架1 未连接", logger.Messages[1]);

        log.BeginCycle();   // 再结算一轮不应重复
        Assert.Equal(2, logger.Messages.Count);
    }

    /// <summary>
    /// 原因抖动（如信号抖动导致原因来回变）：最小间隔内不写，避免反而刷屏；
    /// 且**不推进基线**，所以间隔过后这次变化仍会被写出（不会丢）。
    /// </summary>
    [Fact]
    public void Flapping_IsThrottledWithoutAdvancingBaseline()
    {
        var logger = new TestLogger();
        var log = new ReasonChangeLog(logger, LogCategory.Scheduling, minIntervalSeconds: 60);

        log.BeginCycle();
        log.Report("k", "候选 P1/1", "天车1:忙", "天车筛选：天车1:忙");

        log.BeginCycle();
        log.Report("k", "候选 P1/1", "天车1:报警", "天车筛选：天车1:报警");   // 间隔内变化 → 限流
        Assert.Single(logger.Messages);

        log.BeginCycle();
        log.Report("k", "候选 P1/1", "天车1:忙", "天车筛选：天车1:忙");       // 基线仍是"忙" → 依旧静默
        Assert.Single(logger.Messages);
    }

    /// <summary>不同对象互不影响（键不同 → 各自首次都写）。</summary>
    [Fact]
    public void DifferentKeys_AreIndependent()
    {
        var logger = new TestLogger();
        var log = new ReasonChangeLog(logger, LogCategory.Generation, minIntervalSeconds: 0);

        log.BeginCycle();
        log.Report("a", "工件 P1/1", "X", "msg-a");
        log.Report("b", "工件 P2/1", "X", "msg-b");

        Assert.Equal(2, logger.Messages.Count);
    }
}
