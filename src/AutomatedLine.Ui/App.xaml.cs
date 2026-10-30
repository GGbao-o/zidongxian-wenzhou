using System.Windows;
using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Alarms;
using AutomatedLine.Application.Collection;
using AutomatedLine.Application.Generation;
using AutomatedLine.Application.Erp;
using AutomatedLine.Infrastructure.Erp;
using AutomatedLine.Application.Line;
using AutomatedLine.Application.Reservations;
using AutomatedLine.Infrastructure.Manual;
using AutomatedLine.Application.RightOfWay;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Application.Scheduling;
using AutomatedLine.Application.Workers;
using AutomatedLine.Domain.Alarms;
using AutomatedLine.Infrastructure.Config;
using AutomatedLine.Infrastructure.Logging;
using AutomatedLine.Infrastructure.Persistence;
using AutomatedLine.Ui.Services;
using AutomatedLine.Ui.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AutomatedLine.Ui;

/// <summary>
/// 应用入口。
/// 负责搭起依赖注入与配置容器、注册各层服务、显示主窗口。
/// 使用 Generic Host 作为容器，好处是配置/日志/生命周期统一，后续常驻逻辑循环（采集、调度、Worker）
/// 都可以注册为宿主服务并行管理（架构文档 §12.1）。
/// </summary>
public partial class App : System.Windows.Application
{
    /// <summary>宿主实例。持有 DI 容器与配置，随应用启动/退出。</summary>
    private IHost? _host;

    private readonly CancellationTokenSource _startupCancellation = new();
    private readonly StartupLog _startupLog = new();
    private SingleInstanceService? _singleInstance;
    private StartupWindow? _startupWindow;
    // 退出先等待初始化离开，避免后台仍在使用已经释放的容器。
    private Task? _startupTask;
    // UI线程上的退出幂等入口，关闭窗口和取消启动共用一次清理。
    private Task? _shutdownTask;

    /// <summary>退出清理已完成后，允许主窗口真正关闭，避免反复确认。</summary>
    public bool IsShutdownComplete { get; private set; }

