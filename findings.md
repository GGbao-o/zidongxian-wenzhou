# 文档与源码分析发现

## 初始信息
- 工作目录：D:\BSH\c#\code\AutomatedLine。
- 分析前 git 工作区干净。
- Docs 实际有 11 份 Markdown 文档；Windows 路径大小写不敏感，Docs/docs 是同一目录。
- 解决方案包含 Domain、Application、Infrastructure、Focas、Ui 与测试项目。
- 本文件保存证据与中间发现；最终结论另存报告。

## 文档分析（源码核对前）
- 架构 v2.61：.NET 8/WPF、分层、单进程、集中调度、一车一个 async Worker、内存任务不落库。
- 核心链：Collector→Generator→Scheduler→per-crane Channel→StateMachine→类型 Handler；执行期获取路权和源/目标预约，竞争失败回 Waiting。
- 工艺参与仅看有无启用设备；缺设备跳过，有设备离线时等待。默认 8 节点，不含货叉。
- 坐标：设备数据库 XYZ + 天车偏移；安全位不加偏移；斜床/研磨 ZDrop=Round(d/2)，其他=Round((d/2)/0.9537+(d/2)/0.866)。
- 到目标后释放源预约、缩小路权、先清源后清目标握手；成功回安全 X 后释放剩余资源。
- 故障统一丢弃并保留记录、清引用、释放逻辑资源、暂停线路、报警弹窗；取料前后不分支。
- 文档明确约定手动不与自动互斥，安全位空驶路线靠现场布局保证，改配置必须停线后重启；这些不是遗漏实现的自动需求。
- 点位：镗/研磨 R*2+1(0基)，打号 1基减1/FC04/UTF8，料架10000端口/254站号/FC02 bit0、bit1，斜床FOCAS8193，天车X11=63497。
- 文档已自述风险：热重载导致旧 Worker/新对象混用、Channel 清空后 Assigned 无回收；斜床配置缺失偏移静默0；顺序读天车延迟；Resize结果忽略。
- 待核对文档矛盾：架构§8.4/§10.2仍写调度直接赋 CurrentTask；§8.5.1尾仍写CAS未做；§8.10接口和物理双向确认描述可能过时；§6.1写斜床工艺不可编辑但§13.1允许；§8.8仍列采集报警而应用层明确无报警；§11缺Created/Paused。

