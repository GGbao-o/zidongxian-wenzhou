# 天车状态机实施计划（第 1 阶段：基础与资源）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为天车线性 async 状态机打好地基：设备 X/Y/Z 坐标（DB 现查）、天车坐标换算、斜床 Y 二次偏移配置与计算、路权/预约的**部分释放**能力。

**Architecture:** 本阶段只做可独立测试的纯逻辑与字段扩展，不改状态机流程、不动 UI。产出的 `CraneCoordinate`/`LatheYOffset`/`RightOfWayManager.Resize`/`ResourceReservationManager.ReleaseSource` 是第 2 阶段（流程实现）的依赖。

**Tech Stack:** .NET 8、xUnit、EF Core 实体（表结构手工维护，迁移仅参考）。

**参考设计：** `Docs/CraneStateMachine设计.md` §4（坐标）、§6（资源部分释放）、§9（Y 偏移）。
**提交规范：** 每个任务的提交按根目录 `AGENTS.md`：`<type>(<scope>): <主题>` + `[why]/[what]/[how]`。

---

## 阶段划分（本计划只做第 1 阶段）

- **第 1 阶段（本文件）**：基础与资源。
- 第 2 阶段：`ITransferHandler` + 各设备 handler + 编排器 `RunFlowAsync` 重写 + 每车独立 Worker + 设备握手/参数点位 + 打桩测试。
- 第 3 阶段：UI（设备 X/Y/Z 字段、斜床 Y 参数配置页）。

---

## 文件结构（第 1 阶段）

| 文件 | 职责 |
|---|---|
| `src/AutomatedLine.Infrastructure/Persistence/Entities/DeviceConfig.cs`（改） | 增加 `PositionY`/`PositionZ` 列 |
| `src/AutomatedLine.Application/Configuration/ConfigDtos.cs`（改） | `DeviceConfigDto` 增加 Y/Z（现查用） |
| `src/AutomatedLine.Infrastructure/Config/ConfigurationStore.cs`（改） | 实体 ↔ DTO 映射 Y/Z |
| `Configs/Sql/add_device_position_yz.sql`（新） | 手工加列脚本 |
| `src/AutomatedLine.Application/Transfer/CraneCoordinate.cs`（新） | 设备坐标 + 偏移 → 天车绝对目标；Z 下压公式 |
| `src/AutomatedLine.Application/Configuration/TransferOptions.cs`（新） | `TransferOptions` / `LatheTransferOptions`（每台斜床） |
| `src/AutomatedLine.Application/Configuration/AppOptions.cs`（改） | 增加 `Transfer` 属性 |
| `src/AutomatedLine.Application/Transfer/LatheYOffset.cs`（新） | 斜床 Y 二次偏移计算 |
| `src/AutomatedLine.Application/RightOfWay/RightOfWayManager.cs`（改） | 增加 `Resize` |
| `src/AutomatedLine.Application/Reservations/ResourceReservationManager.cs`（改） | 增加 `ReleaseSource`（源可空） |
| `tests/AutomatedLine.Tests/DevicePositionTests.cs`（新） | `DeviceConfigDto` 携带 Y/Z |
| `tests/AutomatedLine.Tests/CraneCoordinateTests.cs`（新） | 坐标/下压公式测试 |
| `tests/AutomatedLine.Tests/LatheYOffsetTests.cs`（新） | Y 偏移测试 |
| `tests/AutomatedLine.Tests/RightOfWayManagerTests.cs`（改） | `Resize` 测试 |
| `tests/AutomatedLine.Tests/ResourceReservationManagerTests.cs`（改） | `ReleaseSource` 测试 |

> 设备坐标**不缓存到 `DeviceBase`**、不改 `ConfigurationLoader`：状态机执行任务时经 `IConfigurationStore.GetDeviceConfigsAsync()`（已有抽象）现查，一轮一次、开销可忽略。

---

### Task 1: `DeviceConfig` 增加 PositionY/PositionZ（DB 现查）

