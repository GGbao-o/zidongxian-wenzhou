using AutomatedLine.Application.Routing;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Devices;

namespace AutomatedLine.Tests.Fakes;

/// <summary>为原有非工艺专项测试提供完整已配置路线，不访问驱动或现场。</summary>
public static class TestProcessPlans
{
    /// <summary>创建上下料及四类已实现加工设备齐全的真实工艺服务。</summary>
    /// <returns>配置及设备均为独立内存实例的工艺服务。</returns>
    public static ProcessPlanService Create()
    {
        var devices = new DeviceRegistry();
        devices.Register(new LoadRack(1, "上料", 0));
        devices.Register(new BoringMachine(2, "双头镗", 1000));
        devices.Register(new LatheMachine(3, "斜床", 2000));
        devices.Register(new GrinderMachine(4, "研磨", 3000));
        devices.Register(new MarkerMachine(5, "打号", 4000));
        devices.Register(new UnloadRack(6, "下料", 5000));
        return new ProcessPlanService(new FakeConfigService(), devices, new TestLogger());
    }
}
