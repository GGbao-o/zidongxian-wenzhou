# 天车状态机实施计划（第 3 阶段：界面）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 配置页支持新坐标与斜床 Y 偏移：设备管理页增加 X/Y/Z（Y/Z 可编辑）；参数设置页增加"斜床搬运"卡片（每台斜床一份 Y 偏移参数）。

**Architecture:** 只改 UI 层（ViewModel/View），复用既有 `IConfigurationStore`（设备 CRUD）与 `IConfigService.SaveAppOptions`（参数写回 appsettings）。设备坐标 Y/Z 已在第 1 阶段加入 DTO/实体。

**Tech Stack:** .NET 8、WPF、CommunityToolkit.Mvvm、lepoco WPF UI。

**依赖：** 第 1 阶段（`DeviceConfigDto.PositionYZ`、`AppOptions.Transfer`、`LatheTransferOptions`）。

---

## 文件结构（第 3 阶段）

| 文件 | 职责 |
|---|---|
| `src/AutomatedLine.Ui/ViewModels/DeviceViewModel.cs`（改） | 编辑弹框增加 Y/Z |
| `src/AutomatedLine.Ui/Views/DevicePageView.xaml`（改） | 列表增加 Y/Z 列 |
| `src/AutomatedLine.Ui/ViewModels/ParamsViewModel.cs`（改） | 增加"斜床搬运"分类（每台斜床） |
| `src/AutomatedLine.Ui/App.xaml.cs`（改，如需） | 确认 `DeviceRegistry` 已注入 ParamsViewModel（已是单例） |

---

### Task 1: 设备管理页增加 Y/Z 坐标

**Files:**
- Modify: `src/AutomatedLine.Ui/ViewModels/DeviceViewModel.cs:155-192`
- Modify: `src/AutomatedLine.Ui/Views/DevicePageView.xaml`

- [ ] **Step 1: 编辑弹框加入 Y/Z 字段**

在 `DeviceViewModel.EditDialog` 的 `items` 列表里，`new("物理位置X", ...)` 之后插入两项：

```csharp
            new("物理位置Y", dto.PositionY.ToString("0.###"), _ => { }),
            new("物理位置Z", dto.PositionZ.ToString("0.###"), _ => { }),
```

并把 `items` 里的字段顺序整理为：设备号、名称、设备类型、协议、IP、端口、**物理位置X、物理位置Y、物理位置Z**、最小直径、最大直径、最小长度、最大长度、启用。

- [ ] **Step 2: 写回 DTO 时解析 Y/Z**

把 `EditDialog` 里 `dto.PositionX = ParseDouble(v[6].Value, dto.PositionX);` 之后的下标整体后移，改为：

```csharp
        dto.PositionX = ParseDouble(v[6].Value, dto.PositionX);
        dto.PositionY = ParseDouble(v[7].Value, dto.PositionY);
        dto.PositionZ = ParseDouble(v[8].Value, dto.PositionZ);
        dto.MinDiameter = ParseDouble(v[9].Value, dto.MinDiameter);
        dto.MaxDiameter = ParseDouble(v[10].Value, dto.MaxDiameter);
        dto.MinLength = ParseDouble(v[11].Value, dto.MinLength);
        dto.MaxLength = ParseDouble(v[12].Value, dto.MaxLength);
        dto.IsEnabled = ParseBool(v[13].Value, dto.IsEnabled);
```

- [ ] **Step 3: 列表增加 Y/Z 列**

在 `DevicePageView.xaml` 里现有"物理位置X"列之后，照同样写法加两列：

```xml
<DataGridTextColumn Binding="{Binding Config.PositionY}" Header="物理位置Y" />
<DataGridTextColumn Binding="{Binding Config.PositionZ}" Header="物理位置Z" />
```

- [ ] **Step 4: 构建**

Run: `dotnet build AutomatedLine.sln -c Debug --nologo -v q`
Expected: 0 error

- [ ] **Step 5: 提交**

```bash
git add src/AutomatedLine.Ui/ViewModels/DeviceViewModel.cs src/AutomatedLine.Ui/Views/DevicePageView.xaml
git commit -m "feat(ui): 设备管理页增加物理位置 Y/Z"
```

---

### Task 2: 参数设置页增加"斜床搬运"（每台一份 Y 偏移）

**Files:**
- Modify: `src/AutomatedLine.Ui/ViewModels/ParamsViewModel.cs`

- [ ] **Step 1: 构造函数注入 `DeviceRegistry`**

