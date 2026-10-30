using System.Diagnostics;
using System.Runtime.Versioning;
using AutomatedLine.Ui.Services;

namespace AutomatedLine.Tests;

/// <summary>验证实例保护的互斥、唤醒和生命周期，不运行生产程序或后台服务。</summary>
[SupportedOSPlatform("windows")]
public sealed class SingleInstanceServiceTests
{
    /// <summary>并发申请只能有一个成功，防止同时双击穿过初始化入口。</summary>
    /// <returns>竞争验证任务。</returns>
    [Fact]
    public async Task ConcurrentAcquisitionHasOnlyOneWinner()
    {
        var name = NewName();
        var services = Enumerable.Range(0, 12).Select(_ => new SingleInstanceService(name)).ToArray();
        try
        {
            var results = await Task.WhenAll(services.Select(s => Task.Run(s.TryAcquire)));
            Assert.Equal(1, results.Count(success => success));
        }
        finally { foreach (var service in services) service.Dispose(); }
    }

    /// <summary>重复实例通过事件唤醒原实例，释放保护后允许新实例取得资格。</summary>
    /// <returns>通知与重新启动验证任务。</returns>
    [Fact]
    public async Task DuplicateNotifiesOwnerAndReleaseAllowsRestart()
    {
        var name = NewName();
        using var owner = new SingleInstanceService(name);
        using var duplicate = new SingleInstanceService(name);
        Assert.True(owner.TryAcquire());
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.StartListening(() => notified.TrySetResult());
        Assert.False(duplicate.TryAcquire());
        Assert.True(await duplicate.NotifyExistingAsync());
        await notified.Task.WaitAsync(TimeSpan.FromSeconds(5));
        owner.Dispose();
        using var restarted = new SingleInstanceService(name);
        Assert.True(restarted.TryAcquire());
    }

    /// <summary>保护已存在但通知端尚未就绪时，通知只能有界失败，不能创建第二个实例。</summary>
    /// <returns>通知期限验证任务。</returns>
    [Fact]
    public async Task MissingNotificationEndpointReturnsFalse()
    {
        using var service = new SingleInstanceService(NewName());
        Assert.False(await service.NotifyExistingAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    /// <summary>通知早于监听任务启动也会保留，避免初始化期间的重复双击丢失唤醒。</summary>
    /// <returns>初始化窗口通知验证任务。</returns>
    [Fact]
    public async Task EarlyNotificationIsRetainedUntilListenerStarts()
    {
        var name = NewName();
        using var owner = new SingleInstanceService(name);
        using var duplicate = new SingleInstanceService(name);
        Assert.True(owner.TryAcquire());
        Assert.False(duplicate.TryAcquire());
        Assert.True(await duplicate.NotifyExistingAsync());
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.StartListening(() => notified.TrySetResult());
        await notified.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>独立进程持有保护时拒绝进入；持有者被强制结束后无需清理锁文件即可重启。</summary>
    /// <returns>跨进程生命周期验证任务。</returns>
    [Fact]
    public async Task OtherProcessBlocksStartupAndCrashReleasesGuard()
    {
        var name = NewName();
        var command = $"$guard=[System.Threading.Mutex]::new($false,'{name}.Instance'); [Console]::WriteLine('READY'); [Console]::ReadLine() | Out-Null; $guard.Dispose()";
        using var child = new Process
        {
            StartInfo = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardInput = true,
                ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", command }
            }
        };
        child.Start();
        try
        {
            Assert.Equal("READY", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            using var duplicate = new SingleInstanceService(name);
            Assert.False(duplicate.TryAcquire());
            child.Kill();
            await child.WaitForExitAsync();
            using var restarted = new SingleInstanceService(name);
            // TerminateProcess返回/进程句柄变为有信号，不保证内核句柄清理已经全部完成。
            var acquired = false;
            for (var attempt = 0; attempt < 40 && !acquired; attempt++)
            {
                acquired = restarted.TryAcquire();
                if (!acquired) await Task.Delay(50);
            }
            Assert.True(acquired);
        }
        finally
        {
            if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
        }
    }

    private static string NewName() => @"Local\AutomatedLine.Tests." + Guid.NewGuid().ToString("N");
}
