using System.Collections.ObjectModel;
using System.Windows.Media;
using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Collection;
using AutomatedLine.Application.Configuration;
using AutomatedLine.Application.Workers;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Ui.Helpers;
using AutomatedLine.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutomatedLine.Ui.ViewModels;

/// <summary>
/// 手动操作页连接状态。
/// </summary>
public enum CraneConnectionState
{
    /// <summary>未选择/未知。</summary>
    Unknown,

    /// <summary>已连接且无报警。</summary>
    Connected,

    /// <summary>已连接但有报警。</summary>
    Alarm,

    /// <summary>状态不可用，包括未采集、读取失败或快照过期。</summary>
    Failed
}

/// <summary>
/// 手动操作页面 ViewModel（仅天车）。
/// 天车列表来自数据库；所选车状态按秒从共享缓存更新，按钮下发手动命令。
/// 当前自动搬运从 Worker 内存刷新，人工中止只取消所选车本次搬运并统一弃板。
/// 状态徽标：绿=有效且无报警、黄=天车报警、红=状态不可用。
/// </summary>
public partial class ManualOperationViewModel : ObservableObject
{
    /// <summary>配置存储（取天车列表）。</summary>
    private readonly IConfigurationStore _configurationStore;

    /// <summary>手动操作服务。</summary>
    private readonly IManualOperationService _manualOperationService;

    /// <summary>只查询共享 PLC 状态，页面不再自行读取整车状态。</summary>
    private readonly ICraneStateProvider _craneStates;

    /// <summary>只通过 Worker 中止当前搬运，不取消宿主或直接写 PLC。</summary>
    private readonly CraneWorker _craneWorker;

    /// <summary>每次点击读取最新绝对移动确认密码；不缓存验证结果。</summary>
    private readonly IConfigService _configService;

    /// <summary>复用页面弹窗抽象，密码输入不进入运动驱动。</summary>
    private readonly IDialogService _dialogService;

    // 保存界面实际显示的搬运 ID，按钮不能在刷新间隔内误中止下一单。
    private Guid? _displayedTaskId;

    /// <summary>当前自动搬运任务及人工中止状态，来自内存。</summary>
    [ObservableProperty]
    private string _currentTaskText = "当前任务：无";

    /// <summary>仅有尚未中止的当前搬运时允许发起中止。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AbortCurrentTaskCommand))]
    private bool _canAbortCurrentTask;

    /// <summary>天车列表。</summary>
    public ObservableCollection<CraneConfigRowViewModel> Cranes { get; } = new();

    /// <summary>当前选中的天车。</summary>
    [ObservableProperty]
    private CraneConfigRowViewModel? _selectedCrane;

    /// <summary>需复位握手位的设备列表（双头镗 / 斜床 / 研磨机，仅启用）。</summary>
    public ObservableCollection<DeviceConfigDto> Devices { get; } = new();

    /// <summary>当前选中的设备（用于"清握手位"）。</summary>
    [ObservableProperty]
    private DeviceConfigDto? _selectedHandshakeDevice;

    /// <summary>连接状态文本。</summary>
    [ObservableProperty]
    private string _connectionText = "请选择天车";

    /// <summary>连接状态颜色。</summary>
    [ObservableProperty]
    private Brush _connectionBrush = Brushes.Gray;

    /// <summary>操作/刷新结果提示。</summary>
    [ObservableProperty]
    private string _message = "选择天车后自动显示最新采集状态。";

    // ── 状态显示：显示值/编码器 ──
    /// <summary>X 显示值坐标。</summary>
    [ObservableProperty] private int? _xDisplay;
    /// <summary>Y 显示值坐标。</summary>
    [ObservableProperty] private int? _yDisplay;
    /// <summary>Z 显示值坐标。</summary>
    [ObservableProperty] private int? _zDisplay;
    /// <summary>X 绝对编码器坐标。</summary>
    [ObservableProperty] private int? _xEncoder;
    /// <summary>Y 绝对编码器坐标。</summary>
    [ObservableProperty] private int? _yEncoder;
    /// <summary>Z 绝对编码器坐标。</summary>
    [ObservableProperty] private int? _zEncoder;