**Files:**
- Modify: `src/AutomatedLine.Infrastructure/Persistence/Entities/DeviceConfig.cs`
- Modify: `src/AutomatedLine.Application/Configuration/ConfigDtos.cs`
- Modify: `src/AutomatedLine.Infrastructure/Config/ConfigurationStore.cs`
- Create: `Configs/Sql/add_device_position_yz.sql`
- Test: `tests/AutomatedLine.Tests/DevicePositionTests.cs`

- [ ] **Step 1: 写失败测试**

创建 `tests/AutomatedLine.Tests/DevicePositionTests.cs`：

```csharp
using AutomatedLine.Application.Configuration;

namespace AutomatedLine.Tests;

/// <summary>
/// 设备配置 DTO 的 X/Y/Z 绝对坐标测试（Y/Z 为 v2.38 新增，供状态机现查）。
/// </summary>
public class DevicePositionTests
{
    /// <summary>DTO 应能携带并读取 Y/Z 绝对坐标。</summary>
    [Fact]
    public void DeviceConfigDto_Carries_PositionYZ()
    {
        var dto = new DeviceConfigDto { PositionX = 1000, PositionY = 200, PositionZ = 2000 };

        Assert.Equal(1000d, dto.PositionX);
        Assert.Equal(200d, dto.PositionY);
        Assert.Equal(2000d, dto.PositionZ);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/AutomatedLine.Tests/AutomatedLine.Tests.csproj --nologo -v q --filter DevicePositionTests`
Expected: FAIL（编译错误：`DeviceConfigDto` 无 `PositionY`）

- [ ] **Step 3: 实体 / DTO 增加 Y/Z**

`src/AutomatedLine.Infrastructure/Persistence/Entities/DeviceConfig.cs`，`PositionX` 下方：

```csharp
    /// <summary>设备物理 Y 位置。</summary>
    public double PositionY { get; set; }

    /// <summary>设备物理 Z 位置（台面 Z）。</summary>
    public double PositionZ { get; set; }
```

`src/AutomatedLine.Application/Configuration/ConfigDtos.cs` 的 `DeviceConfigDto`，`PositionX` 下方：

```csharp
    /// <summary>物理 Y 位置。</summary>
    public double PositionY { get; set; }

    /// <summary>物理 Z 位置（台面 Z）。</summary>
    public double PositionZ { get; set; }
```

- [ ] **Step 4: Store 映射 Y/Z**

`src/AutomatedLine.Infrastructure/Config/ConfigurationStore.cs`：`ToDto` 里 `PositionX = e.PositionX,` 下方补：

```csharp
        PositionY = e.PositionY,
        PositionZ = e.PositionZ,
```

`Apply` 里 `e.PositionX = dto.PositionX;` 下方补：

```csharp
        e.PositionY = dto.PositionY;
        e.PositionZ = dto.PositionZ;
```

- [ ] **Step 5: 加列脚本**

创建 `Configs/Sql/add_device_position_yz.sql`：

```sql
-- v2.38：设备增加 X/Y/Z 绝对坐标（原只有 PositionX）。
-- 表结构手工维护，此脚本供现网库补列；新建库直接建含列结构即可。
ALTER TABLE DeviceConfig ADD COLUMN PositionY DOUBLE NOT NULL DEFAULT 0;
ALTER TABLE DeviceConfig ADD COLUMN PositionZ DOUBLE NOT NULL DEFAULT 0;
```

- [ ] **Step 6: 运行测试确认通过**

Run: `dotnet test tests/AutomatedLine.Tests/AutomatedLine.Tests.csproj --nologo -v q`
Expected: PASS（17 + 1 = 18 通过）

- [ ] **Step 7: 提交**

```bash
git add src/AutomatedLine.Infrastructure/Persistence/Entities/DeviceConfig.cs \
        src/AutomatedLine.Application/Configuration/ConfigDtos.cs \
        src/AutomatedLine.Infrastructure/Config/ConfigurationStore.cs \
        Configs/Sql/add_device_position_yz.sql \
        tests/AutomatedLine.Tests/DevicePositionTests.cs
git commit -m "feat(device): DeviceConfig 增加 PositionY/PositionZ（运行期现查）"
```

