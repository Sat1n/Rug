# 08C-02 验证记录 — 2026-10-22

基线 628c01a；SDK 10.0.402，Windows 桌面，x64。本次只组合既有恢复能力，
没有重建冻结的 Hook 诊断包，也没有修改输入检测策略。

## 自动化功能（Debug / Release 均通过）

| 场景 | BEFORE down/up | AFTER down/up | 结果 |
| --- | --- | --- | --- |
| success | 1/1 | 1/1 | 仅交还不继续，红拒绝；绿验证后同协程继续，新画面确认 |
| stop-wait / revoke-wait | 1/1 | 0/0 | 等待中结束，不重放 |
| stop-validation / revoke-validation | 1/1 | 0/0 | 验证中关闭准入，真实捕获任务结束后才清理 |
| cancel-late / stop-late | 1/1 | 0/0 | 迟到绿色结果不恢复主线 |
| lua-error | 1/1 | 0/0 | Faulted，未转换为人工等待 |

success 在真实绿色验证返回前设置屏障，同时提交同 ID 重复与不同 ID 并发请求。
只有一次绿色验证（另一次为此前红色拒绝），继续后重发同 ID 仍不增加点击。
Lua 局部 table nonce/phase、init_count=1、before=1 与同一运行状态共同证明协程保留。
前后动作使用不同来源帧，各自后置确认也来自新捕获；没有 BEFORE 重放。
所有场景 on_stop=1、共享帧池/操作/租约/实例归零、capturer 关闭、恢复会话及建议
任务归零；离线轨迹 Valid、CleanupSucceeded=true、ActualWorkOutstanding=0。

实际 PNG/v1 JSON 在等待人工前生成。UpdateRecovery 后 incidentId、TraceLink、
PNG 引用保持不变；成功归档 verification=resolved，验证代次=2。Lua 错误未进入
resolve_anomaly，所以该负例没有伪造 incident/recovery 字段。

配置分离报告（有限元数据，不含像素/配置/脚本状态）：

- [Debug](evidence/recovery-debug-report.json)：成功 session
  `a73c7b6b-8804-42f8-9f2a-3b3a2424a3d7`，trace
  `1b38fd10-3f4e-431f-b432-f7bf56541f01`，incident
  `a499a60f-120a-4408-b4f1-03db92b44817`。
- [Release](evidence/recovery-release-report.json)：完整身份、恢复 ID、输入 execution、
  来源/验证/确认帧、清理及退出结果见文件。

完整事件与 PNG/JSON 保留在本机 artifacts/controlled-recovery-{Configuration}/
recovery-{invocation}/；报告中的 ID 可用于只读查询。这些生成物不承诺永久保留。

## 实际 WinUI / WGC 交互

两配置均使用实际宿主、生产 Raw 监测、真实授权对话框、准星绑定、Return control、
Verify recovery 和 Stop。红色拒绝后，自动控制端将独立目标改绿，一次显式验证继续，
实际总计 2/2；等待 AFTER 的新模板确认事件后停止。查看器选择实际异常并定位到
同一轨迹，退出后再检查持久化清理摘要。UIA 断言通过；不等于用户真人接管验收。

**进程正常退出未通过：** 两配置 Rug.UI.exe 均为 `-1073740286 / 0xC0000602`，
保留 `Attempt to execute managed code after the .NET runtime thread state has been destroyed`
及 ceemain.cpp:1721。完整样例命令仍非零，不能因功能/清理通过而记整体成功。

## 构建及回归命令

以下对 Debug、Release 分别执行：

```powershell
dotnet build Rug.UI/Rug.UI.csproj -c Debug -p:Platform=x64 --no-restore
dotnet build tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 --no-restore
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 --no-build
dotnet run --project Rug.UI.Tests/Rug.UI.Tests.csproj -c Debug -p:Platform=x64
pwsh -NoProfile -File tests/run_controlled_recovery_sample.ps1 -Configuration Debug
```

WinUI/Core/脚本构建两配置均 0 警告、0 错误；UI 核心 Schema/状态/热重载/轨迹详情
归属测试两配置通过。完整脚本回归两配置通过，包含 Gateway、授权、输入仲裁、
观察点击、生命周期、恢复、检查点、更新准备/切换及轨迹。
真实样例功能与 UIA 通过，但最后一条命令因上述 CLR 退出失败保持非零。

额外复跑原 08C-01 Debug 真实八场景：功能、接收计数、业务确认和只读查看器通过，
同样保留 CLR 非零退出。原 Release 08C-01 本轮未单独重跑；新恢复样例两配置均实测。

沿用的一个诊断限制：cancel-late 的 Gateway 返回 Cancelled；现有 06B 在等待验证任务
收到关联取消时可能将归档摘要记为 ValidationTimedOut/needs_human。报告保留该实际值，
没有将它冒充验证成功或修改协调器的分类；迟到结果不恢复原 Lua。

未执行：新真人操作验收、业务 SendInput、VLM/游戏、跨进程协程恢复。无新增原生
C++/C-ABI 改动，使用配置匹配的现有 DLL；Hook 独立缺口及既有验收限制保持开放。
