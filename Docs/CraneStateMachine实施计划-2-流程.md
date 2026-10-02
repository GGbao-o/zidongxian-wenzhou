# 天车状态机实施计划（第 2 阶段：流程与执行）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 落地线性 async 搬运流程：`TransferContext` 原语、按设备类型的 `ITransferHandler`（6 个）、编排器 `RunFlowAsync`、`TaskAbortCoordinator.RollbackToSource`、每车独立 `CraneWorker`；用打桩驱动做流程测试。

**Architecture:** 编排器（`CraneStateMachine`）只做通用骨架（acquire → pick → transit → place → finish）；取放料各自按设备类型交给 handler 的一大串线性代码。所有设备交互经 `IDeviceDriver`/`ICraneDriver`，测试用 fake。

**Tech Stack:** .NET 8、xUnit、CommunityToolkit（无 UI 依赖）、Generic Host DI。

**依赖：** 第 1 阶段产出（`CraneCoordinate`/`LatheYOffset`/`Resize`/`ReleaseSource`/`DeviceConfigDto.YZ`）。
**参考设计：** `Docs/CraneStateMachine设计.md` §3/§5/§6/§7/§10。
**提交规范：** 按 `AGENTS.md`（`<type>(<scope>): <主题>` + `[why]/[what]/[how]`）。

---

## 文件结构（第 2 阶段）

| 文件 | 职责 |
|---|---|
| `src/AutomatedLine.Application/Transfer/TransferContext.cs`（新） | 单次流程上下文：天车/工件/驱动/坐标现查/原语/已充磁标志 |
| `src/AutomatedLine.Application/Transfer/ITransferHandler.cs`（新） | `CanHandle/PickAsync/PlaceAsync` |
| `src/AutomatedLine.Application/Transfer/TransferHandlerRegistry.cs`（新） | 按 `DeviceType` 取 handler |
| `src/AutomatedLine.Application/Transfer/Handlers/LoadRackHandler.cs`（新） | 料架取料 |
| `src/AutomatedLine.Application/Transfer/Handlers/BoringHandler.cs`（新） | 双头镗取/放料 |
| `src/AutomatedLine.Application/Transfer/Handlers/LatheHandler.cs`（新） | 斜床取/放料（Y 二次偏移） |
| `src/AutomatedLine.Application/Transfer/Handlers/GrinderHandler.cs`（新） | 研磨机取/放料 |
| `src/AutomatedLine.Application/Transfer/Handlers/MarkerHandler.cs`（新） | 打号机取/放料 |
| `src/AutomatedLine.Application/Transfer/Handlers/UnloadRackHandler.cs`（新） | 下料架放料 |
| `src/AutomatedLine.Application/Workers/CraneStateMachine.cs`（改写） | 编排器 `RunFlowAsync` |
| `src/AutomatedLine.Application/Alarms/TaskAbortCoordinator.cs`（改） | 增加 `RollbackToSource` |
| `src/AutomatedLine.Application/Workers/CraneWorker.cs`（改写） | 每车一个独立循环 |
| `src/AutomatedLine.Ui/App.xaml.cs`（改） | 注册 6 个 handler + registry |
| `tests/AutomatedLine.Tests/Fakes/FakeCraneDriver.cs`（新） | 打桩天车驱动 |
| `tests/AutomatedLine.Tests/Fakes/FakeDeviceDriver.cs`（新） | 打桩设备驱动 |
| `tests/AutomatedLine.Tests/Fakes/FakeConfigurationStore.cs`（新） | 返回设备坐标 |
| `tests/AutomatedLine.Tests/TransferFlowTests.cs`（新） | 各流程顺序断言 |

> 现有设备驱动已基本具备所需点名（见 `DeviceModbusMaps` / `LatheFocasDriver`）；本阶段不改驱动，只按点名调用。

---

### Task 1: `TransferContext` 与流程原语

**Files:**
- Create: `src/AutomatedLine.Application/Transfer/TransferContext.cs`

- [ ] **Step 1: 实现 `TransferContext`**

```csharp
using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Configuration;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Cranes;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Domain.Workpieces;

namespace AutomatedLine.Application.Transfer;

/// <summary>
/// 一次搬运流程的上下文（《CraneStateMachine设计.md》§4）。
/// 提供给 handler 的原语：坐标换算（设备坐标现查数据库）、移动、等待信号、读写设备、充放磁。
/// </summary>
public sealed class TransferContext
{
    /// <summary>驱动注册表（取天车/设备驱动）。</summary>
    private readonly DriverRegistry _drivers;

    /// <summary>配置存储（现查设备 X/Y/Z）。</summary>
    private readonly IConfigurationStore _store;

    /// <summary>日志。</summary>
    private readonly IAppLogger _logger;

    /// <summary>设备坐标缓存（本任务内只查一次）。</summary>
    private IReadOnlyDictionary<int, DeviceConfigDto>? _deviceConfigs;

    /// <summary>构造函数。</summary>
    /// <param name="crane">执行天车</param>
    /// <param name="workpiece">工件任务</param>
    /// <param name="drivers">驱动注册表</param>
    /// <param name="store">配置存储</param>
    /// <param name="options">程序参数</param>
    /// <param name="logger">日志</param>
    public TransferContext(
        Crane crane,
        WorkpieceTask workpiece,
        DriverRegistry drivers,
        IConfigurationStore store,
        AppOptions options,
        IAppLogger logger)
    {
        Crane = crane;
        Workpiece = workpiece;
        _drivers = drivers;
        _store = store;
        Options = options;
        _logger = logger;
    }

    /// <summary>执行天车。</summary>
    public Crane Crane { get; }

    /// <summary>被搬运工件。</summary>
    public WorkpieceTask Workpiece { get; }

    /// <summary>程序参数（超时等）。</summary>
    public AppOptions Options { get; }

    /// <summary>已充磁（板可能已吸上天车）——不可回滚边界。</summary>
    public bool HasPieceOnCrane { get; set; }

    /// <summary>天车驱动。</summary>
    public ICraneDriver CraneDriver
        => _drivers.GetCrane(Crane.Id) ?? throw new InvalidOperationException($"天车 {Crane.Id} 无驱动");

    /// <summary>按设备号取设备驱动。</summary>
    /// <param name="deviceId">设备号</param>
    /// <returns>设备驱动</returns>
    public IDeviceDriver DeviceDriver(int deviceId)
        => _drivers.GetDevice(deviceId) ?? throw new InvalidOperationException($"设备 {deviceId} 无驱动");

    /// <summary>取设备绝对基坐标（设备库 X/Y/Z + 本车偏移；坐标现查一次并缓存）。</summary>
    /// <param name="device">设备</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>(基坐标 X, 基坐标 Y, 基坐标 Z)</returns>
    public async Task<(double X, double Y, double Z)> BaseOfAsync(DeviceBase device, CancellationToken cancellationToken)
    {
        _deviceConfigs ??= (await _store.GetDeviceConfigsAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(d => d.Id);
        if (!_deviceConfigs.TryGetValue(device.Id, out var cfg))
        {
            throw new InvalidOperationException($"设备配置 {device.Id} 不存在");
        }

        return (
            CraneCoordinate.BaseX(cfg.PositionX, Crane),
            CraneCoordinate.BaseY(cfg.PositionY, Crane),
            CraneCoordinate.BaseZ(cfg.PositionZ, Crane));
    }

    /// <summary>绝对移动并校验成功（失败抛业务异常，走二分中止）。</summary>
    /// <param name="x">X</param>
    /// <param name="y">Y</param>
    /// <param name="z">Z</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task MoveAbsoluteAsync(int x, int y, int z, CancellationToken cancellationToken)
    {
        var result = await CraneDriver.MoveAbsoluteAsync(x, y, z, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"天车绝对移动失败：{result.Code} {result.Message}");
        }
    }

    /// <summary>相对移动并校验成功。</summary>
    /// <param name="dx">X 相对值</param>
    /// <param name="dy">Y 相对值</param>
    /// <param name="dz">Z 相对值</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task MoveRelativeAsync(int dx, int dy, int dz, CancellationToken cancellationToken)
    {
        var result = await CraneDriver.MoveRelativeAsync(dx, dy, dz, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"天车相对移动失败：{result.Code} {result.Message}");
        }
    }

    /// <summary>充磁（内部轮询 X6）并置不可回滚边界。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task MagnetOnAsync(CancellationToken cancellationToken)
    {
        var result = await CraneDriver.MagnetOnAsync(cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"充磁失败：{result.Code} {result.Message}");
        }

        HasPieceOnCrane = true;   // 指令一发出即视为已吸起，之后异常不可回滚
    }

    /// <summary>放磁（内部轮询 X7）并校验成功。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task MagnetOffAsync(CancellationToken cancellationToken)
    {
        var result = await CraneDriver.MagnetOffAsync(cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"放磁失败：{result.Code} {result.Message}");
        }
    }

    /// <summary>轮询天车状态直到满足条件；超时/报警抛异常。</summary>
    /// <param name="predicate">条件（如 dto =&gt; dto.HasPlate == true）</param>
    /// <param name="description">条件描述（日志/异常）</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task WaitCraneAsync(Func<CraneStatusDto, bool> predicate, string description, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromMilliseconds(Options.StateMachine.HandshakeTimeoutMs);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dto = await CraneDriver.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
            if (dto is not null && dto.HasAlarm)
            {
                throw new InvalidOperationException($"天车报警，等待 {description} 中止");
            }

            if (dto is not null && predicate(dto))
            {
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException($"等待天车 {description} 超时（{timeout.TotalMilliseconds}ms）");
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>轮询设备输入信号直到等于期望值；超时抛异常。</summary>
    /// <param name="device">目标设备</param>
    /// <param name="signal">点名</param>
    /// <param name="expected">期望布尔值</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task WaitDeviceAsync(DeviceBase device, string signal, bool expected, CancellationToken cancellationToken)
    {
        var driver = DeviceDriver(device.Id);
        var timeout = TimeSpan.FromMilliseconds(Options.StateMachine.HandshakeTimeoutMs);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var signals = await driver.ReadAllSignalsAsync(cancellationToken).ConfigureAwait(false);
            var value = signals.TryGetValue(signal, out var v) && v is bool b && b;
            if (value == expected)
            {
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException($"设备[{device.Name}] 等待 {signal}={expected} 超时（{timeout.TotalMilliseconds}ms）");
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>写设备点位并校验（驱动不支持时抛异常）。</summary>
    /// <param name="device">设备</param>
    /// <param name="signal">点名</param>
    /// <param name="value">值</param>
    /// <param name="cancellationToken">取消令牌</param>
    public Task WriteDeviceAsync(DeviceBase device, string signal, object? value, CancellationToken cancellationToken)
        => DeviceDriver(device.Id).WriteSignalAsync(signal, value, cancellationToken);
}
```