---

### Task 2: 天车坐标换算 `CraneCoordinate`

**Files:**
- Create: `src/AutomatedLine.Application/Transfer/CraneCoordinate.cs`
- Test: `tests/AutomatedLine.Tests/CraneCoordinateTests.cs`

- [ ] **Step 1: 写失败测试**

创建 `tests/AutomatedLine.Tests/CraneCoordinateTests.cs`：

```csharp
using AutomatedLine.Application.Transfer;
using AutomatedLine.Domain.Cranes;
using AutomatedLine.Domain.Devices;

namespace AutomatedLine.Tests;

/// <summary>
/// 天车坐标换算测试：设备 X/Y/Z + 本车偏移，Z 再减下压量。
/// </summary>
public class CraneCoordinateTests
{
    /// <summary>构造一台天车并给定三轴偏移。</summary>
    private static Crane CraneWithOffset(double ox, double oy, double oz)
    {
        var crane = new Crane(1, 1, "天车1");
        crane.OffsetX = ox;
        crane.OffsetY = oy;
        crane.OffsetZ = oz;
        return crane;
    }

    /// <summary>斜床/研磨：Z 下压 = Round(d/2)；X/Y 加偏移。</summary>
    [Fact]
    public void ToAbsoluteTarget_Lathe_Uses_HalfDiameter()
    {
        var crane = CraneWithOffset(10, -20, 30);

        var (x, y, z) = CraneCoordinate.ToAbsoluteTarget(
            DeviceType.LatheMachine, posX: 1000, posY: 200, posZ: 2000, crane, diameter: 103.1);

        Assert.Equal(1010, x);
        Assert.Equal(180, y);
        Assert.Equal(1978, z);   // 2030 - Round(103.1/2)=2030-52
    }

    /// <summary>其他设备：Z 下压 = Round((d/2)/0.9537 + (d/2)/0.866)。</summary>
    [Fact]
    public void ToAbsoluteTarget_Boring_Uses_TwoCoefficients()
    {
        var crane = CraneWithOffset(10, -20, 30);

        var (x, y, z) = CraneCoordinate.ToAbsoluteTarget(
            DeviceType.BoringMachine, posX: 1000, posY: 200, posZ: 2000, crane, diameter: 103.1);

        Assert.Equal(1010, x);
        Assert.Equal(180, y);
        Assert.Equal(1916, z);   // 2030 - Round(51.55/0.9537 + 51.55/0.866)=2030-114
    }

    /// <summary>Z 下压量单独可算（供测试/日志）。</summary>
    [Fact]
    public void ZDrop_Matches_Formulas()
    {
        Assert.Equal(52d, CraneCoordinate.ZDrop(DeviceType.LatheMachine, 103.1));
        Assert.Equal(52d, CraneCoordinate.ZDrop(DeviceType.GrinderMachine, 103.1));
        Assert.Equal(114d, CraneCoordinate.ZDrop(DeviceType.BoringMachine, 103.1));
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/AutomatedLine.Tests/AutomatedLine.Tests.csproj --nologo -v q --filter CraneCoordinateTests`
Expected: FAIL（编译错误：找不到 `CraneCoordinate`）

- [ ] **Step 3: 实现 `CraneCoordinate`**

创建 `src/AutomatedLine.Application/Transfer/CraneCoordinate.cs`：