## 源码初步核对
- App 启动先装载配置、启动 HostedService、订阅报警弹窗、700ms 后冷启动核对、最后显示窗口。加载失败后仍启动（Workers 可能为0）。
- ConfigurationLoader.LoadAsync 实际同步查库，先 Clear 各注册表和通道再读库；只装启用资源；不重建 Worker。
- Worker.StartAsync 捕获启动时 Crane 与 Channel；循环只在出队时判 IsRunning，RunFlowAsync 内无线路暂停门控。因此暂停只阻止新任务，不停止已执行流程；故障暂停也不取消其他车。
- StateMachine Acquire 在 try 之前，执行顺序与应用层文档大致一致。Transit 先 ReleaseSource 再清源握手，有窗口让另一车预约并启动源设备的新上料，需重点验证。
- SignalSnapshot.GetBool 缺失/类型不符返回false；CanReceive 对打号/下料只检查非null后取反。生成器另检查IsConnected挡住普通断开快照，但 connected缺点位仍误判。
- WaitDeviceAsync 只检查键存在、bool转换与期望，不检查连接/质量/时间/版本；不存在信号不能通过，但非bool值可能当false；需核对实际驱动类型。
- DeviceStateCollector WhenAll 后批量发布，没有独立整轮超时与Stale判定，慢设备可延迟健康设备的快照更新。
- ColdStartChecker 仅告警，不设阻塞；对天车有板告警后调度是否排除待核对。
- 已确认调度 eligible 只查空闲/覆盖范围/状态非null/无报警，不排除HasPlate=true或未知。冷启动提示“将不参与调度”与筛选实际行为不同。
- RuntimeTaskManager.Pause 用锁内检查 Waiting 再普通写 Paused；Scheduler CAS 不使用同一锁，两者存在 check/write 竞态。Remove 不做状态二次校验，需核对UI调用。
- 调度日志 Short(task.Id)是工件ID而非TransportTask.TaskId，与状态机任务短号不一致。
- 6个Handler基本符合现场寄存器文档与应用层逐步流程；Lathe缺配置静默0。源/目标双向有料确认并未逐一实现（以X11/X6/X7与设备握手为主）。
- CraneService.IsPressureLimitOnAsync 明确捕获IOException/SocketException后返回false；与“未知不当0”约定冲突，是优先问题。
- 修正：X2捕获的是Exception（也包含取消），不止IO/Socket异常。
- 新优先问题：Modbus ExchangeAsync 内部CancelAfter超时抛OperationCanceledException，未与调用者取消区分；CraneService运动/命令原样重抛OCE，Worker catch(OCE)永久退出。单次通信超时可导致整车Worker停摆，重启线路不能重建。
- 普通PulseAsync无finally；PulseMagnetAsync finally沿用已取消令牌，宿主取消/请求超时时触发位可能残留2；MoveAsync的finally使用None，仅运动位被覆盖。
- PollAxesAsync读到了ServoAlarm/PlcServoAlarm/PlcAlarm但从不检查，仅判断坐标/X2；运动过程中报警可不被立即发现。
- 大屏VM在Shell构造时已实例化并永久订阅ticker，无页面可见性门控、无刷新重入保护；每1秒启动一次全车读取，慢读可堆积并与自动运动争连接。
- Remove只靠UI缓存CanClear，服务无状态保护；可删除刚被CAS派发/执行的任务。
- dotnet --info输出SDK和运行时，但工作负载信息读取SCM遭沙箱拒绝；后续独立检查编译/测试。
- 本机回环诊断已复现：直接编译原ModbusTcpClient.cs，200ms请求超时，213ms后OperationCanceledException，调用令牌None且IsCancellationRequested=false，Connected=false。驱动超时误判取消确认；Worker永久退出由源码调用链确认。
- 完整解决方案构建通过：0警告/0错误；现有66测试通过。
- 斜床缺配置不只是“偏移默认为0”：Handler仍调用MoveRelativeAsync(0,0,0)，真实CraneService返回Rejected，发生取料则已充磁后弃板；假驱动总成功掩盖这一点。有效公式刚好算0也触发同问题。
- 料架文档写“同IP同采集器”，DriverFactory实际每个设备对象创建独立ModbusDeviceDriver/连接，未共享读取。
- 实际设备写异常会直接抛并立即Abort，不一定等后续握手超时；应用层文档§12对此表述有误。

## 最终产出
- 完整结论见 Docs/文档与源码分析报告_2026-10-08.md，共13项问题；每项区分代码证据、本机复现和现场待确认。
- 增补冷启动有板天车未排除、FOCAS失效会话与排队写取消、脉冲复位和协议/参数边界分析。
- 明确已披露热重载设计边界，以及暂停后仍有在途流程与界面即时重载之间的额外窗口。
- 报告52个本地文件引用均存在且行号未越界；已跟踪文件无修改。

## 授权修复结果
- Modbus内部超时与调用者取消分离；状态机和Worker仅在执行器令牌真的取消时走退出分支。
- 回归证明旧代码会丢失下一单的消费者：两种异常入口的恢复测试均失败；修改后均通过。
- 新增8项回归，当前74项全量通过；完整构建无警告/错误。停止Worker仍清理在途任务。
- 本次未增加循环监督/自动重建；仍保留人工恢复线路和Discarded记录。
- 后续现场约束已确认：运动报警和冷启动/恢复无板由PLC/现场保证；原问题3、7不再列待修复P1。
- 取消令牌作用域属于整个Worker，单单取消当前没有独立实现；删除throw仍会因while令牌条件退出。

## Docs全面更新基线
- 已实现：内部通信超时转TimeoutException；取料充磁调用前异常保留工件，调用后弃板；单次200ms充退磁脉冲与X6/X7反馈；取料Z到位500ms后单读X11。
- 已实现：到目标路权Resize同步卡片Runtime区间，上料架悬浮工件使用队头。
- 已确认：天车没有独立采集器，调度、大屏、手动页按需实时读；PLC运动报警联锁、现场人工取板及回安全位由现场负责。