`ParamsViewModel` 顶部 using 增加：

```csharp
using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Devices;
```

字段与构造函数改为：

```csharp
    /// <summary>运行态容器（枚举斜床设备）。</summary>
    private readonly DeviceRegistry _deviceRegistry;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="configService">配置服务</param>
    /// <param name="dialogService">弹窗服务</param>
    /// <param name="deviceRegistry">设备注册表</param>
    public ParamsViewModel(IConfigService configService, IDialogService dialogService, DeviceRegistry deviceRegistry)
    {
        _configService = configService;
        _dialogService = dialogService;
        _deviceRegistry = deviceRegistry;
        BuildCategories();
    }
```

- [ ] **Step 2: 增加 `DoubleItem` 助手**

在 `IntItem`/`LongItem`/`TextItem` 附近追加：

```csharp
    /// <summary>构造浮点参数项。</summary>
    private static ParamItemViewModel DoubleItem(string name, Func<double> get, Action<double> set)
        => new(name, get().ToString("0.###"), text => set(double.TryParse(text, out var v) ? v : 0));
```

- [ ] **Step 3: `BuildCategories` 增加"斜床搬运"分类**

在 `logging` 分类之前插入：

```csharp
        // 斜床搬运参数：每台斜床一份（顶尖距离 / 大孔 / 小孔偏移），保存到 App:Transfer
        var lathes = _deviceRegistry.Snapshot()
            .Where(d => d.Type == DeviceType.LatheMachine)
            .OrderBy(d => d.Id)
            .ToList();
        if (lathes.Count > 0)
        {
            var transfer = new ParamCategoryViewModel("斜床搬运", "每台斜床的 Y 二次偏移参数");
            foreach (var lathe in lathes)
            {
                if (!_options.Transfer.Lathes.TryGetValue(lathe.Id, out var opt))
                {
                    opt = new LatheTransferOptions();
                    _options.Transfer.Lathes[lathe.Id] = opt;
                }

                transfer.Items.Add(DoubleItem($"{lathe.Name} 顶尖距离", () => opt.TailstockCenterDistance, v => opt.TailstockCenterDistance = v));
                transfer.Items.Add(DoubleItem($"{lathe.Name} 大孔偏移", () => opt.BigHoleOffset, v => opt.BigHoleOffset = v));
                transfer.Items.Add(DoubleItem($"{lathe.Name} 小孔偏移", () => opt.SmallHoleOffset, v => opt.SmallHoleOffset = v));
            }

            Categories.Add(transfer);
        }
```

- [ ] **Step 4: 构建**

Run: `dotnet build AutomatedLine.sln -c Debug --nologo -v q`
Expected: 0 error

- [ ] **Step 5: 手动验证（可选）**

运行程序 → 参数设置页出现"斜床搬运"卡片 → 打开编辑 → 修改某台斜床"顶尖距离" → 确定 → 确认 `bin\...\appsettings.json` 的 `App:Transfer:Lathes` 已写入。

- [ ] **Step 6: 提交**

```bash
git add src/AutomatedLine.Ui/ViewModels/ParamsViewModel.cs
git commit -m "feat(ui): 参数页增加斜床 Y 二次偏移（每台一份）"
```

---

### Task 3: 文档同步

**Files:**
- Modify: `Docs/页面布局/主页面.md` 或新增页面说明（若无设备/参数页文档则跳过）
- Modify: `Docs/架构设计文档.md`（§13.3 设备页字段、§13.5 参数页分类、变更记录 v2.40）

- [ ] **Step 1: 架构文档 §13.3**

设备管理展示字段补"物理位置 Y/Z"。

- [ ] **Step 2: 架构文档 §13.5**

参数设置分类补"斜床搬运（每台斜床：顶尖距离/大孔偏移/小孔偏移）"。

- [ ] **Step 3: 变更记录**

新增 `v2.40` 行，说明设备页 Y/Z 与斜床搬运参数页。

- [ ] **Step 4: 提交**

```bash
git add Docs/架构设计文档.md
git commit -m "docs: 设备页 Y/Z 与斜床搬运参数页 (v2.40)"
```

---

## 完成标准（第 3 阶段）

- 设备管理页可查看/编辑 X/Y/Z；保存后写入 DeviceConfig。
- 参数设置页出现"斜床搬运"卡片，每台斜床三项参数可编辑并写回 `appsettings.json` 的 `App:Transfer:Lathes`。
- 构建 0 错。
