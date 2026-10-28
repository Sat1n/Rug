---
id: rug_production_input_acceptance
type: logic_node
inputs: [rug_ui_core, rug_script_tests]
outputs: []
tags: [diagnostics, raw-input, acceptance]
---

# 05B 生产事件源：四项独立验收

固定 [原生反转证据包](../NativeInputProbe/RegistrationReversalReview.md)及其
五份二进制，不重新构建原生项目，不重复 ON/OFF/ON，不运行 A/B/C 对照。
本轮只使用原托管诊断入口：实际 InputService → 固定 Rug.Core → 隔离
目标窗口，实际 RawInputEventSource 使用原来的 INPUTSINK | DEVNOTIFY
注册、分类、缓冲和清理实现。自动接管仍关闭。

[RawInputDiagnostics](RawInputDiagnostics.cs#class:RawInputDiagnostics)只补
消费端诊断聚合、既有目标计数读取和分项判定；未改发送动作、软件标记、
分类、Hook 安装/回调/转发、注册或接管逻辑。
[ProductionInputEvidence](ProductionInputEvidence.cs#class:ProductionInputEvidence)
保留不可变计数快照，真人键只计数量；仅已带 Rug 标记的测试 F8/B 分开计数。
聚合在事件源消费者线程中执行，不增加消息回调的工作或事件订阅数量。

Current scope clarification: this document preserves the 05B production-source
diagnostic sessions. Its “no takeover” statements describe that fixture/stage;
later 05B-02 production integration and user confirmation are recorded in
[InputIntervention](../../Rug.UI.Core/InputIntervention.md) and the
[stage matrix](../../docs/phase3.5/ACCEPTANCE.md). The independent Hook gap remains open.

## 一次完整会话，返回一份输出和视觉确认

在仓库目录中，先只构建托管项目，再运行：

```powershell
dotnet build F:/Cardinal/Rug/Rug/tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 --no-restore -warnaserror
dotnet run --project F:/Cardinal/Rug/Rug/tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 --no-build -- --raw-input-diagnostic
```

1. 关闭旧诊断进程。无需原生 A/B/C，也无需管理员对照。等待 READY/STARTED；
   输出实际加载 Core 路径、大小、文件哈希、注册 HWND/flags 和来源代次。
   queriedFlags 可能只显示 INPUTSINK；原请求仍是 0x2100，不调整注册。
2. HUMAN BEFORE：在白色隔离窗口前台按下/释放/保持普通键，移动、点击、
   滚轮；切到另一个普通应用，再做键盘和鼠标操作，检查快捷键和窗口操作
   正常。原窗口空白是预期。回终端输入 `next`。
3. 五秒倒计时内点击隔离窗口标题栏，释放所有键和按钮；随后四轮 AUTO
   全程不操作。顺序为 SendInput/F8、SendInput/B、PostMessage/F8、
   PostMessage/B。不要在某轮 PASS/INCOMPLETE 后提前操作。
4. 等明确的 HUMAN AFTER 提示，再重复第 2 步的前台/后台真人操作；回终端
   输入 `done`，等待 STOPPED、EVENT_SOURCE_CONTINUITY 和 PROBE STOPPED。
5. 返回从 CORE LOADED 到清理结束的完整输出，并明确说明普通输入、快捷键、
   窗口操作是否正常，以及 AUTO 是否误触键鼠。无需第二观察终端。

用户已执行此输出版本，最新同会话证据及人工反馈见下方“用户原托管会话”。
假事件单元测试只验证计数器/判定，不替代真人或实际 Rug 输入证据。

## 分项判定规则

| 验证项 | 输出及验收条件 | 未采集/异常情况 |
|---|---|---|
| 目标送达 | 每轮 TARGET_DELIVERY：指定 F8/B down/up 精确为 1/1；保留通用键鼠计数 | 特定键 0/1 即 INCOMPLETE，通用 1/1 不能补齐；前台被阻止为 NOT RUN |
| 自身输入归因 | 每轮 SELF_INPUT_ATTRIBUTION + raw/production DELTA：SendInput 软件键盘/鼠标=2/5、键 down/up=1/1、移动/按钮 down/up=2/1/2，指定软件 F8 或 B=1/1、另一键=0/0，候选键鼠=0/0 | 计数缺失/多余、标签丢失均 INCOMPLETE；独立未标记 Hook 活动为 INTERFERED/NOT RUN，不自动归因软件误报 |
| 真人检测 | HUMAN BEFORE END 和 HUMAN AFTER END 的 production DELTA：各自 ForegroundKeyboard、BackgroundKeyboard、ForegroundMouse、BackgroundMouse 均大于零；加用户正常操作确认 | 缺任何组合为 NOT RUN；normalOperation 在日志中为 NOT COLLECTED，必须由用户另外确认 |
| 事件源连续性 | 同代次，无 ForeignGenerations、故障、溢出或 consumerErrors；EVENT_SOURCE_CONTINUITY 输出停止状态、空缓冲、零订阅、零注册；StopAsync 已等待接收器/消费者结束 | 没有完整退出输出为 NOT COLLECTED；故障/代次变化/清理失败为 INCOMPLETE |

PostMessage 两轮本来不进入 Raw Input，应分别为软件键鼠=0/0、候选键鼠
=0/0，同时指定目标收到 1/1。不能要求 PostMessage 产生软件 Raw 数据。
四轮全部执行时，SendInput 合计软件键盘/鼠标=4/10、键 down/up=2/2、
移动/按钮 down/up=4/2/4；阶段快照包括其阶段边界内的活动，不能混入
倒计时的人工聚焦操作作 AUTO 判定。

## Hook 与进程退出码另列

HOOK_OBSERVATION 与完整回调计数继续输出。SendInput 键盘入口为零或
带标记注入序列不完整，仍为 INCOMPLETE / independent probe failure；
原 Hook 失败检查继续使退出码非零，不隐藏、删除或改写成成功。
生产 SELF_INPUT_ATTRIBUTION 可以与 Hook INCOMPLETE 同时出现，它们
证明不同事项。LEGACY_COMBINED_CHECK 仅保留旧目标+Raw+Hook 合并检查，
不是四项验收结论。不要只以退出码或任意一行 PASS 判整个会话通过。
软件回合前台激活受阻时没有发输入，三项回合证据明确为 NOT RUN。
同步屏障成功只证明队列推进，不证明 Hook 仍有效。

正常操作、来源可信性及推进条件由用户共同复核；没有 Rug 标记只表示候选
外部输入，不是认证真人。就算生产四项满足，Hook 缺口仍须单独保留。
本单元不调用 TakeOverAsync、不停止自动化任务或改变交还逻辑。

## 开发 agent 验证记录（早于用户会话）

2026-10-10 托管 Debug/x64 构建（warnings-as-errors）0 警告/0 错误。
正常 Script.Tests 全套通过：Lua/调度器/状态、Gateway/截图缓存、授权、
输入仲裁、Raw 解析/生命周期及新增聚合判定回归。真人仍 **NOT RUN**。

一次非交互真实路径检查（PID 57704）结果分开记录：

| 项目 | 结果 |
|---|---|
| 目标送达 | PostMessage/F8、B 指定 down/up 各 1/1，通过；SendInput 两轮前台激活被阻止，NOT RUN，无输入发送 |
| 自身输入归因 | PostMessage 两轮 Raw 软件/候选键鼠均为零，符合预期；SendInput 标签/分类 NOT RUN，不能由 PostMessage 推断 |
| 真人检测 | NOT RUN；没有 HUMAN BEFORE/AFTER，也没有普通操作视觉确认 |
| 事件源连续性 | 此非交互会话 generation=1，fault/overflow/consumerErrors=0，停止后 buffered/subscribers/registrations=0；真人会话连续性尚待采集 |
| Hook（另列） | 安装成功，PostMessage 没有软件 Hook 事件，线程最终 WM_QUIT；SendInput 观察 NOT RUN，不证明原 Hook 缺口恢复 |

实际加载 Core 文件哈希与冻结值相同，注册两类设备 queriedFlags=0x100，
原请求仍为 0x2100。完整开发检查输出在构建目录的
`bin/x64/Debug/net10.0/production-self-test.log`（未入库，非真人记录）。
诊断非零退出是 SendInput 未运行所致，不改为总 PASS。Debug/x64 WinUI
构建也为 0 警告/0 错误；UI 真人流程本轮 NOT RUN。原生五份二进制哈希
核对未变，反转复核包未修改。

## 用户原托管会话（PID 33724）

2026-10-10 复核用户附件 `cc4c73b3-fda3-4ecd-b8a9-fcaba22f8102/Pasted text.txt`，
SHA256 `249603FF37D187455646242320700209E3262FBD1CEA43C4BCCC13461A2C55A6`。
运行已有托管诊断二进制（`--no-build -- --raw-input-diagnostic`），不是原生
ON/OFF/ON 对照。实际路径为 InputService → 固定 Rug.Core → HWND 0xD00C30，
生产 RawInputEventSource 代次 1，注册请求仍为 0x2100，键鼠接收 HWND
0xE90CD2，查询 flags 为 0x100。加载 Core 为 2649600 字节，SHA256
`5E0C9B3F75A49DBA002436B2E438A440E493EEB01B4263E08B787D4FE475A405`。

| 验证项 | 本会话结果及独立证据 |
|---|---|
| 目标送达 | PASS：SendInput/F8、SendInput/B、PostMessage/F8、PostMessage/B 的指定键 down/up 均为 1/1 |
| 自身输入归因 | PASS：每轮 SendInput 软件键盘/鼠标=2/5、键 down/up=1/1、移动/按钮 down/up=2/1/2，指定软件 F8/B 各 1/1；四轮 AUTO 候选键鼠=0/0；PostMessage 软件/候选计数均为零 |
| 真人检测 | BEFORE/AFTER 计数完整：同一会话各自四类前后台键鼠均大于零。用户复核后反馈“真人检测这里应该没问题”；正常操作采用用户反馈，不由日志推断，也不增加未单独描述的快捷键或窗口动作结论 |
| 事件源连续性 | PASS：代次 1→1，fault=None，overflow/consumerErrors/foreignGenerations=0；停止后 buffered/subscribers/registrations=0；诊断线程以 WM_QUIT 退出、无异常 |

| 真人阶段 | 前台键盘 | 后台键盘 | 前台鼠标 | 后台鼠标 |
|---|---|---|---|---|
| HUMAN BEFORE | 118 | 75 | 3502 | 920 |
| HUMAN AFTER | 44 | 25 | 1669 | 536 |

AUTO 合计软件键盘/鼠标=4/10、键 down/up=2/2、移动/按钮 down/up=4/2/4，
软件 F8/B 各 down/up=1/1；没有候选输入或未标记 Hook 活动的轮内干扰。
倒计时聚焦动作不混入 AUTO。普通应用的真人按键内容或轨迹不入库。

**Hook 观察单独保留为 INCOMPLETE**：同代次键盘 Hook 在 HUMAN BEFORE
入口 75 次、AUTO 0 次、HUMAN AFTER 25 次。两轮 SendInput 键盘均
callbackTotal/actionableTotal/taggedInjected=0；鼠标标记每轮 5 次。
PostMessage 没有软件 Hook 事件符合预期。现有 Hook 失败检查保留，不能将
非零退出改成总 PASS，也不能用生产分类通过声称 Hook 缺口修复。

此会话的 B 目标送达完整，不证明冻结原生 OFF 轮 B down/up=0/1 的独立
异常已修复。原生反转包和五份固定二进制未修改、未重建；本次只复核
附件和文件哈希，没有重跑输入、构建或功能测试。

生产四项证据与人工反馈现已归档，**不等于整个 05B 已无保留验收通过**。
Hook 缺口继续作为独立未解决项，自动接管仍关闭；是否进入后续集成由
用户与宿主共同确认。本次不新增人工干预策略，不启用监听或接管。
