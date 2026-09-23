using System.Collections.ObjectModel;
using System.Windows.Media;
using AutomatedLine.Application.Abstractions;
using AutomatedLine.Ui.Helpers;
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

    /// <summary>连接失败。</summary>
    Failed
}

/// <summary>
/// 手动操作页面 ViewModel（仅天车）。
/// 天车列表来自数据库；选择后点“刷新”读取状态，按钮下发手动命令。
/// 连接状态：绿=已连接、黄=天车报警、红=❌连接失败。
/// </summary>
public partial class ManualOperationViewModel : ObservableObject
{
    /// <summary>配置存储（取天车列表）。</summary>
    private readonly IConfigurationStore _configurationStore;

    /// <summary>手动操作服务。</summary>
    private readonly IManualOperationService _manualOperationService;

    /// <summary>天车列表。</summary>
    public ObservableCollection<CraneConfigRowViewModel> Cranes { get; } = new();

    /// <summary>当前选中的天车。</summary>
    [ObservableProperty]
    private CraneConfigRowViewModel? _selectedCrane;

    /// <summary>连接状态文本。</summary>
    [ObservableProperty]
    private string _connectionText = "请选择天车";

    /// <summary>连接状态颜色。</summary>
    [ObservableProperty]
    private Brush _connectionBrush = Brushes.Gray;

    /// <summary>操作/刷新结果提示。</summary>
    [ObservableProperty]
    private string _message = "选择天车后点击“刷新”读取状态。";

    // ── 状态显示：显示值/编码器 ──
    /// <summary>X 显示值坐标。</summary>
    [ObservableProperty] private int _xDisplay;
    /// <summary>Y 显示值坐标。</summary>
    [ObservableProperty] private int _yDisplay;
    /// <summary>Z 显示值坐标。</summary>
    [ObservableProperty] private int _zDisplay;
    /// <summary>X 绝对编码器坐标。</summary>
    [ObservableProperty] private int _xEncoder;
    /// <summary>Y 绝对编码器坐标。</summary>
    [ObservableProperty] private int _yEncoder;
    /// <summary>Z 绝对编码器坐标。</summary>
    [ObservableProperty] private int _zEncoder;

    // ── 回原点/报警 ──
    /// <summary>X 已回原点。</summary>
    [ObservableProperty] private bool _xHome;
    /// <summary>Y 已回原点。</summary>
    [ObservableProperty] private bool _yHome;
    /// <summary>Z 已回原点。</summary>
    [ObservableProperty] private bool _zHome;
    /// <summary>伺服报警。</summary>
    [ObservableProperty] private int _servoAlarm;
    /// <summary>PLC 伺服指令报警。</summary>
    [ObservableProperty] private int _plcServoAlarm;
    /// <summary>PLC 报警。</summary>
    [ObservableProperty] private int _plcAlarm;

    // ── 三轴速度显示（读回当前速度参数）──
    /// <summary>X 速度。</summary>
    [ObservableProperty] private int _xSpeedDisp;
    /// <summary>X 加速度。</summary>
    [ObservableProperty] private int _xAccelDisp;
    /// <summary>X 减速度。</summary>
    [ObservableProperty] private int _xDecelDisp;
    /// <summary>Y 速度。</summary>
    [ObservableProperty] private int _ySpeedDisp;
    /// <summary>Y 加速度。</summary>
    [ObservableProperty] private int _yAccelDisp;
    /// <summary>Y 减速度。</summary>
    [ObservableProperty] private int _yDecelDisp;
    /// <summary>Z 速度。</summary>
    [ObservableProperty] private int _zSpeedDisp;
    /// <summary>Z 加速度。</summary>
    [ObservableProperty] private int _zAccelDisp;
    /// <summary>Z 减速度。</summary>
    [ObservableProperty] private int _zDecelDisp;

    // ── X 区信号 ──
    /// <summary>X0 接液盘门开到位。</summary>
    [ObservableProperty] private bool _x0DrainOpen;
    /// <summary>X1 接液盘门关到位。</summary>
    [ObservableProperty] private bool _x1DrainClose;
    /// <summary>X2 磁铁下压限位。</summary>
    [ObservableProperty] private bool _x2Pressure;
    /// <summary>X3 X 轴物理原点。</summary>
    [ObservableProperty] private bool _x3HomeX;
    /// <summary>X4 Y 轴物理原点。</summary>
    [ObservableProperty] private bool _x4HomeY;
    /// <summary>X5 Z 轴物理原点。</summary>
    [ObservableProperty] private bool _x5HomeZ;
    /// <summary>X6 充磁反馈。</summary>
    [ObservableProperty] private bool _x6Magnetize;
    /// <summary>X7 退磁反馈。</summary>
    [ObservableProperty] private bool _x7Demagnetize;
    /// <summary>X11 检测有版。</summary>
    [ObservableProperty] private bool _x11HasPlate;

    // ── 绝对移动输入 ──
    /// <summary>用户输入 X 绝对值。</summary>
    [ObservableProperty] private int _absX;
    /// <summary>用户输入 Y 绝对值。</summary>
    [ObservableProperty] private int _absY;
    /// <summary>用户输入 Z 绝对值。</summary>
    [ObservableProperty] private int _absZ;

