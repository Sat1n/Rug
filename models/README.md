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

## `model.json` (bundle manifest)

A thin **identity + file-roles** descriptor (`id`, `kind`, `engine`, `version`,
`files`). Numeric pre-processing parameters and the dictionary are **not** stored
here — they live in the sidecar `*.yml` and are the single source of truth, so
nothing is duplicated. `model.json` is currently informational (host tooling /
future bundling); the C++ loader reads the `.yml` files directly.