```csharp
using AutomatedLine.Domain.Cranes;
using AutomatedLine.Domain.Devices;

namespace AutomatedLine.Application.Transfer;

/// <summary>
/// 天车坐标换算（《CraneStateMachine设计.md》§4.1）。
/// 绝对运动目标 = 设备数据库 X/Y/Z + 本车偏移量，Z 再减去随工件直径变化的下压量。
/// 设备坐标由调用方现查数据库后以参数传入（不缓存到领域对象）。
/// </summary>
public static class CraneCoordinate
{
    /// <summary>Z 下压经验系数 1（非斜床/研磨设备）。</summary>
    public const double ZCoef1 = 0.9537;

    /// <summary>Z 下压经验系数 2（非斜床/研磨设备）。</summary>
    public const double ZCoef2 = 0.866;

    /// <summary>设备基坐标 X = 设备 X + 本车 X 偏移。</summary>
    /// <param name="positionX">设备 X 绝对坐标（数据库现查）</param>
    /// <param name="crane">执行本任务的天车</param>
    /// <returns>X 基坐标</returns>
    public static double BaseX(double positionX, Crane crane) => positionX + crane.OffsetX;

    /// <summary>设备基坐标 Y = 设备 Y + 本车 Y 偏移。</summary>
    /// <param name="positionY">设备 Y 绝对坐标（数据库现查）</param>
    /// <param name="crane">执行本任务的天车</param>
    /// <returns>Y 基坐标</returns>
    public static double BaseY(double positionY, Crane crane) => positionY + crane.OffsetY;

    /// <summary>设备基坐标 Z = 设备 Z + 本车 Z 偏移。</summary>
    /// <param name="positionZ">设备 Z 绝对坐标（数据库现查）</param>
    /// <param name="crane">执行本任务的天车</param>
    /// <returns>Z 基坐标</returns>
    public static double BaseZ(double positionZ, Crane crane) => positionZ + crane.OffsetZ;

    /// <summary>
    /// Z 下压量：斜床/研磨 = Round(d/2)，其余 = Round((d/2)/0.9537 + (d/2)/0.866)。
    /// </summary>
    /// <param name="type">设备类型</param>
    /// <param name="diameter">当前工件直径</param>
    /// <returns>下压量（已取整）</returns>
    public static double ZDrop(DeviceType type, double diameter)
    {
        var half = diameter / 2.0;
        var drop = type is DeviceType.LatheMachine or DeviceType.GrinderMachine
            ? half
            : half / ZCoef1 + half / ZCoef2;
        return Math.Round(drop);
    }

    /// <summary>
    /// 计算某设备在此天车坐标系下的绝对运动目标（X/Y 含偏移；Z 含偏移再减下压量）。
    /// </summary>
    /// <param name="type">设备类型（决定 Z 下压公式）</param>
    /// <param name="posX">设备 X 绝对坐标</param>
    /// <param name="posY">设备 Y 绝对坐标</param>
    /// <param name="posZ">设备 Z 绝对坐标</param>
    /// <param name="crane">执行本任务的天车</param>
    /// <param name="diameter">当前工件直径</param>
    /// <returns>绝对目标 (X, Y, Z)</returns>
    public static (int X, int Y, int Z) ToAbsoluteTarget(
        DeviceType type, double posX, double posY, double posZ, Crane crane, double diameter)
    {
        var x = (int)Math.Round(BaseX(posX, crane));
        var y = (int)Math.Round(BaseY(posY, crane));
        var z = (int)Math.Round(BaseZ(posZ, crane) - ZDrop(type, diameter));
        return (x, y, z);
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/AutomatedLine.Tests/AutomatedLine.Tests.csproj --nologo -v q --filter CraneCoordinateTests`
Expected: PASS（3 个）

- [ ] **Step 5: 提交**

```bash
git add src/AutomatedLine.Application/Transfer/CraneCoordinate.cs tests/AutomatedLine.Tests/CraneCoordinateTests.cs
git commit -m "feat(crane): 新增坐标换算 CraneCoordinate（偏移量 + Z 下压公式）"
```

---

### Task 3: 斜床 Y 二次偏移配置与计算

**Files:**
- Create: `src/AutomatedLine.Application/Configuration/TransferOptions.cs`
- Modify: `src/AutomatedLine.Application/Configuration/AppOptions.cs`
- Create: `src/AutomatedLine.Application/Transfer/LatheYOffset.cs`
- Test: `tests/AutomatedLine.Tests/LatheYOffsetTests.cs`

