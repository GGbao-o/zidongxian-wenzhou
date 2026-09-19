using AutomatedLine.Application.Abstractions;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Domain.Diagnostics;
using AutomatedLine.Infrastructure.Persistence;
using AutomatedLine.Infrastructure.Persistence.Entities;

namespace AutomatedLine.Infrastructure.Simulation;

/// <summary>
/// 【模拟数据专用】配置表为空时写入一套样例设备/天车数据，仅用于未接现场时联调界面与采集管线。
///
/// ★★★ 接入现场前请整体移除，步骤：★★★
/// 1) 删除本文件（AutomatedLine.Infrastructure/Simulation/SimulationDataSeeder.cs）；
/// 2) 删除 ConfigurationLoader 中对本类的调用（编译器会提示）；
/// 3) 删除 appsettings.json 中的 App:Simulation 节点（AppOptions.Simulation 可保留，不影响）；
/// 4) 清空/重建数据库中的 DeviceConfig、CraneConfig 表，确保只有现场真实配置。
///
/// 本类之外没有任何模拟业务逻辑，删除后不影响任何真实代码路径。
/// </summary>
public static class SimulationDataSeeder
{
    /// <summary>
    /// 若设备表与天车表均为空，则写入样例数据。
    /// </summary>
    /// <param name="db">数据库上下文</param>
    /// <param name="logger">日志</param>
    /// <returns>是否写入了样例数据</returns>
    public static bool SeedIfEmpty(AppDbContext db, IAppLogger logger)
    {
        if (db.CraneConfigs.Any() || db.DeviceConfigs.Any())
        {
            // 已有真实（或已有）数据，绝不写入，避免污染
            return false;
        }

        db.DeviceConfigs.AddRange(
            new DeviceConfig { Id = 1, Name = "上料架", DeviceType = DeviceType.LoadRack, Protocol = "Simulated", PositionX = 1000 },
            new DeviceConfig { Id = 2, Name = "双头镗", DeviceType = DeviceType.BoringMachine, Protocol = "Simulated", PositionX = 6000, MinDiameter = 0, MaxDiameter = 1000, MinLength = 0, MaxLength = 5000 },
            new DeviceConfig { Id = 3, Name = "斜床01", DeviceType = DeviceType.LatheMachine, Protocol = "Simulated", PositionX = 12000, MinDiameter = 0, MaxDiameter = 1000, MinLength = 0, MaxLength = 5000 },
            new DeviceConfig { Id = 4, Name = "斜床02", DeviceType = DeviceType.LatheMachine, Protocol = "Simulated", PositionX = 14000, MinDiameter = 0, MaxDiameter = 1000, MinLength = 0, MaxLength = 5000 },
            new DeviceConfig { Id = 5, Name = "研磨机01", DeviceType = DeviceType.GrinderMachine, Protocol = "Simulated", PositionX = 18000, MinDiameter = 0, MaxDiameter = 1000, MinLength = 0, MaxLength = 5000 },
            new DeviceConfig { Id = 6, Name = "动平衡", DeviceType = DeviceType.BalancingMachine, Protocol = "Simulated", PositionX = 22000, MinDiameter = 0, MaxDiameter = 1000, MinLength = 0, MaxLength = 5000 },
            new DeviceConfig { Id = 7, Name = "下料架", DeviceType = DeviceType.UnloadRack, Protocol = "Simulated", PositionX = 26000 });

        db.CraneConfigs.AddRange(
            new CraneConfig { Id = 1, No = 1, Name = "天车1", Protocol = "Simulated", StartX = 0, EndX = 20000, Width = 800, SafeX = 0, SafeZ = 0 },
            new CraneConfig { Id = 2, No = 2, Name = "天车2", Protocol = "Simulated", StartX = 15000, EndX = 30000, Width = 800, SafeX = 30000, SafeZ = 0 });

        db.SaveChanges();
        logger.Info(LogCategory.System, "配置表为空，已写入样例设备/天车数据（模拟专用，接现场前请删除 SimulationDataSeeder）");
        return true;
    }
}
