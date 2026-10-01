# TransportTaskGenerator 设计（候选任务生成器）

> 对应架构文档 §8.3。本文说明 `AutomatedLine.Application.Generation.TransportTaskGenerator` 的完整设计：
> 什么时候生成、依据什么生成、源/目标怎么判定、输出什么。

## 1. 定位

- **输入**：内存工件任务（`IRuntimeTaskManager`）+ 设备 PLC 输入信号（`DeviceBase.Signals`）+ 设备预约（`ResourceReservationManager`）+ 内置工艺路线（`ProcessRoute`）。
- **输出**：候选任务池 `CandidateTaskPool`（每周期**整体重建**，不是队列）。
- **候选 ≠ 已派发**：只是"当前理论上可执行的一次搬运"；真正占用由调度器在派发时做。

## 2. 生成门控（关键）

| 层级 | 条件 | 行为 |
|---|---|---|
| 线路级 | `LineControlService.IsRunning` | 运行中才生成；**未启动/暂停/故障暂停 → 清空候选池**，不生成 |
| 任务级 | `WorkpieceState == Waiting` | 只有"已启动并排队"的工件参与；**未启动（Created）/已暂停（Paused）/已派发等一律跳过** |

> 采集是**常开**的（设备输入信号一直读进缓存），与生成无关。

## 3. 任务状态（`WorkpieceState`）

| 状态 | 含义 | 是否生成候选 |
|---|---|---|
| `Created` | 新增未启动 | 否 |
| `Waiting` | 已启动、排队中 | **是** |
| `Paused` | 人工暂停该任务 | 否 |
| `Assigned`/`Running`/`Completed`/`Discarded` | 已派发/执行/完成/丢弃 | 否 |

- 主页面行内操作：**启动**（Created/Paused → Waiting）、**暂停**（Waiting → Paused）、**删除**（未派发时可删）。
- 线路级"启动/暂停"是另一回事（控制生成门控，见 §2）。

### 3.1 入线顺序（上料架待取板队列）

- **入线顺序 = 当前队列里的排位（1、2、3…连续）**，只有"已启动、`Waiting`、在队列"的工件占位。
- 点**启动**：入队（排到**队尾**）；点**暂停**：出队，**后面的自动补位**；再次启动重新入队尾。
- 点**添加**只入内存列表（`Created`），**不入队、不占位、不生成任务**，排位显示 `-`。
- **队头** = 排位 1 的工件；生成器**只给队头**生成上料候选。没有队头 = 无任务。
- 天车把队头取走（状态变 `Assigned`）后即出队，后面整体前移。

> 实现：内部用入队时分配的**入队序号**（`QueueOrder`）决定先后；展示的"入线顺序"是把它**压实成连续排位**后的结果（暂停/删除不留空号）。

## 4. 生成流程（每 Waiting 工件）

```
仅当线路运行中：
  candidates = []
  queueHead = 队列中 Waiting 且入线顺序号最小者        // 上料只取队头（见 §3.1）

  foreach 工件 in 内存列表（添加顺序=队列序）:
     if 工件.TaskState != Waiting: continue

     source = ResolveSource(工件, queueHead)        // 见 §5
     if source == null: continue

     current = 工件.CurrentProcess == Unknown ? Loading : 工件.CurrentProcess
     next = ProcessRoute.GetNextAvailable(current, 有启用设备)   // 跳过无启用设备的节点
     if next == null: 工件.TaskState = Completed; continue

     foreach target in 所有启用设备 where target.Type == next.DeviceType:   // 全枚举
        满足: 已连接 && 未预约 && !报警 && 加工范围匹配 && CanReceive(target)
        → candidates.Add(CandidateTask(工件, source, target, priority=100, next.Process))

  CandidateTaskPool.Replace(candidates)
```

## 5. 源判定 `ResolveSource`

| 工件位置 | 源 | 额外条件 |
|---|---|---|
| 在设备上 | 该设备 | 内存一致（`device.CurrentWorkpiece == 工件`） |
| 在队列 | **唯一上料架** | **仅队头**可上料 |

源通用硬条件：`已连接 && 未被预约 && 未报警 && CanProvide(源)`。

## 6. 就绪判定 `DeviceHandshakeRules`（按设备类型读输入信号）

**源就绪 `CanProvide`**

| 设备类型 | 条件（点位名） |
|---|---|
| 上料架 | `HasMaterial == true`（有板） |
| 双头镗 / 斜床 / 研磨机 | `RequestUnload == true`（请求下料） |
| 打号机 | `ProcessDone == true`（加工完成） |

**目标就绪 `CanReceive`**

| 设备类型 | 条件（点位名） |
|---|---|
| 双头镗 / 斜床 / 研磨机 | `RequestData == true`（请求数据） |
| 打号机 | `Idle == true`（设备空闲） |
| 下料架 | `HasMaterial == false`（无板=空闲） |

**报警 `IsAlarm`**：研磨机 `RunState == 2`（机床报警）；源/目标都排除报警设备。
**无快照/无数据**：一律视为"不就绪"。

## 7. 工序跳过（启用设备决定实际工序）

`ProcessRoute.GetNextAvailable` 改为：**跳过所有没有启用设备的中间工序节点**。
例：路线为 上料→双头镗→斜床→研磨→动平衡→打号→验点→下料；若没有动平衡设备，则研磨的下一道就是打号；再没有验点，就再到下料。返回 `null` 表示后面无可执行工序（工件视为完成）。

## 8. 全枚举与备份

- 同一工件的下一道工序有多台同类设备时，**每个满足条件的目标各生成一个候选**（不预先选最优）。
- 调度器在多个候选间选择；某个目标路权/预约拿不到时可改用同一工件的另一个目标。

## 9. 与其它组件的关系

- **CraneScheduler**：从 `CandidateTaskPool` 取候选，按"任务×天车"分配并原子获取路权/预约；生成器只做"排除已预约"，真正占用在派发时。
- **CraneStateMachine / CraneWorker**：执行被派发的任务；每段完成后把工件置回 `Waiting`，下一段再由生成器生成。
- **DeviceStateCollector**：提供设备输入信号（本生成器的判定依据）。

## 10. 常量与参数

| 项 | 值/来源 |
|---|---|
| 生成周期 | `Scheduling.GenerateIntervalMs`（默认 500ms） |
| 候选优先级 | 暂固定 `100` |
| 上料架 | 全系统唯一 |
| 队列顺序 | 主页面添加顺序（`RuntimeTaskManager` 列表顺序） |
