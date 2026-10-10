using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Workpieces;

namespace AutomatedLine.Tests;

/// <summary>
/// RuntimeTaskManager 任务表测试：覆盖编辑准入、暂停与派发互斥及暂停后的队列顺序。
/// 只有 Created 可改数据；暂停不得覆盖调度器已取得的执行归属。
/// </summary>
public class RuntimeTaskManagerTests
{
    /// <summary>构造一个未启动（Created）的任务。</summary>
    /// <returns>任务表与刚添加的任务</returns>
    private static (RuntimeTaskManager Manager, WorkpieceTask Task) CreateCreated()
    {
        var manager = new RuntimeTaskManager(new TestLogger());
        var task = manager.Add(new WorkpieceInfo
        {
            PlateNo = "P1",
            SerialNo = 1,
            Diameter = 100,
            Length = 700
        });

        return (manager, task);
    }

    /// <summary>未启动的任务可以改数据；RuntimeId 沿用 → 身份不变。</summary>
    [Fact]
    public void TryUpdate_Created_ReplacesInfoAndKeepsRuntimeId()
    {
        var (manager, task) = CreateCreated();
        var originalId = task.Id;

        var updated = new WorkpieceInfo
        {
            RuntimeId = originalId,
            PlateNo = "P1",
            SerialNo = 1,
            Diameter = 120,
            Length = 800,
            PlugHole = 100,
            PrintContent = "X",
            InnerHoleTaper = 1.5,
            FilletSize = 2.5,
            LeftPlugThickness = 3,
            RightPlugThickness = 4,
            LatheMachiningMode = 7
        };

        Assert.True(manager.TryUpdate(originalId, updated));

        // 数据换掉了、身份没变、状态仍是未启动
        Assert.Equal(originalId, task.Id);
        Assert.Equal(120, task.Info.Diameter);
        Assert.Equal(800, task.Info.Length);
        Assert.Equal(7, task.Info.LatheMachiningMode);
        Assert.Equal(WorkpieceState.Created, task.Runtime.TaskState);
    }

    /// <summary>已启动（Waiting，已入线）的任务拒绝改数据。</summary>
    [Fact]
    public void TryUpdate_AfterStart_IsRejected()
    {
        var (manager, task) = CreateCreated();

        Assert.True(manager.Start(task.Id));
        Assert.Equal(WorkpieceState.Waiting, task.Runtime.TaskState);

        Assert.False(manager.TryUpdate(task.Id, new WorkpieceInfo { RuntimeId = task.Id, Diameter = 999 }));

        // 数据未被改动
        Assert.Equal(100, task.Info.Diameter);
    }

    /// <summary>启动过又暂停（Paused）的任务同样拒绝：它已经入过线。</summary>
    [Fact]
    public void TryUpdate_AfterPause_IsRejected()
    {
        var (manager, task) = CreateCreated();

        Assert.True(manager.Start(task.Id));
        Assert.True(manager.Pause(task.Id));
        Assert.Equal(WorkpieceState.Paused, task.Runtime.TaskState);

        Assert.False(manager.TryUpdate(task.Id, new WorkpieceInfo { RuntimeId = task.Id, Diameter = 999 }));
        Assert.Equal(100, task.Info.Diameter);
    }

    /// <summary>任务不存在返回 false。</summary>
    [Fact]
    public void TryUpdate_UnknownTask_ReturnsFalse()
        => Assert.False(new RuntimeTaskManager(new TestLogger())
            .TryUpdate(Guid.NewGuid(), new WorkpieceInfo { Diameter = 1 }));

    /// <summary>派发或执行已开始时，暂停失败且保留入线顺序。</summary>
    /// <param name="state">暂停请求到达时的工件状态</param>
    [Theory]
    [InlineData(WorkpieceState.Assigned)]
    [InlineData(WorkpieceState.Running)]
    public void Pause_AfterDispatch_PreservesStateAndQueueOrder(WorkpieceState state)
    {
        var (manager, task) = CreateCreated();
        Assert.True(manager.Start(task.Id));
        var order = task.Runtime.QueueOrder;
        task.Runtime.TaskState = state;

        Assert.False(manager.Pause(task.Id));
        Assert.Equal(state, task.Runtime.TaskState);
        Assert.Equal(order, task.Runtime.QueueOrder);
    }

    /// <summary>暂停成功才出队，再次启动保持原有入队尾行为。</summary>
    [Fact]
    public void Pause_ThenStart_RequeuesAtTail()
    {
        var (manager, task) = CreateCreated();
        Assert.True(manager.Start(task.Id));
        var order = task.Runtime.QueueOrder;

        Assert.True(manager.Pause(task.Id));
        Assert.Equal(WorkpieceState.Paused, task.Runtime.TaskState);
        Assert.Equal(0, task.Runtime.QueueOrder);
        Assert.True(manager.Start(task.Id));
        Assert.Equal(WorkpieceState.Waiting, task.Runtime.TaskState);
        Assert.True(task.Runtime.QueueOrder > order);
    }

    /// <summary>并发暂停与调度 CAS 只能有一方成功，失败暂停不能清掉派发工件的排位。</summary>
    [Fact]
    public void Pause_RacingDispatch_OnlyOneTransitionSucceeds()
    {
        var (manager, task) = CreateCreated();
        using var barrier = new Barrier(2);
        var stop = false;
        var assigned = false;
        var round = 0;
        string? violation = null;
        // 持续复用两条线程，并变化派发时机，覆盖暂停检查与状态写入之间的竞争窗口。
        var dispatcher = new Thread(() =>
        {
            while (true)
            {
                barrier.SignalAndWait();
                if (stop) return;
                Thread.SpinWait(round % 30);
                assigned = task.Runtime.TryTransition(WorkpieceState.Waiting, WorkpieceState.Assigned);
                barrier.SignalAndWait();
            }
        }) { IsBackground = true };
        dispatcher.Start();
        try
        {
            for (round = 0; round < 10000; round++)
            {
                task.Runtime.QueueOrder = 7;
                task.Runtime.TaskState = WorkpieceState.Waiting;
                barrier.SignalAndWait();
                var paused = manager.Pause(task.Id);
                barrier.SignalAndWait();
                if (paused == assigned
                    || task.Runtime.TaskState != (paused ? WorkpieceState.Paused : WorkpieceState.Assigned)
                    || task.Runtime.QueueOrder != (paused ? 0 : 7))
                {
                    violation = $"第{round}轮：暂停={paused}，派发={assigned}，状态={task.Runtime.TaskState}，排位={task.Runtime.QueueOrder}";
                    break;
                }
            }
        }
        finally
        {
            // 先结束竞争线程再断言，避免断言失败留下等待下一轮的后台线程。
            stop = true;
            barrier.SignalAndWait();
            dispatcher.Join();
        }

        Assert.Null(violation);
    }
}
