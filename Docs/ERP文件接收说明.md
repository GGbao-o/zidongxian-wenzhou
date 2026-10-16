# ERP文件工件接收

核对日期：2026-10-14。ERP每次写完一批多行后，上位机读取；本功能不处理双方同时写文件的交互协议。

## 1. 主页面操作

主页面“添加任务”旁新增按钮。第一次点击“监听 ERP 工件”开启持续监听，按钮变为“暂停 ERP 监听”；再次点击暂停后续批次。暂停不打断已开始批次，该批完成添加和文件清理后停止读取。再次开启可继续接收文件里的工件。

程序打开时默认暂停监听，不因启动线路自动开启。切换页面不停止监听；程序关闭由宿主停止监听服务。ERP监听与线路状态独立，不启动工件、不改变线路状态、不控制PLC。

按钮下方显示监听/暂停状态、最近批次结果或失败原因、本进程累计接收件数。重复点击复用同一个宿主实例，不生成第二个监听循环。

## 2. 配置与参数页面

运行目录的appsettings.json增加App.ErpImport；缺项采用以下默认值。参数设置页面“ERP接收 → 编辑”通过现有动态XAML显示三个输入项。

```json
"ErpImport": {
  "FilePath": "D:\\job1\\job1.txt",
  "PollIntervalMs": 500,
  "Encoding": "utf-8"
}
```

| 参数页面名称 | JSON路径 | 用途 |
|---|---|---|
| 监听文件路径 | App.ErpImport.FilePath | 默认D:\job1\job1.txt；不能为空 |
| 文件检查间隔(ms) | App.ErpImport.PollIntervalMs | 默认500，必须大于0；一轮完成后等待，不是固定时刻并发触发 |
| 文件编码 | App.ErpImport.Encoding | 默认utf-8，可配置gbk；非法编码或解码失败会报接收失败 |

监听每轮重新获取配置系统内存中的最新AppOptions。参数保存且配置重载后，下一轮采用新路径/间隔/编码，无需重启程序；已开始的一批仍使用读取时的路径和编码，清理不会误写到新路径。

文件不存在则等待，不主动创建文件夹或文件；空文件不创建任务，也不会抹掉最近一批成功消息。UTF8回写不加BOM，全成功的UTF8文件清理后为0字节。

## 3. 文件格式和映射

一个非空行代表一个工件；英文逗号分隔，至少8列，使用前8列，额外列忽略。字段前后空白会去掉，字段内部不支持CSV引号转义或包含分隔逗号。数值使用小数点，不随Windows区域设置变化。

| 列/下标 | 文件数据 | 工件字段 | 解析与校验 |
|---|---|---|---|
| 1 / 0 | 打印内容 | PrintContent | 字符串；沿用当前录入规则，不额外强制非空 |
| 2 / 1 | 版号 | PlateNo | 非空字符串 |
| 3 / 2 | 序号 | SerialNo | 正整数 |
| 4 / 3 | 长度 | Length | 有限正double |
| 5 / 4 | 直径 | Diameter | 有限正double |
| 6 / 5 | 堵孔 | PlugHole | 整数70或100 |
| 7 / 6 | 斜床工艺编号 | LatheMachiningMode | 整数1至10 |
| 8 / 7 | 堵厚 | PlugThickness | 有限double；沿用录入规则，不额外限制正数 |

NaN/Infinity、列数不足和字段转换失败都不创建工件。解析失败行保留，主页面提示第几行和原因，操作日志记录错误。

示例：

```text
A261014001-1,A261014001,1,1200.1,180.3,70,1,25
A261014001-2,A261014001,2,1200.2,180.4,100,2,25.5
```

生成两个工件，分别为A261014001/1、A261014001/2。长度、直径、堵厚保留原始小数，不在ERP解析时做双头镗的倍率换算/舍入。内部RuntimeId由新Guid标识，与旧程序拼接字符串的任务ID不同。

## 4. 工件状态与文件消费

每轮顺序：一次读取全部原始行 → 按文件顺序整批解析 → ERP专用批量入口一次添加 → 全批提交后只发一次Changed通知主页面刷新 → 回写未接收的原始行。批量入口返回与输入顺序对应的任务/null，成功和失败仍映射回原文件行号。

一批有新增任务才通知，无新增不通知；逐件task/operation日志仍保留。主页面沿用原有列表刷新方式，只是每批一次，不再每个工件清空重建一次。例如已有200件、接收50件，从50次列表重建/11275个行ViewModel改为1次重建/250个行ViewModel；这里是本机真实ViewModel验证的工作量，不是现场画面耗时。