- 当前源码没有数据库Protocol/Port；DriverFactory按类型固定协议/端口。
- 状态机初始化、Acquire均已纳入try；取料X11失败位于充磁标志之前。
- ColdStartChecker只Warning，未设置隔离标志；报警文案不等于实际调度过滤。
- Resize成功同步Runtime.LockedInterval，文档旧“忽略结果”已过期。

- 修正：Protocol/Port仍存在EF实体/列，DriverFactory不使用；字段是兼容保留，不是已删除。
- 料架同IP仍是每个设备独立ModbusDeviceDriver，不共享采集器；没有自动放料完成FC05。
- 寄存器文档斜床/研磨的取料顺序早于当前实现，需要改成先设备就绪、XY/Z、500ms单读X11、充磁、设备释放。

### 全面文档更新补充核对
- CraneService创建ModbusTcpClient未传Collection.CommunicationTimeoutMs，天车实际固定客户端默认3000ms；设备驱动工厂才使用该程序参数。
- BuildFailure仅含车名/路线/阶段/原因/充磁开始标记，没有LastStep字段，前序动作查步骤日志。
- ResourceState枚举仍存在，设备行ViewModel派生其展示值，未实现BlockedByPhysicalState隔离。
- 当前候选池实际类名CandidateTaskPool；Worker.StopAsync逐个WhenAny等待车循环。

## 2026-10-09 全项目恢复复查：边界与初始线索
- 只分析，排除PLC信号异常及既定现场实物处理。普通异常充磁边界、人工中止一律弃板为正确设计；已入队当前步骤继续执行按用户意图接受。
- Docs明确配置保存会重装注册表及驱动、清车通道，却不重建Worker/工件运行态；核对Assigned任务与旧设备工件引用能否恢复。
- 删除依赖UI缓存CanClear，Remove无二次校验；验证与调度CAS竞争。
- 检查大屏读取积压、线路暂停并发、UI同步事件、资源锁顺序。按证据筛选，不直接将设计观察当缺陷。

## 2026-10-09 全项目复查：最终证据
- 边界：只分析软件；未修改src、Tests正式文件或Docs，未提交。211份文件SHA256最终比较changed=0/added=0。临时诊断仅在忽略的tests/AutomatedLine.Tests/bin/project-review。
- 普通异常充磁前保板、充磁后弃板；人工中止一律弃板。成功/竞争回滚/异常路径能释放当前TransportTask的路权和预约。未找到必然托管锁死锁；不能由此证明所有线程交错皆安全。
- 源预约过早释放：CraneStateMachine.cs322释放源；339随后await源握手清零。两车可以先后获得同一设备，旧任务继续写新任务的点。用真实StateMachine、BoringHandler、LoadRackHandler、UnloadRackHandler，替代驱动及控制线程交错的关口复现：OldReservationReleased=True，NewWorkpieceStillOnDevice=True，新DataSentDone/LoadDone均1，旧清理后均0。PLC/原生SDK未调用，不声称已现场复现握手失败。建议源写入结束后释放预约。
- 过期按钮删除执行任务：MainViewModel.ClearTask只检查秒级缓存CanClear，RuntimeTaskManager.Remove无状态核验。真实VM命令复现CachedCanClear=True/ActualState=Running/Removed=True，车工件引用清掉，当前搬运及路权仍存在。资源未因删除自动释放不代表永不释放，状态机仍会尝试完成/异常清理；问题是运行中工件记录与归属先被移除。建议业务入口原子核验删除条件及派发互斥。
- 暂停与派发不互斥：RuntimeTaskManager.Pause在私有列表锁内检查Waiting后普通赋值Paused，调度Waiting->Assigned CAS不参加该锁。真实方法竞争复现两者均成功、状态Paused且已入通道；真实Worker继续进入该工件Pick。建议Pause也使用与派发一致的原子迁移，避免覆写Assigned。
- 配置热重载不完整（现行Docs明确要求改配置后重启）：加载先Clear两个运行注册表/驱动/通道，然后查询DB并建新实例；Worker只启动时取车引用。复现通道空、工件Assigned、UI启动/暂停/删除全部false；同ID新车Idle，旧车Running，GetCurrentExecution为空、TryAbortCurrentTask拒绝；新增车任务无人消费。设备CurrentWorkpiece亦随new丢失，生成器ResolveSource会拒绝旧工件。未调用真实Loader数据库读取，仅复现其使用的实际Clear/Register方法及真实Worker。遵守重启流程可避免热重载后继续运行，但页面没有强制；查询失败先破坏旧注册表为静态证据。建议配置应用与运行态切换统一设计，或强制空线/重启，不能仅重建Worker。
- 大屏刷新重入：ScreenViewModel85每Tick直接发RefreshAsync，无并发门；真实VM加延迟状态驱动，6次触发6个未完成请求。Modbus通信锁在请求取得锁后才启动超时；慢读下累积等待会拖慢自动流程。仅证明重入，没有实测现场积压量。建议上一轮完成再接受刷新或复用快照。
- 路权覆盖条件限制：Docs架构§8.7.1已说明仅源/目标区间；缩目标后Finish去SafeX未重新申请路径。真实ROW管理器：首车[17800,18200]时第二任务[9800,16200]取得成功，首车18000->0的扫掠区间[-200,18200]与其相交。只证明软件允许这种几何组合，不声称现场会碰撞；是否构成实际问题依赖共享轨道、配置和路径交叉。建议在会交叉的配置中保护去源和回安全位路径。
- 排除：PLC信号不符、现场实物异常、允许弃板、FOCAS当前已提交步骤继续执行、额外自动急停、重复幂等清理，不列为本次缺陷。
- 验证：dotnet build AutomatedLine.sln --no-restore --nologo -m:1 -p:UseSharedCompilation=false，0警告0错误；dotnet test --no-build --no-restore，115通过/0失败。定向诊断编译并执行退出0。

