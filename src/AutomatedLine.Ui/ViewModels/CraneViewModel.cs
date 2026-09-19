using System.Collections.ObjectModel;
using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Configuration;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Ui.Helpers;
using AutomatedLine.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutomatedLine.Ui.ViewModels;

/// <summary>
/// 天车管理页面 ViewModel。
/// 展示所有天车的连接、坐标、状态、当前任务与报警；并支持对天车配置做增删改（写数据库后重载运行态）。
/// 由运行态节拍器定期刷新。
/// </summary>
public partial class CraneViewModel : ObservableObject
{
    /// <summary>天车注册表。</summary>
    private readonly CraneRegistry _craneRegistry;

    /// <summary>设备注册表，用于把任务的源/目标设备号解析成名称。</summary>
    private readonly DeviceRegistry _deviceRegistry;

    /// <summary>配置存储（数据库 CRUD）。</summary>
    private readonly IConfigurationStore _configurationStore;

    /// <summary>配置装载器，配置改动后重载运行态。</summary>
    private readonly IConfigurationLoader _configurationLoader;

    /// <summary>弹窗服务，用于配置编辑对话框。</summary>
    private readonly IDialogService _dialogService;

    /// <summary>天车行集合（界面绑定源）。</summary>
    public ObservableCollection<CraneRowViewModel> Cranes { get; } = new();

    /// <summary>
    /// 构造函数。订阅节拍器定期刷新。
    /// </summary>
    /// <param name="craneRegistry">天车注册表</param>
    /// <param name="deviceRegistry">设备注册表</param>
    /// <param name="configurationStore">配置存储</param>
    /// <param name="configurationLoader">配置装载器</param>
    /// <param name="dialogService">弹窗服务</param>
    /// <param name="ticker">运行态节拍器</param>
    public CraneViewModel(
        CraneRegistry craneRegistry,
        DeviceRegistry deviceRegistry,
        IConfigurationStore configurationStore,
        IConfigurationLoader configurationLoader,
        IDialogService dialogService,
        IRuntimeTicker ticker)
    {
        _craneRegistry = craneRegistry;
        _deviceRegistry = deviceRegistry;
        _configurationStore = configurationStore;
        _configurationLoader = configurationLoader;
        _dialogService = dialogService;

        ticker.Tick += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>新增天车配置。</summary>
    [RelayCommand]
    private async Task AddCraneAsync()
    {
        var dto = new CraneConfigDto();
        if (!EditDialog("新增天车", dto))
        {
            return;
        }

        await _configurationStore.SaveCraneConfigAsync(dto);
        await _configurationLoader.LoadAsync();
        Refresh();
    }

    /// <summary>编辑天车配置。</summary>
    /// <param name="row">选中的天车行</param>
    [RelayCommand]
    private async Task EditCraneAsync(CraneRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var configs = await _configurationStore.GetCraneConfigsAsync();
        var dto = configs.FirstOrDefault(c => c.Id == row.Id);
        if (dto is null)
        {
            return;
        }

        if (!EditDialog("编辑天车", dto))
        {
            return;
        }

        await _configurationStore.SaveCraneConfigAsync(dto);
        await _configurationLoader.LoadAsync();
        Refresh();
    }

    /// <summary>删除天车配置。</summary>
    /// <param name="row">选中的天车行</param>
    [RelayCommand]
    private async Task DeleteCraneAsync(CraneRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            $"确定删除天车【{row.Name}】的配置吗？", "删除确认",
            System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.OK)
        {
            return;
        }

        await _configurationStore.DeleteCraneConfigAsync(row.Id);
        await _configurationLoader.LoadAsync();
        Refresh();
    }