新增状态固定为Created（未启动），位置Queue，QueueOrder为0。不会调用Start，也不会自动成为上料架等待队头或生成候选；操作员仍按现有启动按钮让它进入Waiting。

| 情况 | 处理 |
|---|---|
| 5行全部合法且身份未重复 | 添加5件未启动工件，清空文件内容，保留job1.txt文件 |
| 4行合法、1行错误 | 添加4件，删除成功行，只保留错误原始行 |
| 版号和序号已在任务总表中 | 不添加、不覆盖，原行保留并提示重复；所有现存状态均参与重复检查 |
| 空白行 | 忽略，不生成任务；回写时移除 |
| 所有非空行均失败 | 不写回文件，保留原始数据 |
| 已添加，但清理失败 | 任务不撤回；保留已接收行索引回执，下轮只重试对应清理，不重复添加 |

ERP去重对版号使用精确字符串相等、序号使用整数相等，在任务表同一锁中检查整批现有/批内身份并添加；仅ERP入口使用该规则，人工Add行为未改。重复行可人工修正，或在处理掉现有记录后重新接收。

清理先写同目录临时文件，再替换原文件；写临时文件失败不会先截断原数据。成功行删除后，剩余行内容保持原样，换行和编码按本批参数重新输出。

清理失败回执按文件路径和原批次行内容匹配，暂停/继续仍保留；如果文件批次已变化则重新解析，并继续按任务身份去重。回执和工件都是本进程内存数据，进程重启不恢复，不新增MySQL工件持久化。

## 5. 生命周期和错误边界

- 单一后台循环，上一轮结束后才等待间隔；暂停期间不读文件。
- 人工暂停只阻止后续批次，不取消本批，避免已经添加但尚未清理的行被遗留。
- 宿主关闭可以取消尚未完成的读取；已经进入添加阶段的一批使用不可取消令牌完成文件清理，宿主等待收尾。
- 文件读取/编码/配置错误记录操作日志并显示接收失败，后续轮次继续尝试，不暂停线路、不调用TaskAbortCoordinator、不触发线路Error弹窗。
- 相同错误按类别抑制重复日志；读取恢复、文件清理成功会记录结果。页面状态通知失败不让监听宿主退出。

## 6. 日志

| 日志 | 记录内容 |
|---|---|
| operation | 开启/暂停、实际文件路径/间隔/编码、每件来源行号/身份/Guid/Created、错误行和重复原因、清理结果与失败重试、读取恢复、宿主停止 |
| task | ERP任务创建及原始尺寸/堵孔/工艺/打印内容，接续已有任务生命周期日志 |

例如：

```text
ERP接收：开启持续监听
ERP监听配置：D:\job1\job1.txt，间隔=500ms，编码=utf-8
ERP接收 D:\job1\job1.txt 第1行：工件A261014001/1，任务=<Guid>，状态=Created（未启动）
ERP文件清理完成：D:\job1\job1.txt，已接收5行，保留0行，忽略空行0
ERP接收：暂停监听，当前批次完成后停止接收
```

## 7. 实现与验证位置

- [ErpImportService](../src/AutomatedLine.Application/Erp/ErpImportService.cs)：单循环、开关、最新配置、成功行回执与清理、日志状态。
- [ErpImportContracts](../src/AutomatedLine.Application/Erp/ErpImportContracts.cs)：UI控制与文件抽象。
- [ErpJobParser](../src/AutomatedLine.Application/Erp/ErpJobParser.cs)：八列原始数据解析。
- [ErpJobFile](../src/AutomatedLine.Infrastructure/Erp/ErpJobFile.cs)：真实文件读取、编码、临时文件回写。
- [RuntimeTaskManager](../src/AutomatedLine.Application/Runtime/RuntimeTaskManager.cs)：ERP原子身份去重与Created创建。
- [MainViewModel](../src/AutomatedLine.Ui/ViewModels/MainViewModel.cs) / [MainPageView](../src/AutomatedLine.Ui/Views/MainPageView.xaml)：切换按钮、状态显示、任务列表。
- [ParamsViewModel](../src/AutomatedLine.Ui/ViewModels/ParamsViewModel.cs) / [AppOptions](../src/AutomatedLine.Application/Configuration/AppOptions.cs)：动态编辑与默认参数。
- [ErpImportTests](../tests/AutomatedLine.Tests/ErpImportTests.cs)：临时磁盘文件、非法/重复行、暂停恢复、清理失败回执、编码和宿主收尾回归。