- [ ] **Step 1: 写失败测试**

创建 `tests/AutomatedLine.Tests/LatheYOffsetTests.cs`：

```csharp
using AutomatedLine.Application.Configuration;
using AutomatedLine.Application.Transfer;

namespace AutomatedLine.Tests;

/// <summary>
/// 斜床 Y 二次偏移计算测试（《CraneStateMachine设计.md》§9.1）。
/// </summary>
public class LatheYOffsetTests
{
    /// <summary>大孔(100)：YOffset = Round((顶尖距离-长度)/2 + 122)。</summary>
    [Fact]
    public void Compute_BigHole()
    {
        var opt = new LatheTransferOptions { TailstockCenterDistance = 800, BigHoleOffset = 122, SmallHoleOffset = 55 };
        var y = LatheYOffset.Compute(opt, plugHole: 100, length: 700);
        Assert.Equal(172, y);   // (800-700)/2 + 122 = 50+122
    }

    /// <summary>小孔(70)：YOffset = Round((顶尖距离-长度)/2 + 55)。</summary>
    [Fact]
    public void Compute_SmallHole()
    {
        var opt = new LatheTransferOptions { TailstockCenterDistance = 800, BigHoleOffset = 122, SmallHoleOffset = 55 };
        var y = LatheYOffset.Compute(opt, plugHole: 70, length: 690);
        Assert.Equal(110, y);   // (800-690)/2 + 55 = 55+55
    }

    /// <summary>结果取整（四舍五入）。</summary>
    [Fact]
    public void Compute_Rounds()
    {
        var opt = new LatheTransferOptions { TailstockCenterDistance = 801, BigHoleOffset = 122, SmallHoleOffset = 55 };
        var y = LatheYOffset.Compute(opt, plugHole: 100, length: 700);
        Assert.Equal(173, y);   // (801-700)/2=50.5 + 122 = 172.5 → 173
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/AutomatedLine.Tests/AutomatedLine.Tests.csproj --nologo -v q --filter LatheYOffsetTests`
Expected: FAIL（找不到 `LatheTransferOptions` / `LatheYOffset`）

- [ ] **Step 3: 配置模型 + AppOptions.Transfer**

创建 `src/AutomatedLine.Application/Configuration/TransferOptions.cs`：

```csharp
namespace AutomatedLine.Application.Configuration;

/// <summary>
/// 搬运流程参数（对应 appsettings 的 App:Transfer）。
/// </summary>
public sealed class TransferOptions
{
    /// <summary>每台斜床的 Y 二次偏移参数，键 = 斜床设备号。</summary>
    public Dictionary<int, LatheTransferOptions> Lathes { get; set; } = new();
}

/// <summary>
/// 单台斜床的 Y 二次偏移参数（每台一份）。
/// </summary>
public sealed class LatheTransferOptions
{
    /// <summary>该斜床当前顶尖距离。</summary>
    public double TailstockCenterDistance { get; set; }

    /// <summary>大孔孔径偏移，默认 122。</summary>
    public double BigHoleOffset { get; set; } = 122;

    /// <summary>小孔孔径偏移，默认 55。</summary>
    public double SmallHoleOffset { get; set; } = 55;
}
```

在 `src/AutomatedLine.Application/Configuration/AppOptions.cs` 的 `AppOptions` 里，`Crane` 属性附近补：

```csharp
    /// <summary>搬运流程参数（斜床 Y 二次偏移等）。</summary>
    public TransferOptions Transfer { get; set; } = new();
```

- [ ] **Step 4: 实现 `LatheYOffset`**

创建 `src/AutomatedLine.Application/Transfer/LatheYOffset.cs`：

