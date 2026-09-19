using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutomatedLine.Ui.ViewModels;

/// <summary>
/// 外壳 ViewModel：负责顶部导航与页面切换。
/// 各业务页面各自是独立 ViewModel，外壳只做"选哪个页面"，不掺业务逻辑（架构文档 §13）。
/// </summary>
public partial class ShellViewModel : ObservableObject
{
    /// <summary>导航项集合。</summary>
    public ObservableCollection<NavItemViewModel> NavItems { get; } = new();

    /// <summary>当前选中的导航项。变化时同步切换内容页。</summary>
    [ObservableProperty]
    private NavItemViewModel? _selectedNav;

    /// <summary>当前内容页 ViewModel，由 ContentControl 通过 DataTemplate 渲染成对应 View。</summary>
    [ObservableProperty]
    private object? _currentPage;

    /// <summary>
    /// 构造函数。注入各页面 ViewModel 并组装导航。
    /// </summary>
    /// <param name="main">主页面</param>
    /// <param name="crane">天车管理</param>
    /// <param name="device">设备管理</param>
    /// <param name="process">工序展示</param>
    /// <param name="parameters">参数设置</param>
    /// <param name="screen">大屏展示</param>
    /// <param name="manual">手动操作</param>
    /// <param name="alarm">报警处理</param>
    public ShellViewModel(
        MainViewModel main,
        CraneViewModel crane,
        DeviceViewModel device,
        ProcessViewModel process,
        ParamsViewModel parameters,
        ScreenViewModel screen,
        ManualOperationViewModel manual,
        AlarmViewModel alarm)
    {
        NavItems.Add(new NavItemViewModel("主页面", main));
        NavItems.Add(new NavItemViewModel("天车管理", crane));
        NavItems.Add(new NavItemViewModel("设备管理", device));
        NavItems.Add(new NavItemViewModel("工序展示", process));
        NavItems.Add(new NavItemViewModel("参数设置", parameters));
        NavItems.Add(new NavItemViewModel("大屏展示", screen));
        NavItems.Add(new NavItemViewModel("手动操作", manual));
        NavItems.Add(new NavItemViewModel("报警处理", alarm));

        // 默认停留在主页面
        SelectedNav = NavItems[0];
    }

    /// <summary>
    /// 选中项变化时切换内容页。
    /// </summary>
    /// <param name="value">新的选中项</param>
    partial void OnSelectedNavChanged(NavItemViewModel? value)
    {
        CurrentPage = value?.Page;
    }
}

/// <summary>
/// 顶部导航项。
/// </summary>
public sealed class NavItemViewModel
{
    /// <summary>显示标题。</summary>
    public string Title { get; }

    /// <summary>对应的页面 ViewModel。</summary>
    public object Page { get; }

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="title">标题</param>
    /// <param name="page">页面 ViewModel</param>
    public NavItemViewModel(string title, object page)
    {
        Title = title;
        Page = page;
    }
}