- [ ] **Step 2: 构建**

Run: `dotnet build src/AutomatedLine.Application/AutomatedLine.Application.csproj -c Debug --nologo -v q`
Expected: 0 error

- [ ] **Step 3: 提交**

```bash
git add src/AutomatedLine.Application/Transfer/TransferContext.cs
git commit -m "feat(transfer): 新增 TransferContext（坐标现查 + 移动/等待/读写原语）"
```

---

### Task 2: 接口、注册表、料架 handler

**Files:**
- Create: `src/AutomatedLine.Application/Transfer/ITransferHandler.cs`
- Create: `src/AutomatedLine.Application/Transfer/TransferHandlerRegistry.cs`
- Create: `src/AutomatedLine.Application/Transfer/Handlers/LoadRackHandler.cs`

- [ ] **Step 1: 接口**

```csharp
using AutomatedLine.Domain.Devices;

namespace AutomatedLine.Application.Transfer;

/// <summary>
/// 设备取放料策略：每类设备一个实现，内部是一整串线性流程。
/// 编排器只按 <see cref="DeviceType"/> 分派 Pick/Place。
/// </summary>
public interface ITransferHandler
{
    /// <summary>是否处理该设备类型。</summary>
    /// <param name="type">设备类型</param>
    /// <returns>处理返回 true</returns>
    bool CanHandle(DeviceType type);

    /// <summary>从源设备取料（含源审核、XY/Z、充磁、源特有交互）。</summary>
    /// <param name="ctx">流程上下文</param>
    /// <param name="source">源设备</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task PickAsync(TransferContext ctx, DeviceBase source, CancellationToken cancellationToken);

    /// <summary>向目标设备放料（含目标审核、参数、Z、目标特有交互、放磁）。</summary>
    /// <param name="ctx">流程上下文</param>
    /// <param name="target">目标设备</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task PlaceAsync(TransferContext ctx, DeviceBase target, CancellationToken cancellationToken);
}
```

- [ ] **Step 2: 注册表**

```csharp
using AutomatedLine.Domain.Devices;

namespace AutomatedLine.Application.Transfer;

/// <summary>
/// 按设备类型分派 <see cref="ITransferHandler"/>。
/// </summary>
public sealed class TransferHandlerRegistry
{
    /// <summary>类型 → handler。</summary>
    private readonly Dictionary<DeviceType, ITransferHandler> _map = new();

    /// <summary>构造函数：把所有 handler 按类型建索引。</summary>
    /// <param name="handlers">全部 handler</param>
    public TransferHandlerRegistry(IEnumerable<ITransferHandler> handlers)
    {
        foreach (var handler in handlers)
        {
            foreach (var type in Enum.GetValues<DeviceType>())
            {
                if (handler.CanHandle(type))
                {
                    _map[type] = handler;
                }
            }
        }
    }

    /// <summary>取某类型的 handler。</summary>
    /// <param name="type">设备类型</param>
    /// <returns>handler</returns>
    /// <exception cref="NotSupportedException">类型无 handler</exception>
    public ITransferHandler Get(DeviceType type)
        => _map.TryGetValue(type, out var handler)
            ? handler
            : throw new NotSupportedException($"设备类型 {type} 无取放料策略");
}
```

- [ ] **Step 3: `LoadRackHandler`**

```csharp
using AutomatedLine.Domain.Devices;

namespace AutomatedLine.Application.Transfer.Handlers;

/// <summary>
/// 料架（上料架）：只作源（取料），无握手输出。
/// </summary>
public sealed class LoadRackHandler : ITransferHandler
{
    /// <inheritdoc />
    public bool CanHandle(DeviceType type) => type == DeviceType.LoadRack;

    /// <inheritdoc />
    public async Task PickAsync(TransferContext ctx, DeviceBase source, CancellationToken cancellationToken)
    {
        // 审核源：上料架有板
        await ctx.WaitDeviceAsync(source, "HasMaterial", true, cancellationToken).ConfigureAwait(false);

        // 到上方 → Z 下降到取料位
        var (x, y, z) = await ctx.BaseOfAsync(source, cancellationToken).ConfigureAwait(false);
        await ctx.MoveAbsoluteAsync((int)Math.Round(x), (int)Math.Round(y), ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);
        var zTarget = (int)Math.Round(z - CraneCoordinate.ZDrop(source.Type, ctx.Workpiece.Info.Diameter));
        await ctx.MoveAbsoluteAsync((int)Math.Round(x), (int)Math.Round(y), zTarget, cancellationToken).ConfigureAwait(false);

        // 有板 → 充磁（边界）→ Z 回安全
        await ctx.WaitCraneAsync(dto => dto.HasPlate == true, "X11 有板", cancellationToken).ConfigureAwait(false);
        await ctx.MagnetOnAsync(cancellationToken).ConfigureAwait(false);
        await ctx.MoveAbsoluteAsync((int)Math.Round(x), (int)Math.Round(y), ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task PlaceAsync(TransferContext ctx, DeviceBase target, CancellationToken cancellationToken)
        => throw new NotSupportedException("上料架不作为放料目标");
}
```

- [ ] **Step 4: 构建**

Run: `dotnet build src/AutomatedLine.Application/AutomatedLine.Application.csproj -c Debug --nologo -v q`
Expected: 0 error

- [ ] **Step 5: 提交**

