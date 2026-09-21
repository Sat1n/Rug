# Rug (Release Your Game)

**Rug** 是一款基于 C++20 与 C# WinUI 3 构建的模块化、高性能桌面自动化托管框架。采用即插即用架构，为多游戏及多应用任务自动化提供纯净的宿主环境。

## 核心特性

- **无痕高清抓图**：基于 Windows Graphics Capture (WGC) 实现硬件加速的高帧率截屏。
- **原生视觉感知**：集成 WinRT Native OCR（含中文去空白优化）与 OpenCV 模板匹配。
- **双模控制器**：支持后台无痕模式（`PostMessage`）与前台模拟人手模式（含三阶贝塞尔曲线平滑轨迹 `SendInput`）。
- **双轨动态 UI**：支持 JSON Schema 声明式 UI 免编译渲染（MVP）与 WebView2 网页容器扩展。
- **嵌入式 Lua 运行时**：轻量级沙箱 Lua 脚本引擎，支持 Manifest 声明式权限管理。
- **Agent & MCP 支持**：内置 MCP Server 协议暴露，支持异步多模态 AI（VLM）服务接入。

## 项目结构

```text
Rug/
├── Rug.Core/            # C++20 核心库（WGC 抓图、WinRT OCR、控制器、C-ABI 导出）
├── Rug.UI/              # WinUI 3 桌面宿主应用
├── Rug.UI.Core/         # C# 宿主核心逻辑（任务调度器、插件加载器、Lua 引擎）
└── Rug.Poc/             # C++ 单体测试与 POC 验证沙箱

```

## 环境要求

* Windows 11 / Windows 10 (21H2+)
* Visual Studio 2022（需安装 *C++ 桌面开发* 与 *.NET 桌面开发* 工作负载）
* Windows App SDK

## 开源协议

本项目采用 [MIT License](LICENSE) 开源协议。