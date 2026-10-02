# 天车搬运状态机设计（线性 async 流程）

> 对应架构文档 §8.5。替代当前"模拟简化版 + `TransportStep` 枚举逐步推进"的实现。
> 目标：每类设备的取放料交互**各写死成一大串**，用线性 `async` 流程执行，可读、好维护。
>
> 本版按现场确认的三条流程（料架→双头镗、双头镗→斜床、斜床→打号机）抽象：**公共骨架 + 源特有段 + 目标特有段**。

## 1. 目标与约束

- 一次搬运 = **取料（源）→ 中转 → 放料（目标）**；各设备交互不同，**按设备类型写死**。
- 执行模型：**每台天车一个独立后台循环**，流程写成一个大 `async` 方法，逐步 `await` 到位/握手。
- 与调度器的边界不变：调度器**只判断**（`CanAcquire`/`IsReserved`）；**状态机开始执行时**才真正
  `TryAcquire`（路权）+ `TryReserve`（设备预约）；拿不到 → 回滚。
- **天车锁 = 繁忙标志**（`Crane.Runtime.CurrentTask != null` / `State != Idle`），不是独立锁对象。
- 异常二分：以**充磁（板可能已吸上天车）**为界，之前回滚、之后全量清理并暂停。
- 全程受 `LineControlService.IsRunning` 与取消令牌约束；线路暂停/急停即取消。

## 2. 执行模型（CraneWorker 改造）

**现状**：`CraneWorker` 单循环顺序推进所有天车，每 tick 一步。
**目标**：每台天车一个独立 `Task.Run` 循环（文档 §8.5 本来就要求），实现真正并行。

```
调度器：只读判断通过 → channel[crane.Id].Writer.TryWrite(task)   // 不碰 Crane.Runtime
对每台启用天车启动一个循环（Worker）：
  while (running):
    if 线路运行 and channel[crane.Id].Reader.TryRead(out task):
        crane.Runtime.CurrentTask = task; crane.Runtime.State = Reserved   // Worker 独占写本车 Runtime
        try:
            await StateMachine.RunFlowAsync(crane, task, ct)   // 从取料到完成，一次性 await 到底
        catch OperationCanceledException: throw
        catch: 由 RunFlowAsync 内部按二分处理（见 §7）
    await Task.Delay(ExecuteIntervalMs)
```

- **任务交接用每车一个 `Channel<TransportTask>`**（容量 1）：调度器只入队、**不写 `Crane.Runtime`**；Worker 出队并**独占**写本车 Runtime。从根上消除"两线程写同一对象"的交接竞态（见架构文档 §8.5.1）。
- 一个任务从头跑到尾（可能数十秒~分钟）；其它天车各自并行，互不阻塞。
- `RunFlowAsync` 每一步运动/握手都 `await` 到"到位 / 超时 / 报警"。
- `TransportStep` 枚举**不再用于逐 tick 推进**；可保留少量作"当前阶段"展示/日志（取料/中转/放料/完成）。

## 3. 流程分解

三条现场流程证明：**取料只依赖源设备类型、放料只依赖目标设备类型、中转通用**。

```
RunFlowAsync(编排，通用唯一一份)：
  AcquireResources    // 原子：路权 + 源/目标预约（+ 确认天车忙标志）；失败回滚
  AuditSource         // 再审核源（PLC 点位 + 内存工件一致）
  await PickAsync(source.Type)        // 一大串：通用取料前缀 + 【源特有段】
  await TransitAsync(target)          // 通用：XY→target、释放源预约、缩小路权
  await PlaceAsync(target.Type)       // 一大串：通用放料前缀 + 【目标特有段】 + 通用放料后缀
  Finish              // X 回安全；释放天车忙标志 + 路权 + 目标预约；Complete
```

### 3.1 公共骨架（三条流程完全一致）

