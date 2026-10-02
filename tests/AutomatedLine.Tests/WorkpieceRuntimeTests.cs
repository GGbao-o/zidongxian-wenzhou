using AutomatedLine.Domain.Workpieces;

namespace AutomatedLine.Tests;

/// <summary>
/// 工件运行状态原子迁移测试（调度器 Waiting→Assigned 只成功一次）。
/// </summary>
public class WorkpieceRuntimeTests
{
    /// <summary>同一状态迁移只成功一次。</summary>
    [Fact]
    public void TryTransition_OnlyOnce()
    {
        var runtime = new WorkpieceRuntime { TaskState = WorkpieceState.Waiting };

        Assert.True(runtime.TryTransition(WorkpieceState.Waiting, WorkpieceState.Assigned));
        Assert.False(runtime.TryTransition(WorkpieceState.Waiting, WorkpieceState.Assigned));
        Assert.Equal(WorkpieceState.Assigned, runtime.TaskState);
    }

    /// <summary>并发迁移只允许一个线程成功。</summary>
    [Fact]
    public void TryTransition_Concurrent_OnlyOneWins()
    {
        var runtime = new WorkpieceRuntime { TaskState = WorkpieceState.Waiting };
        var wins = 0;

        Parallel.For(0, 200, _ =>
        {
            if (runtime.TryTransition(WorkpieceState.Waiting, WorkpieceState.Assigned))
            {
                Interlocked.Increment(ref wins);
            }
        });

        Assert.Equal(1, wins);
    }
}