```csharp
using AutomatedLine.Application.Configuration;

namespace AutomatedLine.Application.Transfer;

/// <summary>
/// 斜床 Y 二次偏移计算（《CraneStateMachine设计.md》§9.1）。
/// YOffset = Round( (顶尖距离 − 工件长度) / 2 + 孔径偏移 )；大孔 122、小孔 55。
/// </summary>
public static class LatheYOffset
{
    /// <summary>大孔（PlugHole=100）对应的孔径偏移取值判定。</summary>
    public const int BigHolePlug = 100;

    /// <summary>
    /// 计算某工件的 Y 二次偏移量（取整）。
    /// </summary>
    /// <param name="options">该斜床的参数</param>
    /// <param name="plugHole">工件堵孔尺寸（100=大孔，70=小孔）</param>
    /// <param name="length">工件长度</param>
    /// <returns>Y 偏移量（取整）</returns>
    public static int Compute(LatheTransferOptions options, int plugHole, double length)
    {
        var holeOffset = plugHole == BigHolePlug ? options.BigHoleOffset : options.SmallHoleOffset;
        return (int)Math.Round((options.TailstockCenterDistance - length) / 2.0 + holeOffset);
    }
}
```

- [ ] **Step 5: 运行测试确认通过**

Run: `dotnet test tests/AutomatedLine.Tests/AutomatedLine.Tests.csproj --nologo -v q --filter LatheYOffsetTests`
Expected: PASS（3 个）

- [ ] **Step 6: 提交**

```bash
git add src/AutomatedLine.Application/Configuration/TransferOptions.cs \
        src/AutomatedLine.Application/Configuration/AppOptions.cs \
        src/AutomatedLine.Application/Transfer/LatheYOffset.cs \
        tests/AutomatedLine.Tests/LatheYOffsetTests.cs
git commit -m "feat(config): 斜床 Y 二次偏移参数（每台一份）与计算"
```

---

### Task 4: 路权 `Resize`（缩小/替换区间）

**Files:**
- Modify: `src/AutomatedLine.Application/RightOfWay/RightOfWayManager.cs`
- Test: `tests/AutomatedLine.Tests/RightOfWayManagerTests.cs`

- [ ] **Step 1: 追加失败测试**

在 `tests/AutomatedLine.Tests/RightOfWayManagerTests.cs` 类内追加：

```csharp
    /// <summary>Resize 把区间缩小，成功且仍持有。</summary>
    [Fact]
    public void Resize_Shrinks_HeldInterval()
    {
        var manager = new RightOfWayManager(new TestLogger());
        var task = Guid.NewGuid();
        manager.TryAcquire(task, new XInterval(5000, 18000));

        Assert.True(manager.Resize(task, new XInterval(9000, 11000)));
        Assert.True(manager.IsHeld(task));
        // 缩小后，原区间两端现在空闲：可被别的任务获取
        Assert.True(manager.TryAcquire(Guid.NewGuid(), new XInterval(5000, 8000)));
    }

    /// <summary>Resize 与其它任务的锁相交时失败，且保留原区间。</summary>
    [Fact]
    public void Resize_Fails_WhenOverlaps_Other()
    {
        var manager = new RightOfWayManager(new TestLogger());
        var task1 = Guid.NewGuid();
        var task2 = Guid.NewGuid();
        manager.TryAcquire(task1, new XInterval(0, 1000));
        manager.TryAcquire(task2, new XInterval(5000, 6000));

        // 想把 task1 扩到与 task2 相交 → 失败
        Assert.False(manager.Resize(task1, new XInterval(0, 5500)));

        // 原区间仍被持有（别的任务无法拿下 0~1000）
        Assert.False(manager.TryAcquire(Guid.NewGuid(), new XInterval(0, 1000)));
    }

    /// <summary>未持有路权时 Resize 返回 false。</summary>
    [Fact]
    public void Resize_Fails_WhenNotHeld()
    {
        var manager = new RightOfWayManager(new TestLogger());
        Assert.False(manager.Resize(Guid.NewGuid(), new XInterval(0, 10)));
    }
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/AutomatedLine.Tests/AutomatedLine.Tests.csproj --nologo -v q --filter RightOfWayManagerTests`
Expected: FAIL（找不到 `Resize`）

