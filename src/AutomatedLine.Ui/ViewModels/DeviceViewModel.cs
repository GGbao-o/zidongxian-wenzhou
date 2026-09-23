using System.Collections.ObjectModel;
using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Configuration;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Ui.Helpers;
using AutomatedLine.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutomatedLine.Ui.ViewModels;

/// <summary>
/// 设备管理页面 ViewModel：**严格数据库 CRUD**。
/// 只读写 DeviceConfig 的参数，不展示运行态，也不后台读信号。
/// </summary>
public partial class DeviceViewModel : ObservableObject
{
    /// <summary>配置存储（数据库 CRUD）。</summary>
    private readonly IConfigurationStore _configurationStore;

    /// <summary>配置装载器，配置改动后重载运行态。</summary>
    private readonly IConfigurationLoader _configurationLoader;

    /// <summary>弹窗服务。</summary>
    private readonly IDialogService _dialogService;

    /// <summary>数据库配置缓存：设备号 → 配置。</summary>
    private Dictionary<int, DeviceConfigDto> _configs = new();

    /// <summary>设备配置行集合。</summary>
    public ObservableCollection<DeviceConfigRowViewModel> Devices { get; } = new();

    /// <summary>
    /// 构造函数。异步加载数据库配置。
    /// </summary>
    /// <param name="configurationStore">配置存储</param>
    /// <param name="configurationLoader">配置装载器</param>
    /// <param name="dialogService">弹窗服务</param>
    public DeviceViewModel(
        IConfigurationStore configurationStore,
        IConfigurationLoader configurationLoader,
        IDialogService dialogService)
    {
        _configurationStore = configurationStore;
        _configurationLoader = configurationLoader;
        _dialogService = dialogService;

        _ = LoadConfigsAsync();
    }

    /// <summary>从数据库重新加载全部设备配置并重建行。</summary>
    private async Task LoadConfigsAsync()
    {
        try
        {
            var configs = await _configurationStore.GetDeviceConfigsAsync();
            _configs = configs.ToDictionary(c => c.Id);
            RebuildRows();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DeviceViewModel] 加载设备配置失败：{ex.Message}");
        }
    }

    /// <summary>重建行（差量）。</summary>
    private void RebuildRows()
    {
        var dtos = _configs.Values.OrderBy(c => c.Id).ToList();
        ObservableCollectionSync.SyncById(
            Devices,
            dtos,
            row => row.Id,
            dto => dto.Id,
            dto => new DeviceConfigRowViewModel(dto),
            (row, dto) => row.ApplyConfig(dto));
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
        await ReloadAsync();
    }

    /// <summary>编辑设备配置。</summary>
    /// <param name="row">选中的设备行</param>
    [RelayCommand]
    private async Task EditDeviceAsync(DeviceConfigRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        if (!EditDialog("编辑设备", row.Config))
        {
            return;
        }

        await _configurationStore.SaveDeviceConfigAsync(row.Config);
        await ReloadAsync();
    }

    /// <summary>删除设备配置。</summary>
    /// <param name="row">选中的设备行</param>
    [RelayCommand]
    private async Task DeleteDeviceAsync(DeviceConfigRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            $"确定删除设备【{row.Config.Name}】的配置吗？", "删除确认",
            System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.OK)
        {
            return;
        }

        await _configurationStore.DeleteDeviceConfigAsync(row.Id);
        await ReloadAsync();
    }

    /// <summary>重载运行态并重新读库；重载失败只记录，不抛出。</summary>
    private async Task ReloadAsync()
    {
        try
        {
            await _configurationLoader.LoadAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DeviceViewModel] 重载设备配置失败：{ex.Message}");
        }

        await LoadConfigsAsync();
    }

    /// <summary>
    /// 用通用编辑弹框编辑设备配置（覆盖数据库全部字段）。字段顺序固定，确定后按顺序写回 DTO。
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
}
