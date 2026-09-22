# Rug Models

Runtime model assets for Rug. These are **data**, not code: the managed host
resolves a model directory at runtime and passes its path to the native core
(e.g. `Rug_CreateOcrEngine(1, "<abs path to models/ocr/ppocr_v6>", &handle)`).

> **Binaries are NOT committed.** `*.onnx` / `*.pdmodel` / `*.pdiparams` under
> this folder are gitignored. Only the layout, `README.md`, `model.json`, the
> sidecar `*.yml` configs and small text assets live in the repo. Download the
> `.onnx` weights per the per-bundle notes below.

## Layout convention

```text
models/
├── README.md              # this file
├── ocr/                   # category: optical character recognition
│   └── ppocr_v6/          # bundle: one directory == one loadable model set
│       ├── det.onnx       # fixed name — text detection (DBNet)        [gitignored]
│       ├── rec.onnx       # fixed name — text recognition (CTC)        [gitignored]
│       ├── det.yml        # fixed name — det pre/post-process config (PaddleX export)
│       ├── rec.yml        # fixed name — rec config + embedded character_dict
│       └── model.json     # bundle manifest (identity + file roles)
└── match/                 # category: template-matching image assets
    └── <name>.png
```

Rules:
- **Two levels:** `models/<category>/<bundle>/`. Category is the capability
  (`ocr`, `match`, future `vlm`, ...); bundle is a concrete model set/version.
- **A bundle is a directory.** For OCR this matches the native contract, which
  takes a directory and reads fixed filenames inside it.
- **Filenames inside a bundle are fixed** (`det.onnx`, `rec.onnx`, `det.yml`,
  `rec.yml`) so the loader never changes per model. Add a new engine/version as a
  NEW bundle directory; extend the ABI `engine_type` only for a new back-end.
- Naming: `<engine>_<version>`, e.g. `ppocr_v6`.

## OCR bundles

### `ocr/ppocr_v6` — PaddleOCR PP-OCRv6 (ONNX, PaddleX export)

- **Det + Rec + dictionary.** Modern PaddleOCR/PaddleX exports ship `det.yml` and
  `rec.yml` sidecars instead of a standalone `keys.txt`; the **CTC character
  dictionary is embedded** in `rec.yml` under `PostProcess.character_dict`
  (~6900 entries, includes full-width glyphs).
- **The native loader parses both yml files at load time** (`yaml-cpp`):
  - `det.yml` → `NormalizeImage` mean/std/scale, `DBPostProcess` thresh /
    box_thresh / unclip_ratio / max_candidates, and the resize side length.
  - `rec.yml` → `character_dict` and `RecResizeImg.image_shape` (recognition height).
  This makes the engine **data-driven**: a different export with different
  constants works without code changes.
- **Required files** (`Rug.Core/PaddleOcrEngine.cpp`): exactly `det.onnx`,
  `rec.onnx`, `det.yml`, `rec.yml` in this directory. Missing any →
  `RUG_ERR_OCR_MODEL_NOT_FOUND`.
- **Where to get them:** the PaddleOCR / PaddleX release, or a ready-to-run ONNX
  export such as RapidAI / RapidOCR. Drop the detection model in as `det.onnx`,
  the recognition model as `rec.onnx`, and keep the exported `det.yml` / `rec.yml`.

## `model.json` (bundle manifest)

A thin **identity + file-roles** descriptor (`id`, `kind`, `engine`, `version`,
`files`). Numeric pre-processing parameters and the dictionary are **not** stored
here — they live in the sidecar `*.yml` and are the single source of truth, so
nothing is duplicated. `model.json` is currently informational (host tooling /
future bundling); the C++ loader reads the `.yml` files directly.
