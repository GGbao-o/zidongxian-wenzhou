using System.Collections.ObjectModel;
using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Cranes;
using AutomatedLine.Ui.Helpers;
using AutomatedLine.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutomatedLine.Ui.ViewModels;

/// <summary>
/// 手动操作页面 ViewModel。
/// 选择天车后可做三轴绝对/相对运动、设置三轴速度/加速度/减速度、清报警，并查看当前坐标、报警与
/// 充磁/放磁/下压/有板等信号。动作最终写天车 PLC（现场点位到位后由驱动映射）。
/// 按现场约定：手动操作不与自动线做互斥/排队。
/// </summary>
public partial class ManualOperationViewModel : ObservableObject
{
    /// <summary>天车注册表。</summary>
    private readonly CraneRegistry _craneRegistry;

    /// <summary>手动操作服务。</summary>
    private readonly IManualOperationService _manualOperationService;

    /// <summary>可选天车列表。</summary>
    public ObservableCollection<CraneRowViewModel> Cranes { get; } = new();

    /// <summary>当前选中的天车。</summary>
    [ObservableProperty]
    private CraneRowViewModel? _selectedCrane;

    /// <summary>绝对运动目标 X。</summary>
    [ObservableProperty]
    private double _absoluteX;

    /// <summary>绝对运动目标 Y。</summary>
    [ObservableProperty]
    private double _absoluteY;

    /// <summary>绝对运动目标 Z。</summary>
    [ObservableProperty]
    private double _absoluteZ;

    // ===== 三轴运动参数：每轴速度/加速度/减速度 =====

    /// <summary>X 轴速度。</summary>
    [ObservableProperty]
    private double _speedX;

    /// <summary>X 轴加速度。</summary>
    [ObservableProperty]
    private double _accelX;

    /// <summary>X 轴减速度。</summary>
    [ObservableProperty]
    private double _decelX;

    /// <summary>Y 轴速度。</summary>
    [ObservableProperty]
    private double _speedY;

    /// <summary>Y 轴加速度。</summary>
    [ObservableProperty]
    private double _accelY;

    /// <summary>Y 轴减速度。</summary>
    [ObservableProperty]
    private double _decelY;

    /// <summary>Z 轴速度。</summary>
    [ObservableProperty]
    private double _speedZ;

    /// <summary>Z 轴加速度。</summary>
    [ObservableProperty]
    private double _accelZ;

    /// <summary>Z 轴减速度。</summary>
    [ObservableProperty]
    private double _decelZ;

    /// <summary>相对运动步长（mm）。</summary>
    [ObservableProperty]
    private double _step;

