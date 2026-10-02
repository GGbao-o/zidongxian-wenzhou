using System.Collections.ObjectModel;
using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Configuration;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutomatedLine.Ui.ViewModels;

/// <summary>
/// 参数设置页面 ViewModel。
/// 参数会持续增多，因此按类别组织成卡片；点击卡片弹出该类参数对话框编辑，确定后写回 appsettings.json
/// （架构文档 §13.5）。
/// </summary>
public partial class ParamsViewModel : ObservableObject
{
    /// <summary>配置服务。</summary>
    private readonly IConfigService _configService;

    /// <summary>弹窗服务。</summary>
    private readonly IDialogService _dialogService;

    /// <summary>运行态容器（枚举斜床设备）。</summary>
    private readonly DeviceRegistry _deviceRegistry;

    /// <summary>当前参数快照。编辑时直接改这里，再整体保存。</summary>
    private AppOptions _options = new();

    /// <summary>参数分类卡片集合。</summary>
    public ObservableCollection<ParamCategoryViewModel> Categories { get; } = new();

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="configService">配置服务</param>
    /// <param name="dialogService">弹窗服务</param>
    /// <param name="deviceRegistry">设备注册表</param>
    public ParamsViewModel(IConfigService configService, IDialogService dialogService, DeviceRegistry deviceRegistry)
    {
        _configService = configService;
        _dialogService = dialogService;
        _deviceRegistry = deviceRegistry;
        BuildCategories();
    }

    /// <summary>
    /// 点击分类卡片：弹出该类参数对话框，确定后应用并保存。
    /// </summary>
    /// <param name="category">分类</param>
    [RelayCommand]
    private void OpenCategory(ParamCategoryViewModel? category)
    {
        if (category is null)
        {
            return;
        }

        var dialogViewModel = new ParamsDialogViewModel(category.Title, category.Items);
        if (!_dialogService.ShowParamsDialog(dialogViewModel))
        {
            return;
        }

        // 把弹框里的编辑结果写回参数项并应用到 AppOptions
        for (var i = 0; i < category.Items.Count && i < dialogViewModel.Items.Count; i++)
        {
            var item = category.Items[i];
            item.Value = dialogViewModel.Items[i].Value;

            try
            {
                item.Apply(item.Value);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ParamsViewModel] 参数 {item.Name} 应用失败：{ex.Message}");
            }
        }

