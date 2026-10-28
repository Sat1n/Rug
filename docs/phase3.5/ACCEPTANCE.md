---
id: rug_phase35_acceptance
type: logic_node
inputs: [rug_core, rug_ui_core, rug_ui]
outputs: []
tags: [acceptance, phase35, evidence]
---

# 阶段 3.5 验收盘点与 PR 草稿

核查日期：2026-10-28。代码基线：`034357d1be977459b52f17ecfd4d2ba1b013d95f`（包含 `5ac4442`）。本次 CLOSE-01 只核查代码、历史记录和文件，整理文档；**未重新运行构建、测试或桌面操作**。下文“通过”归属于所链接的原验证，不是本轮新增实测。

**建议：可以准备有明确限制的阶段 PR，供整体审查；不能宣布阶段无保留完成，也不能承诺强制应用控制策略、任意应用输入或后续 AI 能力已经验收。** 不建议为盘点重跑整套桌面测试。若发布标准要求强制策略兼容，应先完成独立的受信部署／签名及强制策略验证单元。

## 证据分层与阅读入口

- [精简证据核查索引](EVIDENCE.md)：已提交材料、本机材料、实际配置、缺口及最小补验；[结构化核查结果](evidence/audit.json)记录文件存在性、哈希与报告摘录。
- 确定性测试证明受控顺序和错误边界；真实桌面测试证明本机受控窗口行为；用户确认只证明用户实际描述的操作。三者不互相替代。
- 输入 API 完成、目标收到输入、新画面确认业务完成、轨迹完整性分别报告。测试入口存在不等于测试已运行；报告中的 NOT RUN／NOT COLLECTED 不等于零。
- 提交是实现／修补边界，不暗示每个提交单独重跑全部矩阵。当前完整回归以 [08D-01 记录](../../tests/evidence/private-candidates/README.md)为主；历史人工验收按其当时版本引用。
- `artifacts/` 和 `.codex/` 为本机材料，不能假定审查者拥有。必要摘要已整理到本目录；不提交二进制、转储、系统策略明细或额外截图。

## 阶段验收矩阵

“已验收”指指定单元的已有验收；“限定范围验收”保留证据自身限制。未发现当前基线证据的扩展能力明确列为待验证／未运行。