    // ── 回原点/报警 ──
    /// <summary>X 已回原点。</summary>
    [ObservableProperty] private bool? _xHome;
    /// <summary>Y 已回原点。</summary>
    [ObservableProperty] private bool? _yHome;
    /// <summary>Z 已回原点。</summary>
    [ObservableProperty] private bool? _zHome;
    /// <summary>伺服报警。</summary>
    [ObservableProperty] private int? _servoAlarm;
    /// <summary>PLC 伺服指令报警。</summary>
    [ObservableProperty] private int? _plcServoAlarm;
    /// <summary>PLC 报警。</summary>
    [ObservableProperty] private int? _plcAlarm;

    // ── 三轴速度显示（读回当前速度参数）──
    /// <summary>X 速度。</summary>
    [ObservableProperty] private int? _xSpeedDisp;
    /// <summary>X 加速度。</summary>
    [ObservableProperty] private int? _xAccelDisp;
    /// <summary>X 减速度。</summary>
    [ObservableProperty] private int? _xDecelDisp;
    /// <summary>Y 速度。</summary>
    [ObservableProperty] private int? _ySpeedDisp;
    /// <summary>Y 加速度。</summary>
    [ObservableProperty] private int? _yAccelDisp;
    /// <summary>Y 减速度。</summary>
    [ObservableProperty] private int? _yDecelDisp;
    /// <summary>Z 速度。</summary>
    [ObservableProperty] private int? _zSpeedDisp;
    /// <summary>Z 加速度。</summary>
    [ObservableProperty] private int? _zAccelDisp;
    /// <summary>Z 减速度。</summary>
    [ObservableProperty] private int? _zDecelDisp;

    // ── X 区信号（显示文本）──
    // 用文本而非 bool 的原因：线圈读取失败时值为"未知"，不能显示成 0/False
    // （按 0 显示会把通信故障误读成"无板 / 未下压"）。读到的电平显示"是/否"，失败显示"—"。
    /// <summary>X0 接液盘门开到位。</summary>
    [ObservableProperty] private string _x0DrainOpenText = "—";
    /// <summary>X1 接液盘门关到位。</summary>
    [ObservableProperty] private string _x1DrainCloseText = "—";
    /// <summary>X2 磁铁下压限位。</summary>
    [ObservableProperty] private string _x2PressureText = "—";
    /// <summary>X3 X 轴物理原点。</summary>
    [ObservableProperty] private string _x3HomeXText = "—";
    /// <summary>X4 Y 轴物理原点。</summary>
    [ObservableProperty] private string _x4HomeYText = "—";
    /// <summary>X5 Z 轴物理原点。</summary>
    [ObservableProperty] private string _x5HomeZText = "—";
    /// <summary>X6 充磁反馈。</summary>
    [ObservableProperty] private string _x6MagnetizeText = "—";
    /// <summary>X7 退磁反馈。</summary>
    [ObservableProperty] private string _x7DemagnetizeText = "—";
    /// <summary>X11 检测有版。</summary>
    [ObservableProperty] private string _x11HasPlateText = "—";

    // Busy与其他只读状态共用有效快照，不能把单点读取失败或过期显示为停止。
    /// <summary>B531（12819）X轴运动中状态文本。</summary>
    [ObservableProperty] private string _xBusyText = "—";
    /// <summary>B536（12824）Y轴运动中状态文本。</summary>
    [ObservableProperty] private string _yBusyText = "—";
    /// <summary>B541（12829）Z轴运动中状态文本。</summary>
    [ObservableProperty] private string _zBusyText = "—";

    // ── 绝对移动输入 ──
    /// <summary>用户输入 X 绝对值。</summary>
    [ObservableProperty] private int _absX;
    /// <summary>用户输入 Y 绝对值。</summary>
    [ObservableProperty] private int _absY;
    /// <summary>用户输入 Z 绝对值。</summary>
    [ObservableProperty] private int _absZ;

