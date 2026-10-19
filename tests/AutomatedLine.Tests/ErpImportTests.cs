using System.Globalization;
using System.Text;
using AutomatedLine.Application.Erp;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Workpieces;
using AutomatedLine.Infrastructure.Erp;
using AutomatedLine.Tests.Fakes;

namespace AutomatedLine.Tests;

/// <summary>ERP真实解析、内存任务和磁盘消费回归，全部使用测试自建目录，不接触现场文件。</summary>
public sealed class ErpImportTests
{
    /// <summary>服务器区域不同也保留长度/直径/堵厚小数，不进行PLC换算。</summary>
    /// <param name="culture">服务器数字区域设置</param>
    [Theory]
    [InlineData("fr-FR")]
    [InlineData("zh-CN")]
    public void Parser_PreservesOriginalValues(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            Assert.True(ErpJobParser.TryParse(" A261014001-1 , A261014001 , 1 , 1200.1 , 180.3 , 70 , 1 , 25.5 , extra", out var info, out var error));
            Assert.Null(error);
            Assert.NotNull(info);
            Assert.Equal("A261014001-1", info.PrintContent);
            Assert.Equal("A261014001", info.PlateNo);
            Assert.Equal(1, info.SerialNo);
            Assert.Equal(1200.1, info.Length);
            Assert.Equal(180.3, info.Diameter);
            Assert.Equal(70, info.PlugHole);
            Assert.Equal(1, info.LatheMachiningMode);
            Assert.Equal(25.5, info.PlugThickness);
            Assert.NotEqual(Guid.Empty, info.RuntimeId);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    /// <summary>ERP序号允许从0开始，仍以整数原值保存业务身份。</summary>
    /// <param name="serial">允许的非负整数序号。</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void Parser_AcceptsNonNegativeSerial(int serial)
    {
        Assert.True(ErpJobParser.TryParse(Row(serial), out var info, out var error));
        Assert.Null(error);
        Assert.NotNull(info);
        Assert.Equal(serial, info.SerialNo);
    }

    /// <summary>非法行明确失败，不能把非法字段默认为0或以NaN创建工件。</summary>
    /// <param name="line">非法ERP原始行</param>
    /// <param name="reason">应指出的错误字段</param>
    [Theory]
    [InlineData("bad", "8列")]
    [InlineData("X,,1,1200,180,70,1,25", "版号")]
    [InlineData("X,P,-1,1200,180,70,1,25", "序号")]
    [InlineData("X,P,,1200,180,70,1,25", "序号")]
    [InlineData("X,P,abc,1200,180,70,1,25", "序号")]
    [InlineData("X,P,2147483648,1200,180,70,1,25", "序号")]
    [InlineData("X,P,1.5,1200,180,70,1,25", "序号")]
    [InlineData("X,P,1,0,180,70,1,25", "长度")]
    [InlineData("X,P,1,NaN,180,70,1,25", "长度")]
    [InlineData("X,P,1,1200,-1,70,1,25", "直径")]
    [InlineData("X,P,1,1200,Infinity,70,1,25", "直径")]
    [InlineData("X,P,1,1200,180,80,1,25", "堵孔")]
    [InlineData("X,P,1,1200,180,70,0,25", "工艺")]
    [InlineData("X,P,1,1200,180,70,11,25", "工艺")]
    [InlineData("X,P,1,1200,180,70,1,NaN", "堵厚")]
    [InlineData("X,P,1,1200,180,70,1,not-number", "堵厚")]
    public void Parser_RejectsInvalidRows(string line, string reason)
    {
        Assert.False(ErpJobParser.TryParse(line, out var info, out var error));
        Assert.Null(info);
        Assert.Contains(reason, error);
    }

    /// <summary>多个ERP入口竞争同一业务身份只能新增一次；不会改变人工Add原本规则。</summary>
    [Fact]
    public async Task ImportedIdentity_IsAtomic_AndManualAddUnchanged()
    {
        var manager = new RuntimeTaskManager(new TestLogger());
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
            manager.TryAddImported(new WorkpieceInfo { PlateNo = "P", SerialNo = 1 }))));
        Assert.Single(results.Where(x => x is not null));
        Assert.Single(manager.Snapshot());
        manager.Add(new WorkpieceInfo { PlateNo = "P", SerialNo = 1 });
        Assert.Equal(2, manager.Snapshot().Count);
    }

    /// <summary>默认不读文件；开启后5行生成5个Created，按原顺序显示且清空为0字节。</summary>
    [Fact]
    public async Task FiveRows_AreCreatedWithoutStarting_AndFileConsumed()
    {
        await using var fixture = new Fixture();
        var notifications = 0;
        fixture.Manager.Changed += (_, _) => Interlocked.Increment(ref notifications);
        await File.WriteAllLinesAsync(fixture.Path, Enumerable.Range(1, 5).Select(Row));
        await fixture.Service.StartAsync(default);
        Assert.False(fixture.Service.Status.IsListening);
        Assert.Equal(0, fixture.File.ReadCount);
        fixture.Service.SetListening(true);
        await Until(() => fixture.Manager.Snapshot().Count == 5 && new FileInfo(fixture.Path).Length == 0);
        Assert.Equal(Enumerable.Range(1, 5), fixture.Manager.Snapshot().Select(t => t.Info.SerialNo));
        Assert.All(fixture.Manager.Snapshot(), t =>
        {
            Assert.Equal(WorkpieceState.Created, t.Runtime.TaskState);
            Assert.Equal(0, t.Runtime.QueueOrder);
            Assert.Equal(WorkpieceLocation.Queue, t.Runtime.CurrentLocation);
        });
        Assert.Equal(5, fixture.Service.Status.ImportedCount);
        Assert.Equal(1, notifications);
    }

    /// <summary>正确行接收并删除，重复和非法行原样保留，既有任务不覆盖。</summary>
    [Fact]
    public async Task InvalidAndDuplicateRows_Remain_ValidRowIsConsumed()
    {
        await using var fixture = new Fixture();
        var existing = fixture.Manager.Add(new WorkpieceInfo { PlateNo = "P", SerialNo = 1, Length = 999 });
        await File.WriteAllLinesAsync(fixture.Path, new[] { Row(1), "bad", Row(2) });
        await fixture.Service.StartAsync(default);
        fixture.Service.SetListening(true);
        await Until(() => fixture.Manager.Snapshot().Count == 2 && fixture.File.WriteCount >= 1);
        fixture.Service.SetListening(false);
        await fixture.Service.StopAsync(default);
        Assert.Equal(new[] { Row(1), "bad" }, await File.ReadAllLinesAsync(fixture.Path));
        Assert.Same(existing, fixture.Manager.Get(existing.Id));
        Assert.Equal(999, existing.Info.Length);
        Assert.Contains(fixture.Logger.Messages, m => m.Contains("第1行") && m.Contains("重复"));
        Assert.Contains(fixture.Logger.Messages, m => m.Contains("第2行") && m.Contains("8列"));
    }

    /// <summary>序号0正常导入并清理，同版号序号0仍去重，负序号原行保留。</summary>
    /// <returns>异步文件消费验证任务。</returns>
    [Fact]
    public async Task ZeroSerial_IsImportedAndConsumed_DuplicatesAndNegativeRemain()
    {
        await using var fixture = new Fixture();
        await File.WriteAllLinesAsync(fixture.Path, new[] { Row(0), Row(0), Row(1), Row(-1) });
        await fixture.Service.StartAsync(default);
        fixture.Service.SetListening(true);
        await Until(() => fixture.Manager.Snapshot().Count == 2 && fixture.File.WriteCount >= 1);
        fixture.Service.SetListening(false);
        await fixture.Service.StopAsync(default);

        Assert.Equal(new[] { 0, 1 }, fixture.Manager.Snapshot().Select(task => task.Info.SerialNo));
        Assert.All(fixture.Manager.Snapshot(), task => Assert.Equal(WorkpieceState.Created, task.Runtime.TaskState));
        Assert.Equal(new[] { Row(0), Row(-1) }, await File.ReadAllLinesAsync(fixture.Path));
        Assert.Equal(2, fixture.Service.Status.ImportedCount);
        Assert.Contains(fixture.Logger.Messages, message => message.Contains("第2行") && message.Contains("重复"));
        Assert.Contains(fixture.Logger.Messages, message => message.Contains("第4行") && message.Contains("序号必须为非负整数"));
    }

    /// <summary>清理失败后暂停/继续仍复用回执，不把成功行作为重复错误遗留。</summary>
    [Fact]
    public async Task CleanupFailure_RetryAfterPause_DoesNotDuplicateTasks()
    {
        await using var fixture = new Fixture();
        fixture.File.FailWrites = true;
        await File.WriteAllLinesAsync(fixture.Path, new[] { Row(1), Row(2) });
        await fixture.Service.StartAsync(default);
        fixture.Service.SetListening(true);
        await fixture.File.WriteFailed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.Service.SetListening(false);
        Assert.Equal(2, fixture.Manager.Snapshot().Count);
        Assert.Equal(2, (await File.ReadAllLinesAsync(fixture.Path)).Length);
        fixture.File.FailWrites = false;
        fixture.Service.SetListening(true);
        await Until(() => new FileInfo(fixture.Path).Length == 0);
        Assert.Equal(2, fixture.Manager.Snapshot().Count);
        Assert.Equal(2, fixture.Service.Status.ImportedCount);
        Assert.DoesNotContain(fixture.Logger.Messages, m => m.Contains("版号和序号重复"));
    }

    /// <summary>暂停不打断当前已开始批次；本批清理结束后不继续读取，恢复后可接下一批。</summary>
    [Fact]
    public async Task Pause_FinishesCurrentBatch_ThenResumeReadsNextBatch()
    {
        await using var fixture = new Fixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.File.BeforeRead = ct => release.Task.WaitAsync(ct);
        await File.WriteAllLinesAsync(fixture.Path, new[] { Row(1) });
        await fixture.Service.StartAsync(default);
        fixture.Service.SetListening(true);
        await fixture.File.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.Service.SetListening(false);
        release.SetResult();
        await Until(() => new FileInfo(fixture.Path).Length == 0);
        var reads = fixture.File.ReadCount;
        await File.WriteAllLinesAsync(fixture.Path, new[] { Row(2) });
        await Task.Delay(150);
        Assert.Equal(reads, fixture.File.ReadCount);
        Assert.Single(fixture.Manager.Snapshot());
        fixture.Service.SetListening(true);
        await Until(() => fixture.Manager.Snapshot().Count == 2 && new FileInfo(fixture.Path).Length == 0);
    }

    /// <summary>读失败只影响ERP入口，宿主循环随后恢复并接收。</summary>
    [Fact]
    public async Task ReadFailure_RecoversWithoutRestartingListener()
    {
        await using var fixture = new Fixture();
        fixture.File.ReadFailures = 1;
        await File.WriteAllLinesAsync(fixture.Path, new[] { Row(1) });
        await fixture.Service.StartAsync(default);
        fixture.Service.SetListening(true);
        await Until(() => fixture.Manager.Snapshot().Count == 1 && new FileInfo(fixture.Path).Length == 0);
        Assert.True(fixture.Service.Status.IsListening);
        Assert.Contains(fixture.Logger.Messages, m => m.Contains("接收失败"));
        Assert.Contains(fixture.Logger.Messages, m => m.Contains("读取已恢复"));
    }

    /// <summary>不存在时保持等待，后续创建文件即可接收；运行中下一轮采用新路径。</summary>
    [Fact]
    public async Task MissingFileAndPathReload_AreHandledWhileListening()
    {
        await using var fixture = new Fixture();
        await fixture.Service.StartAsync(default);
        fixture.Service.SetListening(true);
        await fixture.File.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(fixture.Manager.Snapshot());
        var otherPath = System.IO.Path.Combine(fixture.Directory, "other.txt");
        await File.WriteAllLinesAsync(otherPath, new[] { Row(1) });
        fixture.Config.Options.ErpImport.FilePath = otherPath;
        await Until(() => fixture.Manager.Snapshot().Count == 1 && new FileInfo(otherPath).Length == 0);
        Assert.False(File.Exists(fixture.Path));
    }

    /// <summary>页面通知异常发生在内存提交后，仍应清理成功行。</summary>
    [Fact]
    public async Task TaskNotificationFailure_DoesNotLoseReceipt()
    {
        await using var fixture = new Fixture();
        fixture.Manager.Changed += (_, _) => throw new InvalidOperationException("模拟UI关闭");
        await File.WriteAllLinesAsync(fixture.Path, new[] { Row(1) });
        await fixture.Service.StartAsync(default);
        fixture.Service.SetListening(true);
        await Until(() => fixture.Manager.Snapshot().Count == 1 && new FileInfo(fixture.Path).Length == 0);
        Assert.Contains(fixture.Logger.Messages, m => m.Contains("列表通知失败"));
    }

    /// <summary>宿主关闭时已新增的批次仍完成清理，不留下取消造成的重导入窗口。</summary>
    [Fact]
    public async Task HostStop_WaitsForAcceptedBatchCleanup()
    {
        await using var fixture = new Fixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.File.BeforeWrite = ct => release.Task.WaitAsync(ct);
        await File.WriteAllLinesAsync(fixture.Path, new[] { Row(1) });
        await fixture.Service.StartAsync(default);
        fixture.Service.SetListening(true);
        await fixture.File.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var stop = fixture.Service.StopAsync(default);
        try
        {
            Assert.False(stop.IsCompleted);
            Assert.Single(fixture.Manager.Snapshot());
        }
        finally { release.TrySetResult(); }
        await stop.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, new FileInfo(fixture.Path).Length);
        Assert.False(fixture.Service.Status.IsListening);
    }

    /// <summary>UTF8和GBK都能保留中文打印内容，空行不会生成工件。</summary>
    /// <param name="encoding">文件编码</param>
    [Theory]
    [InlineData("utf-8")]
    [InlineData("gbk")]
    public async Task FileEncoding_PreservesChinese_AndSkipsBlankLines(string encoding)
    {
        await using var fixture = new Fixture();
        fixture.Config.Options.ErpImport.Encoding = encoding;
        await File.WriteAllLinesAsync(fixture.Path, new[] { "  ", "打印中文,P,1,1200,180,100,10,25" }, Encoding.GetEncoding(encoding));
        await fixture.Service.StartAsync(default);
        fixture.Service.SetListening(true);
        await Until(() => fixture.Manager.Snapshot().Count == 1 && new FileInfo(fixture.Path).Length == 0);
        Assert.Equal("打印中文", fixture.Manager.Snapshot()[0].Info.PrintContent);
        Assert.Equal(10, fixture.Manager.Snapshot()[0].Info.LatheMachiningMode);
    }

    /// <summary>回写编码错误不能先截断原ERP文件，临时文件也会清理。</summary>
    [Fact]
    public async Task FailedRewrite_PreservesOriginalFile()
    {
        await using var fixture = new Fixture();
        await File.WriteAllTextAsync(fixture.Path, Row(1));
        await Assert.ThrowsAsync<ArgumentException>(() => new ErpJobFile().WriteRemainingAsync(fixture.Path, Array.Empty<string>(), "invalid-encoding", default));
        Assert.Equal(Row(1), await File.ReadAllTextAsync(fixture.Path));
        Assert.Single(System.IO.Directory.GetFiles(fixture.Directory));
    }

    /// <summary>第一次接收结束后仍持续监听，不用再次点击就能接收第二批。</summary>
    [Fact]
    public async Task ContinuousListening_ReceivesNextBatchWithoutToggling()
    {
        await using var fixture = new Fixture();
        await File.WriteAllLinesAsync(fixture.Path, new[] { Row(1) });
        await fixture.Service.StartAsync(default);
        fixture.Service.SetListening(true);
        await Until(() => fixture.Manager.Snapshot().Count == 1 && new FileInfo(fixture.Path).Length == 0);
        await File.WriteAllLinesAsync(fixture.Path, new[] { Row(2) });
        await Until(() => fixture.Manager.Snapshot().Count == 2 && new FileInfo(fixture.Path).Length == 0);
        Assert.True(fixture.Service.Status.IsListening);
        Assert.Equal(2, fixture.Service.Status.ImportedCount);
    }

    /// <summary>非法文件编码不能被当成空文件或删除；相同读取错误不刷日志。</summary>
    [Fact]
    public async Task InvalidEncodedFile_IsPreserved_AndWarningsAreSuppressed()
    {
        await using var fixture = new Fixture();
        byte[] bytes = { 0xC3, 0x28 };
        await File.WriteAllBytesAsync(fixture.Path, bytes);
        await fixture.Service.StartAsync(default);
        fixture.Service.SetListening(true);
        await Until(() => fixture.File.ReadCount >= 3);
        fixture.Service.SetListening(false);
        await fixture.Service.StopAsync(default);
        Assert.Empty(fixture.Manager.Snapshot());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.Path));
        Assert.Single(fixture.Logger.Messages.Where(m => m.Contains("ERP接收失败")));
    }

    /// <summary>坏行与清理失败同时存在时，各类重复错误分别抑制，保持回执不重复新增。</summary>
    [Fact]
    public async Task InvalidRowsAndCleanupFailure_DoNotFloodLogs()
    {
        await using var fixture = new Fixture();
        fixture.File.FailWrites = true;
        await File.WriteAllLinesAsync(fixture.Path, new[] { Row(1), "bad" });
        await fixture.Service.StartAsync(default);
        fixture.Service.SetListening(true);
        await Until(() => fixture.File.WriteCount >= 3);
        fixture.Service.SetListening(false);
        await fixture.Service.StopAsync(default);
        Assert.Single(fixture.Manager.Snapshot());
        Assert.Single(fixture.Logger.Messages.Where(m => m.Contains("ERP文件清理失败")));
        Assert.Single(fixture.Logger.Messages.Where(m => m.Contains("保留1行")));
        Assert.Equal(new[] { Row(1), "bad" }, await File.ReadAllLinesAsync(fixture.Path));
    }

    /// <summary>既有身份和批内重复按输入索引返回null，成功任务按原顺序添加且只通知一次。</summary>
    [Fact]
    public void BatchAdd_PreservesOrderAndDuplicateResults_AndNotifiesOnce()
    {
        var manager = new RuntimeTaskManager(new TestLogger());
        var existing = manager.Add(Info(1));
        var notifications = 0;
        var observedCount = 0;
        manager.Changed += (_, _) => { notifications++; observedCount = manager.Snapshot().Count; };
        var infos = new[] { Info(1), Info(2), Info(2), Info(3) };
        var result = manager.TryAddImportedBatch(infos);
        Assert.Equal(4, result.Count);
        Assert.Null(result[0]);
        Assert.Same(infos[1], result[1]!.Info);
        Assert.Null(result[2]);
        Assert.Same(infos[3], result[3]!.Info);
        Assert.Equal(new[] { 1, 2, 3 }, manager.Snapshot().Select(t => t.Info.SerialNo));
        Assert.Same(existing, manager.Snapshot()[0]);
        Assert.Equal(1, notifications);
        Assert.Equal(3, observedCount);
        Assert.All(manager.Snapshot(), t => Assert.Equal(WorkpieceState.Created, t.Runtime.TaskState));
    }

    /// <summary>空批次与全部重复都不产生集合变更通知。</summary>
    [Fact]
    public void BatchAdd_NoNewTask_DoesNotNotify()
    {
        var manager = new RuntimeTaskManager(new TestLogger());
        manager.Add(Info(1));
        var notifications = 0;
        manager.Changed += (_, _) => notifications++;
        Assert.Empty(manager.TryAddImportedBatch(Array.Empty<WorkpieceInfo>()));
        Assert.All(manager.TryAddImportedBatch(new[] { Info(1), Info(1) }), task => Assert.Null(task));
        Assert.Equal(0, notifications);
        Assert.Single(manager.Snapshot());
    }

    /// <summary>并发批次仍只创建两个业务身份，总共只发一次成功批次通知。</summary>
    [Fact]
    public async Task ConcurrentBatches_DoNotDuplicateIdentities()
    {
        var manager = new RuntimeTaskManager(new TestLogger());
        var notifications = 0;
        manager.Changed += (_, _) => Interlocked.Increment(ref notifications);
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
            manager.TryAddImportedBatch(new[] { Info(1), Info(2) }))));
        Assert.Equal(2, results.Sum(batch => batch.Count(task => task is not null)));
        Assert.Equal(2, manager.Snapshot().Count);
        Assert.Equal(1, notifications);
    }

    /// <summary>已有200件时从真实文件再接50件，也只发一次通知且保持所有既有任务。</summary>
    [Fact]
    public async Task FiftyRows_WithExistingTasks_NotifyOnce()
    {
        await using var fixture = new Fixture();
        var existing = fixture.Manager.TryAddImportedBatch(Enumerable.Range(1, 200).Select(Info).ToArray());
        var notifications = 0;
        fixture.Manager.Changed += (_, _) => Interlocked.Increment(ref notifications);
        await File.WriteAllLinesAsync(fixture.Path, Enumerable.Range(201, 50).Select(Row));
        await fixture.Service.StartAsync(default);
        fixture.Service.SetListening(true);
        await Until(() => fixture.Manager.Snapshot().Count == 250 && new FileInfo(fixture.Path).Length == 0);
        Assert.Equal(1, notifications);
        Assert.Equal(50, fixture.Service.Status.ImportedCount);
        var snapshot = fixture.Manager.Snapshot();
        for (var index = 0; index < existing.Count; index++) Assert.Same(existing[index], snapshot[index]);
    }

    /// <summary>批内重复和校验失败交错出现时，剩余文件行仍按原顺序保留。</summary>
    [Fact]
    public async Task MixedRows_RetainOriginalOrderAfterBatchAdd()
    {
        await using var fixture = new Fixture();
        var lines = new[] { "bad-first", Row(1), Row(1), "bad-last", Row(2) };
        await File.WriteAllLinesAsync(fixture.Path, lines);
        await fixture.Service.StartAsync(default);
        fixture.Service.SetListening(true);
        await Until(() => fixture.Manager.Snapshot().Count == 2 && fixture.File.WriteCount >= 1);
        fixture.Service.SetListening(false);
        await fixture.Service.StopAsync(default);
        Assert.Equal(new[] { lines[0], lines[2], lines[3] }, await File.ReadAllLinesAsync(fixture.Path));
        Assert.Equal(new[] { 1, 2 }, fixture.Manager.Snapshot().Select(t => t.Info.SerialNo));
    }

    private static WorkpieceInfo Info(int serial)
    {
        Assert.True(ErpJobParser.TryParse(Row(serial), out var info, out _));
        return info!;
    }

    private static string Row(int serial) => $"A-{serial},P,{serial},1200.1,180.3,70,1,25.5";

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(condition(), "等待ERP状态超时");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AutomatedLine-Erp-" + Guid.NewGuid().ToString("N"));
        public string Path { get; }
        public FakeConfigService Config { get; } = new();
        public TestLogger Logger { get; } = new();
        public RuntimeTaskManager Manager { get; }
        public ScriptedFile File { get; } = new();
        public ErpImportService Service { get; }

        /// <summary>为每个测试独立创建临时目录和真实磁盘网关，配置不使用现场默认路径。</summary>
        public Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            Path = System.IO.Path.Combine(Directory, "job1.txt");
            Config.Options.ErpImport.FilePath = Path;
            Config.Options.ErpImport.PollIntervalMs = 50;
            Manager = new RuntimeTaskManager(Logger);
            Service = new ErpImportService(File, Manager, Config, Logger);
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await Service.StopAsync(default);
            Service.Dispose();
            System.IO.Directory.Delete(Directory, true);
        }
    }

    private sealed class ScriptedFile : IErpJobFile
    {
        private readonly ErpJobFile _inner = new();
        private int _readCount;
        private int _writeCount;
        public int ReadCount => Volatile.Read(ref _readCount);
        public int WriteCount => Volatile.Read(ref _writeCount);
        public bool FailWrites { get; set; }
        public int ReadFailures;
        public Func<CancellationToken, Task>? BeforeRead { get; set; }
        public Func<CancellationToken, Task>? BeforeWrite { get; set; }
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WriteFailed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <inheritdoc />
        public async Task<string[]?> ReadAsync(string path, string encoding, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _readCount);
            ReadStarted.TrySetResult();
            if (BeforeRead is not null) await BeforeRead(cancellationToken);
            if (Interlocked.Exchange(ref ReadFailures, 0) > 0) throw new IOException("模拟读取失败");
            return await _inner.ReadAsync(path, encoding, cancellationToken);
        }

        /// <inheritdoc />
        public async Task WriteRemainingAsync(string path, IReadOnlyList<string> lines, string encoding, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _writeCount);
            WriteStarted.TrySetResult();
            if (BeforeWrite is not null) await BeforeWrite(cancellationToken);
            if (FailWrites)
            {
                WriteFailed.TrySetResult();
                throw new IOException("模拟清理失败");
            }
            await _inner.WriteRemainingAsync(path, lines, encoding, cancellationToken);
        }
    }
}