| 能力／实际任务 | 实现状态 | 验收状态 | 代码基线／范围 | 证据 | 实际边界／剩余问题 |
| --- | --- | --- | --- | --- | --- |
| .NET 10、确切 SDK、构建基线（3.5-01 及后续 SDK 调整） | 已实现 | 限定范围验收 | `5ed7510`；`d2367a6`；EXIT-01 `73b018f` | [BuildBaseline](../../Rug.UI.Core/BuildBaseline.md)、[ENV-01](../../tests/evidence/environment-recheck/README.md)、[08D 构建与回归](../../tests/evidence/private-candidates/README.md) | SDK 10.0.402，rollForward=disable；Windows App SDK 1.6.240829007。历史六个托管项目两配置 12 次重建不等于 034357d 再重建六项目；最新为 WinUI、脚本宿主及其引用项目两配置构建和 UI 核心测试。强制策略兼容未验收。 |
| 配置、实例状态和运行身份（3.5-02） | 已实现 | 已验收 | `224d1c1` → `0690a05` | [L2 状态／命令契约](../../Rug.UI.Core/README.md)、完整脚本／UI 核心回归 | AppState／PluginConfig／InstanceState 分区、配置快照、代次及绑定隔离；不把全部状态自动持久化或恢复。早期原始逐项日志未集中归档，当前回归作为回归证据。 |
| 统一 Gateway（3.5-03） | 已实现 | 已验收 | `b80c15c`、`7f1541d` 及后续操作扩展 | [Gateway](../../Rug.UI.Core/AutomationGateway.md)、08D 两配置完整回归 | 身份、授权、deadline、去重、调用方返回与实际工作分离；去重是宿主进程内契约，不承诺跨进程恰好执行一次。 |
| 显式授权（3.5-04） | 已实现 | 已验收 | `53a587b`、`7f9d04c` 及后续能力扩展 | [Authorization](../../Rug.UI.Core/Authorization.md)、完整回归／真实样例显式授权 | manifest 声明不是授权；新权限不自动授予，撤销保持生效。历史授权不随检查点恢复。 |
| 输入仲裁（3.5-05A） | 已实现 | 已验收 | `cd26a81`、`b577713` 及停止边界修补 | [InputArbitration](../../Rug.UI.Core/InputArbitration.md)、完整及 ObservedClick 回归 | 共享租约、释放和隔离；真实 SendInput 兼容性不是 Fake 仲裁测试或 PostMessage 结果。 |
| 生产候选输入事件源（3.5-05B 事件源验收） | 已实现 | 限定范围验收 | `039eccf`、`a08fe26` | [四项生产证据](../../tests/Rug.UI.Core.Script.Tests/ProductionSourceAcceptance.md)用户原托管会话 | 目标送达、自身归因、同会话 HUMAN BEFORE／AFTER、连续性分列。无 Rug 标记只表示候选外部输入；独立 Hook 缺口未解决。 |
| 候选输入触发全局接管（3.5-05B-02） | 已实现 | 限定范围验收 | `3001f7a` → `81fca0b`；人工归档 `5784ada` | [InputIntervention 用户确认](../../Rug.UI.Core/InputIntervention.md)；当前完整回归 | 用户在英文输入条件确认 B 从 (0,0) 到 (4,4)、真人键鼠接管、timer／vision 保留、交还不复活旧运行、正常 on_stop。不是认证真人识别，也不是 Hook 验收。 |
| 生命周期、确认暂停（3.5-06A） | 已实现 | 已验收 | `0a9fd2a` → `76e4bb8` | [Lifecycle](../../Rug.UI.Core/Lifecycle.md)、确定性并发回归；用户阶段验收确认 | Pause 接受不等于 Paused；真实工作及必要释放结束才确认。Stop 先关准入，重复 Stop 共享清理；暂停不是人工接管。硬件不可取消 Native 暂停的专项人工证据仍依原文 NOT RUN。 |
| 原 Lua 协程异常恢复（3.5-06B-01） | 已实现 | 限定范围验收 | `d090f81` → `dabceb2` | [Recovery](../../Rug.UI.Core/Recovery.md)、用户绿色单次验证／红色拒绝／等待中 Stop 确认；08C-02 | 默认无执行者进入 AwaitingHuman；Return control 不继续，Verify 成功才恢复原协程。不含 VLM／救场输入；取消归档分类限制见 R4。 |
| 检查点保存、可靠读取（3.5-06C-01） | 已实现 | 已验收 | `41fa127` → `e60e337`，含 `35c017d`／`ab85d03` | [Checkpoints](../../Rug.UI.Core/Checkpoints.md)、真实文件及子进程重开测试、完整回归 | 验证后保存有界业务 JSON；原始取消源复核、精确数值、非法版本 Corrupt。OS 动作与磁盘记录不原子；不保存 Lua 栈／授权／句柄。 |
| 检查点启动新实例（3.5-06C-02） | 已实现 | 限定范围验收 | `2b4e6a3` → `0c29f32` | [CheckpointStartup](../../Rug.UI.Core/CheckpointStartup.md)、重启及 WinUI 验证记录、完整回归 | 新身份、重绑定／授权、当前配置和新画面验证；on_restore 而非 on_init，不是原协程继续。跨进程恰好启动一次、迁移不在范围。 |
| 候选快照预验证、仅 UI 热重载（3.5-07A） | 已实现 | 已验收 | `79d99c6` → `8d85c53` | [HotReload](../../Rug.UI.Core/HotReload.md)、实际 WinUI 类型冲突及回归 | 无效更新保留旧 UI／配置／实例；UI-only 保留 Lua 身份及暂停。运行内容变化先待应用，新增权限不自动批准。 |
| 安全检查点冻结与取消（3.5-07B-01） | 已实现 | 已验收 | `0009d84` → `68d4bb8` | [RuntimeUpdates](../../Rug.UI.Core/RuntimeUpdates.md)、实际 Lua 屏障／UI 回归 | 当前成功检查点返回前冻结，取消继续原协程；仅支持约定的 Running／可信检查点集合，历史记录不能代替当前边界。旧尝试所有结果校验归属。 |
| 兼容代码组切换（3.5-07B-02） | 已实现 | 限定范围验收 | `c767e2d` → `d0a5333` | [RuntimeSwitch](../../Rug.UI.Core/RuntimeSwitch.md)、实际 WGC／WinUI、组故障与退出屏障回归 | 受信跨版本规则，停止旧组后不可回退；新组 on_restore，继承用户暂停。已提交不等于恢复完成；无通用迁移／事务式 OS 回滚。 |
| 观察身份、帧持有、识别溯源（3.5-08A-01） | 已实现 | 限定范围验收 | `feb7a75` | [Observations](../../Rug.UI.Core/Observations.md)、并发／ROI／stride／资源测试、真实捕获识别冒烟记录 | 缓存复用不是新捕获；结果关联实际帧，全局容量有界。该单元原始 Native 冒烟使用 Debug DLL，不把托管 Release 构建写成 Release Native 实测；当前样例另有两配置证据。 |
| 原生帧时间与几何（3.5-08A-02A） | 已实现 | 限定范围验收 | `61b725b` → `8125d99` | [CaptureFacts](../../Rug.Core/CaptureFacts.md)、[观察契约](../../Rug.UI.Core/Observations.md)、capture／capture-lifetime／geometry 测试说明 | 精确绑定窗口支持路径、真实标记、resize 及历史实际 DPI 144→96；最终像素／时间／内容尺寸同帧。生命周期中途变化拒绝是受控标记注入，不是实际 HWND 复用实测；不覆盖所有跨屏组合或当前基线重跑。 |
| 观察关联点击（3.5-08A-02B） | 已实现 | 限定范围验收 | `5487170` → `4139fa6` | [ObservedClicks](../../Rug.UI.Core/ObservedClicks.md)、末端取消／年龄屏障、真实 PostMessage 样例 | QPC 年龄、身份、几何、新鲜 ROI、单次资格，经 Gateway／授权／仲裁；不回退旧点击、不自动重试。ROI 一致不等于完整业务语义或消除所有窗口竞态。 |
| 结构化有界轨迹（3.5-08B-01） | 已实现 | 已验收 | `1bcaa7a` | [ExecutionTraces](../../Rug.UI.Core/ExecutionTraces.md)、[样例轨迹](../../tests/samples/execution-trace/README.md)、两配置回归 | best effort；CallerReturned、WorkCompleted、CleanupCompleted 分开。轨迹故障不改变执行／停止；不完整必须如实展示，不是事务审计保证。 |
| 只读查看器／异常关联（3.5-08B-02） | 已实现 | 已验收 | `d44bb1d` → `21404e6` | [TraceReview](../../Rug.UI.Core/TraceReview.md)、分页／迟到详情归属测试、真实查看器及重启读取 | 当前保留窗口不是完整历史；incident 关联是异常边界快照，不猜测操作。legacy／未知版本不伪造；无回放／MCP。 |
| 真实视觉业务示例（3.5-08C-01） | 已实现 | 限定范围验收 | `628c01a`；当前回归 `034357d` | [持久示例](../../samples/controlled-visual/README.md)、08D Debug／Release visual-report | WGC→模板→ClickObserved→实际 PostMessage→新画面→轨迹：稳定 1/1 并 confirmed；变化／resize 等 0/0。只证明自建窗口，不代表任意真实应用／游戏。 |
| 人工恢复示例组合（3.5-08C-02） | 已实现 | 限定范围验收 | `95149f3`；当前回归 `034357d` | [示例](../../samples/controlled-visual/RECOVERY.md)、[历史记录](../../samples/controlled-visual/RECOVERY-VALIDATION.md)、08D recovery-report | 8 场景两配置；正例 BEFORE 1/1、AFTER 1/1，原协程及局部值继续，新画面 confirmed。自动化改画面／点击验证不是新真人接管验收。 |
| 宿主退出（3.5-EXIT-01） | 已实现 | 已验收 | `73b018f`；定向复查 `3c72382`；当前 08D | [退出证据](../../tests/evidence/host-exit/README.md)、[时序复查](../../tests/evidence/host-exit/time-rejections/README.md)、[Shutdown](../../Rug.UI/Shutdown.md) | 空宿主两配置各 5/5 自然退出 0；查看器／预览／活跃／恢复等待／重复关闭覆盖。旧 CLR 非零记录保留。退出修复不代表历史 Stale／TimeUnavailable 同时修复。 |
| 当前环境恢复（3.5-ENV-01） | 部分实现（核查，非信任修复） | 限定范围验收 | `5ac4442` | [ENV 记录](../../tests/evidence/environment-recheck/README.md)、本次精简 ENV 命令索引 | 当前 SAC Off、来源未知，20 条原命令退出 0；非管理员。未改变策略／依赖；不接受为原强制策略的永久受信加载修复。 |
| 私有验证候选有界获取（3.5-08D-01） | 已实现 | 限定范围验收 | `034357d` | [08D 证据](../../tests/evidence/private-candidates/README.md)、独立 review 摘要及 TEMP 对照 | 每阶段最多 3 候选，共用 ≤750ms；source 默认 1000ms 保留；仅候选时间问题可换帧，无输入重放。真实四命令自然丢弃 0 次，该分支由确定性测试覆盖。 |
| 阶段四 VLM／MCP／webhook 等 | 未实现（本阶段不交付） | 未运行 | 无阶段 3.5 实现基线 | 本阶段范围声明 | 仅具备后续可复用的宿主、权限、观察、恢复、轨迹基础；不宣称已有模型服务／自动救场。 |