    /// <summary>操作结果/提示信息。</summary>
    [ObservableProperty]
    private string _message = "请先选择天车。";

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="craneRegistry">天车注册表</param>
    /// <param name="manualOperationService">手动操作服务</param>
    /// <param name="configService">配置服务（取默认参数初值）</param>
    /// <param name="ticker">运行态节拍器</param>
    public ManualOperationViewModel(
        CraneRegistry craneRegistry,
        IManualOperationService manualOperationService,
        IConfigService configService,
        IRuntimeTicker ticker)
    {
        _craneRegistry = craneRegistry;
        _manualOperationService = manualOperationService;

        var manual = configService.GetAppOptions().Manual;
        SpeedX = SpeedY = SpeedZ = manual.DefaultSpeed;
        AccelX = AccelY = AccelZ = manual.DefaultAccel;
        DecelX = DecelY = DecelZ = manual.DefaultDecel;
        Step = manual.DefaultStep;

        ticker.Tick += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>绝对运动命令。</summary>
    [RelayCommand]
    private async Task MoveAbsoluteAsync()
    {
        if (!EnsureSelected(out var crane))
        {
            return;
        }

        var ok = await _manualOperationService.MoveAbsoluteAsync(
            crane.Id, AbsoluteX, AbsoluteY, AbsoluteZ, BuildParameters());
        Message = ok ? $"已下发绝对运动 → ({AbsoluteX:0.###}, {AbsoluteY:0.###}, {AbsoluteZ:0.###})" : "下发失败";
    }

    /// <summary>
    /// 相对运动命令。参数为轴向+方向，如 "X+"/"X-"/"Y+"/"Z-"。
    /// </summary>
    /// <param name="param">轴向与方向</param>
    [RelayCommand]
    private async Task MoveRelativeAsync(string? param)
    {
        if (!EnsureSelected(out var crane) || string.IsNullOrWhiteSpace(param) || param.Length < 2)
        {
            return;
        }

        var axis = param[0] switch
        {
            'X' or 'x' => CraneAxis.X,
            'Y' or 'y' => CraneAxis.Y,
            'Z' or 'z' => CraneAxis.Z,
            _ => (CraneAxis?)null
        };
        if (axis is null)
        {
            return;
        }

        var delta = param[1] == '-' ? -Step : Step;
        var ok = await _manualOperationService.MoveRelativeAsync(crane.Id, axis.Value, delta, BuildParameters());
        Message = ok ? $"已下发相对运动 {param}{Math.Abs(delta):0.###}" : "下发失败";
    }

    /// <summary>仅下发三轴参数（不运动）。</summary>
    [RelayCommand]
    private async Task SetParametersAsync()
    {
        if (!EnsureSelected(out var crane))
        {
            return;
        }

        var ok = await _manualOperationService.SetParametersAsync(crane.Id, BuildParameters());
        Message = ok ? "已下发三轴速度/加速度/减速度" : "下发失败";
    }

    /// <summary>清除报警命令。</summary>
    [RelayCommand]
    private async Task ClearAlarmAsync()
    {
        if (!EnsureSelected(out var crane))
        {
            return;
        }

        var ok = await _manualOperationService.ClearAlarmAsync(crane.Id);
        Message = ok ? "已下发清除报警" : "下发失败";
    }

    /// <summary>充磁命令。</summary>
    [RelayCommand]
    private async Task MagnetizeAsync()
    {
        if (!EnsureSelected(out var crane))
        {
            return;
        }

        await _manualOperationService.MagnetizeAsync(crane.Id);
        Message = "已下发充磁";
    }

    /// <summary>放磁命令。</summary>
    [RelayCommand]
    private async Task DemagnetizeAsync()
    {
        if (!EnsureSelected(out var crane))
        {
            return;
        }

        await _manualOperationService.DemagnetizeAsync(crane.Id);
        Message = "已下发放磁";
    }

    /// <summary>下压命令。</summary>
    [RelayCommand]
    private async Task PressDownAsync()
    {
        if (!EnsureSelected(out var crane))
        {
            return;
        }

        await _manualOperationService.PressDownAsync(crane.Id);
        Message = "已下发下压";
    }

    /// <summary>把界面上的三轴参数组装成参数对象。</summary>
    private TripleAxisMotionParameters BuildParameters()
        => new(
            new AxisMotionParameters(SpeedX, AccelX, DecelX),
            new AxisMotionParameters(SpeedY, AccelY, DecelY),
            new AxisMotionParameters(SpeedZ, AccelZ, DecelZ));

    /// <summary>
    /// 校验已选中天车。
    /// </summary>
    /// <param name="crane">选中的天车</param>
    /// <returns>已选中返回 true</returns>
    private bool EnsureSelected(out CraneRowViewModel crane)
    {
        if (SelectedCrane is null)
        {
            Message = "请先选择天车。";
            crane = null!;
            return false;
        }

        crane = SelectedCrane;
        return true;
    }

    /// <summary>
    /// 差量刷新天车列表。
    /// </summary>
    private void Refresh()
    {
        ObservableCollectionSync.SyncById(
            Cranes,
            _craneRegistry.Snapshot().OrderBy(c => c.No).ToList(),
            row => row.Id,
            crane => crane.Id,
            crane => new CraneRowViewModel(crane),
            (row, crane) => row.Refresh(crane));
    }

    /// <summary>
    /// 切换天车时，用当前坐标初始化绝对运动输入框。
    /// </summary>
    /// <param name="value">新选中天车</param>
    partial void OnSelectedCraneChanged(CraneRowViewModel? value)
    {
        if (value is null)
        {
            return;
        }

        AbsoluteX = value.X;
        AbsoluteY = value.Y;
        AbsoluteZ = value.Z;
        Message = $"已选择 {value.Name}";
    }
}
