---
id: rug_controlled_visual_recovery
type: logic_node
inputs: [rug_ui_core, rug_ui]
outputs: []
tags: [sample, recovery, observation, trace]
---

# 人工恢复与原 Lua 继续（08C-02）

持久示例：[recovery-plugin/main.lua](recovery-plugin/main.lua)。与原 08C-01 分开保留。
使用真实 WGC、OpenCV、Gateway、共享输入仲裁、ClickObserved 和 PostMessage。
输入调用、目标接收、新画面确认、轨迹完整性分别断言。

## 自动验证

仓库根目录运行，需要对应 x64 原生 DLL：

```powershell
.\tests\run_controlled_recovery_sample.ps1 -Configuration Debug
.\tests\run_controlled_recovery_sample.ps1 -Configuration Release
```

每轮新建隔离安装、目标及数据目录。插件复制持久样例，仅 Lua 错误负例修改自己
的副本。退出删除窗口和安装副本，保留 report、PNG/JSON、轨迹。
**EXIT-01 已补充两配置自然退出 0 的验证**；历史非零报告保持不变。功能、UI 交互、资源清理及退出码仍分开记录，见[宿主退出说明](../../Rug.UI/Shutdown.md)。

## 业务和宿主契约

1. 红背景的 button 模板经真实定位提交 BEFORE 一次。目标实际收到正确区域的一对
   down/up 才显示 complete；Lua 新捕获匹配确认后请求
   `resolve_anomaly('controlled-visual-ready', context)`。
2. 生产协调器归档 PNG/v1 JSON，计划交还输入、保留 on_init 协程。无默认执行者。
   Return control 不继续；普通 Resume 拒绝；红色显式验证拒绝。
3. 目标按 Space 或宿主 repair，将背景改绿并重新显示 button；修复不等于批准恢复。
   必须显式 Verify recovery。安装/场景绑定的 FreshGreenFrameValidator 只读新 WGC
   捕获确认绿色面板，不获得输入能力，也不证明通用业务正确性。
4. 原 Lua 返回 resolved，检查局部 table nonce/phase、init_count=1、before=1。
   新捕获/定位/观察提交 AFTER 一次，再新捕获确认 complete。

hit.x/y+16 仅是模板中心，不是窗口定位常量。没有旧点击回退、重试、重跑 on_init
或检查点启动。真实 Lua、权限、捕获、归档及清理错误维持原有语义。

WinUI 组合根新增可信选项 `RUG_EXACT_BOUND_CAPTURE=1`，显式启用精确绑定窗口捕获，
未设置时默认不变。Lua/请求/manifest 无权设置它。仅固定安装
controlled_visual_recovery 与 controlled-visual-ready 场景注册绿色验证器，权限仍须授权。
测试宿主明确批准隔离副本权限；WinUI 通过真实对话框 UIA 授权并启用生产 Raw 监测。
带 Rug 标记的准星鼠标设置发生在会话前；业务输入仅 PostMessage。自动修复及 UIA
验证**不是新真人接管或业务 SendInput 验收，也不关闭 Hook 缺口**。

## 屏障与证据

八个场景：success（含仅交还、红拒绝、重复/并发验证）、stop-wait、revoke-wait、
stop-validation、revoke-validation、cancel-late、stop-late、lua-error。
屏障分别持有真实捕获服务任务与真实绿色验证结果，验证 Stop/撤权不会提前释放
资源，取消/停止优先于迟到成功。有限 UI 轮询只等待真实状态/事件，不替代决策。

比较 UpdateRecovery 前后的 incidentId、TraceLink、PNG 引用不变；保留 recoveryId、
验证代次与结果。异常截图是独立捕获。结构化模板结果关联前后确认帧；ClickObserved
关联 execution、来源、验证帧。离线要求 Valid、cleanup succeeded、actual work=0；
另检查帧池、租约、实例、捕获、恢复等待归零和 on_stop 一次。
成功为 BEFORE=1/1、AFTER=1/1、原协程继续与新画面确认；负例 AFTER=0/0、最终确认
NOT RUN，Lua 错误 Faulted。InstanceState 停止后正常清理，证据在停止前读取。

## 可选手动查看

先构建 Debug/x64 WinUI，再运行：

```powershell
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 --no-build -- --controlled-recovery-manual
```

1. 开发工具准星绑定图案目标，选择 Controlled visual recovery，运行绑定窗口任务并授权。
2. 等 BEFORE 1/1、等待人工及人工控制。只交还仍应等待，计数不增。
3. 红色点验证并继续应拒绝。目标按 Space（或终端 repair，仅自动变画面），交还后再验证。
4. 同一实例继续，总计 2/2。Stop 后打开轨迹与异常，刷新异常、选记录、定位关联轨迹。
5. 终端 status 仅报告实际总计数；done 清理自己的窗口、宿主及安装。

真人步骤未获用户报告前记为 NOT RUN。本单元不要求重做接管验收；实际结果见
[验收记录](RECOVERY-VALIDATION.md)。没有修改通用恢复/接管/观察时效/ROI/热更新协议，
没有 VLM、补偿、检查点重启或跨进程协程持久化。
