---
id: rug_native_registration_reversal_review
type: logic_node
inputs: [rug_native_two_process_registration_control]
outputs: []
tags: [diagnostics, evidence, expert-review]
---

# A+B 注册反转：专家复核包（2026-10-10）

## 当前问题与结论边界

相同 Rug.Core C-ABI 发送路径、纯原生 A+B 宿主下，B 的键盘 Raw Input
注册 ON → OFF → ON，对应 A/B 带标记键盘 Hook 观察缺失 → 恢复 → 缺失。
F8 三轮发送成功且目标按下/释放完整，是当前最强的反转证据。
这支持“与注册条件相关”，尚不能解释原因或证明生产事件源失效。

OFF 的 SendInput/B 另有目标按下缺失：Hook 完整，指定 B 目标计数 0/1。
OFF 并非整套验收通过；该问题不得混入 Hook 缺失结论或归为真人干扰。
自动接管仍关闭，原始托管集成失败场景尚未闭环。

## 固定布局与复现

源码基线 `a8ac507487b145812a902510932aa2188731300e`；本轮磁盘核对
[五份固定二进制 SHA256](TwoProcessControl.md#fixed-experiment-contract)
全部相符，没有重新构建。B 日志记录已加载 Hook/Raw DLL 哈希；A 仅记录
已加载 Core 路径和 2649600 字节，不能替代运行时 Core 哈希。

```text
A（先启动，纯 C++）
  工作线程 → Rug.Core.dll C-ABI → SendInput / PostMessage → B 目标
  专用消息线程 → WH_KEYBOARD_LL / WH_MOUSE_LL → A 聚合计数
B（后启动，纯 C++，不加载 Core/CLR，不发输入）
  目标线程 → 普通可见窗口 → 指定 F8/B 按下/释放计数
  Hook 线程 → 消息窗口 + Rug.NativeKeyboardHook.dll → B 聚合计数
  Raw 线程 → 消息窗口 + Rug.NativeRawReceiver.dll → WM_INPUT/清理计数
```

无 C，Raw/目标拥有者均为 B。注册键盘 usage page=1、usage=6、flags=0，
显式 hwndTarget 为 Raw 接收窗口；INPUTSINK、DEVNOTIFY 均关闭。
OFF 保留 DLL、线程、窗口、定时器、屏障和清理，只不调用注册。
receiverReady=1 在 OFF 表示基础设施就绪，不表示设备已注册。
Hook 安装 hmod 等于回调 DLL 模块，attach 在线程内完成，无自动重装。

```powershell
# A：先等待 MANAGED TARGET 提示（历史名称；此处 B 仍是原生）。
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeInputProbe.exe --managed-target
# B：各轮仅将 raw-registration 设为 on / off / on。
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeRoleProbe.exe --role-b --raw-owner B --target-owner B --core-load off --raw-registration on --device-notify off --input-sink off
```

每轮新进程：B PREPARED → B install/INSTALLED → 绑定 B HWND/PID 到 A
→ HUMAN BEFORE → B baseline → A next → 五秒内聚焦 B 并释放键鼠
→ 四轮 AUTO 全程不操作 → A HUMAN AFTER 提示后 B snapshot
→ HUMAN AFTER → A done → B snapshot/done。保持外部程序和提权条件固定。
旧 B 提示提及 B/C，不要求本实验启动 C。
[操作和有效性门禁](TwoProcessRegistrationControl.md)已执行，不要求再重复。

## 六份原日志与分开的证据

附件目录内文件均为 `Pasted text.txt`。原日志保留在用户附件中，仓库仅
保存汇总与校验值，不保存真人按键序列或鼠标轨迹。

| 编号 | 条件 | 附件 ID | PID |
|---|---|---|---|
| 1A | ON | 4ad2ae79-7b7f-4c79-82fd-5930a3e42ed2 | 55004 |
| 1B | ON | f6b6e746-3de4-417b-a251-c83c6da0f167 | 54592 |
| 2A | OFF | 29e3a615-bc16-4059-94d7-f77a6ee52c63 | 32560 |
| 2B | OFF | 17804cdc-68f3-4736-a439-a689615762c0 | 15096 |
| 3A | ON | 1363b62a-0f11-4d24-bbc2-f9db43ccc9a6 | 59312 |
| 3B | ON | d3a02ac8-e363-4347-9fb0-f5cbb7cf7c0e | 6476 |

| 原文件 | SHA256（本轮读取时计算） |
|---|---|
| 1A | B259A0545C941B536D6795F504051FE00B0225DEE5EC54B72A19AEB69C8175F5 |
| 1B | 5B046A57A06D2AEB15E2A8701411B6772626C505E9AA280F5F9710D657A8D94A |
| 2A | CC9F17E56761A3B09C8D378D574B9F26130D83D16877D99516422E7A5B11489F |
| 2B | 2E21CA7DDF2FF87BC00D02EC2165E4703A12D51F02560C53FBAAEA33233920C2 |
| 3A | E78B1A8AA781C225C1050AEE9950257D504513D3CE3EB888194A84B66B45423E |
| 3B | 7B43C1EABDDEA4012F76DFA4634FD4AF3A4F4AD63E7AE6BC84BCCCC5AE7FB19C |

| 独立观察项 | ON 1 | OFF 2 | ON 3 |
|---|---|---|---|
| 实际发送记录 | 8 次 requested=1/returned=1 | 同左 | 同左 |
| SendInput/F8 指定目标 down/up | 1/1 | 1/1 | 1/1 |
| SendInput/B 指定目标 down/up | 1/1 | **0/1，未解决** | 1/1 |
| 两轮 PostMessage 指定目标 | 各 1/1，PASS | 各 1/1，PASS | 各 1/1，PASS |
| A 两轮 SendInput 键盘入口增量 | 各 0 | 各 2，均 taggedInjected | 各 0 |
| B 首次 snapshot 带标记 F8/B | 各 0/0 | 各 1/1 | 各 0/0 |
| A 整个 AUTO 鼠标 taggedInjected | 10 | 10 | 10 |
| A AUTO 未标记 unflagged 键盘/鼠标 | 0/0 | 0/0 | 0/0 |
| B baseline→首次 snapshot 键盘增量 | 28 unflagged | 4 taggedInjected + 48 unflagged | 28 unflagged |
| Raw baseline→首次 snapshot→停止 input/cleanup | 45→49→97，均相等 | 全部 0 | 74→78→156，均相等 |
| A 真人阶段键盘 BEFORE/AFTER | 481/45 | 104/107 | 92/55 |
| A 真人阶段鼠标 BEFORE/AFTER | 14181/4185 | 11880/3932 | 8486/5850 |

24 条发送记录均 failureError=0、无丢弃；PostMessage returned=1 是投递
成功，不能写成全部都是 SendInput 插入证据。会话未报告解析、线程、
所有权或清理故障；停止后线程退出、callback detached、目标销毁、注册
归零。正常快捷键/前后台操作仍须用户视觉确认，计数不能替代。

B snapshot 包含控制台/焦点切换，范围比 A AUTO 大，不能声称 B 整段
入口为零。OFF 通用 targetKeyDown/Up=1/1 不覆盖指定 B 的 0/1；B 累计
指定 F8=2/2、B=1/2，到 END 仍保持，未观察到迟到的指定 B 按下。
两套 Hook 共享 Windows 低级 Hook 机制，不是两个独立传感器。
Raw 仅计消息并清理，软件/候选分类 **NOT COLLECTED**；ON 增加四条消息
不能解释成已确认四条带标记键盘事件，不能与历史解析模式拼接。

## 线程与时序

| 会话 | 目标 thread/HWND | Hook thread/HWND | Raw thread/HWND |
|---|---|---|---|
| ON 1 | 57192 / 0x2ca0dae | 48700 / 0x750f9e | 55668 / 0x12e0cbc |
| OFF 2 | 15120 / 0x14b0cbc | 15144 / 0xd80e64 | 31528 / 0x320f26 |
| ON 3 | 17900 / 0x1070cea | 58172 / 0x620f5a | 60164 / 0x6110dc |

QPC 频率均 10000000；仅比较同会话时序，不横向比较绝对时间。

| 会话 | 注册调用区间 | PREPARED | Hook 安装区间 | baseline | 首次发送→末次发送结束 | 首次 snapshot |
|---|---|---|---|---|---|---|
| ON 1 | 36296113247..36296113641 | 36296279765 | 36393175575..36393175689 | 36947812282 | 37034587786..37035220489 | 37233365314 |
| OFF 2 | NOT CALLED | 43917688270 | 43991182330..43991182466 | 44323560504 | 44399458610..44450118393 | 44563324505 |
| ON 3 | 52889576640..52889576965 | 52889763942 | 52935312304..52935312408 | 53263947726 | 53338601219..53339228238 | 53418365374 |

ON 注册 succeeded=1/error=0，requestedFlags=queriedFlags=0，记录在后续
快照不变。OFF 注册记录字段全零（含 succeeded=0）表示未调用，不是
注册失败；仍有活跃 Raw 线程/窗口/定时器。

## 源码复核入口与专家问题

- [RunRole](NativeRoles.cpp#function:RunRole) / [RoleWindow](NativeRoles.cpp#class:RoleWindow)：
  三个独立线程；Raw ready 后安装；分别计通用/指定 WM_KEYDOWN/UP 和
  WM_SYSKEYDOWN/UP；GetMessage 不限定 HWND。
- [Receiver](RawReceiver.cpp#class:Receiver)：显式 hwndTarget 为消息窗口；
  WM_INPUT 经 DefWindowProc 清理，不读取数据、不等待消费者。
- [callback](KeyboardHook.cpp#function:callback)：入口/独立分类/线程/耗时
  汇总，转发 CallNextHookEx，安装使用所属 DLL hmod。
- [CoreInput](main.cpp#class:CoreInput) 固定四轮 C-ABI 操作；
  [EmitKey](../../Rug.Core/Input/Win32InputController.cpp#function:EmitKey)为真实发送位置。

请专家先复核以下问题，指出能区分假设的具体条件：

1. flags=0、显式消息窗口接收、目标/Hook/Raw 分离线程的布局中，是否
   有需要额外观测的输入队列或窗口关联语义？
2. 哪项源码/API 使用能解释“F8 到达目标、同代 Hook 前后仍有真人回调，
   AUTO 却无入口”？安装或屏障成功不能证明 Hook 观察健康。
3. OFF 的 B 指定按下缺失需要什么最小计数型目标证据？保留发送方式，
   不同时改变扫描码、时序、重试或 IME 条件。

## 后续决策树（尚未实施）

```text
冻结六份日志/五份二进制 → 专家复核最小反转
├─ 明确 API 或实验契约缺陷
│  └─ 只修该缺陷，原参数→修正→原参数，保留失败检查
├─ 接收 HWND / 窗口类型 / 所属线程有可判别假设
│  └─ 先声明支持/削弱/不确定结果，再加一项独立选择
│     ├─ 随选择反转：定位布局相关，仍须解释机制
│     └─ 无差异：削弱假设，不继续扩充相似入口
└─ 无源码缺陷或可判别假设
   └─ 供 Windows/输入链专家分析，保持 Hook 问题未解决

独立分支：OFF 的 B 目标 0/1 → 补最小目标证据，不改发键方式
恢复验收：原因/修复可解释 + 原托管场景回归 + 同会话真人前/自动/真人后
```

没有新增手工测试或命令变体，不重复 ON/OFF/ON、不凭相关性改生产监听器。
可另行安排不依赖自动干预的开发；自动接管继续等待原场景验收。
“没有 Rug 标记”仅表示候选外部输入，不是经过认证的真人操作。

本提交仅汇总证据与同步索引；复核六份日志、五份磁盘二进制哈希与文档
链接。功能测试、WinUI 构建和新物理输入实验本轮 **NOT RUN**，没有改代码
或二进制。历史自动生命周期通过不替代手工验收。
