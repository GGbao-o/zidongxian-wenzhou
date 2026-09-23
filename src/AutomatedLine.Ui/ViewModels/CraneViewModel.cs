using System.Collections.ObjectModel;
using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Configuration;
using AutomatedLine.Ui.Helpers;
using AutomatedLine.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutomatedLine.Ui.ViewModels;

/// <summary>
/// 天车管理页面 ViewModel：**严格数据库 CRUD**。
/// 只读写 CraneConfig 的参数（坐标、范围、速度等、是否启用），不展示运行态，也不后台读信号。
/// </summary>
public partial class CraneViewModel : ObservableObject
{
    /// <summary>配置存储（数据库 CRUD）。</summary>
    private readonly IConfigurationStore _configurationStore;

    /// <summary>配置装载器，配置改动后重载运行态。</summary>
    private readonly IConfigurationLoader _configurationLoader;

    /// <summary>弹窗服务。</summary>
    private readonly IDialogService _dialogService;

    /// <summary>数据库配置缓存：天车号 → 配置。</summary>
    private Dictionary<int, CraneConfigDto> _configs = new();

    /// <summary>天车配置行集合。</summary>
    public ObservableCollection<CraneConfigRowViewModel> Cranes { get; } = new();

    /// <summary>
    /// 构造函数。异步加载数据库配置。
    /// </summary>
    /// <param name="configurationStore">配置存储</param>
    /// <param name="configurationLoader">配置装载器</param>
    /// <param name="dialogService">弹窗服务</param>
    public CraneViewModel(
        IConfigurationStore configurationStore,
        IConfigurationLoader configurationLoader,
        IDialogService dialogService)
    {
        _configurationStore = configurationStore;
        _configurationLoader = configurationLoader;
        _dialogService = dialogService;

        _ = LoadConfigsAsync();
    }

    /// <summary>从数据库重新加载全部天车配置并重建行。</summary>
    private async Task LoadConfigsAsync()
    {
        try
        {
            var configs = await _configurationStore.GetCraneConfigsAsync();
            _configs = configs.ToDictionary(c => c.Id);
            RebuildRows();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CraneViewModel] 加载天车配置失败：{ex.Message}");
        }
    }

    /// <summary>重建行（差量）。</summary>
    private void RebuildRows()
    {
        var dtos = _configs.Values.OrderBy(c => c.No).ToList();
        ObservableCollectionSync.SyncById(
            Cranes,
            dtos,
            row => row.Id,
            dto => dto.Id,
            dto => new CraneConfigRowViewModel(dto),
            (row, dto) => row.ApplyConfig(dto));
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
        await ReloadAsync();
    }

    /// <summary>编辑天车配置。</summary>
    /// <param name="row">选中的天车行</param>
    [RelayCommand]
    private async Task EditCraneAsync(CraneConfigRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        if (!EditDialog("编辑天车", row.Config))
        {
            return;
        }

        await _configurationStore.SaveCraneConfigAsync(row.Config);
        await ReloadAsync();
    }

    /// <summary>删除天车配置。</summary>
    /// <param name="row">选中的天车行</param>
    [RelayCommand]
    private async Task DeleteCraneAsync(CraneConfigRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            $"确定删除天车【{row.Config.Name}】的配置吗？", "删除确认",
            System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.OK)
        {
            return;
        }

        await _configurationStore.DeleteCraneConfigAsync(row.Id);
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
            Console.WriteLine($"[CraneViewModel] 重载天车配置失败：{ex.Message}");
        }

        await LoadConfigsAsync();
    }

    /// <summary>
    /// 用通用编辑弹框编辑天车配置（覆盖数据库全部字段）。字段顺序固定，确定后按顺序写回 DTO。
    /// </summary>
    /// <param name="title">弹框标题</param>
    /// <param name="dto">待编辑配置</param>
    /// <returns>用户确定返回 true</returns>
    private bool EditDialog(string title, CraneConfigDto dto)
    {
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
            new("X偏移", dto.OffsetX.ToString("0.###"), _ => { }),
            new("Y偏移", dto.OffsetY.ToString("0.###"), _ => { }),
            new("Z偏移", dto.OffsetZ.ToString("0.###"), _ => { }),
            new("X速度", dto.SpeedX.ToString("0.###"), _ => { }),
            new("X加速度", dto.AccelX.ToString("0.###"), _ => { }),
            new("X减速度", dto.DecelX.ToString("0.###"), _ => { }),
            new("Y速度", dto.SpeedY.ToString("0.###"), _ => { }),
            new("Y加速度", dto.AccelY.ToString("0.###"), _ => { }),
            new("Y减速度", dto.DecelY.ToString("0.###"), _ => { }),
            new("Z速度", dto.SpeedZ.ToString("0.###"), _ => { }),
            new("Z加速度", dto.AccelZ.ToString("0.###"), _ => { }),
            new("Z减速度", dto.DecelZ.ToString("0.###"), _ => { }),
            new("运动超时(ms)", dto.MoveTimeoutMs.ToString(), _ => { }),
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
        dto.OffsetX = ParseDouble(v[11].Value, dto.OffsetX);
        dto.OffsetY = ParseDouble(v[12].Value, dto.OffsetY);
        dto.OffsetZ = ParseDouble(v[13].Value, dto.OffsetZ);
        dto.SpeedX = ParseDouble(v[14].Value, dto.SpeedX);
        dto.AccelX = ParseDouble(v[15].Value, dto.AccelX);
        dto.DecelX = ParseDouble(v[16].Value, dto.DecelX);
        dto.SpeedY = ParseDouble(v[17].Value, dto.SpeedY);
        dto.AccelY = ParseDouble(v[18].Value, dto.AccelY);
        dto.DecelY = ParseDouble(v[19].Value, dto.DecelY);
        dto.SpeedZ = ParseDouble(v[20].Value, dto.SpeedZ);
        dto.AccelZ = ParseDouble(v[21].Value, dto.AccelZ);
        dto.DecelZ = ParseDouble(v[22].Value, dto.DecelZ);
        dto.MoveTimeoutMs = ParseInt(v[23].Value, dto.MoveTimeoutMs);
        dto.IsEnabled = ParseBool(v[24].Value, dto.IsEnabled);
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