```bash
git add src/AutomatedLine.Application/Transfer/ITransferHandler.cs \
        src/AutomatedLine.Application/Transfer/TransferHandlerRegistry.cs \
        src/AutomatedLine.Application/Transfer/Handlers/LoadRackHandler.cs
git commit -m "feat(transfer): 新增 ITransferHandler / Registry / 料架取料"
```

---

### Task 3: 双头镗 / 研磨机 handler

**Files:**
- Create: `src/AutomatedLine.Application/Transfer/Handlers/BoringHandler.cs`
- Create: `src/AutomatedLine.Application/Transfer/Handlers/GrinderHandler.cs`

- [ ] **Step 1: `BoringHandler`**

```csharp
using AutomatedLine.Domain.Devices;

namespace AutomatedLine.Application.Transfer.Handlers;

/// <summary>
/// 双头镗：取料（请求下料→有板→充磁→Z升→下料完成）；
/// 放料（请求数据→下发参数+数据下发完成→请求上料→Z降→放磁→Z升→上料完成）。
/// </summary>
public sealed class BoringHandler : ITransferHandler
{
    /// <inheritdoc />
    public bool CanHandle(DeviceType type) => type == DeviceType.BoringMachine;

    /// <inheritdoc />
    public async Task PickAsync(TransferContext ctx, DeviceBase source, CancellationToken cancellationToken)
    {
        await ctx.WaitDeviceAsync(source, "RequestUnload", true, cancellationToken).ConfigureAwait(false);

        var (x, y, z) = await ctx.BaseOfAsync(source, cancellationToken).ConfigureAwait(false);
        await ctx.MoveAbsoluteAsync((int)Math.Round(x), (int)Math.Round(y), ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);
        var zTarget = (int)Math.Round(z - CraneCoordinate.ZDrop(source.Type, ctx.Workpiece.Info.Diameter));
        await ctx.MoveAbsoluteAsync((int)Math.Round(x), (int)Math.Round(y), zTarget, cancellationToken).ConfigureAwait(false);

        await ctx.WaitCraneAsync(dto => dto.HasPlate == true, "X11 有板", cancellationToken).ConfigureAwait(false);
        await ctx.MagnetOnAsync(cancellationToken).ConfigureAwait(false);

        // 源特有：Z 升 → 下料完成
        await ctx.MoveAbsoluteAsync((int)Math.Round(x), (int)Math.Round(y), ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(source, "UnloadDone", 1, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task PlaceAsync(TransferContext ctx, DeviceBase target, CancellationToken cancellationToken)
    {
        await ctx.WaitDeviceAsync(target, "RequestData", true, cancellationToken).ConfigureAwait(false);
        await WriteParamsAsync(ctx, target, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "DataSentDone", 1, cancellationToken).ConfigureAwait(false);
        await ctx.WaitDeviceAsync(target, "RequestLoad", true, cancellationToken).ConfigureAwait(false);

        var (x, y, z) = await ctx.BaseOfAsync(target, cancellationToken).ConfigureAwait(false);
        var zTarget = (int)Math.Round(z - CraneCoordinate.ZDrop(target.Type, ctx.Workpiece.Info.Diameter));
        await ctx.MoveAbsoluteAsync((int)Math.Round(x), (int)Math.Round(y), zTarget, cancellationToken).ConfigureAwait(false);

        await ctx.MagnetOffAsync(cancellationToken).ConfigureAwait(false);
        await ctx.MoveAbsoluteAsync((int)Math.Round(x), (int)Math.Round(y), ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "LoadDone", 1, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>双头镗加工参数（长度原值；其余驱动内 ×100）。</summary>
    private static async Task WriteParamsAsync(TransferContext ctx, DeviceBase target, CancellationToken cancellationToken)
    {
        var info = ctx.Workpiece.Info;
        await ctx.WriteDeviceAsync(target, "Length", info.Length, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "Diameter", info.Diameter, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "LeftPlugThickness", info.LeftPlugThickness, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "RightPlugThickness", info.RightPlugThickness, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "InnerHoleTaper", info.InnerHoleTaper, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "BorePlug", info.PlugHole, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "FilletSize", info.FilletSize, cancellationToken).ConfigureAwait(false);
    }
}
```

- [ ] **Step 2: `GrinderHandler`**

```csharp
using AutomatedLine.Domain.Devices;

namespace AutomatedLine.Application.Transfer.Handlers;

/// <summary>
/// 新代研磨机：取料（请求下料→有板→充磁→下料到达松开位→松开完成退出→Z升→下料完成）；
/// 放料（请求数据→下发参数+传输完成→请求上料→Z降→到达锁紧位→锁紧完成退出→放磁→Z升→上料完成）。
/// </summary>
public sealed class GrinderHandler : ITransferHandler
{
    /// <inheritdoc />
    public bool CanHandle(DeviceType type) => type == DeviceType.GrinderMachine;

    /// <inheritdoc />
    public async Task PickAsync(TransferContext ctx, DeviceBase source, CancellationToken cancellationToken)
    {
        await ctx.WaitDeviceAsync(source, "RequestUnload", true, cancellationToken).ConfigureAwait(false);

        var (x, y, z) = await ctx.BaseOfAsync(source, cancellationToken).ConfigureAwait(false);
        await ctx.MoveAbsoluteAsync((int)Math.Round(x), (int)Math.Round(y), ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);
        var zTarget = (int)Math.Round(z - CraneCoordinate.ZDrop(source.Type, ctx.Workpiece.Info.Diameter));
        await ctx.MoveAbsoluteAsync((int)Math.Round(x), (int)Math.Round(y), zTarget, cancellationToken).ConfigureAwait(false);

        await ctx.WaitCraneAsync(dto => dto.HasPlate == true, "X11 有板", cancellationToken).ConfigureAwait(false);
        await ctx.MagnetOnAsync(cancellationToken).ConfigureAwait(false);

        // 源特有：下料到达松开位置 → 松开完成退出 → Z 升 → 下料完成
        await ctx.WriteDeviceAsync(source, "UnloadInPlace", 1, cancellationToken).ConfigureAwait(false);
        await ctx.WaitDeviceAsync(source, "UnloadReleaseDone", true, cancellationToken).ConfigureAwait(false);
        await ctx.MoveAbsoluteAsync((int)Math.Round(x), (int)Math.Round(y), ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(source, "UnloadDone", 1, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task PlaceAsync(TransferContext ctx, DeviceBase target, CancellationToken cancellationToken)
    {
        await ctx.WaitDeviceAsync(target, "RequestData", true, cancellationToken).ConfigureAwait(false);

        var info = ctx.Workpiece.Info;
        await ctx.WriteDeviceAsync(target, "RollerDiameter", info.Diameter, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "RollerLength", info.Length, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "MachiningMode", info.PlugHole, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "DataTransDone", 1, cancellationToken).ConfigureAwait(false);
        await ctx.WaitDeviceAsync(target, "RequestLoad", true, cancellationToken).ConfigureAwait(false);

        var (x, y, z) = await ctx.BaseOfAsync(target, cancellationToken).ConfigureAwait(false);
        var zTarget = (int)Math.Round(z - CraneCoordinate.ZDrop(target.Type, info.Diameter));
        await ctx.MoveAbsoluteAsync((int)Math.Round(x), (int)Math.Round(y), zTarget, cancellationToken).ConfigureAwait(false);

        // 目标特有：到达锁紧位置 → 锁紧完成退出 → 放磁 → Z 升 → 上料完成
        await ctx.WriteDeviceAsync(target, "LoadInPlace", 1, cancellationToken).ConfigureAwait(false);
        await ctx.WaitDeviceAsync(target, "LoadLockDone", true, cancellationToken).ConfigureAwait(false);
        await ctx.MagnetOffAsync(cancellationToken).ConfigureAwait(false);
        await ctx.MoveAbsoluteAsync((int)Math.Round(x), (int)Math.Round(y), ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "LoadDone", 1, cancellationToken).ConfigureAwait(false);
    }
}
```

- [ ] **Step 3: 构建**

Run: `dotnet build src/AutomatedLine.Application/AutomatedLine.Application.csproj -c Debug --nologo -v q`
Expected: 0 error

- [ ] **Step 4: 提交**