| # | 步骤 | 归属 |
|---|---|---|
| 1 | 原子获取：路权 + 源/目标预约 +（确认）天车忙标志 | 编排 |
| 2 | 再审核**源**：PLC 点位 + 内存工件一致 | 通用（判定信号随源类型，见 §3.2） |
| 3 | XY → source | 通用（**斜床源 Y 用二次偏移位置**，见 §3.2） |
| 4 | Z 下降到取料位 | 通用 |
| 5 | 感应有板（`X11`） | 通用 |
| 6 | **充磁**（不可回滚边界） | 通用 |
| 7 | 源特有交互（含各自 Z 回升；见 §3.2） | **源特有** |
| 8 | XY → target | 中转 |
| 9 | **释放源设备预约 + 缩小路权到 `[targetX−w/2, targetX+w/2]`** | 中转 |
| 10 | 再审核 **target** | 通用（信号随目标类型，见 §3.3） |
| 11 | 下发工件参数 + 写"下发完成" | **目标特有**（下料架没有；内容随设备） |
| 12 | 读"请求上料=1" | **目标特有**（打号机/下料架没有） |
| 13 | Z 下降到放料位 | 通用 |
| 14 | 目标特有交互（见 §3.3） | **目标特有** |
| 15 | 放磁 | 通用 |
| 16 | Z 回升 | 通用 |
| 17 | 写"上料完成" | **目标特有**（下料架没有） |
| 18 | X 回安全 | 完成 |
| 19 | 释放 天车忙标志 + 路权 + 目标设备预约 | 完成 |
| 20 | `Complete`（工件 `TaskState`） | 完成 |

### 3.2 源设备矩阵（审核信号 + 充磁之后的特有动作，含各自 Z 时机）

| 源设备 | 审核信号 | 充磁之后（含 Z） | 步骤 3 的 approach Y |
|---|---|---|---|
| 料架 | 有板 | （无）→ Z 回升 | 库位 Y |
| 双头镗 | 请求下料 | Z 回升 → 写"下料完成=1"（`12217`） | 库位 Y |
| 斜床 | 请求下料 | 写"下料到达=1"（`#1104`）→ 读"尾座张开=1"（`#1005`）→ **Y 二次移动回库位** → Z 回升 → 写"下料完成"（`#1105`） | **库位 Y − YOffset** |
| 研磨机 | 请求下料 | 写"下料到达松开位置=1"（`14629`）→ 读"松开完成下料退出=1"（`14611`）→ Z 回升 → 写"下料完成"（`14631`） | 库位 Y |
| 打号机 | 加工完成 | Z 回升 → 写"下料完成" | 库位 Y |

> Z 回升位置因源设备而异，因此**源特有段拥有自己的 Z 动作**，不放在通用骨架里。
> 斜床作为源时，approach Y 直接取"二次偏移位置"（库位 Y − `YOffset`）。

### 3.3 目标设备矩阵（审核 / 参数 / 请求上料 / 放磁前 / 上料完成）

| 目标设备 | 审核信号 | 下发参数 | 读请求上料 | 放磁前特有动作 | 写上料完成 |
|---|---|---|---|---|---|
| 双头镗 | 请求数据 | 有 | 有（`12207`） | （无） | 有（`12209`） |
| 斜床 | 请求数据 | 有（`#800/#801/#802/#909`） | 有（`#1002`） | **Y 二次偏移** → 读"夹紧完成=1"（`#1003`） | 有（`#1103`） |
| 研磨机 | 请求数据 | 有（`14633/14635/14637`） | 有（`14605`） | 写"到达锁紧位置=1"（`14625`）→ 读"锁紧完成上料退出=1"（`14607`） | 有（`14627`） |
| 打号机 | 设备空闲 | 有（打印内容） | **无** | （无） | 有 |
| 下料架 | 无板 | **无** | **无** | （无） | **无** |

## 4. 通用子步骤与坐标换算

### 4.1 坐标换算与 Z 目标（重要）

- 设备在数据库存**绝对坐标 X/Y/Z**（现状 `DeviceConfig` 只有 `PositionX`，**需补 `PositionY`/`PositionZ`**）。
- 设备坐标**运行期现查数据库**：状态机每次任务开始时经 `IConfigurationStore.GetDeviceConfigsAsync()`（已有的 Application 抽象、Infrastructure 实现）取一次，**不缓存到 `DeviceBase`**。一轮才查一次（几行数据），对性能无影响。
- 天车有**偏移量** `OffsetX/Y/Z`（`CraneConfig`，字段已存在，但**当前未参与任何计算**，需接入）。
- 发给天车的**绝对运动目标 = 设备绝对坐标 + 本车偏移**（Z 再减去下压量）：

