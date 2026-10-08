# 通信超时与执行器取消修复 Implementation Plan

> **For agentic workers:** 按当前会话已批准的方案在本会话逐步执行，使用 verification-before-completion 校验结果。

**Goal:** 通信超时只中止当前搬运，人工恢复后天车可以继续执行新任务。

**Architecture:** 在通信边界区分超时与外部取消；在状态机及 Worker 两层用调用者令牌判断退出资格。保留现有统一弃板和故障暂停。

**Tech Stack:** C# / .NET 8 / xUnit / 本机 TCP 回环服务。

---

## 1. 先增加回归验证

- [x] 新增 tests/AutomatedLine.Tests/ModbusTcpClientTests.cs，编译链接原客户端文件，不引入 MySQL/FOCAS 依赖。
- [x] 新增 tests/AutomatedLine.Tests/CraneWorkerTests.cs，覆盖流程内及入口的非调用者取消、下一单完成、真实取消和停机清理。
- [x] 执行 dotnet test tests/AutomatedLine.Tests/AutomatedLine.Tests.csproj --no-restore --filter "FullyQualifiedName~ModbusTcpClientTests|FullyQualifiedName~CraneWorkerTests" --verbosity minimal，确认原代码的超时分类和下一单完成断言失败。

## 2. 修改通信边界和退出条件

- [x] src/AutomatedLine.Infrastructure/Communication/ModbusTcpClient.cs：将自动连接纳入请求异常处理；先捕获内部取消并断开：

```csharp
catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
{
    await DisconnectAsync().ConfigureAwait(false);
    throw new TimeoutException($"Modbus 请求 {_ip}:{_port} 超时（{_timeoutMs}ms）", ex);
}
```

- [x] src/AutomatedLine.Application/Workers/CraneStateMachine.cs 和 CraneWorker.cs：取消分支改为以下条件，并同步中文意图注释：

```csharp
catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
```

- [x] 重跑定向回归，确认分类、重连、恢复下一单及真实取消行为。

## 3. 文档与交付

- [x] 更新 Docs/应用层设计.md §5、Docs/架构设计文档.md 版本及异常边界；分析报告保留原始复现并注明问题 1 已修复。
- [x] 执行全量 dotnet test 与 dotnet build AutomatedLine.sln --no-restore --verbosity minimal。
- [x] 检查 git diff --check、修改范围及新测试断言；分别暂存修复/测试和文档。
- 修复提交为 `1918cf4`；此计划随文档独立提交。每次生成提交信息前执行 git diff --staged 和 git log --oneline -5，提交后核对最终 git status。

## 执行结果

新增 8 项回归：旧实现 5 失败、3 通过，修复后 8/8 通过。全量测试 74/74 通过，完整解决方案构建 0 警告、0 错误；未执行现场设备通信。