```bash
git add src/AutomatedLine.Application/Transfer/Handlers/BoringHandler.cs \
        src/AutomatedLine.Application/Transfer/Handlers/GrinderHandler.cs
git commit -m "feat(transfer): 双头镗/研磨机取放料 handler"
```

---

### Task 4: 斜床 / 打号机 / 下料架 handler

**Files:**
- Create: `src/AutomatedLine.Application/Transfer/Handlers/LatheHandler.cs`
- Create: `src/AutomatedLine.Application/Transfer/Handlers/MarkerHandler.cs`
- Create: `src/AutomatedLine.Application/Transfer/Handlers/UnloadRackHandler.cs`

- [ ] **Step 1: `LatheHandler`（含 Y 二次偏移）**

```csharp
using AutomatedLine.Domain.Devices;

namespace AutomatedLine.Application.Transfer.Handlers;

/// <summary>
/// 沈阳斜床：取/放料均含 Y 二次偏移（每台一份参数，见《CraneStateMachine设计.md》§9.1）。
/// </summary>
public sealed class LatheHandler : ITransferHandler
{
    /// <inheritdoc />
    public bool CanHandle(DeviceType type) => type == DeviceType.LatheMachine;

    /// <inheritdoc />
    public async Task PickAsync(TransferContext ctx, DeviceBase source, CancellationToken cancellationToken)
    {
        await ctx.WaitDeviceAsync(source, "RequestUnload", true, cancellationToken).ConfigureAwait(false);

        var (x, y, z) = await ctx.BaseOfAsync(source, cancellationToken).ConfigureAwait(false);
        var yOffset = OffsetOf(ctx, source.Id);
        var pickY = (int)Math.Round(y) - yOffset;   // 二次偏移位置

        await ctx.MoveAbsoluteAsync((int)Math.Round(x), pickY, ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);
        var zTarget = (int)Math.Round(z - CraneCoordinate.ZDrop(source.Type, ctx.Workpiece.Info.Diameter));
        await ctx.MoveAbsoluteAsync((int)Math.Round(x), pickY, zTarget, cancellationToken).ConfigureAwait(false);

        await ctx.WaitCraneAsync(dto => dto.HasPlate == true, "X11 有板", cancellationToken).ConfigureAwait(false);
        await ctx.MagnetOnAsync(cancellationToken).ConfigureAwait(false);

        // 源特有：下料到达 → 尾座张开 → Y 回库位 → Z 升 → 下料完成
        await ctx.WriteDeviceAsync(source, "CraneUnloadInPlace", 1, cancellationToken).ConfigureAwait(false);
        await ctx.WaitDeviceAsync(source, "TailstockOpened", true, cancellationToken).ConfigureAwait(false);
        await ctx.MoveRelativeAsync(0, yOffset, 0, cancellationToken).ConfigureAwait(false);
        await ctx.MoveAbsoluteAsync((int)Math.Round(x), (int)Math.Round(y), ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(source, "CraneUnloadDone", 1, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task PlaceAsync(TransferContext ctx, DeviceBase target, CancellationToken cancellationToken)
    {
        await ctx.WaitDeviceAsync(target, "RequestData", true, cancellationToken).ConfigureAwait(false);

        var info = ctx.Workpiece.Info;
        await ctx.WriteDeviceAsync(target, "RollerLength", info.Length, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "RollerDiameter", info.Diameter, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "BorePlug", info.PlugHole, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "MachiningMode", 0, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "DataSentDone", 1, cancellationToken).ConfigureAwait(false);
        await ctx.WaitDeviceAsync(target, "RequestLoad", true, cancellationToken).ConfigureAwait(false);

        var (x, y, z) = await ctx.BaseOfAsync(target, cancellationToken).ConfigureAwait(false);
        var yOffset = OffsetOf(ctx, target.Id);

        var zTarget = (int)Math.Round(z - CraneCoordinate.ZDrop(target.Type, info.Diameter));
        await ctx.MoveAbsoluteAsync((int)Math.Round(x), (int)Math.Round(y), zTarget, cancellationToken).ConfigureAwait(false);

        // 目标特有：Y 二次偏移 → 尾座夹紧
        await ctx.MoveRelativeAsync(0, -yOffset, 0, cancellationToken).ConfigureAwait(false);
        await ctx.WaitDeviceAsync(target, "TailstockClamped", true, cancellationToken).ConfigureAwait(false);
        await ctx.MagnetOffAsync(cancellationToken).ConfigureAwait(false);
        await ctx.MoveAbsoluteAsync((int)Math.Round(x), (int)Math.Round(y), ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "CraneLoadDone", 1, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>取该斜床的 Y 偏移量（按设备号，缺省用 0）。</summary>
    private static int OffsetOf(TransferContext ctx, int latheId)
    {
        var info = ctx.Workpiece.Info;
        if (!ctx.Options.Transfer.Lathes.TryGetValue(latheId, out var opt))
        {
            return 0;
        }

        return LatheYOffset.Compute(opt, info.PlugHole, info.Length);
    }
}
```

- [ ] **Step 2: `MarkerHandler`**

```csharp
using AutomatedLine.Domain.Devices;

namespace AutomatedLine.Application.Transfer.Handlers;

/// <summary>
/// 打号机：取料（加工完成→有板→充磁→Z升→下料完成）；放料（空闲→下发打印内容→Z降→放磁→Z升→上料完成）。
/// </summary>
public sealed class MarkerHandler : ITransferHandler
{
    /// <inheritdoc />
    public bool CanHandle(DeviceType type) => type == DeviceType.MarkerMachine;

    /// <inheritdoc />
    public async Task PickAsync(TransferContext ctx, DeviceBase source, CancellationToken cancellationToken)
    {
        await ctx.WaitDeviceAsync(source, "ProcessDone", true, cancellationToken).ConfigureAwait(false);

        var (x, y, z) = await ctx.BaseOfAsync(source, cancellationToken).ConfigureAwait(false);
        await ctx.MoveAbsoluteAsync((int)Math.Round(x), (int)Math.Round(y), ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);
        var zTarget = (int)Math.Round(z - CraneCoordinate.ZDrop(source.Type, ctx.Workpiece.Info.Diameter));
        await ctx.MoveAbsoluteAsync((int)Math.Round(x), (int)Math.Round(y), zTarget, cancellationToken).ConfigureAwait(false);

        await ctx.WaitCraneAsync(dto => dto.HasPlate == true, "X11 有板", cancellationToken).ConfigureAwait(false);
        await ctx.MagnetOnAsync(cancellationToken).ConfigureAwait(false);

        // 源特有：Z 升 → 下料完成
        await ctx.MoveAbsoluteAsync((int)Math.Round(x), (int)Math.Round(y), ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(source, "UnloadDone", 1, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task PlaceAsync(TransferContext ctx, DeviceBase target, CancellationToken cancellationToken)
    {
        await ctx.WaitDeviceAsync(target, "Idle", true, cancellationToken).ConfigureAwait(false);

        // 下发打印内容（FC16）与直径
        var info = ctx.Workpiece.Info;
        await ctx.WriteDeviceAsync(target, "PrintContent", info.PrintContent, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "RollerOuterDiameter", info.Diameter, cancellationToken).ConfigureAwait(false);

        var (x, y, z) = await ctx.BaseOfAsync(target, cancellationToken).ConfigureAwait(false);
        var zTarget = (int)Math.Round(z - CraneCoordinate.ZDrop(target.Type, info.Diameter));
        await ctx.MoveAbsoluteAsync((int)Math.Round(x), (int)Math.Round(y), zTarget, cancellationToken).ConfigureAwait(false);

        await ctx.MagnetOffAsync(cancellationToken).ConfigureAwait(false);
        await ctx.MoveAbsoluteAsync((int)Math.Round(x), (int)Math.Round(y), ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "LoadDone", 1, cancellationToken).ConfigureAwait(false);
    }
}
```

- [ ] **Step 3: `UnloadRackHandler`**

