using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Workpieces;

namespace AutomatedLine.Tests;

/// <summary>
/// RuntimeTaskManager 任务表测试：覆盖"编辑任务"的准入判据
/// （只有 <c>Created</c> 可改；已入线一律拒绝；替换时必须沿用 RuntimeId 保持身份不变）。
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
}
