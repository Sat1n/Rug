---
id: rug_phase35_evidence
type: logic_node
inputs: [rug_phase35_acceptance]
outputs: []
tags: [evidence, audit, phase35]
---

# 证据索引及核查边界

返回[验收主文档](ACCEPTANCE.md)。本次只读核查，不重新执行测试。路径均相对仓库根目录；`audit.json` 是本次文件核查摘要，不是新的测试报告。

## 当前基线的已提交证据

| 材料 | 已核查内容 | 能证明／不能证明 |
| --- | --- | --- |
| `tests/evidence/private-candidates/` | 四份 Debug／Release visual／recovery 报告各 8 场景；functionalPassed 和各 scene passed；所有 scene actualWorkOutstanding=0、cleanupSucceeded/resourcesDrained=true、traceIntegrity=Valid；viewer/ui ExitCode=0；命令退出码四个 0 | API、实际接收、业务确认、轨迹及清理／UI 退出分别有字段。非“一个 PASS”替代四项。四份本机原报告存在，SHA256 与已提交副本逐一相同；详见 [audit](evidence/audit.json)。 |
| 同目录 `Debug/Release-full-final.txt`、`ui-core-final.txt` 及专项／构建日志 | 两配置最终全量、UI 核心及专项通过；构建 0 警告 0 错误；README 提供命令和退出结果 | 最近功能回归依据，不把历史每次尝试都标为通过。日志未逐一附独立退出 sidecar 的地方，以原记录为退出来源，日志成功行不是额外推断的进程码。 |
| 同目录 `real-evidence-summary.json`、`first-real-command-exits.json`、`real-stage-end-sample.jsonl` | 首次真实四命令均 0，自然 discardedCandidates=0，阶段结束原因与场景区分 | 无效候选→有效候选分支由实际 Gateway／事务确定性测试覆盖；真实 run 未自然覆盖该分支，不循环补凑。 |
| `tests/evidence/host-exit/` | 旧断言／失败对照、Debug／Release empty 各 5 轮、viewer／关闭矩阵／08C 报告及 stderr | EXIT-01 自然退出证据独立于业务功能。旧失败原样保留；不能用宿主退出 0 代替测试总命令成功。 |
| `tests/evidence/host-exit/time-rejections/` | 旧／新时间采样、首轮与复跑样例报告、真实 TimeRejection、命令退出记录 | 当前与 `95149f3` 配对 Native 哈希一致；未来时间在旧基线也出现，不归因为退出修复。保留 08C-01 首轮 TimeUnavailable 非零和历史 Stale 证据缺失。 |
| `samples/controlled-visual/evidence/`、`RECOVERY-VALIDATION.md` | 早期功能与 CLR 非零退出分别记录，后续 EXIT／08D 链接 | 早期功能通过不是早期整体命令通过；后来的成功不能覆盖旧退出断言。 |
| `tests/samples/execution-trace/` | 有限可读事件及摘要样例，保留原 envelope／checksum | 离线可读，不恢复任务、授权或帧资源；不是所有历史运行的完整归档。 |

## 从本机材料提炼的可提交摘要

1. [独立 08D review 摘要](evidence/08d-independent-summary.json)：原 `artifacts/review-08d01/review-summary.json` 的逐字副本，明确标明 review 原初失败和剩余问题，不将它重新解释为本次执行结果。
2. [隔离离线轨迹失败](evidence/isolated-offline-trace-failure.txt)：原 `Debug-observed-click-tests.log` 完整六行异常栈；错误为 `time diagnostics not retained by offline checked trace`。
3. [TEMP 对照摘录](evidence/temp-contrast.txt)：普通桌面与隔离但项目内 TEMP/TMP 的最终两行 PASS。完整日志仍在本机，哈希及存在性收录 audit；精简摘录不保留完整启动环境／失败退出码，因此后两者为 NOT COLLECTED。**只接受“对照通过、原因未知”，不接受“TEMP 已证明是根因”**。
4. [ENV 命令摘要](evidence/env-command-summary.json)：从原 `artifacts/environment-recheck/env01-20261028/all-commands.json` 白名单提取 Name／Arguments／ExitCode／Forced／StartError；20 条原命令均 0 且未强杀。原文件哈希收录 audit，不复制系统策略、签名事件、机器清单或安全设置明细。

ENV 原始 `capture-summary.json`／`natural-exit-summary.json`／各命令 JSON、stdout、stderr 和策略材料保留在本机 ignored artifacts。提交中的 [ENV README](../../tests/evidence/environment-recheck/README.md)已记录每配置首次及重建后捕获／退出结果：四组各 128 次采集，首轮 Debug future/unknown 各 1，其他三组为 0；捕获停止且零输入。宿主共 8 个子进程自然退出 0。当前 SAC Off 及状态变化来源未知，不能宣称 enforced 已复验。

另一个独立历史失败：ENV 隔离捕获 `Rug_CreateCapturer=-4`，用户桌面原入口成功；不是上面的离线轨迹断言，也不是 `0x800711C7`。原日志路径由 ENV README 保留，未追补缺失根因。

08D 开发过程中 `initial-full-writer-cleanup-failure.txt` 是 test-owner 在有界 recorder 关闭后过早删除目录；开发修补等待实际 writer 完成再离线读／删除。它与 reviewer 的隔离／TEMP 对照失败分开记录，不能声称一项修补解释了两项现象。

## Native 配置核对