    /// <summary>
    /// 用通用编辑弹框编辑天车配置。字段顺序固定，确定后按顺序写回 DTO。
    /// </summary>
    /// <param name="title">弹框标题</param>
    /// <param name="dto">待编辑配置</param>
    /// <returns>用户确定返回 true</returns>
    private bool EditDialog(string title, CraneConfigDto dto)
    {
        // 复用参数编辑弹框：字段名 + 当前值；Apply 委托在此不需要，统一空实现
        var items = new List<ParamItemViewModel>
        {
            new("天车号", dto.Id.ToString(), _ => { }),
            new("序号", dto.No.ToString(), _ => { }),
            new("名称", dto.Name, _ => { }),
            new("协议", dto.Protocol, _ => { }),
            new("IP", dto.IpAddress, _ => { }),
            new("端口", dto.Port.ToString(), _ => { }),
            new("StartX", dto.StartX.ToString("0.###"), _ => { }),
            new("EndX", dto.EndX.ToString("0.###"), _ => { }),
            new("天车宽度", dto.Width.ToString("0.###"), _ => { }),
            new("安全位X", dto.SafeX.ToString("0.###"), _ => { }),
            new("安全位Z", dto.SafeZ.ToString("0.###"), _ => { }),
            new("X速度", dto.SpeedX.ToString("0.###"), _ => { }),
            new("X加速度", dto.AccelX.ToString("0.###"), _ => { }),
            new("X减速度", dto.DecelX.ToString("0.###"), _ => { }),
            new("Y速度", dto.SpeedY.ToString("0.###"), _ => { }),
            new("Y加速度", dto.AccelY.ToString("0.###"), _ => { }),
            new("Y减速度", dto.DecelY.ToString("0.###"), _ => { }),
            new("Z速度", dto.SpeedZ.ToString("0.###"), _ => { }),
            new("Z加速度", dto.AccelZ.ToString("0.###"), _ => { }),
            new("Z减速度", dto.DecelZ.ToString("0.###"), _ => { }),
            new("启用(true/false)", dto.IsEnabled ? "true" : "false", _ => { })
        };

        var dialog = new ParamsDialogViewModel(title, items);
        if (!_dialogService.ShowParamsDialog(dialog))
        {
            return false;
        }

        var v = dialog.Items;
        dto.Id = ParseInt(v[0].Value, dto.Id);
        dto.No = ParseInt(v[1].Value, dto.No);
        dto.Name = v[2].Value;
        dto.Protocol = v[3].Value;
        dto.IpAddress = v[4].Value;
        dto.Port = ParseInt(v[5].Value, dto.Port);
        dto.StartX = ParseDouble(v[6].Value, dto.StartX);
        dto.EndX = ParseDouble(v[7].Value, dto.EndX);
        dto.Width = ParseDouble(v[8].Value, dto.Width);
        dto.SafeX = ParseDouble(v[9].Value, dto.SafeX);
        dto.SafeZ = ParseDouble(v[10].Value, dto.SafeZ);
        dto.SpeedX = ParseDouble(v[11].Value, dto.SpeedX);
        dto.AccelX = ParseDouble(v[12].Value, dto.AccelX);
        dto.DecelX = ParseDouble(v[13].Value, dto.DecelX);
        dto.SpeedY = ParseDouble(v[14].Value, dto.SpeedY);
        dto.AccelY = ParseDouble(v[15].Value, dto.AccelY);
        dto.DecelY = ParseDouble(v[16].Value, dto.DecelY);
        dto.SpeedZ = ParseDouble(v[17].Value, dto.SpeedZ);
        dto.AccelZ = ParseDouble(v[18].Value, dto.AccelZ);
        dto.DecelZ = ParseDouble(v[19].Value, dto.DecelZ);
        dto.IsEnabled = ParseBool(v[20].Value, dto.IsEnabled);
        return true;
    }

    /// <summary>解析整数，失败用默认值。</summary>
    private static int ParseInt(string text, int fallback) => int.TryParse(text, out var v) ? v : fallback;

    /// <summary>解析浮点，失败用默认值。</summary>
    private static double ParseDouble(string text, double fallback) => double.TryParse(text, out var v) ? v : fallback;

    /// <summary>解析布尔，失败用默认值。</summary>
    private static bool ParseBool(string text, bool fallback)
        => bool.TryParse(text, out var v) ? v
            : text.Trim() == "1" ? true
            : text.Trim() == "0" ? false
            : fallback;

    /// <summary>
    /// 差量刷新天车行，避免定时清空重建导致滚动与选中跳动。
    /// </summary>
    private void Refresh()
    {
        var cranes = _craneRegistry.Snapshot().OrderBy(c => c.No).ToList();
        ObservableCollectionSync.SyncById(
            Cranes,
            cranes,
            row => row.Id,
            crane => crane.Id,
            crane => new CraneRowViewModel(crane, _deviceRegistry.Get),
            (row, crane) => row.Refresh(crane));
    }
}
