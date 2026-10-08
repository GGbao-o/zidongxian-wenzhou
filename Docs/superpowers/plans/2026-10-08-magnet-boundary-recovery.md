# 天车异常分流实施记录

依据：[已确认设计](../specs/2026-10-08-magnet-boundary-recovery-design.md)。

1. 新增回归测试，验证充磁前保留设备/队列工件；旧实现下两项测试失败，证明原先统一弃板。
2. 各设备 PickAsync 在调用充磁前置标记，底层充磁方法与驱动接口保持原样。
3. 协调器增加保留路径，共享暂停、资源释放与报警；初始化纳入状态机保护。
4. 保留取料异常分流与生成器→调度器→Worker 恢复测试，覆盖真正取消和充磁后失败。
5. 同步应用层与架构文档，代码与文档分别提交 Git，保留工作区原有改动。

验证结果：`dotnet build AutomatedLine.sln --no-restore --nologo -m:1 -p:UseSharedCompilation=false` 通过，0 警告、0 错误；全部 85 项测试通过。本机测试不访问现场 PLC。