## 2026-10-10 修复状态更新
- 源预约提前释放与暂停/派发同时成功两项已按用户选择修复，见progress和task_plan最新段。
- 旧本机project-review诊断保留为调查记录，其SourceReleaseRace脚本按旧错误顺序编写，不能用于新实现的成功判据；正式回归以CraneStateMachineTests/RuntimeTaskManagerTests新增案例为准。
- 全量121项测试通过；其他调查结果不属于本轮修复范围。

## 2026-10-10 大屏重入问题修复
ScreenViewModel.RefreshAsync现在保护整轮刷新：上一轮未结束时跳过本次节拍，不等待、不排队、不补跑；结束或异常后在finally释放标记，下一个节拍继续刷新。6次触发的受控验证从6次未完成读取降为1次；正常结束、通信超时和快照异常后均可继续刷新。读取顺序与页面数据内容未变，手动页面数据共享不属于本次范围。提交1d44293 / bfb638b，Docs页面布局已同步。

## 2026-10-10 天车采集架构落地
四处展示/选车/启动核对共享Application后台快照，PLC数据不混入Crane.Runtime，两个页面维持独立ViewModel。后台按车独立发布，不受其他车等待影响；缓存拒绝未采集、失败、过期、停用及旧驱动。动作前后确认和运动底层保持即时读取；WaitCraneAsync当前无调用，保留实现。参数为500ms轮间等待和2000ms最大年龄；冷启动未知X11不判无板，等待超时记录未完成而不新增开线阻塞。实现bd73154，文档651afbe，136项测试及WPF绑定验证通过。

## 2026-10-11 文档与工作区核对
- 当前源码变化主要为学习注释和TaskAbortCoordinator系统动作原文调整；只更新Docs，不改这些源码。
- 文档明确错误：数据字典称活动报警保存首次/末次时间，但ActiveAlarm只有LastAt；暂停CAS说明遗漏；运行说明默认日志容量100MiB与AppOptions/示例及本地配置10MiB不符；参数页保存失败实际只有Console信息，无失败弹窗。
- 补全：异常MessageBox固定标题、内容/来源/动作/时间、充磁边界/人工中止/宿主取消/Worker兜底、启动订阅时机和其他UI提示；缓存按CraneId单例字典、地面设备筛选与两个Id字典、500ms采集与1s页面赋值/PropertyChanged的区分；参数动态XAML绑定与重载延迟；冷启动两处入口、并发等待及仅提示边界。

