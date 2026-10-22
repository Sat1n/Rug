---
id: rug_controlled_visual_sample
type: logic_node
inputs: [rug_ui_core, rug_ui]
outputs: []
tags: [sample, wgc, template, observed-click, trace]
---

# 受控视觉业务闭环 — 3.5-08C-01

这是可以阅读和复用的插件示例，不是临时拼接 Lua。`plugin/` 中保留 manifest、
完整 Lua、最小 UI 和三张确定生成的 32×32 BMP 模板。`generate_assets.py` 可重新
生成相同模板；它们不是运行截图。示例不连接游戏、模型、MCP 或恢复服务。

## 最短运行方法

在仓库根目录，使用 SDK 10.0.402 和已有匹配配置的 `x64/Debug/Rug.Core.dll`。
最短入口会先构建托管宿主，再运行场景，保留底层失败退出码：

```powershell
.\tests\run_controlled_visual_sample.ps1 -Configuration Debug
```

等价的展开命令：

```powershell
dotnet build Rug.UI/Rug.UI.csproj -c Debug -p:Platform=x64 --no-restore
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 -- --controlled-visual-sample artifacts/controlled-visual-Debug
```

Release 将命令中的 Debug 全部改为 Release。输出目录下每次新增唯一的
`sample-<GUID>` 子目录，不覆盖旧结果。命令自行创建和关闭目标窗口，使用隔离插件
安装副本；不会修改正常插件目录。测试宿主明确授予这份临时安装的四项声明权限，
并在控制台说明此事；这不是生产自动授权机制。无需键鼠操作或人工接管验收。

**已知退出限制仍保留：** 当前两种配置的功能断言及查看器检查通过，但 WinUI
退出出现 CLR `0xC0000602`，所以完整命令仍返回非零。不要把它改成成功或据此说
阶段收尾已完成。`report.json` 分开保留功能、查看器交互及进程退出结果。

## 插件与宿主契约

`main.lua`：真实捕获 → `rug.find_image('button.bmp', .99)` → 取命中结果实际的
observation/frame/引用和坐标 → `rug.click_observed` **一次** → 新捕获识别完成模板。
业务位置不是硬编码：`hit.x/y + 16` 只表示已定位的 32×32 模板中心。测试目标的位置
逐场景改变。没有旧 `rug.click` 回退、重新定位或点击重试。

插件只获视觉、输入及计时能力，不读取目标内部状态、接收计数或测试控制端答案。
`wait_image('complete.bmp',1500,.99,25)` 每次重新捕获，只有只读确认会轮询。
没有新帧或未匹配时只报告 `unconfirmed_timeout`，真实捕获/服务错误仍按原语义失败。
确认成功必须具有与点击来源不同的新 frameId。`input_status` 与 `business` 状态分开；
不能把输入完成当作业务完成。

正向宿主显式使用 `CaptureService(exactBoundWindow:true)`、真实 `TemplateMatchService`、
`InputService`、Lua、Gateway、生产授权及共享输入仲裁。模式固定 PostMessage；
`roi-consistency-v1` 仍为原有 1000ms 来源上限、750ms 验证期限及 17×17 ROI。
没有放宽策略、忽略窗口或新的输入锁。

