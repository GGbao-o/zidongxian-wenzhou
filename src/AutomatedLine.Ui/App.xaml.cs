using System.Windows;
using System.Windows.Threading;
using AutomatedLine.Domain.Diagnostics;
using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Alarms;
using AutomatedLine.Application.Collection;
using AutomatedLine.Application.Generation;
using AutomatedLine.Application.Line;
using AutomatedLine.Application.Reservations;
using AutomatedLine.Infrastructure.Manual;
using AutomatedLine.Application.RightOfWay;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Application.Scheduling;
using AutomatedLine.Application.Workers;
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

    /// <summary>
    /// 应用启动。构建容器、注册服务、解析并显示主窗口。
    /// </summary>
    /// <param name="e">启动参数</param>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 以程序输出目录为内容根，保证发布后也能读到 appsettings.json 与 Logs 目录，
        // 不受"从哪个工作目录启动"的影响
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory
        });

        ConfigureServices(builder.Services, builder.Configuration);

        _host = builder.Build();

        // 全局兜底：任何漏网的未处理异常都记录下来，绝不让程序崩溃退出
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // 先装载数据库配置为内存对象与驱动，再启动宿主：
        // 这样采集器启动时注册表里已经有资源可采（架构文档 §8.2）。
        // 数据库不可达时不阻塞界面启动：记录报警后继续，界面仍可打开，稍后重启线路再试。
        var loader = _host.Services.GetRequiredService<IConfigurationLoader>();
        try
        {
            var loadResult = await loader.LoadAsync();
            Console.WriteLine($"[App] 配置装载：设备 {loadResult.DeviceCount}、天车 {loadResult.CraneCount}、驱动 {loadResult.DriverCount}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[App] 配置装载失败（数据库不可用？）：{ex.Message}");
            _host.Services.GetRequiredService<AutomatedLine.Application.Alarms.AlarmManager>().Raise(
                AutomatedLine.Domain.Alarms.AlarmLevel.Error,
                "系统",
                $"配置装载失败：{ex.Message}",
                "界面继续启动；请检查 appsettings.json 的连接串与数据库可用性");
        }

        await _host.StartAsync();

        // 冷启动核对：等采集器跑一轮拿到信号后，检查现场是否有无法关联的内存在制品
        var coldStartChecker = _host.Services.GetRequiredService<IColdStartChecker>();
        await Task.Delay(700);
        var blocks = coldStartChecker.Check();
        if (blocks.Count > 0)
        {
            Console.WriteLine($"[App] 冷启动核对发现 {blocks.Count} 个物理阻塞资源");
        }

        // 从容器解析主窗口与外壳 ViewModel，DataContext 通过 DI 注入，View 不 new ViewModel
        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        var shellViewModel = _host.Services.GetRequiredService<ShellViewModel>();
        mainWindow.DataContext = shellViewModel;
        mainWindow.Show();
    }

    /// <summary>
    /// 应用退出。停止宿主并释放容器资源。
    /// </summary>
    /// <param name="e">退出参数</param>
    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        base.OnExit(e);
    }

    /// <summary>
    /// UI 线程未处理异常：记录日志并提示，标记已处理以阻止进程退出。
    /// </summary>
    /// <param name="sender">事件源</param>
    /// <param name="e">事件参数</param>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        HandleUnhandled("UI 未处理异常", e.Exception);
        System.Windows.MessageBox.Show($"发生未处理异常（已记录，程序继续运行）：{e.Exception.Message}",
            "异常", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    /// <summary>
    /// 非 UI 线程未处理异常：只记录日志。
    /// </summary>
    /// <param name="sender">事件源</param>
    /// <param name="e">事件参数</param>
    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        HandleUnhandled("后台未处理异常", e.ExceptionObject as Exception);
    }

    /// <summary>
    /// 未观察的任务异常：记录并标记已观察，避免影响进程。
    /// </summary>
    /// <param name="sender">事件源</param>
    /// <param name="e">事件参数</param>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        HandleUnhandled("未观察的任务异常", e.Exception);
        e.SetObserved();
    }

    /// <summary>
    /// 统一记录未处理异常：优先写日志，失败则退化为控制台输出。
    /// </summary>
    /// <param name="source">来源描述</param>
    /// <param name="exception">异常</param>
    private void HandleUnhandled(string source, Exception? exception)
    {
        var message = $"{source}：{exception?.Message}";
        try
        {
            _host?.Services.GetService<IAppLogger>()?.Error(LogCategory.System, message, exception);
        }
        catch
        {
            // 日志本身失败也不能再抛
        }

        Console.WriteLine($"[App] {message}");
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

        // 数据库：只存配置。运行时用 AutoDetect 探测服务器版本，兼容本地/远程、MySQL/MariaDB，
        // 不把版本写死；连接串完全来自 appsettings.json，部署到远程只改配置即可。
        // （设计时工厂 DesignTimeDbContextFactory 仍用固定版本，保证离线也能生成迁移。）
        services.AddDbContext<AppDbContext>(options =>
        {
            var connectionString = configService.GetConnectionString();
            options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString));
        });

        // 运行态容器：全线唯一实例，跨页面/服务共享（架构文档 §7）
        services.AddSingleton<IRuntimeTaskManager, RuntimeTaskManager>();
        services.AddSingleton<DeviceRegistry>();
        services.AddSingleton<CraneRegistry>();
        services.AddSingleton<DriverRegistry>();

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
        services.AddSingleton<ResourceReservationManager>();
        services.AddSingleton<RightOfWayManager>();
        services.AddSingleton<LineControlService>();
        services.AddSingleton<TaskAbortCoordinator>();
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
