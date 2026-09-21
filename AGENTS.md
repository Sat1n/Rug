---
id: rug_root
type: logic_node
inputs: []
outputs: [rug_core, rug_ui_core, rug_ui, rug_poc]
tags: [root, panorama]
---

# AGENTS.md — Rug Panorama (L1)

> Supreme behavioral spec: [BLUEPRINT.md](BLUEPRINT.md). This file is the **L1
> root panorama only** — global architecture, tech stack, directory topology.
> Implementation rules live in the L2 module indexes linked in §6.

## 1. System Overview

Rug is a desktop automation hosting framework that drives ordinary Windows
applications through visual perception and humanized input. It captures target
windows, reads on-screen text, and issues mouse/keyboard actions, exposing the
pipeline to scripts, plugins and AI agents. It serves automation authors who need
UI-level control without touching protected process internals.

## 2. Technology Stack & Rationale

* **Native core:** C++20 (`/utf-8`) — direct WinRT access to Windows.Graphics.Capture
  (WGC) and Windows.Media.Ocr plus low-level input synthesis, exported over a C-ABI.
* **Host / UI:** C# WinUI 3 + CommunityToolkit.Mvvm — desktop shell, dynamic
  JSON-Schema/WebView2 UI, async orchestration of native calls.
* **Scripting:** Lua runtime — sandboxed plugin/automation logic behind a permission gate.
* **Interop:** `extern "C"` C-ABI — stable boundary between the C++ core and the managed host.

## 3. Directory Topology

```text
Rug/
├── AGENTS.md        # This file: L1 panorama
├── BLUEPRINT.md     # Meta-specification (immutable rules)
├── Rug.slnx         # Solution (x64 / x86 / arm64)
├── Rug.Core/        # C++20 DLL — WGC, OCR, input, C-ABI    (L2: Rug.Core/README.md)
├── Rug.UI.Core/     # C# host logic — scheduler, plugins, Lua (L2: Rug.UI.Core/README.md)
├── Rug.UI/          # C# WinUI 3 app — views, VMs, P/Invoke   (L2: Rug.UI/README.md)
└── Rug.Poc/         # C++ sandbox — pipeline validator        (L2: Rug.Poc/README.md)
```

## 4. Top-Level Data Flow

```text
[Target Window]
      │ WGC capture
      ▼
[Rug.Core]  SoftwareBitmap ─> [WinRT OCR] ─> normalized coords ─> [InputController] ─> [OS input]
      ▲
      │ C-ABI (int32_t codes + owned buffers)
[Rug.UI / Rug.UI.Core]  orchestrate · script · render UI
```

## 5. Architectural Rules & Constraints (global)

1. **Layering / dependency direction:** `Rug.UI` → `Rug.UI.Core` → `Rug.Core`.
   Native code never depends on managed code; all cross-boundary traffic goes
   through the C-ABI.
2. **Compliance:** no kernel anti-cheat bypass, no memory hacking, no automated
   CAPTCHA solving. Automation relies solely on visual perception and humanized input.
3. **Boundary discipline:** the C-ABI contract and memory-ownership rules are owned
   by [Rug.Core L2](Rug.Core/README.md); the managed-side P/Invoke restriction is
   owned by [Rug.UI L2](Rug.UI/README.md).
4. **Zero documentation rot:** L3 changes and their L2 doc updates land in the same
   commit (BLUEPRINT §4 / §6).

## 6. Sub-Domain Indexes (L2)

* **Rug.Core** — native pipeline + C-ABI: [Rug.Core/README.md](Rug.Core/README.md)
* **Rug.UI.Core** — host logic, Lua, plugins: [Rug.UI.Core/README.md](Rug.UI.Core/README.md)
* **Rug.UI** — WinUI 3 shell + P/Invoke: [Rug.UI/README.md](Rug.UI/README.md)
* **Rug.Poc** — validator sandbox: [Rug.Poc/README.md](Rug.Poc/README.md)

## 7. Roadmap (navigation aid)

* **Phase 1 (current):** `Rug.Core` decoupling — `WgcCapturer`, `WinRtOcr`, `InputController`, C-ABI.
* **Phase 2:** `Rug.UI.Core` host logic — Lua runtime, `TaskScheduler`, permission interceptor.
* **Phase 3:** JSON-Schema dynamic UI, `CommandBus`, hot reload.
* **Phase 4:** async VLM engine, MCP server, webhooks.
* **Phase 5:** DevTools frame tracing/annotation, WebView2 container, demo plugin.