## 剩余问题与阶段影响

编号用于本盘点追踪，不冒充已有 issue 编号。已接受的单元限制直接沿用；下表的阻塞判断是建议，不替代整体审查决定。

| ID／问题 | 影响、当前处理 | 证据及下一步 | 阶段收尾建议 |
| --- | --- | --- | --- |
| R1 公共采集／原 source 时间不可用 | 真实 WGC 可出现未来时间；原生无效不能等 QPC 追上后改为有效。公共观察或源帧仍可能拒绝，08D 只选择私有候选，不改变公共缓存、OCR／匹配或未筛选 probe。 | [时序证据](../../tests/evidence/host-exit/time-rejections/README.md)、[ENV 捕获统计](../../tests/evidence/environment-recheck/README.md)首轮 Debug 128 中 future=1／unknown=1；后续零次不证明消失。后续独立采样单元保留原始时间／QPC／frame 关联，按需要调查，不循环凑全绿。 | 保留为已公开限制；不阻塞限定范围 PR。阻塞“公共捕获时间始终有效”的承诺。 |
| R2 历史偶发 Stale | 拒绝陈旧帧本身符合保护；原始延迟成因未定位，旧日志缺精确 native 帧龄。SourceResolved→拒绝或 ROI→移动耗时不能冒充帧龄。 | 同上时序证据保留首轮失败及复跑；新 TimeRejection 有有符号帧龄。需再发时只针对首个失败保全时间链，不放宽阈值／自动重试点击，不追补缺失数据。 | 已接受保留的调查项；不阻塞有限样例验收。 |
| R3 独立低级 Hook 观测缺口 | Hook 机制两观察者并非独立传感器；Raw 注册反转相关性未给根因。不是生产 Raw 失效的充分证据。 | [冻结反转包](../../tests/NativeInputProbe/RegistrationReversalReview.md)、[生产四项](../../tests/Rug.UI.Core.Script.Tests/ProductionSourceAcceptance.md)、[05B-02 用户确认](../../Rug.UI.Core/InputIntervention.md)分别引用。若续查仅做有预测的窗口／输入队列实验或交专家；不重复 ON/OFF/ON。 | 保留既有明确限制；不能再把早期“自动接管关闭”当当前全局状态，也不能写缺口已修复。 |
| R4 Cancelled 归档为 ValidationTimedOut | 当前 RecoveryCoordinator 的验证 WaitAsync 内层 OCE 分支只判断关联 deadline 与宿主停止，可能先于外层 callerToken 分支将请求取消记录为 ValidationTimedOut/needs_human。影响返回／归档原因的准确解释，不只是 UI 文案。现有路径仍等待人工，未判 resolved，迟到成功不能继续 Lua。 | [实际代码](../../Rug.UI.Core/Services/RecoveryCoordinator.cs)、[08C-02 限制](../../samples/controlled-visual/RECOVERY-VALIDATION.md)、cancel-late 报告。建议独立分类修补单元，直接区分原始取消与真实超时，保留 Stop 和归档失败语义；本轮不改逻辑。 | 已明确另行跟踪；建议不阻塞本 PR，但必须列出，不能承诺取消原因总是精确。 |
| R5 SAC／强制策略兼容 | 当前可运行伴随 SAC Off；何时、为何变化未知。签名／信任条件未闭环，不能由“无新 3077”推导原 enforced 下兼容。 | [ENV-01](../../tests/evidence/environment-recheck/README.md)。后续由环境／部署单元定义签名、依赖及 enforced 验证矩阵；保留原阻断记录。不在本轮更改系统策略。 | **若目标含强制策略部署则阻塞该目标**；否则可明确限定当前环境准备 PR。永久信任修复尚未被接受。 |
| R6 未覆盖的真实场景 | 当前两配置真实业务闭环为隔离 WGC／OpenCV／PostMessage，不是业务 SendInput、真实游戏／应用验收。早期 SendInput 及用户接管证据不可扩大到当前视觉样例或任意软件。 | [EVIDENCE](EVIDENCE.md)分列历史输入、OCR 模型、DPI 等；按部署对象提出最小独立补验，不用 Fake 或老版本记录替代当前实测。 | 不阻塞受控框架 PR；阻塞这些扩展兼容性声明。 |
| R7 隔离环境测试失败 | 独立 08D Debug observed-click 的离线轨迹断言失败；普通桌面、隔离+项目内 TEMP/TMP 对照通过，因果未知。另有 ENV 隔离 CreateCapturer=-4；不能混为同一问题或 SAC 错误。 | [保留的原日志与对照](EVIDENCE.md)。08D 开发首轮 writer 文件清理竞争有独立 test-owner 排空修补，**不能据此声称 reviewer 的 TEMP 相关断言根因已修复**。建议需要隔离 CI 时另建有界复现单元。 | 不阻塞已通过桌面矩阵的 PR；阻塞“所有隔离环境均全绿”的表述。 |