```
baseX = device.PositionX + crane.OffsetX
baseY = device.PositionY + crane.OffsetY
baseZ = device.PositionZ + crane.OffsetZ

// Z 下降量（到设备取料/放料时的 Z 偏移），d = 当前工件直径（WorkpieceInfo.Diameter）
zDrop = (device.Type is Lathe or Grinder)
          ? Round(d / 2)
          : Round( (d/2)/0.9537 + (d/2)/0.866 )
zTarget = baseZ - zDrop
```

- **XY 到设备上方**：`MoveAbsoluteAsync(baseX, baseY, crane.SafeZ)`（`SafeZ` 是本车字段，**不再加偏移**）。
- **Z 下降到设备**：`MoveAbsoluteAsync(baseX, baseY, zTarget)`。
- 例：设备库坐标 `(1000, 200, 2000)`、1 号车偏移 `(10, −20, 30)`、工件直径 `103.1` →
  X=`1010`，Y=`180`，Z=`2030 − Round(103.1/2) = 2030 − 52 = 1978`
  （料架/非斜床非研磨用另一条公式 `2030 − Round(51.55/0.9537 + 51.55/0.866)`）。
- 斜床的 **Y 二次偏移**是**相对移动**（不是直接写绝对坐标）：XY 到达库位后，读当前坐标，按 `YOffset`（§9.1）做相对移动；上料 `−YOffset`、下料回库位 `+YOffset`。
- 实现上集中到一个 `CraneCoordinate` 帮助类，避免每个流程手写换算。
- `Round` 用 `Math.Round`；`0.9537` / `0.866` 为常量（含义待现场确认，命名常量保存）。

> 待确认：Z 公式是否取料/放料一致；`Round` 取整方式（`.5` 进位规则）。

### 4.2 通用原语

编排与各设备流程共用（Application 层，调用 `ICraneDriver` / `IDeviceDriver`）：

| 原语 | 说明 |
|---|---|
| `MoveXYAsync(x, y)` | XY 到设备上方（Z 保持安全高度） |
| `MoveZAsync(z)` | Z 到指定位置（下压时 `ICraneDriver` 内部已含 X2 保护/容差判定） |
| `MoveXSafeAsync()` | X 回本车安全位（`Crane.SafeX`） |
| `WaitCraneAsync(dto => bool, timeout)` | 轮询天车状态（有板 `X11`、到位、报警等） |
| `ReadDeviceAsync(device)` | 读设备输入信号快照 |
| `WaitDeviceAsync(device, name, expected, timeout)` | 轮询设备某输入信号到期望值 |
| `WriteDeviceAsync(device, name, value)` | 写设备握手输出 |
| `WriteParamsTo(device, wp)` | 按设备类型下发加工参数（见 §8） |
| `MagnetOnAsync()` / `MagnetOffAsync()` | 充/退磁（`ICraneDriver` 内部已轮询 `X6`/`X7`） |

- 所有等待带超时：运动用 `Crane.MoveTimeoutMs`，握手用 `StateMachine.HandshakeTimeoutMs`/`PickTimeoutMs`/`PlaceTimeoutMs`。
- 超时/报警 → 抛业务异常，由 `RunFlowAsync` 按 §7 二分处理。
- **触发位复位**：`MoveStart` 等触发脉冲在 `finally` 里用**不可取消令牌**复位，避免取消时残留。

## 5. 各设备流程（写死）

> 步骤编号沿用 §3.1 骨架；下面写各设备在骨架上的具体动作与点位。
> 点位名以各设备驱动/地址类为准（地址见 `Docs/设备寄存器地址/`）。

### 5.1 料架取料 `PickFromLoadRack`
- 前缀：XY→上料架上方 → Z 降 → 等待有板（料架 `HasMaterial=1`）。
- 充磁（边界）。
- 源特有：（无）→ Z 回升。
- 尾：内存单点写 `CurrentLocation = OnCrane(crane.Id)`（料架无握手输出，不写设备）。