```csharp
using AutomatedLine.Domain.Devices;

namespace AutomatedLine.Application.Transfer.Handlers;

/// <summary>
/// 下料架：只作目标（放料）。无参数、无请求上料、无"上料完成"，放料完成脉冲现场暂不需要。
/// </summary>
public sealed class UnloadRackHandler : ITransferHandler
{
    /// <inheritdoc />
    public bool CanHandle(DeviceType type) => type == DeviceType.UnloadRack;

    /// <inheritdoc />
    public Task PickAsync(TransferContext ctx, DeviceBase source, CancellationToken cancellationToken)
        => throw new NotSupportedException("下料架不作为取料源");

    /// <inheritdoc />
    public async Task PlaceAsync(TransferContext ctx, DeviceBase target, CancellationToken cancellationToken)
    {
        // 审核目标：无板
        await ctx.WaitDeviceAsync(target, "HasMaterial", false, cancellationToken).ConfigureAwait(false);

        var (x, y, z) = await ctx.BaseOfAsync(target, cancellationToken).ConfigureAwait(false);
        var zTarget = (int)Math.Round(z - CraneCoordinate.ZDrop(target.Type, ctx.Workpiece.Info.Diameter));
        await ctx.MoveAbsoluteAsync((int)Math.Round(x), (int)Math.Round(y), zTarget, cancellationToken).ConfigureAwait(false);

        await ctx.MagnetOffAsync(cancellationToken).ConfigureAwait(false);
        await ctx.MoveAbsoluteAsync((int)Math.Round(x), (int)Math.Round(y), ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);
    }
}
```

- [ ] **Step 4: 构建**

Run: `dotnet build src/AutomatedLine.Application/AutomatedLine.Application.csproj -c Debug --nologo -v q`
Expected: 0 error

- [ ] **Step 5: 提交**

```bash
git add src/AutomatedLine.Application/Transfer/Handlers/LatheHandler.cs \
        src/AutomatedLine.Application/Transfer/Handlers/MarkerHandler.cs \
        src/AutomatedLine.Application/Transfer/Handlers/UnloadRackHandler.cs
git commit -m "feat(transfer): 斜床(Y 二次偏移)/打号机/下料架 handler"
```

---

### Task 5: 打桩驱动与流程测试

**Files:**
- Create: `tests/AutomatedLine.Tests/Fakes/FakeCraneDriver.cs`
- Create: `tests/AutomatedLine.Tests/Fakes/FakeDeviceDriver.cs`
- Create: `tests/AutomatedLine.Tests/Fakes/FakeConfigurationStore.cs`
- Create: `tests/AutomatedLine.Tests/TransferFlowTests.cs`

- [ ] **Step 1: `FakeCraneDriver`**

```csharp
using AutomatedLine.Application.Abstractions;

namespace AutomatedLine.Tests.Fakes;

/// <summary>打桩天车驱动：记录动作顺序，信号由测试脚本化。</summary>
public sealed class FakeCraneDriver : ICraneDriver
{
    /// <summary>动作记录（如 "MOVE(1000,0,500)"、"MAGNET_ON"）。</summary>
    public List<string> Actions { get; } = new();

    /// <summary>当前状态（测试可改）。</summary>
    public CraneStatusDto? Status { get; set; } = new() { HasPlate = true };

    /// <inheritdoc />
    public int CraneId => 1;
    /// <inheritdoc />
    public bool IsConnected => true;
    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
    /// <inheritdoc />
    public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, object?>> ReadAllSignalsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
    /// <inheritdoc />
    public Task WriteSignalAsync(string pointName, object? value, CancellationToken ct = default) => Task.CompletedTask;
    /// <inheritdoc />
    public Task<CraneStatusDto?> ReadStatusAsync(CancellationToken ct = default) => Task.FromResult(Status);
    /// <inheritdoc />
    public Task<CraneCommandResult> MoveAbsoluteAsync(int x, int y, int z, CancellationToken ct = default)
    {
        Actions.Add($"MOVE({x},{y},{z})");
        return Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.InPosition, "ok"));
    }
    /// <inheritdoc />
    public Task<CraneCommandResult> MoveRelativeAsync(int dx, int dy, int dz, CancellationToken ct = default)
    {
        Actions.Add($"REL({dx},{dy},{dz})");
        return Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.InPosition, "ok"));
    }
    /// <inheritdoc />
    public Task<CraneCommandResult> MagnetOnAsync(CancellationToken ct = default)
    {
        Actions.Add("MAGNET_ON");
        return Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.Completed, "ok"));
    }
    /// <inheritdoc />
    public Task<CraneCommandResult> MagnetOffAsync(CancellationToken ct = default)
    {
        Actions.Add("MAGNET_OFF");
        return Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.Completed, "ok"));
    }
    /// <inheritdoc />
    public Task<CraneCommandResult> MoveAsync(int writeX, int writeY, int writeZ, int targetX, int targetY, int targetZ, bool pollEncoder, int zDirection, CancellationToken ct = default)
        => Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.InPosition, "ok"));
    /// <inheritdoc />
    public Task<CraneCommandResult> JogAsync(char axis, int distance, bool positive, CancellationToken ct = default)
        => Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.Completed, "ok"));
    /// <inheritdoc />
    public Task<CraneCommandResult> HomeAsync(char axis, CancellationToken ct = default)
        => Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.Completed, "ok"));
    /// <inheritdoc />
    public Task<CraneCommandResult> SetAbsoluteSpeedAsync(int speedX, int accelX, int decelX, int speedY, int accelY, int decelY, int speedZ, int accelZ, int decelZ, CancellationToken ct = default)
        => Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.Completed, "ok"));
    /// <inheritdoc />
    public Task<CraneCommandResult> DrainOpenAsync(CancellationToken ct = default)
        => Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.Completed, "ok"));
    /// <inheritdoc />
    public Task<CraneCommandResult> DrainCloseAsync(CancellationToken ct = default)
        => Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.Completed, "ok"));
    /// <inheritdoc />
    public Task<CraneCommandResult> ServoOnAsync(CancellationToken ct = default)
        => Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.Completed, "ok"));
    /// <inheritdoc />
    public Task<CraneCommandResult> ServoOffAsync(CancellationToken ct = default)
        => Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.Completed, "ok"));
    /// <inheritdoc />
    public Task<CraneCommandResult> SlowStopAsync(CancellationToken ct = default)
        => Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.Completed, "ok"));
    /// <inheritdoc />
    public Task<CraneCommandResult> EmergencyStopAsync(CancellationToken ct = default)
        => Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.Completed, "ok"));
    /// <inheritdoc />
    public Task<CraneCommandResult> ClearAlarmAsync(CancellationToken ct = default)
        => Task.FromResult(CraneCommandResult.Ok(CraneCommandCode.Completed, "ok"));
}
```

- [ ] **Step 2: `FakeDeviceDriver`**

```csharp
using AutomatedLine.Application.Abstractions;

namespace AutomatedLine.Tests.Fakes;

/// <summary>打桩设备驱动：内部信号字典，读/写均记账。</summary>
public sealed class FakeDeviceDriver : IDeviceDriver
{
    /// <summary>信号值（测试可预置/改）。</summary>
    public Dictionary<string, object?> Signals { get; } = new();

    /// <summary>写入记录。</summary>
    public List<string> Writes { get; } = new();

    /// <summary>构造函数。</summary>
    /// <param name="deviceId">设备号</param>
    public FakeDeviceDriver(int deviceId) => DeviceId = deviceId;

    /// <inheritdoc />
    public int DeviceId { get; }
    /// <inheritdoc />
    public bool IsConnected => true;
    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
    /// <inheritdoc />
    public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, object?>> ReadAllSignalsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>(Signals));
    /// <inheritdoc />
    public Task WriteSignalAsync(string pointName, object? value, CancellationToken ct = default)
    {
        Writes.Add($"{pointName}={value}");
        Signals[pointName] = value;
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 3: `FakeConfigurationStore`**

```csharp
using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Configuration;

namespace AutomatedLine.Tests.Fakes;

/// <summary>打桩配置存储：只回答设备坐标。</summary>
public sealed class FakeConfigurationStore : IConfigurationStore
{
    /// <summary>设备坐标（设备号 → DTO）。</summary>
    public List<DeviceConfigDto> Devices { get; } = new();

