using System.Windows;
using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Collection;
using AutomatedLine.Application.Line;
using AutomatedLine.Application.Runtime;
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

        // 先装载数据库配置为内存对象与驱动，再启动宿主：
        // 这样采集器启动时注册表里已经有资源可采（架构文档 §8.2）
        var loader = _host.Services.GetRequiredService<IConfigurationLoader>();
        var loadResult = await loader.LoadAsync();
        Console.WriteLine($"[App] 配置装载：设备 {loadResult.DeviceCount}、天车 {loadResult.CraneCount}、驱动 {loadResult.DriverCount}");

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

        // 数据库：只存配置。这里刻意使用固定版本的 ServerVersion，而不是 AutoDetect。
        // 原因：AutoDetect 会在解析 DbContext 时立即连库；而 dotnet ef 生成迁移时会通过宿主解析 DbContext，
        // 导致无库环境下无法生成迁移。固定版本可让"模型构建"与"真正连接"解耦，本地无 MySQL 也能开发。
        // 配置表结构简单，不依赖具体小版本特性；现场 MySQL 版本变化时同步调整此处即可。
        services.AddDbContext<AppDbContext>(options =>
        {
            var connectionString = configService.GetConnectionString();
            options.UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 36)));
        });

        // 运行态容器：全线唯一实例，跨页面/服务共享（架构文档 §7）
        services.AddSingleton<IRuntimeTaskManager, RuntimeTaskManager>();
        services.AddSingleton<DeviceRegistry>();
        services.AddSingleton<CraneRegistry>();
        services.AddSingleton<DriverRegistry>();

        // 配置装载与冷启动核对
        services.AddSingleton<IConfigurationLoader, ConfigurationLoader>();
        services.AddSingleton<IColdStartChecker, ColdStartChecker>();

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
        services.AddSingleton<AlarmViewModel>();

        // 外壳：顶部导航 + 页面切换
        services.AddSingleton<ShellViewModel>();

        // 主窗口由容器解析
        services.AddTransient<MainWindow>();
    }
}
