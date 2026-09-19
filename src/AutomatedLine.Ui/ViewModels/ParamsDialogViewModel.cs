using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutomatedLine.Ui.ViewModels;

/// <summary>
/// 参数分类编辑弹框的 ViewModel。
/// 持有一份分类参数的编辑副本，确定后由 <see cref="ParamsViewModel"/> 写回并保存。
/// </summary>
public partial class ParamsDialogViewModel : ObservableObject
{
    /// <summary>分类标题，用作弹框标题。</summary>
    public string Title { get; }

    /// <summary>可编辑的参数项。</summary>
    public ObservableCollection<ParamEditItemViewModel> Items { get; } = new();

    /// <summary>
    /// 构造函数。复制来源参数项，避免取消编辑时污染原值。
    /// </summary>
    /// <param name="title">分类标题</param>
    /// <param name="source">来源参数项</param>
    public ParamsDialogViewModel(string title, IEnumerable<ParamItemViewModel> source)
    {
        Title = title;
        foreach (var item in source)
        {
            Items.Add(new ParamEditItemViewModel(item.Name, item.Value));
        }
    }
}

/// <summary>
/// 弹框内单个可编辑参数项。
/// </summary>
public partial class ParamEditItemViewModel : ObservableObject
{
    /// <summary>参数名称。</summary>
    public string Name { get; }

    /// <summary>参数值（可编辑）。</summary>
    [ObservableProperty]
    private string _value;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="name">名称</param>
    /// <param name="value">初值</param>
    public ParamEditItemViewModel(string name, string value)
    {
        Name = name;
        _value = value;
    }
}
