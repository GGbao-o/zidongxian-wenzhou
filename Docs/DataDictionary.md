# 数据字典（配置表）

> 本文档是数据库配置表的字段定义来源，与 EF Core 实体一一对应。
> 数据库**只存配置**：天车、设备；工件任务、报警运行态只存内存（见 Architecture.md §3.4、§14）。
> 工艺路线顺序固定在代码中（Domain 的 `ProcessRoute`），不进数据库。
> 枚举以字符串形式存储，便于现场直接读表排查问题。

## 1. CraneConfig（天车配置表）

对应实体：`AutomatedLine.Infrastructure.Persistence.Entities.CraneConfig`

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| Id | int | 是 | 主键，同时作为天车号 |
| No | int | 是 | 显示序号，唯一 |
| Name | varchar(50) | 是 | 天车名称，如"天车1" |
| Protocol | varchar(30) | 是 | 协议类型（ModbusTcp / Mc / Simulated 等） |
| IpAddress | varchar(50) | 是 | PLC IP |
| Port | int | 是 | PLC 端口 |
| StartX | double | 是 | 活动范围左端 X |
| EndX | double | 是 | 活动范围右端 X |
| Width | double | 是 | 天车物理宽度，用于路权区间外扩 |
| SafeX | double | 是 | 安全位 X（任务结束回到此处） |
| SafeZ | double | 是 | 安全位 Z（安全高度） |
| SpeedX / SpeedY / SpeedZ | double | 是 | 三轴速度 |
| AccelX / AccelY / AccelZ | double | 是 | 三轴加速度 |
| DecelX / DecelY / DecelZ | double | 是 | 三轴减速度 |
| IsEnabled | bool | 是 | 是否启用（停用则不参与调度） |
| Remark | varchar(200) | 否 | 备注 |
| UpdatedAt | datetime | 是 | 最后更新时间（UTC） |

## 2. DeviceConfig（设备配置表）

对应实体：`AutomatedLine.Infrastructure.Persistence.Entities.DeviceConfig`

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| Id | int | 是 | 主键，同时作为设备号 |
| Name | varchar(50) | 是 | 设备名称，如"斜床01" |
| DeviceType | varchar(30) | 是 | 设备类型枚举，见下方枚举表 |
| Protocol | varchar(30) | 是 | 协议类型 |
| IpAddress | varchar(50) | 是 | PLC IP |
| Port | int | 是 | PLC 端口 |
| PositionX | double | 是 | 设备物理 X 位置，用于路权区间计算 |
| SafePosition | double | 是 | 设备侧安全位 |
| MinDiameter / MaxDiameter | double | 是 | 可加工直径范围，用于目标设备匹配 |
| MinLength / MaxLength | double | 是 | 可加工长度范围，用于目标设备匹配 |
| IsEnabled | bool | 是 | 是否启用 |
| Remark | varchar(200) | 否 | 备注 |
| UpdatedAt | datetime | 是 | 最后更新时间（UTC） |

## 3. 工艺路线（不进数据库）

工艺顺序**固定在代码中**，由 `AutomatedLine.Domain.Routing.ProcessRoute` 提供。
原因：车间排布一旦确定就不再调换设备，只有坐标可能微调，而坐标已由 `DeviceConfig.PositionX` 管理，
固定顺序无需表驱动，避免多一处可能不一致的配置（Architecture.md §6.5 已同步）。

## 4. 枚举定义

### DeviceType（设备类型）

`Unknown` / `LoadRack` / `ForkMachine` / `BoringMachine` / `LatheMachine` / `GrinderMachine` / `BalancingMachine` / `MarkerMachine` / `CheckMachine` / `UnloadRack`

### ProcessType（工序）

`Unknown` / `Loading` / `Forking` / `Boring` / `Lathing` / `Grinding` / `Balancing` / `Marking` / `Checking` / `Unloading`

> 工艺顺序以现场确认为准；v1 固定内置在代码中，若将来确需运行期调整再改为表驱动（Architecture.md §6.5、§16.1）。
