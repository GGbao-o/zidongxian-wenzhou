# 取料有板单次检查实施计划

本次按用户已明确的步骤实施，并在当前会话完成验证与提交。

**目标**：五种源设备取料 Z 到位后等 500ms，只读一次 X11；无板或通信失败仍属于充磁前，保留工件。

**方案**：`ICraneDriver.ReadHasPlateAsync` 隔离地址，`CraneService` 复用已有单线圈读取，`TransferContext` 统一等待与校验，各 `PickAsync` 在充磁标记之前调用。其他等待和充磁异常分界沿用现有行为。

**技术**：C# / .NET 8 / xUnit / Modbus TCP。

- [x] 给 `ICraneDriver` 和 `CraneService` 增加 `Task<bool> ReadHasPlateAsync(CancellationToken)`，后者调用 `ReadCoilAsync(CraneAddress.X11_HasPlate, cancellationToken)`，通信失败继续抛出。
- [x] `TransferContext.CheckPlateBeforeMagnetAsync` 执行 `Task.Delay(500, cancellationToken)`，随后单次读取；返回 false 时抛带 `X11=0` 的 `InvalidOperationException`。
- [x] `LoadRackHandler`、`BoringHandler`、`LatheHandler`、`GrinderHandler`、`MarkerHandler` 的取料 X11 轮询替换成 `await ctx.CheckPlateBeforeMagnetAsync(cancellationToken)`；检查通过后才设置 `MagnetCommandStarted`。
- [x] `PickPlateCheckTests` 覆盖五种源设备成功、无板、通信失败，校验等待时序、一次读取及失败未充磁。
- [x] `CraneMagnetPulseTests` 用本机 Modbus 服务确认真实入口只读一次 X11。
- [x] `CraneStateMachineTests` 确认无板和通信失败保留源引用、位置、工序、排位，释放资源并暂停，恢复后同一工件可执行。
- [x] 运行 `dotnet build AutomatedLine.sln --no-restore --nologo -m:1 -p:UseSharedCompilation=false`，要求零错误。
- [x] 运行 `dotnet test tests/AutomatedLine.Tests/AutomatedLine.Tests.csproj --no-build --no-restore --nologo`，要求全部通过。
- [x] 同步《应用层设计.md》《架构设计文档.md》，按仓库格式分别提交代码和文档，保留用户未提交改动。

验证结果：完整解决方案编译成功，0 警告、0 错误；完整测试通过 108/108。
