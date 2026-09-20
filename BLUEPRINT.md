# Rug Meta-Specification (BLUEPRINT)

**PRIME DIRECTIVE FOR AI AGENTS:** This document is your permanent
context and supreme behavioral blueprint. Adhere strictly to the
navigation protocols, architecture constraints and update mechanisms
below. Do not hallucinate project structure; navigate via Markdown links.

## 1. Core Architecture & Zoom Levels

### L1 - Root Panorama

* **Location:** `AGENTS.md` at the project root.
* **Token Constraint:** < 2000 tokens.
* **Responsibility:** global architecture, tech stack, directory topology.
* **Constraint:** no implementation details; link to L2 indexes only.

### L2 - Sub-Domain Index

* **Location:** `README.md` inside sub-module directories.
* **Token Constraint:** < 4000 tokens.
* **Responsibility:** module design, interface contracts, internal topology.
* **Constraint:** beyond the limit, chunk into sibling documents and link.

### L3 - Physical Implementation

* **Location:** source files.
* **Responsibility:** actual business logic.
* **Constraint:** complex functions document data shapes and upstream
  sources via machine-readable docstring tags (see section 5).

## 2. YAML Data Flow Contract

L1/L2 documents begin with structured YAML frontmatter:

```yaml
---
id: unique_module_id
type: logic_node
inputs: [upstream_id]
outputs: [downstream_id]
tags: [core]
---
```

## 3. Symbol-Level Anchoring

Link to L3 symbols instead of raw files:

* Class: `[Auth](src/auth.py#class:AuthManager)`
* Function: `[Extract](src/ocr.py#function:extract_features)`
* Variable: `[Timeout](src/config.py#var:REQUEST_TIMEOUT)`

## 4. Incremental Update Protocol

Never rewrite whole Markdown files. Replace targeted blocks only; keep
code changes and their documentation in the same commit.

## 5. Machine-Readable L3 Tags

Complex functions carry docstring tags:

* `@shape` — dimensions/types of inputs and outputs.
* `@source` — upstream dependency anchor.

```python
def process(data):
    """
    @shape data: [B, C]
    @shape return: [B, F]
    @source data: src/dataloader.py#function:load_batch
    """
```

## 6. Execution Rules for AI Agents

1. **Read map first** — `AGENTS.md`, then the relevant L2 index.
2. **Navigate precisely** — use symbol anchors, never blind searches.
3. **Synchronize** — update affected L2 docs in the same work cycle.
4. **Zero documentation rot** — keep code and docs passing the linter.
