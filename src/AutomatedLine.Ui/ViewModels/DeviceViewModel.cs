using System.Collections.ObjectModel;
using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Configuration;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Ui.Helpers;
using AutomatedLine.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutomatedLine.Ui.ViewModels;

/// <summary>
/// 设备管理页面 ViewModel。
/// 展示所有设备的类型、位置、连接、状态、当前工件与信号；并支持对设备配置做增删改（写数据库后重载运行态）。
/// </summary>
public partial class DeviceViewModel : ObservableObject
{
    /// <summary>设备注册表。</summary>
    private readonly DeviceRegistry _deviceRegistry;

    /// <summary>配置存储（数据库 CRUD）。</summary>
    private readonly IConfigurationStore _configurationStore;

    /// <summary>配置装载器，配置改动后重载运行态。</summary>
    private readonly IConfigurationLoader _configurationLoader;

    /// <summary>弹窗服务，用于配置编辑对话框。</summary>
    private readonly IDialogService _dialogService;

    /// <summary>设备行集合（界面绑定源）。</summary>
    public ObservableCollection<DeviceRowViewModel> Devices { get; } = new();

    /// <summary>
    /// 构造函数。订阅节拍器定期刷新。
    /// </summary>
    /// <param name="deviceRegistry">设备注册表</param>
    /// <param name="configurationStore">配置存储</param>
    /// <param name="configurationLoader">配置装载器</param>
    /// <param name="dialogService">弹窗服务</param>
    /// <param name="ticker">运行态节拍器</param>
    public DeviceViewModel(
        DeviceRegistry deviceRegistry,
        IConfigurationStore configurationStore,
        IConfigurationLoader configurationLoader,
        IDialogService dialogService,
        IRuntimeTicker ticker)
    {
        _deviceRegistry = deviceRegistry;
        _configurationStore = configurationStore;
        _configurationLoader = configurationLoader;
        _dialogService = dialogService;

        ticker.Tick += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>新增设备配置。</summary>
    [RelayCommand]
    private async Task AddDeviceAsync()
    {
        var dto = new DeviceConfigDto();
        if (!EditDialog("新增设备", dto))
        {
            return;
        }

        await _configurationStore.SaveDeviceConfigAsync(dto);
        await _configurationLoader.LoadAsync();
        Refresh();
    }

    /// <summary>编辑设备配置。</summary>
    /// <param name="row">选中的设备行</param>
    [RelayCommand]
    private async Task EditDeviceAsync(DeviceRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var configs = await _configurationStore.GetDeviceConfigsAsync();
        var dto = configs.FirstOrDefault(d => d.Id == row.Id);
        if (dto is null)
        {
            return;
        }

        if (!EditDialog("编辑设备", dto))
        {
            return;
        }

        await _configurationStore.SaveDeviceConfigAsync(dto);
        await _configurationLoader.LoadAsync();
        Refresh();
    }

    /// <summary>删除设备配置。</summary>
    /// <param name="row">选中的设备行</param>
    [RelayCommand]
    private async Task DeleteDeviceAsync(DeviceRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            $"确定删除设备【{row.Name}】的配置吗？", "删除确认",
            System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.OK)
        {
            return;
        }

        await _configurationStore.DeleteDeviceConfigAsync(row.Id);
        await _configurationLoader.LoadAsync();
        Refresh();
    }

    /// <summary>
    /// 用通用编辑弹框编辑设备配置。字段顺序固定，确定后按顺序写回 DTO。
    /// </summary>
    /// <param name="title">弹框标题</param>
    /// <param name="dto">待编辑配置</param>
    /// <returns>用户确定返回 true</returns>
    private bool EditDialog(string title, DeviceConfigDto dto)
    {
        var items = new List<ParamItemViewModel>
        {
            new("设备号", dto.Id.ToString(), _ => { }),
            new("名称", dto.Name, _ => { }),
            new("设备类型", dto.DeviceType.ToString(), _ => { }),
            new("协议", dto.Protocol, _ => { }),
            new("IP", dto.IpAddress, _ => { }),
            new("端口", dto.Port.ToString(), _ => { }),
            new("物理位置X", dto.PositionX.ToString("0.###"), _ => { }),
            new("安全位", dto.SafePosition.ToString("0.###"), _ => { }),
            new("最小直径", dto.MinDiameter.ToString("0.###"), _ => { }),
            new("最大直径", dto.MaxDiameter.ToString("0.###"), _ => { }),
            new("最小长度", dto.MinLength.ToString("0.###"), _ => { }),
            new("最大长度", dto.MaxLength.ToString("0.###"), _ => { }),
            new("启用(true/false)", dto.IsEnabled ? "true" : "false", _ => { })
        };

        var dialog = new ParamsDialogViewModel(title, items);
        if (!_dialogService.ShowParamsDialog(dialog))
        {
            return false;
        }

        var v = dialog.Items;
        dto.Id = ParseInt(v[0].Value, dto.Id);
        dto.Name = v[1].Value;
        dto.DeviceType = Enum.TryParse<DeviceType>(v[2].Value, true, out var type) ? type : dto.DeviceType;
        dto.Protocol = v[3].Value;
        dto.IpAddress = v[4].Value;
        dto.Port = ParseInt(v[5].Value, dto.Port);
        dto.PositionX = ParseDouble(v[6].Value, dto.PositionX);
        dto.SafePosition = ParseDouble(v[7].Value, dto.SafePosition);
        dto.MinDiameter = ParseDouble(v[8].Value, dto.MinDiameter);
        dto.MaxDiameter = ParseDouble(v[9].Value, dto.MaxDiameter);
        dto.MinLength = ParseDouble(v[10].Value, dto.MinLength);
        dto.MaxLength = ParseDouble(v[11].Value, dto.MaxLength);
        dto.IsEnabled = ParseBool(v[12].Value, dto.IsEnabled);
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
    /// 差量刷新设备行。
    /// </summary>
    private void Refresh()
    {
        var devices = _deviceRegistry.Snapshot().OrderBy(d => d.Id).ToList();
        ObservableCollectionSync.SyncById(
            Devices,
            devices,
            row => row.Id,
            device => device.Id,
            device => new DeviceRowViewModel(device),
            (row, device) => row.Refresh(device));
    }
}