    // 用文本保留未完成或非法输入，点击时再校验，避免解析失败后误用上一次有效步长。
    /// <summary>六个单轴相对移动按钮共用的步长文本，单位mm，点击时要求为正整数。</summary>
    [ObservableProperty] private string _relativeStep = "0";

    // ── 速度设置输入（绝对速度） ──
    /// <summary>X 速度。</summary>
    [ObservableProperty] private int _speedX = 100;
    /// <summary>X 加速度。</summary>
    [ObservableProperty] private int _accelX = 50;
    /// <summary>X 减速度。</summary>
    [ObservableProperty] private int _decelX = 30;
    /// <summary>Y 速度。</summary>
    [ObservableProperty] private int _speedY = 60;
    /// <summary>Y 加速度。</summary>
    [ObservableProperty] private int _accelY = 30;
    /// <summary>Y 减速度。</summary>
    [ObservableProperty] private int _decelY = 20;
    /// <summary>Z 速度。</summary>
    [ObservableProperty] private int _speedZ = 40;
    /// <summary>Z 加速度。</summary>
    [ObservableProperty] private int _accelZ = 20;
    /// <summary>Z 减速度。</summary>
    [ObservableProperty] private int _decelZ = 20;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="configurationStore">配置存储</param>
    /// <param name="manualOperationService">手动操作服务</param>
    /// <param name="craneStates">全线共享的有效天车状态</param>
    /// <param name="craneWorker">当前搬运执行器</param>
    /// <param name="ticker">UI 内存状态刷新节拍，不触发 PLC 读取</param>
    /// <param name="configService">读取绝对移动密码的配置服务</param>
    /// <param name="dialogService">绝对移动密码确认弹窗服务</param>
    public ManualOperationViewModel(IConfigurationStore configurationStore, IManualOperationService manualOperationService, ICraneStateProvider craneStates,
        CraneWorker craneWorker, IRuntimeTicker ticker, IConfigService configService, IDialogService dialogService)
    {
        _configurationStore = configurationStore;
        _manualOperationService = manualOperationService;
        _craneStates = craneStates;
        _craneWorker = craneWorker;
        _configService = configService;
        _dialogService = dialogService;
        ticker.Tick += (_, _) => { RefreshCurrentTask(); RefreshStatus(); };
        _ = LoadCranesAsync();
        _ = LoadHandshakeDevicesAsync();
    }

    partial void OnSelectedCraneChanged(CraneConfigRowViewModel? value)
    {
        RefreshCurrentTask();
        RefreshStatus();
    }

    // 当前任务仍来自 Worker；PLC 展示数据由独立共享采集提供，不混入执行登记。
    private void RefreshCurrentTask()
    {
        var current = SelectedCrane is null ? null : _craneWorker.GetCurrentExecution(SelectedCrane.Id);
        _displayedTaskId = current?.Task.TaskId;
        CanAbortCurrentTask = current is { IsAbortRequested: false };
        CurrentTaskText = current is { } active
            ? $"当前任务：设备{active.Task.SourceDeviceId} → 设备{active.Task.TargetDeviceId}"
              + $"（{active.Task.TaskId.ToString("N")[..8]}）" + (active.IsAbortRequested ? " 中止中…" : "")
            : "当前任务：无";
    }

    /// <summary>人工弃掉所选车当前任务；清理由状态机完成，现场负责停止实物运动。</summary>
    [RelayCommand(CanExecute = nameof(CanAbortCurrentTask))]
    private void AbortCurrentTask()
    {
        if (SelectedCrane is null || _displayedTaskId is not { } taskId) return;
        Message = _craneWorker.TryAbortCurrentTask(SelectedCrane.Id, taskId)
            ? $"{SelectedCrane.Config.Name}：已请求中止当前任务，线路已暂停；清理后工件标记为已丢弃。"
            : "当前任务已变化、已结束或已请求中止，请查看最新任务状态。";
        RefreshCurrentTask();
    }