    /// <inheritdoc />
    public Task<IReadOnlyList<DeviceConfigDto>> GetDeviceConfigsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DeviceConfigDto>>(Devices);
    /// <inheritdoc />
    public Task<IReadOnlyList<CraneConfigDto>> GetCraneConfigsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CraneConfigDto>>(Array.Empty<CraneConfigDto>());
    /// <inheritdoc />
    public Task SaveCraneConfigAsync(CraneConfigDto dto, CancellationToken ct = default) => Task.CompletedTask;
    /// <inheritdoc />
    public Task DeleteCraneConfigAsync(int id, CancellationToken ct = default) => Task.CompletedTask;
    /// <inheritdoc />
    public Task SaveDeviceConfigAsync(DeviceConfigDto dto, CancellationToken ct = default) => Task.CompletedTask;
    /// <inheritdoc />
    public Task DeleteDeviceConfigAsync(int id, CancellationToken ct = default) => Task.CompletedTask;
}
```

- [ ] **Step 4: 流程测试（料架→双头镗 顺序断言）**

创建 `tests/AutomatedLine.Tests/TransferFlowTests.cs`：

```csharp
using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Configuration;
using AutomatedLine.Application.Transfer;
using AutomatedLine.Application.Transfer.Handlers;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Cranes;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Domain.Workpieces;
using AutomatedLine.Tests.Fakes;

namespace AutomatedLine.Tests;

/// <summary>取放料流程顺序测试（打桩驱动）。</summary>
public class TransferFlowTests
{
    private static TransferContext BuildContext(
        out FakeCraneDriver craneDriver,
        out FakeDeviceDriver sourceDriver,
        out FakeDeviceDriver targetDriver,
        DeviceBase source,
        DeviceBase target,
        WorkpieceTask wp)
    {
        var crane = new Crane(1, 1, "天车1");
        var registry = new DriverRegistry();
        craneDriver = new FakeCraneDriver();
        sourceDriver = new FakeDeviceDriver(source.Id);
        targetDriver = new FakeDeviceDriver(target.Id);
        registry.RegisterCrane(craneDriver);
        registry.RegisterDevice(sourceDriver);
        registry.RegisterDevice(targetDriver);

        var store = new FakeConfigurationStore();
        store.Devices.Add(new DeviceConfigDto { Id = source.Id, PositionX = 1000, PositionY = 0, PositionZ = 500 });
        store.Devices.Add(new DeviceConfigDto { Id = target.Id, PositionX = 2000, PositionY = 0, PositionZ = 500 });

        return new TransferContext(crane, wp, registry, store, new AppOptions(), new TestLogger());
    }

    /// <summary>料架取料：审核有板 → XY → Z 降 → 有板 → 充磁 → Z 升。</summary>
    [Fact]
    public async Task LoadRack_Pick_Sequence()
    {
        var source = new LoadRack(1, "上料架", 1000);
        var target = new BoringMachine(2, "双头镗", 2000);
        var wp = new WorkpieceTask(new WorkpieceInfo { Diameter = 100, Length = 700 });
        var ctx = BuildContext(out var craneDriver, out var sourceDriver, out _, source, target, wp);
        sourceDriver.Signals["HasMaterial"] = true;

        await new LoadRackHandler().PickAsync(ctx, source, CancellationToken.None);

        Assert.Equal(new[] { "MOVE(1000,0,500)", "MOVE(1000,0,450)", "MAGNET_ON", "MOVE(1000,0,500)" }, craneDriver.Actions);
        Assert.True(ctx.HasPieceOnCrane);
    }
}
```

> 注：`WorkpieceInfo` 的 `Length`/`Diameter` 是 `init` 属性；`BoringMachine(2, "双头镗", 2000)` 的第三参是 X。`AppOptions` 的 `Crane.SafeZ` 在 `Crane` 上（测试里 `new Crane` 的 SafeZ 默认 0，所以上面断言里的 Z 用 0 的话应为 `MOVE(1000,0,0)`）。**修正**：测试里设置 `crane.SafeZ = 500;`（在 `BuildContext` 内），并据此断言 `MOVE(1000,0,500)` 与下降 Z=`500 - Round(100/2)=450`。

- [ ] **Step 5: 修正 `BuildContext` 设 SafeZ，运行测试**

在 `BuildContext` 里 `var crane = new Crane(1, 1, "天车1");` 后补 `crane.SafeZ = 500;`。

Run: `dotnet test tests/AutomatedLine.Tests/AutomatedLine.Tests.csproj --nologo -v q --filter TransferFlowTests`
Expected: PASS（1 个）

- [ ] **Step 6: 追加 Boring→Lathe 放料特有断言**

在 `TransferFlowTests` 追加：

```csharp
    /// <summary>双头镗放料：请求数据 → 参数 → 请求上料 → Z 降 → 放磁 → Z 升 → 上料完成。</summary>
    [Fact]
    public async Task Boring_Place_Writes_Params_And_Done()
    {
        var source = new LoadRack(1, "上料架", 1000);
        var target = new BoringMachine(2, "双头镗", 2000);
        var wp = new WorkpieceTask(new WorkpieceInfo { Diameter = 100, Length = 700 });
        var ctx = BuildContext(out var craneDriver, out _, out var targetDriver, source, target, wp);
        targetDriver.Signals["RequestData"] = true;
        targetDriver.Signals["RequestLoad"] = true;

        await new BoringHandler().PlaceAsync(ctx, target, CancellationToken.None);

        Assert.Contains("DataSentDone=1", targetDriver.Writes);
        Assert.Contains("LoadDone=1", targetDriver.Writes);
        Assert.Equal("MAGNET_OFF", craneDriver.Actions[^2]);
    }
```

Run: `dotnet test tests/AutomatedLine.Tests/AutomatedLine.Tests.csproj --nologo -v q --filter TransferFlowTests`
Expected: PASS（2 个）

- [ ] **Step 7: 提交**

```bash
git add tests/AutomatedLine.Tests/Fakes tests/AutomatedLine.Tests/TransferFlowTests.cs
git commit -m "test(transfer): 打桩驱动与取放料流程顺序测试"
```

---

### Task 6: 编排器 `RunFlowAsync` 与回滚

**Files:**
- Modify: `src/AutomatedLine.Application/Alarms/TaskAbortCoordinator.cs`
- Rewrite: `src/AutomatedLine.Application/Workers/CraneStateMachine.cs`

- [ ] **Step 1: `TaskAbortCoordinator.RollbackToSource`**

在 `TaskAbortCoordinator` 增加：

```csharp
    /// <summary>
    /// 取料阶段异常/取消的回滚：释放路权/预约/天车锁，任务回 <see cref="Domain.Workpieces.WorkpieceState.Waiting"/>；
    /// **不清内存、不弃板、不暂停线路**（此时板仍在源设备、未写设备）。
    /// </summary>
    /// <param name="crane">天车</param>
    /// <param name="task">搬运任务</param>
    /// <param name="reason">原因（日志）</param>
    public void RollbackToSource(Crane crane, TransportTask task, string reason)
    {
        _rightOfWayManager.Release(task.TaskId);
        _reservationManager.Release(task.TaskId);

        crane.Runtime.CurrentTask = null;
        crane.Runtime.State = CraneState.Idle;
        crane.Runtime.Step = TransportStep.Idle;
        crane.Runtime.LockedInterval = null;

        var workpieceTask = _taskManager.Get(task.WorkpieceRuntimeId);
        if (workpieceTask is not null)
        {
            workpieceTask.Runtime.TaskState = Domain.Workpieces.WorkpieceState.Waiting;
        }

        _logger.Warn(LogCategory.Task, $"{crane.Name} 取料阶段回滚任务 {task.TaskId:N}：{reason}");
    }
```

（文件已 `using` 相关命名空间；若缺 `Domain.Workpieces` 可用完全限定名。）

- [ ] **Step 2: 重写 `CraneStateMachine`**

用下列内容整体替换 `src/AutomatedLine.Application/Workers/CraneStateMachine.cs`（保留构造函数注入，新增 `IConfigurationStore`、`TransferHandlerRegistry`）：

```csharp
using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Alarms;
using AutomatedLine.Application.Configuration;
using AutomatedLine.Application.Reservations;
using AutomatedLine.Application.RightOfWay;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Application.Transfer;
using AutomatedLine.Domain.Cranes;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Domain.Diagnostics;
using AutomatedLine.Domain.Spatial;
using AutomatedLine.Domain.Tasks;
using AutomatedLine.Domain.Workpieces;

