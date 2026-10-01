# CraneScheduler 设计（集中式调度 / 分配矩阵）

> 对应架构文档 §8.4。本文说明 `AutomatedLine.Application.Scheduling.CraneScheduler` 的完整设计：
> 何时派发、如何把候选任务分配给天车、如何处理路权/预约冲突、与生成器/状态机的边界。

## 1. 职责

全系统**唯一**的集中调度器：决定"**哪个候选任务 → 哪台天车**"。
**只做判断（是否可执行），不真正加锁**——路权/预约的**真正获取**在**天车状态机开始执行任务时**完成（见 §9）。
天车自己不抢任务，避免两台车抢同一任务。

## 2. 输入

| 输入 | 来源 |
|---|---|
| 候选任务池 | `CandidateTaskPool`（生成器每周期重建） |
| 天车目录 | `CraneRegistry` |
| 天车状态（**按需读**） | `ICraneDriver.ReadStatusAsync` |
| 路权 | `RightOfWayManager` |
| 设备预约 | `ResourceReservationManager` |
| 线路 | `LineControlService.IsRunning` |

## 3. 触发与门控

- 常驻循环，每 `Scheduling.ScheduleIntervalMs` 跑一轮。
- **仅当线路运行中**才派发；未启动/暂停时本轮跳过。

## 4. 按需读天车状态

- 每轮只对"**启用且空闲**"的天车各读一次状态（连接/报警/X），存本轮字典。
- 读失败（null）/报警 → 该车本轮不可用。
- 天车**不做后台采集**，与采集解耦。

## 5. 分配算法（任务 × 天车矩阵，贪心）

```
DispatchOnceAsync:
  cranes   = 启用天车快照
  statuses = 读空闲天车状态（§4）
  cands    = 候选池快照，按 [Priority 降序 → 距离最近] 排序
  used     = {}   // 本轮已派出的天车

  foreach cand in cands:
     task = cand.Workpiece
     if task.TaskState != Waiting: continue                 // 已派发/暂停/完成 → 跳过

     eligible = cranes.Where(c => c.IsEnabled && c.IsIdle && c.Id not in used
                     && c.Covers(cand.Source.PositionX) && c.Covers(cand.Target.PositionX)
                     && statuses[c.Id] is not null && !statuses[c.Id].HasAlarm)
                .OrderBy(距离源最近)

     foreach crane in eligible:                              // 换车
         interval = cand.GetRightOfWayInterval(crane.Width)  // 每车宽度不同 → 区间不同

         if !RightOfWay.CanAcquire(interval):                // 【判断】区间是否空闲，不加锁
             continue                                        // 拿不到 → 试下一台车，不死等
         if Reservation.IsReserved(cand.Source.Id) || Reservation.IsReserved(cand.Target.Id):
             continue                                        // 【判断】源/目标是否已被别的任务预约
         // 判断通过 → 派发（不改任何锁）

## 6. 选车规则

1. **空闲**（`crane.IsIdle`，一车一任务，硬约束）；
2. 启用、活动范围 `StartX/EndX` **覆盖源与目标**（硬约束）；
3. 已连接、无报警（硬约束）；
4. 距离源最近（`|status.X − sourceX|`）。
5. 候选排序 tie-break：**优先级降序 → 距离最近**（不做负载均衡）。

> 天车正在执行状态机（`CurrentTask != null`）绝不再派任务。

## 7. 竞态与原子性

- **调度器只判断、不加锁**：`CanAcquire` / `IsReserved` 都是**只读查询**。
- 判断通过后**派发**，但**真正加锁在状态机执行时**（见 §9）。因此存在"判断通过 → 执行前被别的车抢先"的窗口；
  这个窗口由**状态机执行时的原子 `TryAcquire`** 兜底（谁先原子拿到谁执行，后到者失败并回滚）。
- 天车"锁" = `crane.Runtime.CurrentTask`；本轮 `used` 集合保证一车一任务。
- 调度器**单线程**，判断过程无不一致空档。

## 8. 换目标 / 换车 / 不死等

- **换车**：同一候选，某台车拿不到路权/预约 → 试下一台（§5）。
- **换目标**：生成器已为同一工件的**每个目标各生成一个候选**；某目标失败时，其它目标候选仍在列表中会被继续尝试。某候选一旦成功，工件 `TaskState=Assigned`，其剩余目标候选下轮自动跳过。
- **不死等**：拿不到只是**本周期不派发**，候选保留在池中，下一轮用最新状态重算再试。

## 9. 派发成功后 / 路权获取时机

派发只置任务归属，**不加锁、不算持路权**：

```
crane.Runtime.CurrentTask = TransportTask{...}
crane.Runtime.State       = Reserved
crane.Runtime.Step        = PreCheck
task.TaskState            = Assigned
```

**真正获取路权/设备预约在状态机开始执行时**（`CraneWorker` / `CraneStateMachine`）：

```
状态机开始执行：
    1) if !RightOfWay.TryAcquire(task.Id, 任务区间):     // 原子加锁，谁先拿谁执行
           → 回滚：释放天车锁、TaskState = Waiting，return（下一轮重新派发）
    2) if !Reservation.TryReserve(task.Id, source, target):
           RightOfWay.Release(task.Id)                    // 回滚已取得的路权
           → 回滚：释放天车锁、TaskState = Waiting，return
    → 持有【路权 + 设备预约】后推进各步骤；任务结束（回安全位后）释放路权与预约
```

- 调度器**只判断**（`CanAcquire` / `IsReserved`），不 `TryAcquire`、不 `TryReserve`、不设置 `LockedInterval`。
- 路权/预约由**状态机持有并在其结束时释放**（与"异常时释放路权/预约"的既有约定一致）。
- **拿不到就回滚**：释放天车锁（`CurrentTask=null`、`State=Idle`）、任务 `TaskState=Waiting`，下一轮由调度器重新派发。

## 10. 边界（不做的事）

- 不等待、不阻塞：派发即返回，执行由 Worker 负责。
- 不做全局重排：只做本周期贪心，拿不到的留到下一轮。
- 不读设备信号（那是生成器的职责）。

## 11. 参数

| 项 | 值/来源 |
|---|---|
| 调度周期 | `Scheduling.ScheduleIntervalMs`（默认 500ms） |
| 一车一任务 | 硬约束 |
| 候选排序 | 优先级 → 距离 |
| 负载均衡 | 暂不做 |

## 12. 日志

调度循环 500ms，非事件日志一律 `Sample` 抽样落盘（间隔 `Logging.SampleSeconds`），"派发成功"用 `Info`。

| 时机 | 级别 | 内容 |
|---|---|---|
| 候选池为空 | Sample | `候选池为空，本轮无任务可派` |
| 候选无可执行天车 | Sample | 逐候选：`候选 X 暂不可派：无可执行天车（车1:忙，车2:范围不覆盖，车3:状态读取失败…）` |
| 有可执行车但全冲突 | Sample | 逐候选：`候选 X 暂未派发：路权区间 [...] 与在途任务冲突（N 台可执行天车均冲突，下轮重试）` |
| 派发成功 | Info | `派发：任务 xxxx 源→目标 给 车（区间 [...]，待状态机执行时取路权/预约）` |
| 读天车状态失败 | Warn | `读取天车[id] 状态失败：...` |

> 逐候选原因日志用抽样键 `dispatch-noeligible-{taskId}` / `dispatch-blocked-{taskId}` 节流；`ExplainNoEligible` 对每台天车给出被排除原因（未启用/忙/范围不覆盖/状态读取失败/报警）。