- 真实 visual 入口 `ControlledVisualSample.cs` 从实际 `AppContext.BaseDirectory` 判定配置，显式加载 `x64/<configuration>/Rug.Core.dll`，再设置程序集 DllImportResolver；recovery 的 `ControlledRecoverySample.LoadNative` 同样如此。不是仅根据命令文字断定。
- 本次检查两份实际磁盘 DLL SHA256 与 08D 提交的 `native-hashes.json` 一致：Debug `643AD2467F7139B909DB3972BD41A46E0641502D26827728C605040F3CAADF64`；Release `4C84E319E4D81714ADCA7D00CEF01D7E0BCF3A262E9CD64FC5E90C2F632E0E5D`。08D 没有 Native 重建。
- 这是**加载器代码、报告配置和文件哈希的交叉核对**；四个原报告没有内嵌完整进程模块清单，不能伪称每轮转储已核验。当前核对也不能倒推所有老报告的 Native 配置。
- 08A-01 `--observation-native` 当时明确使用 Debug Native；托管 Release 通过不能当成该单元 Release Native OCR 冒烟。
- 08A-02A 的 `CaptureGeometryTests.RunNativeAsync` 当前按输出目录选配置，历史 [CaptureFacts](../../Rug.Core/CaptureFacts.md)记录两配置 Native 构建／命令和真实 DPI 144→96。本机 `.codex/capture-geometry-native-{debug,release}-verified.log` 各有 marker／resize／cross-monitor PASS，已将小型原日志复制到 [Debug](evidence/historical-geometry-Debug.txt)／[Release](evidence/historical-geometry-Release.txt)。它们是历史日志，并未附同轮进程模块哈希，不能扩大为当前基线重跑或所有 DPI 组合通过。

## 历史确认与尚未覆盖

| 项目 | 证据类别／范围 | 当前状态与最小补验 |
| --- | --- | --- |
| 05B-02 英文键盘 B 成对增长、键鼠接管／交还、timer／vision 保留 | 用户实际确认，基线 81fca0b；5784ada 归档 InputIntervention | 已接受的特定人工场景，不要求重测；未证明独立 Hook 根因。 |
| 06B 单次绿色验证、持续红色拒绝、人工等待 Stop | 用户确认归档 Recovery；早期截图／附件不在仓库 | 已接受；新 08C 自动化不是替代用户确认，也不新增真人验收。 |
| 06A／06C／07A／07B UI 行为 | 各契约内的既有 agent UI 自动化／阶段用户审查记录，部分原始材料只在本机 | 当前完整确定性回归覆盖逻辑；缺完整旧原始日志时按文字验收记录引用，不虚构独立报告。实际不可取消硬件输入暂停专门人工路径仍按 Lifecycle 的 NOT RUN。 |
| 业务 SendInput | 历史 05B 管理路径及用户键鼠验收；当前观察点击两模式 Fake 边界测试 | 08C 当前真实执行为 PostMessage；不承诺业务 SendInput。部署需要时单独协调前台／检测器与真实目标计数验证。 |
| 真实应用／游戏 | 早期 Phase2 Minecraft E2E 提交 a8bd395；CaptureFacts 明确本轮 Minecraft NOT RUN | 历史不是当前兼容性证书；不为盘点重跑，不计当前通过。 |
| OCR／本地模型 | Phase1 提供双引擎与模型发现；08A 原生 `all 0` 是选定 WinRT 的 correctness／stress／matching／discovery，历史观察冒烟 WinRT 三 blocks；本机旧日志有模型创建 | 当前 08C 使用 OpenCV 模板而非 OCR，不把创建模型等同识别所有模型成功。当前基线全本地 PP-OCR 模型逐一识别未运行；需发布该能力时单独列模型版本／输入与识别结果／实际 Native 配置。 |
| DPI／跨屏 | 历史实际受控窗口 144→96 标记映射及 resize，通过 CaptureFacts 和原日志 | 已有真实历史覆盖，不能写成全部未测；当前 08D 未重跑该专项，也不覆盖任意跨屏变化下的输入动作。 |
| 未来时间丢弃路径 | 最近四个真实样例均无自然 discard | 实际 Gateway／事务＋可信捕获／时钟测试替身覆盖，非生产“验证成功”注入；不为凑证据反复跑桌面。 |
| 阶段四集成 | VLM／MCP／webhook 等不在本阶段交付 | 未运行；依其独立任务验收。 |

## 原命令入口（供审查复用，不表示本次执行）

完整参数及产物见上述原 README。08D 最近每个 Debug／Release 分别执行：

```powershell
dotnet build Rug.UI/Rug.UI.csproj -c <configuration> -p:Platform=x64 -warnaserror
dotnet build tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c <configuration> -p:Platform=x64 -warnaserror
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c <configuration> -p:Platform=x64 --no-build
dotnet run --project Rug.UI.Tests/Rug.UI.Tests.csproj -c <configuration> -p:Platform=x64
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c <configuration> -p:Platform=x64 --no-build -- --private-candidate-tests
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c <configuration> -p:Platform=x64 --no-build -- --controlled-visual-sample <outputRoot>
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c <configuration> -p:Platform=x64 --no-build -- --controlled-recovery-sample <outputRoot>
```

本轮文档检查只验证本地链接存在、JSON 可解析、报告字段／原副本哈希一致、变更仅文档和精简证据。没有将“本地文件存在”升级为新的运行证据。