namespace AutomatedLine.Application.Workers;

/// <summary>
/// 天车搬运编排器（线性 async，架构文档 §8.5 / 《CraneStateMachine设计.md》）。
/// 通用骨架：Acquire → Pick(源类型) → Transit → Place(目标类型) → Finish；
/// 取放料细节由 <see cref="ITransferHandler"/> 按设备类型实现。
/// </summary>
public sealed class CraneStateMachine
{
    private readonly DeviceRegistry _deviceRegistry;
    private readonly DriverRegistry _driverRegistry;
    private readonly IRuntimeTaskManager _taskManager;
    private readonly RightOfWayManager _rightOfWayManager;
    private readonly ResourceReservationManager _reservationManager;
    private readonly TaskAbortCoordinator _abortCoordinator;
    private readonly TransferHandlerRegistry _handlers;
    private readonly IConfigurationStore _configStore;
    private readonly IConfigService _configService;
    private readonly IAppLogger _logger;

    /// <summary>构造函数。</summary>
    public CraneStateMachine(
        DeviceRegistry deviceRegistry,
        DriverRegistry driverRegistry,
        IRuntimeTaskManager taskManager,
        RightOfWayManager rightOfWayManager,
        ResourceReservationManager reservationManager,
        TaskAbortCoordinator abortCoordinator,
        TransferHandlerRegistry handlers,
        IConfigurationStore configStore,
        IConfigService configService,
        IAppLogger logger)
    {
        _deviceRegistry = deviceRegistry;
        _driverRegistry = driverRegistry;
        _taskManager = taskManager;
        _rightOfWayManager = rightOfWayManager;
        _reservationManager = reservationManager;
        _abortCoordinator = abortCoordinator;
        _handlers = handlers;
        _configStore = configStore;
        _configService = configService;
        _logger = logger;
    }

    /// <summary>执行一次完整搬运（从取料到完成）。</summary>
    /// <param name="crane">天车</param>
    /// <param name="task">搬运任务</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task RunFlowAsync(Crane crane, TransportTask task, CancellationToken cancellationToken)
    {
        var wp = _taskManager.Get(task.WorkpieceRuntimeId)
                 ?? throw new InvalidOperationException("找不到工件任务");
        var source = _deviceRegistry.Get(task.SourceDeviceId)
                     ?? throw new InvalidOperationException($"源设备 {task.SourceDeviceId} 不存在");
        var target = _deviceRegistry.Get(task.TargetDeviceId)
                     ?? throw new InvalidOperationException($"目标设备 {task.TargetDeviceId} 不存在");

        var ctx = new TransferContext(crane, wp, _driverRegistry, _configStore, _configService.GetAppOptions(), _logger);

        // 1) 原子获取路权 + 预约；失败回滚、任务回 Waiting
        if (!Acquire(crane, task))
        {
            return;
        }

        crane.Runtime.State = CraneState.Running;
        wp.Runtime.TaskState = WorkpieceState.Running;

        try
        {
            // 2) 再审核源（内存一致；PLC 点位由 handler 首步等待）
            AuditSource(source, wp);

            // 3) 取料
            await _handlers.Get(source.Type).PickAsync(ctx, source, cancellationToken).ConfigureAwait(false);
            source.CurrentWorkpiece = null;
            crane.CurrentWorkpiece = wp;
            wp.Runtime.CurrentLocation = WorkpieceLocation.OnCrane(crane.Id);

            // 4) 中转：XY→目标、释放源预约、缩小路权
            await TransitAsync(crane, task, target, cancellationToken).ConfigureAwait(false);

            // 5) 放料
            await _handlers.Get(target.Type).PlaceAsync(ctx, target, cancellationToken).ConfigureAwait(false);
            target.CurrentWorkpiece = wp;
            crane.CurrentWorkpiece = null;
            wp.Runtime.CurrentLocation = WorkpieceLocation.AtDevice(target.Id);
            wp.Runtime.CurrentProcess = task.TargetProcess;

            // 6) 完成：回安全位、释放全部
            await FinishAsync(crane, task, wp, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            HandleAbnormal(crane, task, ctx, "任务被取消");
            throw;
        }
        catch (Exception ex)
        {
            HandleAbnormal(crane, task, ctx, ex.Message);
        }
    }

    /// <summary>原子获取路权 + 源/目标预约；失败回滚并让出天车。</summary>
    private bool Acquire(Crane crane, TransportTask task)
    {
        var interval = task.GetRightOfWayInterval(crane.Width);

        if (!_rightOfWayManager.TryAcquire(task.TaskId, interval))
        {
            Rollback(crane, task, "路权被占用");
            return false;
        }

        if (!_reservationManager.TryReserve(task.TaskId, task.SourceDeviceId, task.TargetDeviceId))
        {
            _rightOfWayManager.Release(task.TaskId);
            Rollback(crane, task, "设备预约失败");
            return false;
        }

        crane.Runtime.LockedInterval = interval;
        return true;
    }

    /// <summary>获取失败回滚（尚未开始搬运）。</summary>
    private void Rollback(Crane crane, TransportTask task, string reason)
    {
        crane.Runtime.LockedInterval = null;
        crane.Runtime.CurrentTask = null;
        crane.Runtime.State = CraneState.Idle;
        crane.Runtime.Step = TransportStep.Idle;
        var wp = _taskManager.Get(task.WorkpieceRuntimeId);
        if (wp is not null)
        {
            wp.Runtime.TaskState = WorkpieceState.Waiting;
        }

        _logger.Warn(LogCategory.StateMachine, $"{crane.Name} 获取资源失败（{reason}），回滚待重派");
    }

    /// <summary>再审核源设备内存一致（上料架队列除外）。</summary>
    private static void AuditSource(DeviceBase source, WorkpieceTask wp)
    {
        if (source.Type == DeviceType.LoadRack)
        {
            return;   // 队列工件不挂在设备内存上
        }

        if (source.CurrentWorkpiece is not null && source.CurrentWorkpiece.Id != wp.Id)
        {
            throw new InvalidOperationException($"源设备 {source.Name} 当前工件与任务不一致");
        }
    }

    /// <summary>中转：XY→目标上方、释放源预约、路权缩小到车体+目标。</summary>
    private async Task TransitAsync(Crane crane, TransportTask task, DeviceBase target, CancellationToken cancellationToken)
    {
        var (x, y, _) = await new TransferContext(crane, _taskManager.Get(task.WorkpieceRuntimeId)!, _driverRegistry, _configStore, _configService.GetAppOptions(), _logger)
            .BaseOfAsync(target, cancellationToken).ConfigureAwait(false);
        // 目标上方（Z 保持安全）
        var moveResult = await (_driverRegistry.GetCrane(crane.Id)?.MoveAbsoluteAsync((int)Math.Round(x), (int)Math.Round(y), crane.SafeZ, cancellationToken)).ConfigureAwait(false);
        if (moveResult is { IsSuccess: false })
        {
            throw new InvalidOperationException($"移动到目标失败：{moveResult.Value.Message}");
        }

        _reservationManager.ReleaseSource(task.TaskId);
        _rightOfWayManager.Resize(task.TaskId, XInterval.FromTask(target.PositionX, target.PositionX, crane.Width));
    }

    /// <summary>完成：回安全位、释放天车/路权/目标预约、改进度。</summary>
    private async Task FinishAsync(Crane crane, TransportTask task, WorkpieceTask wp, CancellationToken cancellationToken)
    {
        // 回到本车安全位 X
        var driver = _driverRegistry.GetCrane(crane.Id);
        if (driver is not null)
        {
            var status = await driver.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
            var currentY = status?.YDisplay ?? 0;
            var currentZ = status?.ZDisplay ?? (int)crane.SafeZ;
            await driver.MoveAbsoluteAsync((int)crane.SafeX, currentY, (int)crane.SafeZ, cancellationToken).ConfigureAwait(false);
        }

        _rightOfWayManager.Release(task.TaskId);
        _reservationManager.Release(task.TaskId);

        crane.Runtime.CurrentTask = null;
        crane.Runtime.State = CraneState.Idle;
        crane.Runtime.Step = TransportStep.Idle;
        crane.Runtime.LockedInterval = null;

        wp.Runtime.TaskState = task.TargetProcess == Domain.Routing.ProcessType.Unloading
            ? WorkpieceState.Completed
            : WorkpieceState.Waiting;

        _logger.Info(LogCategory.StateMachine, $"{crane.Name} 完成任务 {task.TaskId:N}");
    }

    /// <summary>异常二分：充磁前回滚、充磁后全量清理 + 暂停。</summary>
    private void HandleAbnormal(Crane crane, TransportTask task, TransferContext ctx, string reason)
    {
        if (ctx.HasPieceOnCrane)
        {
            _abortCoordinator.Abort(crane, reason);
        }
        else
        {
            _abortCoordinator.RollbackToSource(crane, task, reason);
        }
    }
}
```

> 说明：上面 `TransitAsync` 中为取坐标临时 new 了 `TransferContext`；实现时可把 `ctx` 作为参数传入 `RunFlowAsync` 后半段复用，避免重复构造（计划 Step 3 收尾时统一）。

- [ ] **Step 3: 复用 `ctx` 收尾**

把 `RunFlowAsync` 里的 `await TransitAsync(crane, task, target, cancellationToken)` 改为 `await TransitAsync(ctx, crane, task, target, cancellationToken)`，并把 `TransitAsync` 首行改为使用传入的 `ctx.BaseOfAsync(target, ...)`；去掉临时 `new TransferContext(...)`。

- [ ] **Step 4: 构建**

Run: `dotnet build src/AutomatedLine.Application/AutomatedLine.Application.csproj -c Debug --nologo -v q`
Expected: 0 error（若报 `TransferHandlerRegistry`/`IConfigurationStore` 未注册，属 DI 问题，Task 7 处理）

- [ ] **Step 5: 提交**

```bash
git add src/AutomatedLine.Application/Alarms/TaskAbortCoordinator.cs \
        src/AutomatedLine.Application/Workers/CraneStateMachine.cs
git commit -m "feat(transfer): 编排器 RunFlowAsync（acquire/transit/finish + 二分回滚）"
```

---

### Task 7: 每车独立 Worker + DI 注册

**Files:**
- Rewrite: `src/AutomatedLine.Application/Workers/CraneWorker.cs`
- Modify: `src/AutomatedLine.Ui/App.xaml.cs`

- [ ] **Step 1: 重写 `CraneWorker`**

```csharp
using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Line;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace AutomatedLine.Application.Workers;

/// <summary>
/// 天车执行 Worker：**每台启用天车一个独立后台循环**，各自推进自己的任务（真正并行）。
/// </summary>
public sealed class CraneWorker : IHostedService
{
    private readonly CraneRegistry _craneRegistry;
    private readonly CraneStateMachine _stateMachine;
    private readonly LineControlService _lineControl;
    private readonly IConfigService _configService;
    private readonly IAppLogger _logger;
    private CancellationTokenSource? _cts;
    private readonly List<Task> _loops = new();