## 跨文档的当前解释

1. 05B 早期探针“不启用接管”是当时的诊断范围；05B-02 已接入生产监测及全局候选输入接管并有用户确认。后来的计划内 AwaitingHuman 仅对完成登记及排空的恢复协程例外；不屏蔽检测。
2. 06B 继续**同一协程／同一实例**；06C-02 和 07B-02 创建**新实例**并调用 on_restore，关联来源但不恢复旧授权／句柄。不能互换验收证据。
3. EXIT-01 修复后自然退出 0，不修改旧 08C 报告中的 CLR 断言／非零退出。`3c72382` 定向复查仍出现业务时间拒绝，08D 的后续通过也不覆盖首次失败。
4. 08D 四个真实完整命令各运行一次且全通过；自然未触发换候选，不据此统计底层异常消失。`--capture-time-probe` 保持不筛选。
5. 当前样例分别证明 API、目标消息、新画面、轨迹及清理／退出；轨迹 Valid 不等于任务成功，业务失败但轨迹完整也有效。界面自动化不是用户真人确认。

## 证据缺口与最小补验建议

- 老单元部分验收是仓库契约内的文字记录及用户阶段确认，原粘贴附件不在 Git；本次不伪造逐项原始日志。若整体评审要求每条旧人工确认可离线复核，应由用户补充相应脱敏确认摘录，而不是重新推断截图／日志结论。
- 当前精简索引核验最近报告、Native 路由及文件哈希；历史每轮的进程模块清单并非全部保留。不得从命令 `-c Release` 一概推定历史 Native Release。需要独立审计旧 Native 加载时，应先找该轮模块记录；缺失则标记缺失。
- 当前基线未新增六项目全部重建、Native 全量重建／全 OCR 模型实测；最近 08D 明确无 Native 修改／重建。若发布要求对每个本地模型及 Native 工具链重新验收，再安排配置匹配的最小清单。
- 本次不需要真人键鼠补验。SAC enforced、真实业务 SendInput、当前基线的跨屏动作、真实应用／游戏等仍是独立范围；历史几何 DPI 实测保持有效，但不扩大范围，也不因盘点自动安排重测。