### 5.2 双头镗
**取料 `PickFromBoring`**
- 前缀：XY→上方 → Z 降 → 等待有板。
- 充磁。
- 源特有：Z 回升 → 写"下料完成=1"（`12217`）。
**放料 `PlaceToBoring`**
- 目标审核：等待"请求数据=1"（`12203`）。
- 下发参数（长度/直径/左堵厚/右堵厚/内孔锥度/内孔成活或版孔/圆角，见 §8）→ 写"数据下发完成=1"（`12205`）。
- 等待"请求上料=1"（`12207`）。
- Z 降。
- 目标特有：（无）。
- 放磁 → Z 升 → 写"上料完成=1"（`12209`）。

### 5.3 斜床（含 Y 二次移动）
**放料/上料 `PlaceToLathe`**
- 目标审核：等待"请求数据=1"（`#1001`）。
- 下发参数（`#800 长度 / #801 成活直径 / #802 堵孔尺寸或版孔类型 / #909 加工模式`）→ 写"数据下发完成=1"（`#1101`）。
- 等待"请求上料=1"（`#1002`）。
- Z 降。
- 目标特有：**Y 二次偏移** = `MoveRelativeAsync(0, −YOffset, 0)`（`YOffset` 见 §9.1）→ 等待"尾座夹紧到位=1"（`#1003`）。
- 放磁 → Z 升 → 写"天车上料完成=1"（`#1103`）。
**取料/下料 `PickFromLathe`**
- 前缀：XY→source，**Y 直接到"二次偏移位置"（数据库 Y − YOffset，不是数据库 Y）** → Z 降 → 等待有板。
- 充磁。
- 源特有：写"天车下料到位=1"（`#1104`）→ 等待"尾座分开到位=1"（`#1005`）→ **`MoveRelativeAsync(0, +YOffset, 0)` 回数据库位** → Z 回升 → 写"天车下料完成=1"（`#1105`）。

### 5.4 研磨机
**放料/上料 `PlaceToGrinder`**
- 目标审核：等待"请求数据=1"（`14603`）。
- 下发参数（版辊直径 `14633`、加工模式 `14635`、版辊长度 `14637`）→ 写"数据传输完成=1"（`14623`）。
- 等待"请求上料=1"（`14605`）。
- Z 降。
- 目标特有：写"上料到达锁紧位置=1"（`14625`）→ 等待"锁紧完成上料退出=1"（`14607`）。
- 放磁 → Z 升 → 写"上料完成=1"（`14627`）。
**取料/下料 `PickFromGrinder`**
- 前缀：XY→上方 → 等待"请求下料=1"（`14609`）→ Z 降 → 等待有板。
- 充磁。
- 源特有：写"下料到达松开位置=1"（`14629`）→ 等待"松开完成下料退出=1"（`14611`）→ Z 回升 → 写"下料完成=1"（`14631`）。

### 5.5 打号机
**放料 `PlaceToMarker`**：等待"设备空闲=1" → 下发参数 → Z 降 → 放磁 → Z 升 → 写"上料完成"。
**取料 `PickFromMarker`**：等待"加工完成=1" → Z 降 → 等待有板 → 充磁 → Z 升 → 写"下料完成"。
（打号机读 FC04/写 FC06、打印内容 FC16；**完整握手序列待现场确认**。）

### 5.6 下料架放料 `PlaceToUnloadRack`
- 目标审核：等待"无板=1"。
- **无参数、无请求上料、无"上料完成"**。
- Z 降 → 放磁 → Z 升。
- （现场说**暂不需要**写放料完成脉冲；如需要再写 FC05 OUT2。）

### 5.7 中转 `TransitAsync`
- `MoveXYAsync(target.PositionX, ...)` 到目标上方（Z 保持安全高度）。
- **释放源设备预约**（`ResourceReservationManager.ReleaseSource(taskId)`）。
- **缩小路权**到 `[targetX − w/2, targetX + w/2]`（`RightOfWayManager.Resize(taskId, interval)`）。
- 无设备交互。

### 5.8 完成 `Finish`
- `MoveXSafeAsync()` 回本车安全位。
- 释放天车忙标志（`CurrentTask=null`、`State=Idle`）。
- 释放路权（`RightOfWayManager.Release`）、释放目标设备预约（`ResourceReservationManager.Release`）。
- `Complete`：工件 `TaskState`（下料工序 → `Completed`，否则 → `Waiting`）。