    /// <summary>构造函数。</summary>
    public CraneWorker(
        CraneRegistry craneRegistry,
        CraneStateMachine stateMachine,
        LineControlService lineControl,
        IConfigService configService,
        IAppLogger logger)
    {
        _craneRegistry = craneRegistry;
        _stateMachine = stateMachine;
        _lineControl = lineControl;
        _configService = configService;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        foreach (var crane in _craneRegistry.Snapshot().Where(c => c.IsEnabled))
        {
            var c = crane;
            _loops.Add(Task.Run(() => RunCraneAsync(c, _cts.Token), CancellationToken.None));
        }
        Console.WriteLine($"[CraneWorker] 已启动 {_loops.Count} 个天车执行循环");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null) { return; }
        _cts.Cancel();
        foreach (var loop in _loops)
        {
            await Task.WhenAny(loop, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
        }
        _cts.Dispose();
        _cts = null;
    }

    /// <summary>单台天车的执行循环：有任务则一次跑完整条流程。</summary>
    private async Task RunCraneAsync(Domain.Cranes.Crane crane, CancellationToken cancellationToken)
    {
        var interval = Math.Max(50, _configService.GetAppOptions().StateMachine.ExecuteIntervalMs);
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_lineControl.IsRunning && crane.Runtime.CurrentTask is { } task)
            {
                try
                {
                    await _stateMachine.RunFlowAsync(crane, task, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Error(LogCategory.StateMachine, $"{crane.Name} 流程异常", ex);
                }
            }

            try { await Task.Delay(interval, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }
}
```

- [ ] **Step 2: DI 注册 handler**

在 `App.xaml.cs` 的 `ConfigureServices`，`services.AddSingleton<CraneStateMachine>();` 之前补：

```csharp
        // 取放料策略：每类设备一个 handler + 注册表
        services.AddSingleton<AutomatedLine.Application.Transfer.ITransferHandler, AutomatedLine.Application.Transfer.Handlers.LoadRackHandler>();
        services.AddSingleton<AutomatedLine.Application.Transfer.ITransferHandler, AutomatedLine.Application.Transfer.Handlers.BoringHandler>();
        services.AddSingleton<AutomatedLine.Application.Transfer.ITransferHandler, AutomatedLine.Application.Transfer.Handlers.LatheHandler>();
        services.AddSingleton<AutomatedLine.Application.Transfer.ITransferHandler, AutomatedLine.Application.Transfer.Handlers.GrinderHandler>();
        services.AddSingleton<AutomatedLine.Application.Transfer.ITransferHandler, AutomatedLine.Application.Transfer.Handlers.MarkerHandler>();
        services.AddSingleton<AutomatedLine.Application.Transfer.ITransferHandler, AutomatedLine.Application.Transfer.Handlers.UnloadRackHandler>();
        services.AddSingleton<AutomatedLine.Application.Transfer.TransferHandlerRegistry>();
```

- [ ] **Step 3: 全量构建 + 测试**

Run: `dotnet build AutomatedLine.sln -c Debug --nologo -v q`
Expected: 0 error

Run: `dotnet test AutomatedLine.sln -c Debug --nologo -v q`
Expected: PASS

- [ ] **Step 4: 提交**

```bash
git add src/AutomatedLine.Application/Workers/CraneWorker.cs src/AutomatedLine.Ui/App.xaml.cs
git commit -m "feat(worker): 每车独立执行循环 + 注册取放料 handler"
```

---

### Task 8: 清理旧步骤状态机与文档

**Files:**
- Modify: `src/AutomatedLine.Domain/Tasks/TransportStep.cs`（可选：保留枚举供展示）
- Modify: `Docs/架构设计文档.md`（§19.2/§19.3 状态更新）

- [ ] **Step 1: 处理 `TransportStep`**

`TransportStep` 不再驱动控制流；保留枚举仅供界面显示（不删）。确认 `DisplayText.TransportStepText` 仍可编译。

- [ ] **Step 2: 更新实现状态**

把 `Docs/架构设计文档.md` §19.2 中"取料流程与中止/Worker 模式/ITransferHandler"三行标记为**已实现**（v2.39），并把 §19.3 对应条目标记完成。

- [ ] **Step 3: 全量构建 + 测试 + 提交**

Run: `dotnet build AutomatedLine.sln -c Debug --nologo -v q`
Run: `dotnet test AutomatedLine.sln -c Debug --nologo -v q`

```bash
git add Docs/架构设计文档.md
git commit -m "docs: 状态机真实化落地，更新 §19 实现状态"
```

---

## 完成标准（第 2 阶段）

- 6 个 handler + 编排器 + 每车独立 Worker 全部落地；构建 0 错、测试全绿。
- 流程顺序有打桩测试覆盖（至少：料架取料、双头镗放料、斜床 Y 二次偏移、研磨机、下料架）。
- 取料阶段异常走 `RollbackToSource`（不弃板不暂停）；充磁后异常走 `Abort`（弃板+暂停）。
- 资源：到目标 `ReleaseSource` + `Resize`；完成回安全位释放其余。