    /// <summary>加载天车列表（数据库，仅启用天车）。</summary>
    private async Task LoadCranesAsync()
    {
        try
        {
            var configs = await _configurationStore.GetCraneConfigsAsync();
            Cranes.Clear();
            foreach (var dto in configs.Where(c => c.IsEnabled).OrderBy(c => c.No))
            {
                Cranes.Add(new CraneConfigRowViewModel(dto));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ManualOperationViewModel] 加载天车列表失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 加载"需要复位握手位"的设备（双头镗 / 斜床 / 研磨机，仅启用）。
    /// 其余类型（上料架/下料架/打号机等）没有上位机写入位，不列出。
    /// </summary>
    private async Task LoadHandshakeDevicesAsync()
    {
        try
        {
            var configs = await _configurationStore.GetDeviceConfigsAsync();
            Devices.Clear();
            foreach (var dto in configs
                         .Where(d => d.IsEnabled
                                     && d.DeviceType is DeviceType.BoringMachine
                                         or DeviceType.LatheMachine
                                         or DeviceType.GrinderMachine)
                         .OrderBy(d => d.Id))
            {
                Devices.Add(dto);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ManualOperationViewModel] 加载设备列表失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 复位选中设备"上位机写入的握手位"（写 0，尽力清）。
    /// 与自动线取料后自动复位**共用同一实现**，用于异常中止后人工补清：
    /// 这些位残留 1 会让该设备下一次上料收不到上升沿。
    /// </summary>
    [RelayCommand]
    private async Task ClearHandshakeAsync()
    {
        if (SelectedHandshakeDevice is null)
        {
            Message = "请先选择设备（双头镗 / 斜床 / 研磨机）";
            return;
        }

        var result = await _manualOperationService.ClearHandshakeAsync(SelectedHandshakeDevice.Id);
        Message = result.Success
            ? $"✅ {SelectedHandshakeDevice.Name}：{result.Message}"
            : $"❌ {SelectedHandshakeDevice.Name}：{result.Message}";
    }

    /// <summary>立即将最新有效缓存应用到所选天车，不触发 PLC 通信。</summary>
    [RelayCommand]
    private Task RefreshAsync()
    {
        RefreshStatus(showMessage: true);
        return Task.CompletedTask;
    }

    // 定时刷新不覆盖操作结果；切车、未采集或失效时清空旧值，避免展示另一台车的坐标。
    private void RefreshStatus(bool showMessage = false)
    {
        var status = SelectedCrane is null ? null : _craneStates.GetFreshStatus(SelectedCrane.Id);
        XDisplay = status?.XDisplay;
        YDisplay = status?.YDisplay;
        ZDisplay = status?.ZDisplay;
        XEncoder = status?.XEncoder;
        YEncoder = status?.YEncoder;
        ZEncoder = status?.ZEncoder;
        XHome = status?.XHomeCompleted;
        YHome = status?.YHomeCompleted;
        ZHome = status?.ZHomeCompleted;
        ServoAlarm = status?.ServoAlarm;
        PlcServoAlarm = status?.PlcServoAlarm;
        PlcAlarm = status?.PlcAlarm;
        XSpeedDisp = status?.XSpeed;
        XAccelDisp = status?.XAccel;
        XDecelDisp = status?.XDecel;
        YSpeedDisp = status?.YSpeed;
        YAccelDisp = status?.YAccel;
        YDecelDisp = status?.YDecel;
        ZSpeedDisp = status?.ZSpeed;
        ZAccelDisp = status?.ZAccel;
        ZDecelDisp = status?.ZDecel;
        X0DrainOpenText = FormatLevel(status?.DrainOpen);
        X1DrainCloseText = FormatLevel(status?.DrainClose);
        X2PressureText = FormatLevel(status?.PressureLimit);
        X3HomeXText = FormatLevel(status?.PhysicalHomeX);
        X4HomeYText = FormatLevel(status?.PhysicalHomeY);
        X5HomeZText = FormatLevel(status?.PhysicalHomeZ);
        X6MagnetizeText = FormatLevel(status?.MagnetizeFeedback);
        X7DemagnetizeText = FormatLevel(status?.DemagnetizeFeedback);
        X11HasPlateText = FormatLevel(status?.HasPlate);
        XBusyText = FormatBusy(status?.XBusy);
        YBusyText = FormatBusy(status?.YBusy);
        ZBusyText = FormatBusy(status?.ZBusy);

        if (SelectedCrane is null)
        {
            SetState(CraneConnectionState.Unknown, "请选择天车");
            if (showMessage) Message = "请先选择天车。";
        }
        else if (status is null)
        {
            SetState(CraneConnectionState.Failed, "状态不可用");
            if (showMessage) Message = "尚未采集、读取失败或快照已过期，等待后台采集更新。";
        }
        else
        {
            SetState(status.HasAlarm ? CraneConnectionState.Alarm : CraneConnectionState.Connected,
                status.HasAlarm ? "天车报警" : "已连接");
            if (showMessage) Message = "已显示最新有效采集状态。";
        }
    }

    // 同时显示PLC原始电平和含义，未知值保持独立，避免误以为轴已经停止。
    private static string FormatBusy(bool? value)
        => value switch { true => "1（运动中）", false => "0（停止）", null => "—" };

    /// <summary>设置三轴绝对速度。</summary>
    [RelayCommand]
    private async Task SetSpeedAsync()
    {
        var result = await WithCrane(id => _manualOperationService.SetSpeedAsync(id,
            new CraneSpeedDto(SpeedX, AccelX, DecelX, SpeedY, AccelY, DecelY, SpeedZ, AccelZ, DecelZ)));
        await FinishAsync(result);
    }

    /// <summary>每次通过密码确认后执行所选天车绝对移动；取消、输错或缺少配置不下发运动。</summary>
    [RelayCommand]
    private async Task MoveAbsoluteAsync()
    {
        var crane = SelectedCrane;
        if (crane is null)
        {
            Message = "请先选择天车";
            return;
        }

        var password = _configService.GetAppOptions().ManualOperation?.AbsoluteMovePassword;
        if (string.IsNullOrWhiteSpace(password))
        {
            Message = "未配置绝对移动确认密码，本次未执行。";
            return;
        }

        // 固定本次确认的车与坐标，保证弹窗说明和最终发送一致；每次点击都重新确认，不保留授权。
        var (x, y, z) = (AbsX, AbsY, AbsZ);
        //弹出绝对移动密码确认框；每次重新输入，只有密码匹配且确认才放行动作。
        if (!_dialogService.ShowAbsoluteMovePasswordDialog(
                $"天车：{crane.Config.Name}\n目标编码器坐标：X={x}，Y={y}，Z={z}\n请输入密码，确认后执行本次绝对移动。", password))
        {
            Message = "已取消绝对移动，本次未执行。";
            return;
        }

        var result = await _manualOperationService.MoveAbsoluteAsync(crane.Id, x, y, z);
        //刷新选中天车状态
        await FinishAsync(result);
    }

    /// <summary>把页面所选轴向和步长转为原相对移动服务的三轴增量，其他两轴传0。</summary>
    /// <param name="direction">按钮轴向：X+、X-、Y+、Y-、Z+或Z-。</param>
    [RelayCommand]
    private async Task MoveRelativeAsync(string? direction)
    {
        if (!int.TryParse(RelativeStep, out var step) || step <= 0)
        {
            Message = "请输入大于0的整数移动步长（mm）。";
            return;
        }

        // 六个按钮共用一个异步命令，执行期间统一禁用；轴向只在UI层映射，不改变运动算法。
        (int X, int Y, int Z)? offset = direction switch
        {
            "X+" => (step, 0, 0),
            "X-" => (-step, 0, 0),
            "Y+" => (0, step, 0),
            "Y-" => (0, -step, 0),
            "Z+" => (0, 0, step),
            "Z-" => (0, 0, -step),
            _ => null
        };
        if (offset is not { } delta)
        {
            Message = "请选择有效的相对移动方向。";
            return;
        }

        var result = await WithCrane(id => _manualOperationService.MoveRelativeAsync(id, delta.X, delta.Y, delta.Z));
        await FinishAsync(result);
    }

    /// <summary>清除报警。</summary>
    [RelayCommand]
    private async Task ClearAlarmAsync() => await FinishAsync(await WithCrane(id => _manualOperationService.ClearAlarmAsync(id)));

    /// <summary>伺服通电。</summary>
    [RelayCommand]
    private async Task ServoOnAsync() => await FinishAsync(await WithCrane(id => _manualOperationService.ServoOnAsync(id)));

    /// <summary>伺服断电。</summary>
    [RelayCommand]
    private async Task ServoOffAsync() => await FinishAsync(await WithCrane(id => _manualOperationService.ServoOffAsync(id)));

    /// <summary>减速停止。</summary>
    [RelayCommand]
    private async Task SlowStopAsync() => await FinishAsync(await WithCrane(id => _manualOperationService.SlowStopAsync(id)));

    /// <summary>急停。</summary>
    [RelayCommand]
    private async Task EmergencyStopAsync() => await FinishAsync(await WithCrane(id => _manualOperationService.EmergencyStopAsync(id)));

    /// <summary>充磁。</summary>
    [RelayCommand]
    private async Task MagnetOnAsync() => await FinishAsync(await WithCrane(id => _manualOperationService.MagnetOnAsync(id)));

    /// <summary>放磁。</summary>
    [RelayCommand]
    private async Task MagnetOffAsync() => await FinishAsync(await WithCrane(id => _manualOperationService.MagnetOffAsync(id)));

    /// <summary>打开接液盘。</summary>
    [RelayCommand]
    private async Task DrainOpenAsync() => await FinishAsync(await WithCrane(id => _manualOperationService.DrainOpenAsync(id)));

    /// <summary>关闭接液盘。</summary>
    [RelayCommand]
    private async Task DrainCloseAsync() => await FinishAsync(await WithCrane(id => _manualOperationService.DrainCloseAsync(id)));

    /// <summary>单轴回原点（参数：X/Y/Z）。</summary>
    /// <param name="axis">轴</param>
    [RelayCommand]
    private async Task HomeAxisAsync(string? axis)
    {
        if (string.IsNullOrWhiteSpace(axis))
        {
            return;
        }

        await FinishAsync(await WithCrane(id => _manualOperationService.HomeAsync(id, axis[0])));
    }

    /// <summary>执行需要天车的操作；未选天车直接返回失败。</summary>
    /// <param name="action">动作</param>
    /// <returns>结果</returns>
    private async Task<ManualOperationResult> WithCrane(Func<int, Task<ManualOperationResult>> action)
    {
        if (SelectedCrane is null)
        {
            return ManualOperationResult.Fail("请先选择天车");
        }

        return await action(SelectedCrane.Id);
    }

    /// <summary>统一处理命令结果：保留操作消息，并在成功后同步当前缓存状态。</summary>
    /// <param name="result">结果</param>
    private Task FinishAsync(ManualOperationResult result)
    {
        Message = result.Message;
        if (result.Success) RefreshStatus();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 把 X 区线圈电平转成显示文本。
    /// 读到电平显示"是/否"；读取失败（null）显示"—"，**不显示成 0/否**，
    /// 避免把"读不到"误读成"无板 / 未下压"。
    /// </summary>
    /// <param name="level">线圈电平；null 表示该点读取失败</param>
    /// <returns>显示文本</returns>
    private static string FormatLevel(bool? level) => level is null ? "—" : level.Value ? "是" : "否";

    /// <summary>设置连接状态显示。</summary>
    /// <param name="state">状态</param>
    /// <param name="text">文本</param>
    private void SetState(CraneConnectionState state, string text)
    {
        ConnectionText = text;
        ConnectionBrush = state switch
        {
            CraneConnectionState.Connected => new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A)),
            CraneConnectionState.Alarm => new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06)),
            CraneConnectionState.Failed => new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)),
            _ => Brushes.Gray
        };
    }
}