## 6. 资源生命周期（含部分释放）

```
AcquireResources：
    TryAcquire 路权 = [源X, 目标X] 两端各外扩 w/2（任务全程区间）
    TryReserve 源设备 + 目标设备  （先路权后预约；预约失败回滚路权）
    （天车忙标志由调度器派发时已在 CurrentTask/State 体现）

到 target（Transit）：
    ReleaseSource(taskId)                   // 只释放源设备预约
    Resize 路权 = [targetX − w/2, targetX + w/2]   // 缩到车体+目标

Finish：
    Release 路权（整把）
    Release 目标设备预约
```

- **新增资源 API 需求**（当前实现只有整把加/解锁）：
  - `RightOfWayManager.Resize(Guid taskId, XInterval interval)`：把某任务的区间锁替换为新区间（仍与其它锁做相交校验；失败返回 false）。
  - `ResourceReservationManager.ReleaseSource(Guid taskId)`：只释放该任务的源设备预约，保留目标设备预约。
- 释放时机对上表；**回安全位时路权已缩小**，现场确认"回安全位不会出问题"。

## 7. 异常与回滚（按板是否可能已吸起二分）

- `HasPieceOnCrane`：流程开始 `false`；**`MagnetOnAsync` 指令下发后立即置 `true`**（不依赖 `X6` 反馈）。
- `RunFlowAsync` 用 `try/catch/finally` 包裹：

| 情况 | 判据 | 处理 |
|---|---|---|
| 取料阶段异常 | `HasPieceOnCrane == false` | `RollbackToSource`：释放已获取的路权/预约/天车忙标志；**内存不动**（位置仍源、源内存仍在）；`TaskState=Waiting`；**不弃板、不写设备**。下轮自然重生成候选、换车重跑 |
| 途中/放料/完成异常 | `HasPieceOnCrane == true` | `AbortDiscard`（`TaskAbortCoordinator`）：释放全部资源 + 清内存 + 弃板 + **暂停线路** |

- `X6` 读失败按"已充磁"处理（不确定 → 不安全 → 全量清理）。
- `finally`：复位天车触发位（`MoveStart` 等），用不可取消令牌。
- 通信闪断走重连重试（§10.4），不算硬故障。

## 8. 加工参数下发（按设备类型）

| 设备 | 参数与换算 |
|---|---|
| 双头镗 | 长度=`Length`；直径/左堵厚/右堵厚/内孔锥度/内孔成活或版孔/圆角均 **×100** 取整 |
| 研磨机 | 版辊直径=`Diameter`(mm)、版辊长度=`Length`(mm)、加工模式=`PlugHole==100(大孔)?1:2`（`R7317`：1=大孔/2=小孔） |
| 斜床 | `#800` 长度、`#801` 成活直径、`#802` 堵孔尺寸/版孔类型=`PlugHole`、`#909` 加工模式**暂写死 1** |
| 打号机 | 打印内容 FC16=`PrintContent`；**外径=`Length`（板长）**、**内径=`PlugHole`（70/100）** |

> 参数下发依赖设备驱动的写点位/宏变量接口；缺哪个补哪个（`DeviceModbusMaps` / `LatheMacroAddress`）。

## 9. 参数配置

### 9.1 斜床 Y 二次偏移（上料/下料）

- **配置（JSON，配置页面要能改；每台斜床各一份，按设备号）**：

  ```jsonc
  "Transfer": {
    "Lathes": {
      "5": { "TailstockCenterDistance": 800, "BigHoleOffset": 122, "SmallHoleOffset": 55 },
      "6": { "TailstockCenterDistance": 820, "BigHoleOffset": 122, "SmallHoleOffset": 55 }
    }
  }
  ```

  - `TailstockCenterDistance`：该斜床当前顶尖距离
  - `BigHoleOffset` = `122`（大孔）
  - `SmallHoleOffset` = `55`（小孔）
- **计算**（孔型由 `WorkpieceInfo.PlugHole`：`100=大孔`、`70=小孔`；**取整**）：

  ```
  holeOffset = PlugHole == 100 ? BigHoleOffset : SmallHoleOffset
  YOffset    = Round( (TailstockCenterDistance − wp.Length) / 2 + holeOffset )
  ```

