# CraneScheduler 设计（集中式调度 / 分配矩阵）

> 对应架构文档 §8.4。本文说明 `AutomatedLine.Application.Scheduling.CraneScheduler` 的完整设计：
> 何时派发、如何把候选任务分配给天车、如何处理路权/预约冲突、与生成器/状态机的边界。

## 1. 职责

全系统**唯一**的集中调度器：决定"**哪个候选任务 → 哪台天车**"，并**原子**占用路权与设备预约。
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

         if !RightOfWay.TryAcquire(task.Id, interval):
             continue                                        // 路权拿不到 → 试下一台车，不死等
         if !Reservation.TryReserve(task.Id, cand.Source.Id, cand.Target.Id):
             RightOfWay.Release(task.Id)                     // 预约失败 → 回滚路权
             continue
         // 路权 + 预约都成功 → 占用

## 6. 选车规则

1. **空闲**（`crane.IsIdle`，一车一任务，硬约束）；
2. 启用、活动范围 `StartX/EndX` **覆盖源与目标**（硬约束）；
3. 已连接、无报警（硬约束）；
4. 距离源最近（`|status.X − sourceX|`）。
5. 候选排序 tie-break：**优先级降序 → 距离最近**（不做负载均衡）。

> 天车正在执行状态机（`CurrentTask != null`）绝不再派任务。

## 7. 竞态与原子性

- **调度器单线程**：一轮内"判断 + 占用"串行，无 check-then-act 空档。
- `TryAcquire`（路权）、`TryReserve`（预约）内部加锁、原子；顺序 **先路权、后预约**，预约失败**回滚路权**。
- 天车"锁" = `crane.Runtime.CurrentTask`；本轮 `used` 集合保证一车一任务。

## 8. 换目标 / 换车 / 不死等

- **换车**：同一候选，某台车拿不到路权/预约 → 试下一台（§5）。
- **换目标**：生成器已为同一工件的**每个目标各生成一个候选**；某目标失败时，其它目标候选仍在列表中会被继续尝试。某候选一旦成功，工件 `TaskState=Assigned`，其剩余目标候选下轮自动跳过。
- **不死等**：拿不到只是**本周期不派发**，候选保留在池中，下一轮用最新状态重算再试。

## 9. 派发成功后

```
crane.Runtime.CurrentTask    = TransportTask{...}
crane.Runtime.State          = Reserved
crane.Runtime.Step           = PreCheck
crane.Runtime.LockedInterval = interval
task.TaskState               = Assigned
```
随后 `CraneWorker` 的状态机接手推进（路权已在派发时取得，状态机内不再获取）。

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
