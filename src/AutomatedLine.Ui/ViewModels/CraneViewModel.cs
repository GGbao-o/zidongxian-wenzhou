using System.Collections.ObjectModel;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Ui.Helpers;
using AutomatedLine.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutomatedLine.Ui.ViewModels;

/// <summary>
/// 天车管理页面 ViewModel。
/// 展示所有天车的连接、坐标、状态、当前任务与报警；由运行态节拍器定期刷新。
/// 配置编辑功能后续补充，当前先做展示与监控。
/// </summary>
public partial class CraneViewModel : ObservableObject
{
    /// <summary>天车注册表。</summary>
    private readonly CraneRegistry _craneRegistry;

    /// <summary>天车行集合（界面绑定源）。</summary>
    public ObservableCollection<CraneRowViewModel> Cranes { get; } = new();

    /// <summary>
    /// 构造函数。订阅节拍器定期刷新。
    /// </summary>
    /// <param name="craneRegistry">天车注册表</param>
    /// <param name="ticker">运行态节拍器</param>
    public CraneViewModel(CraneRegistry craneRegistry, IRuntimeTicker ticker)
    {
        _craneRegistry = craneRegistry;
        ticker.Tick += (_, _) => Refresh();
        Refresh();
    }

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
            crane => new CraneRowViewModel(crane),
            (row, crane) => row.Refresh(crane));
    }
}