        try
        {
            _configService.SaveAppOptions(_options);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ParamsViewModel] 保存参数失败：{ex.Message}");
        }

        // 保存后按内存值重建卡片；此处**不重读配置**（文件重载有延迟，会读回旧值导致看起来没生效）
        RebuildCards();
    }

    /// <summary>
    /// 依据当前 AppOptions 重建分类卡片。新增参数只需在此追加，界面自动出现卡片与弹框项。
    /// </summary>
    private void BuildCategories()
    {
        _options = _configService.GetAppOptions();
        RebuildCards();
    }

    /// <summary>
    /// 用当前 <c>_options</c> 重建分类卡片。**不重读配置**，避免磁盘重载延迟读到旧值。
    /// </summary>
    private void RebuildCards()
    {
        Categories.Clear();

        var collection = new ParamCategoryViewModel("采集", "设备/天车采集周期与通信超时");
        collection.Items.Add(IntItem("设备采集周期(ms)", () => _options.Collection.DeviceIntervalMs, v => _options.Collection.DeviceIntervalMs = v));
        collection.Items.Add(IntItem("通信超时(ms)", () => _options.Collection.CommunicationTimeoutMs, v => _options.Collection.CommunicationTimeoutMs = v));
        Categories.Add(collection);

        var scheduling = new ParamCategoryViewModel("调度", "候选任务重算与派发节奏");
        scheduling.Items.Add(IntItem("候选任务生成周期(ms)", () => _options.Scheduling.GenerateIntervalMs, v => _options.Scheduling.GenerateIntervalMs = v));
        scheduling.Items.Add(IntItem("调度周期(ms)", () => _options.Scheduling.ScheduleIntervalMs, v => _options.Scheduling.ScheduleIntervalMs = v));
        Categories.Add(scheduling);

        var crane = new ParamCategoryViewModel("天车", "运动超时与到位/下压容差");
        crane.Items.Add(IntItem("运动超时(ms)", () => _options.Crane.MoveTimeoutMs, v => _options.Crane.MoveTimeoutMs = v));
        crane.Items.Add(IntItem("到位容差(mm)", () => _options.Crane.PositionToleranceMm, v => _options.Crane.PositionToleranceMm = v));
        crane.Items.Add(IntItem("下压容差(mm)", () => _options.Crane.PressureToleranceMm, v => _options.Crane.PressureToleranceMm = v));
        Categories.Add(crane);

        var stateMachine = new ParamCategoryViewModel("状态机", "握手超时与推进周期");
        stateMachine.Items.Add(IntItem("握手超时(ms)", () => _options.StateMachine.HandshakeTimeoutMs, v => _options.StateMachine.HandshakeTimeoutMs = v));
        stateMachine.Items.Add(IntItem("状态机推进周期(ms)", () => _options.StateMachine.ExecuteIntervalMs, v => _options.StateMachine.ExecuteIntervalMs = v));
        Categories.Add(stateMachine);

        // 斜床搬运参数：每台斜床一份（顶尖距离 / 大孔 / 小孔偏移），保存到 App:Transfer
        var lathes = _deviceRegistry.Snapshot()
            .Where(d => d.Type == DeviceType.LatheMachine)
            .OrderBy(d => d.Id)
            .ToList();
        if (lathes.Count > 0)
        {
            var transfer = new ParamCategoryViewModel("斜床搬运", "每台斜床的 Y 二次偏移参数");
            foreach (var lathe in lathes)
            {
                if (!_options.Transfer.Lathes.TryGetValue(lathe.Id, out var opt))
                {
                    opt = new LatheTransferOptions();
                    _options.Transfer.Lathes[lathe.Id] = opt;
                }

                transfer.Items.Add(DoubleItem($"{lathe.Name} 顶尖距离", () => opt.TailstockCenterDistance, v => opt.TailstockCenterDistance = v));
                transfer.Items.Add(DoubleItem($"{lathe.Name} 大孔偏移", () => opt.BigHoleOffset, v => opt.BigHoleOffset = v));
                transfer.Items.Add(DoubleItem($"{lathe.Name} 小孔偏移", () => opt.SmallHoleOffset, v => opt.SmallHoleOffset = v));
            }

            Categories.Add(transfer);
        }

        var logging = new ParamCategoryViewModel("日志", "目录与滚动策略");
        logging.Items.Add(TextItem("日志目录", () => _options.Logging.Directory, v => _options.Logging.Directory = v));
        logging.Items.Add(IntItem("保留天数", () => _options.Logging.RetentionDays, v => _options.Logging.RetentionDays = v));
        logging.Items.Add(LongItem("单文件上限(字节)", () => _options.Logging.MaxFileSizeBytes, v => _options.Logging.MaxFileSizeBytes = v));
        Categories.Add(logging);

    }

    /// <summary>构造整数参数项。</summary>
    private static ParamItemViewModel IntItem(string name, Func<int> get, Action<int> set)
        => new(name, get().ToString(), text => set(int.TryParse(text, out var v) ? v : 0));

    /// <summary>构造长整数参数项。</summary>
    private static ParamItemViewModel LongItem(string name, Func<long> get, Action<long> set)
        => new(name, get().ToString(), text => set(long.TryParse(text, out var v) ? v : 0));


    /// <summary>构造文本参数项。</summary>
    private static ParamItemViewModel TextItem(string name, Func<string> get, Action<string> set)
        => new(name, get(), set);

    /// <summary>构造浮点参数项。</summary>
    private static ParamItemViewModel DoubleItem(string name, Func<double> get, Action<double> set)
        => new(name, get().ToString("0.###"), text => set(double.TryParse(text, out var v) ? v : 0));
}

/// <summary>
/// 参数分类。
/// </summary>
public sealed class ParamCategoryViewModel
{
    /// <summary>分类标题。</summary>
    public string Title { get; }

    /// <summary>分类说明。</summary>
    public string Description { get; }

    /// <summary>该分类下的参数项。</summary>
    public ObservableCollection<ParamItemViewModel> Items { get; } = new();

    /// <summary>参数项数量（界面显示）。</summary>
    public int ItemCount => Items.Count;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="title">标题</param>
    /// <param name="description">说明</param>
    public ParamCategoryViewModel(string title, string description)
    {
        Title = title;
        Description = description;
    }
}

/// <summary>
/// 单个参数项（含"如何写回强类型参数"的委托）。
/// </summary>
public sealed class ParamItemViewModel
{
    /// <summary>参数名称。</summary>
    public string Name { get; }

    /// <summary>参数值（字符串形式）。</summary>
    public string Value { get; set; }

    /// <summary>把字符串值写回强类型参数的动作。</summary>
    public Action<string> Apply { get; }

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="name">名称</param>
    /// <param name="value">初值</param>
    /// <param name="apply">写回动作</param>
    public ParamItemViewModel(string name, string value, Action<string> apply)
    {
        Name = name;
        Value = value;
        Apply = apply;
    }
}
