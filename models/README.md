# Rug Models

Runtime model assets for Rug. These are **data**, not code: the managed host
resolves a model directory at runtime and passes its path to the native core
(e.g. `Rug_CreateOcrEngine(1, "<abs path to models/ocr/ppocr_v6>", &handle)`).

> **Model files are NOT committed.** Everything a bundle needs at runtime — the
> `*.onnx` weights **and** the `*.yml` configs — is gitignored. Only this
> `README.md` and each bundle's `model.json` (identity + file roles) live in the
> repo. Every developer downloads and renames the model files locally, per below.

## Layout convention

```text
models/
├── README.md              # this file (committed)
├── ocr/                   # category: optical character recognition
│   └── ppocr_v6/          # bundle: one directory == one loadable model set
│       ├── det.onnx       # text detection (DBNet)            [downloaded, gitignored]
│       ├── rec.onnx       # text recognition (CTC)            [downloaded, gitignored]
│       ├── det.yml        # det pre/post-process config       [downloaded, gitignored]
│       ├── rec.yml        # rec config + embedded character_dict [downloaded, gitignored]
│       └── model.json     # bundle manifest (identity + file roles)  (committed)
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

### `ocr/ppocr_v6` — PaddleOCR PP-OCRv6 (ONNX)

**Det + Rec + dictionary.** Modern PaddleOCR/PaddleX exports ship an `inference.yml`
sidecar per model instead of a standalone `keys.txt`; the **CTC character dictionary
is embedded** in the recognition config under `PostProcess.character_dict` (~6900
entries, includes full-width glyphs). The native loader parses both yml files at
load time (`yaml-cpp`) to get the dictionary and all pre/post-process parameters,
so the engine is data-driven and a different export works without code changes.

**Where to get them — HuggingFace.** Download the PP-OCRv6 **detection** and
**recognition** ONNX bundles from HuggingFace. Each bundle ships an
`inference.onnx` + `inference.yml`. Rename them into this directory as follows:

| Downloaded (detection bundle)  | -> | Local name      |
|--------------------------------|----|-----------------|
| `inference.onnx`               | -> | `det.onnx`      |
| `inference.yml`                | -> | `det.yml`       |

| Downloaded (recognition bundle)| -> | Local name      |
|--------------------------------|----|-----------------|
| `inference.onnx`               | -> | `rec.onnx`      |
| `inference.yml`                | -> | `rec.yml`       |

**Required files** (`Rug.Core/PaddleOcrEngine.cpp`): exactly `det.onnx`,
`rec.onnx`, `det.yml`, `rec.yml` in this directory. Missing any →
`RUG_ERR_OCR_MODEL_NOT_FOUND`. None of them are committed — they are downloaded
and renamed locally.

## `model.json` (bundle manifest — REQUIRED for discovery)

Each bundle must have a `model.json`: the **identity + routing** descriptor the
native catalog reads. Numeric pre-processing params and the dictionary stay in the
sidecar `*.yml` (single source of truth, not duplicated here).

Required fields:

| Field     | Value          | Purpose                                            |
|-----------|----------------|----------------------------------------------------|
| `id`      | unique string  | Selector passed to `Rug_CreateOcrEngineById`.       |
| `kind`    | `"ocr"`        | Category guard; non-`ocr` bundles are skipped.      |
| `engine`  | `"paddle"`     | Routes to the Paddle back-end (only one supported). |
| `version` | e.g. `"6.0"`   | Informational (shown when listing models).          |

Conventions:
- **Folder name should equal `id`**, and **ids must be unique** across `models/ocr`.
- A bundle is discovered only when `model.json` is valid **and** the four required
  files (`det.onnx`, `rec.onnx`, `det.yml`, `rec.yml`) are present — so a bundle
  whose weights are not downloaded yet is simply skipped, not an error.

### Adding another Paddle model (size or version)

Drop a new directory under `models/ocr/` with its own `model.json`; no code change
is needed. For example:

```text
models/ocr/ppocr_v6_tiny/    id "ppocr_v6_tiny"   version "6.0"
models/ocr/ppocr_v6_small/   id "ppocr_v6_small"  version "6.0"
models/ocr/ppocr_v6_medium/  id "ppocr_v6_medium" version "6.0"
models/ocr/ppocr_v5/         id "ppocr_v5"        version "5.0"
```

Each needs its `det.onnx`/`rec.onnx`/`det.yml`/`rec.yml` (downloaded + renamed as
above). `Rug_ScanOcrModels` then lists them and `Rug_CreateOcrEngineById(dir,
"ppocr_v6_small", &h)` loads the chosen one. Because det/rec parameters and the
dictionary come from each bundle's own `*.yml`, different sizes/versions/languages
all run through the same pipeline.

## Discovery API (native, model.json driven)

* `Rug_ScanOcrModels(ocr_dir, &list)` → `Rug_ModelListGetCount` /
  `Rug_ModelListGetInfo` (id, engine, version) → `Rug_FreeModelList`.
* `Rug_CreateOcrEngineById(ocr_dir, id, &engine)` routes by the `engine` field
  (Paddle today; other engines need a new `IOcrEngine` + `RugOcrEngineType`).
* `ocr_dir` is the category directory, e.g. `<repo>/models/ocr`.
