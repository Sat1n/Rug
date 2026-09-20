---
id: rug_root
type: logic_node
inputs: []
outputs: []
tags: [core]
---

# AGENTS.md - Rug Panorama

## 1. System Overview

<One paragraph: what this project does, who it serves, and which problem
it solves. Keep it free of implementation details.>

## 2. Technology Stack & Rationale

* **Runtime:** <language / version> — <why this choice>.
* **Key libraries:** <libraries> — <why>.
* **Build & CI:** <tooling>.

## 3. Global Architecture & Directory Topology

```
Rug/
├── AGENTS.md        # This file: L1 panorama
├── BLUEPRINT.md     # Meta-specification (immutable rules)
├── src/             # Primary source tree (L2 index: src/README.md)
└── tests/           # Test suite
```

## 4. Top-Level Data Flow

```
[Input] ──> [Core Pipeline] ──> [Output]
```

## 5. Architectural Rules & Constraints

1. <Hard rule 1 — e.g. layering and dependency direction.>
2. <Hard rule 2 — e.g. IO boundaries or error handling.>
3. **Zero Documentation Rot:** L3 changes and their L2 documentation
   updates must land in the same commit.

## 6. Sub-Domain Indexes

* <Module name>: [module index](src/README.md)
