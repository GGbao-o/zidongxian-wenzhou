using AutomatedLine.Application.Configuration;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutomatedLine.Ui.ViewModels;

/// <summary>
/// 天车管理页的一行：**只展示数据库配置字段**（严格 CRUD，不掺运行态）。
/// 配置整包绑定到 <see cref="Config"/>。
/// </summary>
public partial class CraneConfigRowViewModel : ObservableObject
{
    /// <summary>天车号（主键）。</summary>
    public int Id { get; }

    /// <summary>数据库配置对象（全字段）。</summary>
    [ObservableProperty]
    private CraneConfigDto _config;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="config">数据库配置</param>
    public CraneConfigRowViewModel(CraneConfigDto config)
    {
        Id = config.Id;
        _config = config;
    }

    /// <summary>更新数据库配置字段。</summary>
    /// <param name="config">新配置</param>
    public void ApplyConfig(CraneConfigDto config) => Config = config;
}