## 可复制的阶段 PR 草稿

标题：`feat: establish phase 3.5 automation safety and observability`

正文草稿：

> 本阶段为 Windows 自动化宿主建立运行隔离、显式授权和统一执行边界，使脚本输入能够受生命周期、共享仲裁、候选外部输入接管和观察前置校验约束，并能追踪调用方返回、实际工作结束及资源清理。
>
> 主要范围：.NET 10 精确 SDK；配置／实例状态隔离；Gateway 与授权；确认暂停、停止及保留 Lua 协程的显式恢复；验证检查点保存／新实例恢复；不可变更新候选、仅 UI 热重载及受信检查点代码切换；WGC 帧来源／时间／几何和 ClickObserved；有界轨迹、异常关联及只读查看器；可复用视觉／人工恢复示例；有序宿主退出；私有验证阶段最多三候选的有界选择。
>
> 验证：详见 `docs/phase3.5/ACCEPTANCE.md` 与 `EVIDENCE.md`。最近 034357d 的 Debug／Release 构建、完整脚本、UI 核心及候选／观察点击／生命周期／恢复／轨迹回归通过；两配置视觉、恢复样例各 8 场景，真实 WGC／OpenCV／PostMessage、目标计数、新画面、资源排空及 WinUI 自然退出分别有报告。历史用户键鼠接管及恢复确认按其版本单列。此次收尾盘点没有重复运行桌面测试。
>
> 已知限制：公共／source 时间不可用与历史 Stale 根因未闭环；私有换帧真实自然触发分支未采集（由确定性测试覆盖）；独立 Hook 观测缺口仍开放；取消可能被归档为验证超时；SAC 当前 Off，不证明原强制策略兼容；隔离环境离线轨迹断言存在未定根因的 TEMP 对照差异。当前真实业务闭环限自建 PostMessage 窗口，不能声称任意软件、游戏、业务 SendInput、全模型或跨屏 DPI 已验证。
>
> 阶段四可以复用权限、Gateway、观察、恢复、检查点及轨迹契约，但仍需独立交付 VLM／MCP／webhook 和部署策略验证；本 PR 不提供模型救场、自动迁移或 OS 动作与磁盘记录的原子提交保证。不将记录缺失解释为可以重放动作。

本单元仅准备上述文案和证据，不创建、推送或合并 PR。