- [ ] **Step 3: 实现 `Resize`**

在 `src/AutomatedLine.Application/RightOfWay/RightOfWayManager.cs` 的 `CanAcquire` 方法之后插入：

```csharp
    /// <summary>
    /// 调整（缩小/替换）某任务已持有的路权区间。用于"到达目标后把路权缩到车体范围"。
    /// 新区间与**其它**任务的锁相交则失败并保留原区间；任务未持有路权也返回 false。
    /// </summary>
    /// <param name="taskId">任务标识</param>
    /// <param name="interval">新区间</param>
    /// <returns>成功返回 true</returns>
    public bool Resize(Guid taskId, XInterval interval)
    {
        lock (_sync)
        {
            if (!_locks.ContainsKey(taskId))
            {
                return false;
            }

            foreach (var kv in _locks)
            {
                if (kv.Key == taskId)
                {
                    continue;   // 只与别的任务比较
                }

                if (kv.Value.Overlaps(interval))
                {
                    return false;
                }
            }

            _locks[taskId] = interval;
            _logger.Info(LogCategory.Scheduling, $"调整路权：任务 {Short(taskId)} → {interval}");
            return true;
        }
    }
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/AutomatedLine.Tests/AutomatedLine.Tests.csproj --nologo -v q --filter RightOfWayManagerTests`
Expected: PASS（3 + 3 = 6 个）

- [ ] **Step 5: 提交**

```bash
git add src/AutomatedLine.Application/RightOfWay/RightOfWayManager.cs tests/AutomatedLine.Tests/RightOfWayManagerTests.cs
git commit -m "feat(row): 路权支持 Resize（到达目标后缩小到车体范围）"
```

---

### Task 5: 预约 `ReleaseSource`（只释放源设备）

**Files:**
- Modify: `src/AutomatedLine.Application/Reservations/ResourceReservationManager.cs`
- Test: `tests/AutomatedLine.Tests/ResourceReservationManagerTests.cs`

- [ ] **Step 1: 追加失败测试**

在 `tests/AutomatedLine.Tests/ResourceReservationManagerTests.cs` 类内追加：

```csharp
    /// <summary>只释放源设备，目标仍被预约。</summary>
    [Fact]
    public void ReleaseSource_Frees_OnlySource()
    {
        var manager = new ResourceReservationManager(new TestLogger());
        var task = Guid.NewGuid();
        manager.TryReserve(task, sourceId: 2, targetId: 3);

        Assert.True(manager.ReleaseSource(task));
        Assert.False(manager.IsReserved(2));   // 源已释放
        Assert.True(manager.IsReserved(3));    // 目标仍占用
        Assert.True(manager.TryReserve(Guid.NewGuid(), 2, 9));   // 源可被别的任务预约
    }

    /// <summary>源已释放后再 ReleaseSource 返回 false（幂等）。</summary>
    [Fact]
    public void ReleaseSource_SecondCall_ReturnsFalse()
    {
        var manager = new ResourceReservationManager(new TestLogger());
        var task = Guid.NewGuid();
        manager.TryReserve(task, 2, 3);

        Assert.True(manager.ReleaseSource(task));
        Assert.False(manager.ReleaseSource(task));
    }

    /// <summary>ReleaseSource 后整体 Release 仍能释放目标。</summary>
    [Fact]
    public void Release_AfterReleaseSource_FreesTarget()
    {
        var manager = new ResourceReservationManager(new TestLogger());
        var task = Guid.NewGuid();
        manager.TryReserve(task, 2, 3);

        manager.ReleaseSource(task);
        Assert.True(manager.Release(task));
        Assert.False(manager.IsReserved(3));
    }
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/AutomatedLine.Tests/AutomatedLine.Tests.csproj --nologo -v q --filter ResourceReservationManagerTests`
Expected: FAIL（找不到 `ReleaseSource`）

- [ ] **Step 3: 把源改为可空并实现 `ReleaseSource`**

`src/AutomatedLine.Application/Reservations/ResourceReservationManager.cs`：

