using AutomatedLine.Application.Configuration;
using AutomatedLine.Domain.Routing;

namespace AutomatedLine.Application.Routing;

/// <summary>工艺模板读写及首次启动快照入口，生成器和页面不得用全局开关改写已启动路线。</summary>
public interface IProcessPlanService
{
    /// <summary>读取当前保存成功的工序开关副本。</summary>
    /// <returns>可独立读取的配置副本。</returns>
    ProcessPlanOptions GetSettings();

    /// <summary>预览新工件将使用的路线，不绑定到任何工件，也不要求当前上下料设备齐全。</summary>
    /// <returns>当前模板结合已启用设备得到的路线。</returns>
    ProcessPlanSnapshot Preview();

    /// <summary>首次启动时读取并校验最新工艺；ERP同一批只调用一次。</summary>
    /// <returns>包含必需起终点的独立快照。</returns>
    ProcessPlanSnapshot Capture();

    /// <summary>保存单道加工工序的开关；保存失败不得发布新模板。</summary>
    /// <param name="process">需要修改的加工工序。</param>
    /// <param name="enabled">是否参与后续新工件工艺。</param>
    /// <returns>保存成功后的完整新路线。</returns>
    ProcessPlanSnapshot SetEnabled(ProcessType process, bool enabled);
}