- **二次移动是相对移动**（读当前坐标后按 `YOffset` 位移）：
  - **上料（放料）**：XY 到数据库位置后 `MoveRelativeAsync(0, −YOffset, 0)`；
    例：当前坐标 `300`、`YOffset=55` → 相对移动到 `245`。
  - **下料（取料）**：直接到二次偏移位置（数据库 Y − `YOffset`）；尾座分开后
    `MoveRelativeAsync(0, +YOffset, 0)` 回数据库位。

### 9.2 其他坐标 / 位置来源

- 设备 X/Y/Z 绝对坐标：来自 `DeviceConfig`（**需补 `PositionY`/`PositionZ` 列**，见 §4.1）；**运行期现查数据库**（`IConfigurationStore`），不缓存。
- 天车 X/Y/Z 偏移量：来自 `CraneConfig.OffsetX/Y/Z`（当前未接入）。
- 各设备取/放料位 Z：由 §4.1 公式算出；若个别设备另需固定偏差，再加 `appsettings` 映射。

## 10. `ITransferHandler` 的定位

- 保留一个薄接口，每类设备一个实现，由 Registry 按 `DeviceType` 取：

```csharp
public interface ITransferHandler
{
    bool CanHandle(DeviceType type);
    Task PickAsync(TransferContext ctx, DeviceBase source, CancellationToken ct);
    Task PlaceAsync(TransferContext ctx, DeviceBase target, CancellationToken ct);
}
```

- 实现：`LoadRackHandler` / `BoringHandler` / `LatheHandler` / `GrinderHandler` / `MarkerHandler` / `UnloadRackHandler`。
- 每个 handler 内部就是"**一大串**"线性代码（审核→移动→握手→参数→充放磁）；参数下发作为 `PlaceAsync` 内一步（用共享原语写）。
- 编排器只做 `handler(source).PickAsync(...)` / `handler(target).PlaceAsync(...)`，外加通用的 acquire / transit / finish。
- `TransferContext` 携带：`crane`、`WorkpieceTask`（含 `Info`）、本车坐标换算、信号等待原语、以及"已充磁"边界标志（供编排器做二分回滚）。
- 既满足"写死、好维护"，又保留多态分派与后续加设备的扩展点。

## 11. 待现场确认 / 未定项

> 多数已确认：斜床参数已加入【参数设置 → 斜床搬运】（每台一份）；打号机外径=`Length`、内径=`PlugHole`；
> `DeviceConfig` 已补 `PositionY/PositionZ`（运行期现查）；Z 下降公式**取放料一致**；下料架不写放料脉冲；
> 料架取料**不需要** X0/X1/X2 动作。

1. 斜床 `#909` 加工模式取值（现**暂写死 1**，待现场给出含义）。
2. 各设备是否还有未列出的中间信号。

## 12. 测试策略

- 现场 PLC 未接，使用**打桩驱动**：测试工程内自建 `FakeCraneDriver`/`FakeDeviceDriver`（可脚本化信号变化）。
- 验证点：流程步骤顺序、握手等待与超时、`HasPieceOnCrane` 二分回滚、路权/预约的部分释放与缩小、
  `TaskState` 迁移（Assigned→Running→Waiting/Completed）、取消时触发位复位。
- 现有 `Simulated*Driver` 已删，测试用 fake 不回归生产代码。

---

## 附：与现有实现的差异（迁移说明）

| 维度 | 现状 | 本设计 |
|---|---|---|
| 推进方式 | `CraneWorker` 单循环 + `TransportStep` 每 tick 一步 | 每车独立循环 + 线性 `async` 一次性跑完 |
| 设备交互 | 模拟（`WriteSignalAsync("PickRequest")` 等） | 按设备类型写死的真实握手序列 |
| 运动 | 只写 `MoveStart` 脉冲，不等到位 | `await` 到位/超时/报警 |
| 资源 | 整把获取/释放 | 到目标释放源预约 + 路权缩小；完成时释放其余 |
| 回滚 | `AcquireResources` 失败回滚 | 同 + 取料阶段异常 `RollbackToSource`，充磁后 `AbortDiscard` |
| `ITransferHandler` | 未实现 | 薄接口 + 每设备 handler |