    // ── 相对移动输入 ──
    /// <summary>X 相对值。</summary>
    [ObservableProperty] private int _relX;
    /// <summary>Y 相对值。</summary>
    [ObservableProperty] private int _relY;
    /// <summary>Z 相对值。</summary>
    [ObservableProperty] private int _relZ;

    // ── 速度设置输入（绝对速度） ──
    /// <summary>X 速度。</summary>
    [ObservableProperty] private int _speedX = 100;
    /// <summary>X 加速度。</summary>
    [ObservableProperty] private int _accelX = 100;
    /// <summary>X 减速度。</summary>
    [ObservableProperty] private int _decelX = 100;
    /// <summary>Y 速度。</summary>
    [ObservableProperty] private int _speedY = 100;
    /// <summary>Y 加速度。</summary>
    [ObservableProperty] private int _accelY = 100;
    /// <summary>Y 减速度。</summary>
    [ObservableProperty] private int _decelY = 100;
    /// <summary>Z 速度。</summary>
    [ObservableProperty] private int _speedZ = 100;
    /// <summary>Z 加速度。</summary>
    [ObservableProperty] private int _accelZ = 100;
    /// <summary>Z 减速度。</summary>
    [ObservableProperty] private int _decelZ = 100;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="configurationStore">配置存储</param>
    /// <param name="manualOperationService">手动操作服务</param>
    public ManualOperationViewModel(IConfigurationStore configurationStore, IManualOperationService manualOperationService)
    {
        _configurationStore = configurationStore;
        _manualOperationService = manualOperationService;
        _ = LoadCranesAsync();
    }

    /// <summary>加载天车列表（数据库）。</summary>
    private async Task LoadCranesAsync()
    {
        try
        {
            var configs = await _configurationStore.GetCraneConfigsAsync();
            Cranes.Clear();
            foreach (var dto in configs.OrderBy(c => c.No))
            {
                Cranes.Add(new CraneConfigRowViewModel(dto));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ManualOperationViewModel] 加载天车列表失败：{ex.Message}");
        }
    }

    /// <summary>刷新选中天车状态。</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (SelectedCrane is null)
        {
            SetState(CraneConnectionState.Unknown, "请选择天车");
            return;
        }

        var status = await _manualOperationService.ReadStatusAsync(SelectedCrane.Id);
        if (status is null)
        {
            SetState(CraneConnectionState.Failed, "❌连接失败");
            Message = "读取状态失败，请检查 IP / 网络 / PLC。";
            return;
        }

        XDisplay = status.XDisplay;
        YDisplay = status.YDisplay;
        ZDisplay = status.ZDisplay;
        XEncoder = status.XEncoder;
        YEncoder = status.YEncoder;
        ZEncoder = status.ZEncoder;
        XHome = status.XHomeCompleted;
        YHome = status.YHomeCompleted;
        ZHome = status.ZHomeCompleted;
        ServoAlarm = status.ServoAlarm;
        PlcServoAlarm = status.PlcServoAlarm;
        PlcAlarm = status.PlcAlarm;
        XSpeedDisp = status.XSpeed;
        XAccelDisp = status.XAccel;
        XDecelDisp = status.XDecel;
        YSpeedDisp = status.YSpeed;
        YAccelDisp = status.YAccel;
        YDecelDisp = status.YDecel;
        ZSpeedDisp = status.ZSpeed;
        ZAccelDisp = status.ZAccel;
        ZDecelDisp = status.ZDecel;
        X0DrainOpen = status.DrainOpen;
        X1DrainClose = status.DrainClose;
        X2Pressure = status.PressureLimit;
        X3HomeX = status.PhysicalHomeX;
        X4HomeY = status.PhysicalHomeY;
        X5HomeZ = status.PhysicalHomeZ;
        X6Magnetize = status.MagnetizeFeedback;
        X7Demagnetize = status.DemagnetizeFeedback;
        X11HasPlate = status.HasPlate;

        SetState(status.HasAlarm ? CraneConnectionState.Alarm : CraneConnectionState.Connected,
            status.HasAlarm ? "天车报警" : "已连接");
        Message = "状态已刷新。";
    }

    /// <summary>设置三轴绝对速度。</summary>
    [RelayCommand]
    private async Task SetSpeedAsync()
    {
        var result = await WithCrane(id => _manualOperationService.SetSpeedAsync(id,
            new CraneSpeedDto(SpeedX, AccelX, DecelX, SpeedY, AccelY, DecelY, SpeedZ, AccelZ, DecelZ)));
        await FinishAsync(result);
    }

    /// <summary>绝对移动。</summary>
    [RelayCommand]
    private async Task MoveAbsoluteAsync()
    {
        var result = await WithCrane(id => _manualOperationService.MoveAbsoluteAsync(id, AbsX, AbsY, AbsZ));
        await FinishAsync(result);
    }

    /// <summary>相对移动。</summary>
    [RelayCommand]
    private async Task MoveRelativeAsync()
    {
        var result = await WithCrane(id => _manualOperationService.MoveRelativeAsync(id, RelX, RelY, RelZ));
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

    /// <summary>统一处理命令结果：显示消息，并在成功后刷新状态。</summary>
    /// <param name="result">结果</param>
    private async Task FinishAsync(ManualOperationResult result)
    {
        Message = result.Message;
        if (result.Success)
        {
            await RefreshAsync();
        }
    }

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
