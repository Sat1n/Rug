# Rug (Release Your Game)

**Rug** is a modular, high-performance desktop automation hosting framework built with C++20 and C# WinUI 3. It provides a plug-and-play architecture for multi-game and multi-application task automation.

## Key Features

- **Zero-Interference Capture**: High-speed, hardware-accelerated screen capture via Windows Graphics Capture (WGC).
- **Neural OCR & Vision**: Native WinRT OCR integration with compact text filtering and OpenCV template matching.
- **Dual-Mode Input Controller**: Supports background mode (`PostMessage`) and humanized foreground mode (`SendInput` with 3rd-order Bezier curve trajectories).
- **Dynamic UI Engine**: Non-compiling plugin UI injection via JSON Schema (MVP) and WebView2 web containers.
- **Embedded Lua Runtime**: Lightweight, sandboxed Lua script execution with declarative permission manifests.
- **Agent & MCP Integration**: Built-in MCP Server and asynchronous VLM (Vision Language Model) API integration.

## Project Architecture

```text
Rug/
├── Rug.Core/            # C++20 Core Library (WGC Capture, WinRT OCR, Controllers, C-ABI)
├── Rug.UI/              # WinUI 3 Desktop Host Application
├── Rug.UI.Core/         # C# Host Logic (Task Scheduler, Plugin Loader, Lua Engine)
└── Rug.Poc/             # C++ Sandbox for POC validation

```

## Prerequisites

* Windows 11 / Windows 10 (21H2+)
* Visual Studio 2022 (with *Desktop development with C++* and *.NET Desktop Development* workloads)
* Windows App SDK

## License

This project is licensed under the [MIT License](LICENSE).