    /// <summary>最先判重，再显示启动提示；窗口操作始终保留在UI线程。</summary>
    /// <param name="e">启动参数。</param>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            _singleInstance = new SingleInstanceService();
            if (!_singleInstance.TryAcquire())
            {
                var notified = await _singleInstance.NotifyExistingAsync();
                _startupLog.Write($"重复启动已拦截，通知原窗口结果={notified}；本进程不启动数据库或后台服务");
                if (!notified) MessageBox.Show("程序已在运行或正在退出，请稍候查看原窗口。", "自动线已启动");
                _singleInstance.Dispose();
                _singleInstance = null;
                Shutdown();
                return;
            }
            _startupLog.Write("取得单实例保护");
            _singleInstance.StartListening(() =>
            {
                if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(ActivateExistingWindow));
            });
            _startupWindow = new StartupWindow();
            _startupWindow.Closing += (_, args) =>
            {
                if (IsShutdownComplete || MainWindow != _startupWindow) return;
                args.Cancel = true;
                _ = ShutdownApplicationAsync();
            };
            _startupWindow.Show();
            // 先让出Dispatcher完成首帧绘制，避免提示尚未出现就开始配置装载。
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            if (_shutdownTask is not null) return;
            _startupTask = InitializeAsync(_startupCancellation.Token);
            await _startupTask;
        }
        catch (OperationCanceledException) when (_startupCancellation.IsCancellationRequested)
        {
            _startupLog.Write("启动已取消，等待后台清理结束");
            await ShutdownApplicationAsync();
        }
        catch (Exception ex)
        {
            _startupLog.Write("启动失败，准备停止已启动的服务", ex);
            MessageBox.Show("程序启动失败，请查看Logs日期目录中的startup.txt和system.txt。", "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            await ShutdownApplicationAsync();
        }
    }

    // 只把非UI装配移到后台，保留原有装载、宿主启动、固定等待和核对顺序。
    // 700ms和冷启动提示策略留待现场阶段耗时验证后再调整，不因显示提示而删除审核。
    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await RunStartupStageAsync("正在读取程序配置……", () => Task.Run(() =>
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = AppContext.BaseDirectory });
            ConfigureServices(builder.Services, builder.Configuration);
            _host = builder.Build();
            _startupLog.Attach(_host.Services.GetRequiredService<IAppLogger>());
        }, cancellationToken));

        await RunStartupStageAsync("正在加载设备和天车配置……", async () =>
        {
            try
            {
                var loader = _host!.Services.GetRequiredService<IConfigurationLoader>();
                var result = await Task.Run(() => loader.LoadAsync(cancellationToken), cancellationToken);
                _startupLog.Write($"配置装载：设备{result.DeviceCount}、天车{result.CraneCount}、驱动{result.DriverCount}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // 保留数据库不可用时继续打开界面的原策略；这不是初始化成功或自动启动线路。
                _startupLog.Write("配置装载失败，按原策略继续打开界面", ex);
                _host!.Services.GetRequiredService<AlarmManager>().Raise(AlarmLevel.Error, "系统",
                    $"配置装载失败：{ex.Message}", "界面继续启动；请检查appsettings.json连接串与数据库可用性");
            }
        });

        await RunStartupStageAsync("正在启动后台服务……", () => Task.Run(() => _host!.StartAsync(cancellationToken), cancellationToken));
        RegisterAlarmDialog();
        await RunStartupStageAsync("正在等待采集状态并执行启动核对……", async () =>
        {
            await Task.Delay(700, cancellationToken);
            var blocks = await _host!.Services.GetRequiredService<IColdStartChecker>().CheckAsync(cancellationToken);
            _startupLog.Write($"冷启动核对结束，提示资源数={blocks.Count}");
        });
        await RunStartupStageAsync("正在打开主界面……", () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = _host!.Services.GetRequiredService<MainWindow>();
            window.DataContext = _host.Services.GetRequiredService<ShellViewModel>();
            MainWindow = window;
            window.Show();
            // 先明确主窗口并显示，再关闭启动窗口，避免最后一个窗口关闭触发意外退出。
            _startupWindow!.Close();
            _startupWindow = null;
            return Task.CompletedTask;
        });
        _startupLog.Write("主窗口已显示；初始化完成，线路保持原有未启动状态");
    }

    private async Task RunStartupStageAsync(string stage, Func<Task> action)
    {
        _startupCancellation.Token.ThrowIfCancellationRequested();
        _startupWindow?.SetStage(stage);
        _startupLog.Write($"阶段开始：{stage}");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await action();
            _startupLog.Write($"阶段完成：{stage}，耗时={watch.ElapsedMilliseconds}ms");
        }
        catch (OperationCanceledException) when (_startupCancellation.IsCancellationRequested)
        {
            _startupLog.Write($"阶段取消：{stage}，耗时={watch.ElapsedMilliseconds}ms");
            throw;
        }
        catch (Exception ex)
        {
            _startupLog.Write($"阶段未完成：{stage}，耗时={watch.ElapsedMilliseconds}ms", ex);
            throw;
        }
    }

    private void RegisterAlarmDialog()
    {
        _host!.Services.GetRequiredService<AlarmManager>().Raised += (_, alarm) =>
        {
            if (alarm.Level < AlarmLevel.Error) return;
            // 后台停止期间不可同步等待Dispatcher，否则退出清理可能与报警互相等待。
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_shutdownTask is not null) return;
                // 和报警页共用详情；传入事件快照，不按当前任务拼接，不查询日志。
                _host!.Services.GetRequiredService<IDialogService>().ShowAlarmDetails(alarm, automatic: true);
            }));
        };
    }

    private void ActivateExistingWindow()
    {
        var window = _startupWindow ?? MainWindow;
        if (window is null) return;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        var activated = window.Activate();
        _startupLog.Write($"收到重复打开请求，阶段={(_shutdownTask is not null ? "正在退出" : _startupWindow is not null ? "启动中" : "主窗口")}，窗口激活={activated}");
    }

    /// <summary>统一取消启动并停止后台服务；清理期间保留窗口和单实例保护。</summary>
    /// <returns>清理结束并请求WPF退出的异步任务；重复调用共用同一任务。</returns>
    public Task ShutdownApplicationAsync() => _shutdownTask ??= ShutdownCoreAsync();

    private async Task ShutdownCoreAsync()
    {
        _startupCancellation.Cancel();
        _startupWindow?.SetStage("正在退出，请等待后台服务停止……");
        if (MainWindow is { } window) window.IsEnabled = false;
        _startupLog.Write("退出开始，停止初始化并等待后台服务；继续持有单实例保护");
        try
        {
            // 等初始化离开不可中断的数据库连接操作，避免容器刚释放又被启动流程继续使用。
            if (_startupTask is not null)
            {
                try { await _startupTask; } catch (Exception ex) { _startupLog.Write("退出时初始化已结束或失败", ex); }
            }
            if (_host is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await Task.Run(() => _host.StopAsync(timeout.Token)); }
                catch (Exception ex) { _startupLog.Write("停止宿主失败或超过30秒，继续释放容器并退出进程", ex); }
                await Task.Run(() => _host.Dispose());
                _host = null;
            }
        }
        catch (Exception ex) { _startupLog.Write("退出清理异常", ex); }
        finally
        {
            _startupLog.Write("退出清理结束，准备结束进程");
            IsShutdownComplete = true;
            Shutdown();
        }
    }

    /// <summary>Dispatcher退出前同步释放唤醒监听和单实例句柄，不依赖async void退出回调。</summary>
    /// <param name="e">退出参数。</param>
    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        _startupCancellation.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// 集中注册依赖。
    /// 依赖方向：Ui → Application / Infrastructure；Application → Domain；Infrastructure → Application / Domain。
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="configuration">配置根对象</param>
    private static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // 配置与日志：先建一个 ConfigService 实例，既注册到容器，也供 AddDbContext 读取连接串
        var configService = new ConfigService(configuration);
        services.AddSingleton(configuration);
        services.AddSingleton<IConfigService>(configService);
        services.AddSingleton<IAppLogger, FileAppLogger>();

        // ERP默认持续监听并自动入队；按钮只切换这个宿主单例，页面切换不会新建循环或停止监听。
        services.AddSingleton<IErpJobFile, ErpJobFile>();
        services.AddSingleton<ErpImportService>();
        services.AddSingleton<IErpImportService>(sp => sp.GetRequiredService<ErpImportService>());
        services.AddHostedService(sp => sp.GetRequiredService<ErpImportService>());

        // 数据库：只存配置。运行时用 AutoDetect 探测服务器版本，兼容本地/远程、MySQL/MariaDB，
        // 不把版本写死；连接串完全来自 appsettings.json，部署到远程只改配置即可。
        // （设计时工厂 DesignTimeDbContextFactory 仍用固定版本，保证离线也能生成迁移。）
        // 缓存成功的版本探测；连接串改变会重新探测，失败不缓存以保留后续恢复能力。
        var serverVersions = new System.Collections.Concurrent.ConcurrentDictionary<string, ServerVersion>();
        services.AddDbContext<AppDbContext>(options =>
        {
            var connectionString = configService.GetConnectionString();
            options.UseMySql(connectionString, serverVersions.GetOrAdd(connectionString, ServerVersion.AutoDetect));
        });

        // 运行态容器：全线唯一实例，跨页面/服务共享（架构文档 §7）
        services.AddSingleton<IRuntimeTaskManager, RuntimeTaskManager>();
        services.AddSingleton<AutomatedLine.Application.Routing.IProcessPlanService, AutomatedLine.Application.Routing.ProcessPlanService>();
        services.AddSingleton<DeviceRegistry>();
        services.AddSingleton<CraneRegistry>();
        services.AddSingleton<DriverRegistry>();

        // 采集与消费者复用同一缓存；查询接口只读，不能触发通信或修改业务运行态。
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<CraneStateStore>();
        services.AddSingleton<ICraneStateProvider>(sp => sp.GetRequiredService<CraneStateStore>());
        services.AddSingleton<CraneStateCollector>();
        services.AddHostedService(sp => sp.GetRequiredService<CraneStateCollector>());

        // 独立宿主循环：暂停线路或切换页面均不停止完成记录清理，不使用任何PLC驱动。
        services.AddHostedService<CompletedTaskCleanupService>();

        // 配置装载、配置存储（CRUD）与冷启动核对
        services.AddSingleton<IConfigurationLoader, ConfigurationLoader>();
        services.AddSingleton<IConfigurationStore, ConfigurationStore>();
        services.AddSingleton<IColdStartChecker, ColdStartChecker>();

        // 手动操作服务：写天车 PLC，供手动操作页使用
        services.AddSingleton<IManualOperationService, ManualOperationService>();

        // 报警：内存活动报警 + 磁盘日志，供各模块 Raise 与报警页读取
        services.AddSingleton<ActiveAlarmStore>();
        services.AddSingleton<AlarmManager>();

        // P3 调度链：候选池、预约、路权、线路控制、异常协调、状态机
        services.AddSingleton<CandidateTaskPool>();
        services.AddSingleton<CraneTaskChannels>();   // 每车任务队列（调度器→Worker 交接）
        services.AddSingleton<ResourceReservationManager>();
        services.AddSingleton<RightOfWayManager>();
        services.AddSingleton<LineControlService>();
        services.AddSingleton<TaskAbortCoordinator>();

        // 取放料策略：每类设备一个 handler + 注册表（状态机按 DeviceType 分派）
        services.AddSingleton<AutomatedLine.Application.Transfer.ITransferHandler, AutomatedLine.Application.Transfer.Handlers.LoadRackHandler>();
        services.AddSingleton<AutomatedLine.Application.Transfer.ITransferHandler, AutomatedLine.Application.Transfer.Handlers.BoringHandler>();
        services.AddSingleton<AutomatedLine.Application.Transfer.ITransferHandler, AutomatedLine.Application.Transfer.Handlers.LatheHandler>();
        services.AddSingleton<AutomatedLine.Application.Transfer.ITransferHandler, AutomatedLine.Application.Transfer.Handlers.GrinderHandler>();
        services.AddSingleton<AutomatedLine.Application.Transfer.ITransferHandler, AutomatedLine.Application.Transfer.Handlers.MarkerHandler>();
        services.AddSingleton<AutomatedLine.Application.Transfer.ITransferHandler, AutomatedLine.Application.Transfer.Handlers.UnloadRackHandler>();
        services.AddSingleton<AutomatedLine.Application.Transfer.TransferHandlerRegistry>();

        services.AddSingleton<CraneStateMachine>();

        // P3 后台循环：候选生成、集中调度、天车执行（同一实例既作服务也作宿主服务）
        services.AddSingleton<TransportTaskGenerator>();
        services.AddSingleton<CraneScheduler>();
        services.AddSingleton<CraneWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<TransportTaskGenerator>());
        services.AddHostedService(sp => sp.GetRequiredService<CraneScheduler>());
        services.AddHostedService(sp => sp.GetRequiredService<CraneWorker>());

        // 采集器：既作为 IDeviceStateCollector 供查询，也作为宿主后台服务随宿主启动/停止。
        // 注册为单例并通过工厂复用同一实例，保证两者拿到的是同一个对象。
        services.AddSingleton<DeviceStateCollector>();
        services.AddSingleton<IDeviceStateCollector>(sp => sp.GetRequiredService<DeviceStateCollector>());
        //IHostedService 也复用同一个实例 —— 宿主启动时会调用它的 StartAsync，停止时调用 StopAsync
        //AddHostedService 把对象注册成了 IHostedService，而 ASP.NET Core / .NET 通用主机的启动流程里内置了一步——遍历所有 IHostedService 并调用它们的 StartAsync。
        services.AddHostedService(sp => sp.GetRequiredService<DeviceStateCollector>());

        // 弹窗服务：ViewModel 通过接口请求对话框，不直接依赖 View
        services.AddSingleton<IDialogService, DialogService>();

        // 界面节拍器：所有页面共用同一个秒级刷新节拍，避免每页各起定时器
        services.AddSingleton<IRuntimeTicker, RuntimeTicker>();

        // 页面 ViewModel 全部单例：页面常驻、共享运行态，且避免重复订阅节拍器
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<CraneViewModel>();
        services.AddSingleton<DeviceViewModel>();
        services.AddSingleton<ProcessViewModel>();
        services.AddSingleton<ParamsViewModel>();
        services.AddSingleton<ScreenViewModel>();
        services.AddSingleton<ManualOperationViewModel>();
        services.AddSingleton<AlarmViewModel>();

        // 外壳：顶部导航 + 页面切换
        services.AddSingleton<ShellViewModel>();

        // 主窗口由容器解析
        services.AddTransient<MainWindow>();
    }
}