## 2026-10-14 料架地址与输入极性
- 用户现场确认：上料架DI1/10001/PDU0，原始1有板允许取料；下料架DI3/10003/PDU2，原始1无板允许放板。DI2不使用。
- 统一业务HasMaterial表示有料；下料架在DeviceModbusMaps配置InvertDiscreteInput=true，仅FC02成功读取后反转，其他设备默认不反转。候选、放料等待、大屏和冷启动继续读取归一化结果。
- 通信失败继续抛异常，由设备采集器发布失败快照，不转换为无板或允许。RackAddress旧UnloadRackHasPlate替换为明确的UnloadRackCanPlace=2。
- 本机真实ModbusDeviceDriver及映射通过TCP桩验证6例，全量178通过；文档说明原始值与页面“有料”并不相同。

## 2026-10-13 取料有限补降
- 五类Pick调用同一CheckPlateBeforeMagnetAsync，统一方法即可，未修改handler业务或底层充磁/运动API。
- 当前Z正向为下降，相对(0,0,+step)保留XY及斜床偏移；用成功指令量计数，实际显示/编码器位置另记录，不承诺实际下降恰好5mm。
- 默认额外3次×5mm，不含原位检查，总共最多4次专用X11读。每次运动成功后500ms再读；通信/运动失败与取消不继续补降，标志仍未充磁。
- 位置诊断使用即时ReadStatusAsync，未知明示，不能以诊断HasPlate或后台缓存替代专用X11；坐标诊断失败本身只Warn，调用者取消仍传播。
- 配置App.StateMachine.PickPlateRetryCount/PickPlateStepMm及参数页动态项接入，示例JSON更新，私有运行配置未改；15份Docs同步。

## ERP TXT 接收设计核对
- 来源默认D:\job1\job1.txt。用户明确写完批次后读取，不考虑生产者/消费者并发写入。
- 字段匹配当前WorkpieceInfo：PrintContent/PlateNo/SerialNo/Length/Diameter/PlugHole/LatheMachiningMode/PlugThickness。Length、Diameter、PlugThickness当前为double，SerialNo、PlugHole、LatheMachiningMode为int。
- 现有录入校验：版号非空、序号正整数、长度直径>0、堵孔70/100、斜床工艺1..10；未对打印内容非空/堵厚正数作现有录入限制，新增导入不应把这些说成已有校验。
- RuntimeTaskManager.Add不去重；ERP重复策略需新入口内原子处理，保留人工Add原逻辑。MainViewModel Changed通过Dispatcher同步刷新，业务监听应后台运行且停止按钮不得同步阻塞UI。
- 旧ERP接口额外固定工艺号/起始工序属于旧程序，不复制到当前既定工艺路线；当前身份Guid独立于版号/序号。

## ERP接收最终实现核对
- Application/Erp三文件：状态及接口契约、纯解析器、单循环宿主服务；Infrastructure/Erp/ErpJobFile负责磁盘。无FileSystemWatcher/PLC依赖，ERP写完批次约定按用户确认。
- 手动暂停仅变IsListening并唤醒循环；已进入的一批不取消。宿主取消尚未完成Read，进入添加后回写用None。Loop继续次批且当前批不重入。
- 原始批次+成功索引回执按路径保留，清理失败下一轮不重复添加，暂停恢复不清回执；文件内容变化重新解析并按现有身份去重。仅本进程有效，与任务总表现有内存生命周期一致。
- 三项JSON默认D:\job1\job1.txt/500/utf-8和参数页动态编辑绑定通过静态检查；主页面按钮ToggleErpListeningCommand，工作件Changed复用。没有新增自动Start调用。
- 全量224通过，新增30项；完整构建0/0，93份引用有效，基线233中只有预期14旧文件变化；本轮未Git提交。

