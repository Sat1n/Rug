# Test Images

Drop the test inputs here. They are **not committed** (only this README is).

## OCR correctness / stress

Place up to five images named with the `ocr_` prefix — the harness scans this
directory for `ocr_*.png` / `ocr_*.jpg` / `ocr_*.jpeg` / `ocr_*.bmp` (sorted):

```
ocr_test_1.png
ocr_test_2.png
ocr_test_3.png
ocr_test_4.png
ocr_test_5.png
```

Any name starting with `ocr_` and ending in `.png`/`.jpg`/`.jpeg`/`.bmp` is
picked up. If none are found, the OCR tests print a clear error and exit.
These images are gitignored (fixtures are provided locally).

## Template matching (optional)

Provide BOTH to run the matcher; if either is missing the test prints `[SKIP]`
and exits cleanly. PNG or JPG both work — the harness picks the first existing
extension (`.png` → `.jpg` → `.jpeg` → `.bmp`):

```
tmpl_search.png   (or .jpg)  # the small template to locate
tmpl_target.png   (or .jpg)  # the larger image searched
```

> `tmpl_search.*` must actually appear inside `tmpl_target.*` for a match.