字段：

```csharp
    /// <summary>任务标识 → (源设备号, 目标设备号)。</summary>
    private readonly Dictionary<Guid, (int Source, int Target)> _byTask = new();
```

改为：

```csharp
    /// <summary>任务标识 → (源设备号, 目标设备号)。源为 null 表示源预约已提前释放。</summary>
    private readonly Dictionary<Guid, (int? Source, int Target)> _byTask = new();
```

`Release` 内改为：

```csharp
            released = _byTask.Remove(taskId, out var devices);
            if (released)
            {
                if (devices.Source is int sourceId)
                {
                    _byDevice.Remove(sourceId);
                }

                _byDevice.Remove(devices.Target);
            }
```

`Release` 之后插入：

```csharp
    /// <summary>
    /// 只释放任务的**源设备**预约，保留目标设备预约。用于"到达目标后释放源"。
    /// 源已释放或任务不存在返回 false（幂等）。
    /// </summary>
    /// <param name="taskId">任务标识</param>
    /// <returns>确实释放了返回 true</returns>
    public bool ReleaseSource(Guid taskId)
    {
        bool released = false;
        lock (_sync)
        {
            if (_byTask.TryGetValue(taskId, out var devices) && devices.Source is int sourceId)
            {
                _byDevice.Remove(sourceId);
                _byTask[taskId] = (null, devices.Target);
                released = true;
            }
        }

        if (released)
        {
            _logger.Info(LogCategory.Scheduling, $"释放源预约：任务 {Short(taskId)}");
        }

        return released;
    }
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/AutomatedLine.Tests/AutomatedLine.Tests.csproj --nologo -v q --filter ResourceReservationManagerTests`
Expected: PASS（4 + 3 = 7 个）

- [ ] **Step 5: 全量构建 + 全量测试**

Run: `dotnet build AutomatedLine.sln -c Debug --nologo -v q`
Expected: 0 error / 0 warning

Run: `dotnet test AutomatedLine.sln -c Debug --nologo -v q`
Expected: PASS（17 + 1 + 3 + 3 + 3 + 3 = 30 通过）

- [ ] **Step 6: 提交**

```bash
git add src/AutomatedLine.Application/Reservations/ResourceReservationManager.cs tests/AutomatedLine.Tests/ResourceReservationManagerTests.cs
git commit -m "feat(reservation): 预约支持 ReleaseSource（到达目标后只释放源设备）"
```

---

### Task 6: 文档同步

**Files:**
- Modify: `Docs/数据字典.md`（DeviceConfig 增加 PositionY/PositionZ）
- Modify: `Docs/架构设计文档.md`（§14.2 表字段）

- [ ] **Step 1: 数据字典补 Y/Z 字段**

在 `Docs/数据字典.md` 的 `DeviceConfig` 表里，`PositionX` 行后补 `PositionY`、`PositionZ`（类型 double，说明"物理 Y/Z 绝对坐标"）。

- [ ] **Step 2: 架构文档 §14.2 同步**

在 `Docs/架构设计文档.md` §14.2 的 DeviceConfig 字段说明里补 PositionY/PositionZ。

- [ ] **Step 3: 提交**

```bash
git add Docs/数据字典.md Docs/架构设计文档.md
git commit -m "docs: DeviceConfig 增加 PositionY/PositionZ 字段说明"
```

---

## 完成标准（第 1 阶段）

- `dotnet build AutomatedLine.sln -c Debug` 0 错 0 警；`dotnet test` 全绿（≥30）。
- `CraneCoordinate` / `LatheYOffset` / `RightOfWayManager.Resize` / `ResourceReservationManager.ReleaseSource` 均有测试覆盖。
- `DeviceConfig` 实体 / DTO / Store / 加列脚本均含 X/Y/Z；状态机侧通过 `IConfigurationStore` 现查（第 2 阶段使用）。
- 第 2 阶段可直接基于这些产出实现流程，无需再改第 1 阶段代码。