## 2026-10-14 ERP批量接收UI卡顿检查（只分析）
- 已确认每行成功TryAddImported触发Changed，MainViewModel.OnTasksChanged同步Dispatcher.Invoke(RefreshTasks)，每次Tasks.Clear并重建全部行ViewModel；原注释误写“异步”。
- 本机独立x86 WPF Dispatcher诊断使用当前编译真实RuntimeTaskManager/MainViewModel，无窗口、无生产App.Startup/宿主/数据库/PLC。空列表50件：50通知/50清空/1275行VM创建；已有200件再加50：50通知/50清空/11275行VM；空列表100件：100通知/100清空/5050行VM。集合操作全部在UI线程。
- 文件读取在ERP宿主按整批异步读取，不逐件读TXT；逐条日志同步落盘可能增加导入耗时，但该探针隔离日志并未测真实DataGrid渲染或现场硬盘耗时。
- 建议ERP整批成功结果只通知一次；UI异步合并通知并保留/增量更新已有行，避免每行Clear重建。仅改Invoke为BeginInvoke仍可能排队大量整表刷新，不足以消除重复工作。
- 未修改生产源码/测试/现行Docs，未提交；临时诊断位于tests/AutomatedLine.Tests/bin/erp-ui-lag-check。
- 首次PowerShell加载32位生产程序集因当前进程架构不兼容失败，改为x86 dotnet+WPF诊断项目后成功；未把失败探针当复现结论。

## 2026-10-14 批量通知优化已验证
- 用户明确一次读整批一次刷新，方案缩小为仅ERP批量提交+一次Changed；保留MainViewModel原有UI刷新代码，不实现先前提议的异步合并/行复用。
- API返回结果与原输入对齐，null表示重复；任务表同锁一次处理批内/既有身份。Service先整批解析，批量提交后按原行索引设置Accepted并重建剩余行，错误顺序不变。
- 定向旧实现5次通知断言失败，修复后原30ERP通过；新增5回归后完整229通过，构建0/0。真实WPF探针三个用例都只1次Changed/Reset，分别50/250/100个行对象。
- 修复基线8文件中仅预期7变化，MainViewModel字节不变；42相关文档路径有效。没有现场帧耗时测量，不声称解决所有UI性能因素。未Git提交。

## 实际JSON维护偏好（用户2026-10-14明确要求）
以后新增JSON参数须补实际源码与运行目录JSON，不再创建appsettings.Example.json模板。已删除4份模板副本并移除复制项，AGENTS开头记录此约定。
本次两份实际JSON各补4字段并按AppOptions/LatheTransferOptions完整核对，现有参数及连接串均保留；源码/运行App值原本不同，没有互相覆盖。私有配置继续Git忽略，内容未打印。

## 2026-10-16 双头镗托料架协议回退构思（只分析，未实施）
- 最新用户图片只保留R6101/12203请求数据、R6102/12205数据完成、R6103/12207请求上料、R6106/12213上料最终通知、R6107/12215请求下料、R6110/12221下料完成。图中12213标注上料到位，但备注要求放板完成/磁铁回升后才发，逻辑继续用LoadDone。
- 删除每侧一组中间交互：Place不写12209、不读12211；Pick不写12217、不读12219。因此取消的是上料和下料各一写一读，不是改最终完成地址。
- Place设计：等12203→301/303/309三参数→12205=1→等12207→Z到放料位→退磁→Z回SafeZ→12213=1。
- Pick设计：等12215→源XY/SafeZ→Z下降→500ms/X11及有限补降→MagnetCommandStarted=true→充磁→Z回SafeZ→12221=1。
- 参数仍FC06单Word：长度Convert.ToInt32、外径Convert.ToInt32(value*10)、堵厚Convert.ToInt32，再转ushort；307内孔斜度和305内孔成活不写，原始double保留。
- 后续实施范围BoringHandler/地址常量/DeviceModbusMaps.Boring/清理清单及相关测试/Docs §7。清理只12205/12213/12221；采集只12203/12207/12215，大屏自动少两条反馈。12201模式点维持现状，不新增自动写入。
- 运动/充退磁底层、X11补降、充磁前保留/后弃板、人工中止/线路恢复和资源释放顺序保持；不改斜床/研磨协议。本轮未修改源码或现行Docs，等待流程设计确认。


2026-10-16：带托料架双头镗交互调整完成。Pick去掉中间到位/松开握手，充磁并回SafeZ后写12221；Place去掉中间到位/夹紧握手，退磁并回SafeZ后写12213。采集仅12203/12207/12215；复位仅12205/12213/12221。三参数301/303/309及1/10/1倍率不变。同步协议、应用层第7节、架构、通信、手动页、大屏和天车操作文档。旧实现下新成功用例2项因缺少卡钳反馈失败；修改后全量232项通过，整套构建0警告0错误。源码及实际运行目录配置值均核对保持原样；本次未提交Git。