普通 WinUI 的默认捕获工厂保持不变；直接将本插件复制到普通安装目录，不能据此
声称已经启用精确窗口捕获。可复用的配置范例位于
[受控宿主](../../tests/Rug.UI.Core.Script.Tests/ControlledVisualSample.cs#class:ControlledVisualSample)，
迁入其他宿主时须由可信组合根明确配置精确窗口路径、授权和绑定，不能让 Lua
声明 `verified=true`。恢复及热更新协议没有变化。

## 独立目标与确定顺序

[目标窗口](../../Rug.UI.Core/Native/CaptureGeometryDiagnosticWindow.cs#class:CaptureGeometryDiagnosticWindow)
由验证宿主创建，拥有独立消息线程、GDI 画面及实际消息计数。每个场景新建一个窗口，
计数从零开始；识别位置与其他场景不同。初始展示 button 模板，只有正确区域的按下
和释放都实际到达才展示 complete 模板和明确的 COMPLETED 标识。控制端可以切换 blocked 模板或 resize；控制端
与计数均不暴露给 Lua。原几何诊断的默认模式不变。

测试装饰器先调用真实 OpenCV，再在返回 Lua 前阻塞，确保后续变化发生在定位之后。
除调度屏障与有界证据外，它不改变匹配结果或画面。过期场景比较来源的真实 WGC
QPC 时间与可信当前 QPC，达到上限后才放行；不是猜测一个 sleep。失效场景用既有
宿主外部输入作用域退休来源资格，不伪装为真人输入。

Stop/撤权发生在这个在途识别屏障：先关闭业务准入，证明停止尚未完成且帧/工作
持有仍有效，再放行实际任务并等待清理。因此这两个场景的 ClickObserved 是
`NOT RUN`，不是成功执行后被回滚。已准入输入中的取消和必要释放仍由既有专项覆盖。

## 四种独立结果

| 场景 | 输入 API | 目标 down/up | 新画面业务确认 | 离线轨迹 |
| --- | --- | --- | --- | --- |
| stable | completed / RoiConsistent | 1/1 | confirmed，新 frameId | Valid |
| changed | rejected / SceneChanged | 0/0 | not_confirmed | Valid |
| resize | rejected / GeometryChanged | 0/0 | not_confirmed | Valid |
| stale | rejected / Stale | 0/0 | not_confirmed | Valid |
| invalidated | rejected / InvalidReference | 0/0 | not_confirmed | Valid |
| stop / revoked | NOT RUN | 0/0 | NOT RUN | Valid |
| no-completion（额外负例） | completed / RoiConsistent | 1/1 | unconfirmed_timeout | Valid |

最后一行专门证明：API 完成、目标收到输入，也仍可能没有业务完成证据。接收器
坐标在报告中单独列出，无按下/释放时坐标是 null，不用 (0,0) 冒充实际接收。

模板匹配的结构化事实使用既有轨迹 schema/kind：`Observation`，reason 为
`template-match:result`，status 为 `Matched` 或 `NoMatch`，附实际来源 observation/frame。
Matched 关联返回的识别观察；NoMatch 关联实际持有的捕获观察，不伪造识别成功。
通用事件不包含路径、模板图像、日志全文或脚本状态，也不替插件宣布业务成功。
固定示例及测试宿主将完成模板的真实匹配和新帧关联成独立业务确认报告。

## 查看结果

命令会打开现有 WinUI 只读查看器，选择稳定场景的历史轨迹并验证 `Valid`。它使用
空插件目录，不能复活旧任务、恢复授权或获得实时帧。关闭查看器/宿主后的异常退出
继续如实记录。重看时可在 PowerShell 中指定已生成目录：

```powershell
$env:RUG_TRACE_ROOT = (Resolve-Path 'artifacts/controlled-visual-Debug/sample-这里替换成实际GUID/traces').Path
$env:RUG_PLUGINS_ROOT = (Resolve-Path 'artifacts/controlled-visual-Debug/sample-这里替换成实际GUID/empty-plugins').Path
& .\Rug.UI\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\Rug.UI.exe
```

点击底部“轨迹与异常”→“刷新轨迹列表”→选择报告中 session/trace 对应的“文件”行。
列表可滚动；按 report.json 的 operationId 过滤 ClickObserved，查看 execution、ROI
验证、调用方返回、实际工作结束与清理。清空过滤可查看前后两次 FindImage 和新帧。
使用完可移除这两个进程环境变量，避免后续宿主沿用测试目录。

保留产物只有有界报告和正常生产轨迹；安装副本在结束后移除，窗口/线程和帧释放。
共享帧池、输入 active/waiting、捕获服务、实例和实际工作均必须排空；轨迹摘要
cleanupSucceeded=true、ActualWorkOutstanding=0。文件的 Valid 不替代 API、接收器或
业务结果。轨迹仍受现有保留/容量策略约束，不是永久审计仓库。

## 验证记录与边界

2026-10-22，Debug/Release 均以真实 WGC、OpenCV、PostMessage 完成上述八场景；
稳定场景实际中心为 (161,141)，完成确认帧不同于来源。实际完整身份与结果保留在
[Debug 报告](evidence/debug-report.json)、[Release 报告](evidence/release-report.json)。
[测试索引](../../tests/Rug.UI.Core.Script.Tests/README.md#08c-01-controlled-visual-business-sample)
区分功能、回归和退出结果。两份报告是实际记录，不是 Fake 观察。

不新增截图保存，不测试真实 SendInput 或重新验收真人接管，不关闭独立 Hook 缺口。
PostMessage 成功仅证明这个受控窗口的链路，不承诺任意应用兼容。ROI 一致性和
完成模板只证明固定局部图案条件，不建立 OS 动作与业务状态的原子性，也不证明
所有外部窗口竞态已经消失。跨进程目标异常、真实采集无帧及 SendInput 不在本次
真实负例覆盖中；既有相关单元语义保持原状。
