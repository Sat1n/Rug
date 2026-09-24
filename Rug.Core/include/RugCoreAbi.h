// =============================================================================
//  RugCoreAbi.h
//  Rug.Core public C-ABI surface.
//
//  This is the ONLY stable boundary between the native C++20 core and managed
//  hosts (C# / WinUI 3 via P/Invoke). See AGENTS.md (L1) and Rug.Core/README.md
//  (L2) for the governing contract.
//
//  Contract summary (CRITICAL):
//    1. Every operation function uses extern "C" and returns an int32_t status
//       code (RUG_OK == 0, RUG_ERR_* < 0). C++ exceptions never cross the ABI.
//    2. Memory allocated by the core is owned by the core and MUST be released
//       by the core via Rug_FreeBuffer / Rug_FreeOcrResult. Managed callers
//       must NEVER Marshal.FreeHGlobal a native pointer.
//    3. All strings crossing the boundary are UTF-8, NUL-terminated.
//    4. Handles are opaque pointers; never dereference or fabricate them.
//
//  Encoding: UTF-8. Comments are ASCII-only so the header compiles cleanly even
//  in configurations that do not pass /utf-8.
// =============================================================================

#ifndef RUGCORE_ABI_H
#define RUGCORE_ABI_H
#pragma once

#include <stdint.h>

// -----------------------------------------------------------------------------
// Export / import macro
// -----------------------------------------------------------------------------
#if defined(_WIN32) || defined(_WIN64)
    #ifdef RUGCORE_EXPORTS
        #define RUGCORE_API __declspec(dllexport)
    #else
        #define RUGCORE_API __declspec(dllimport)
    #endif
#else
    #ifdef RUGCORE_EXPORTS
        #define RUGCORE_API __attribute__((visibility("default")))
    #else
        #define RUGCORE_API
    #endif
#endif

// Explicit calling convention. C# P/Invoke must use CallingConvention.Cdecl.
#ifndef RUGCORE_CALL
    #if defined(_WIN32) || defined(_WIN64)
        #define RUGCORE_CALL __cdecl
    #else
        #define RUGCORE_CALL
    #endif
#endif

#ifdef __cplusplus
extern "C" {
#endif

// -----------------------------------------------------------------------------
// Status codes
// -----------------------------------------------------------------------------
// Functions return int32_t. These named constants describe every value the core
// may produce. RUG_OK == 0 means success; all errors are negative.
typedef enum RugStatus {
    RUG_OK                   =  0,  // Success.
    RUG_ERR_UNKNOWN          = -1,  // Unclassified failure.
    RUG_ERR_INVALID_PARAM    = -2,  // A null/invalid argument or handle was passed.
    RUG_ERR_NOT_INITIALIZED  = -3,  // Core or object not ready (e.g. D3D/WGC init failed).
    RUG_ERR_CAPTURE_FAILED   = -4,  // Window capture produced no usable frame.
    RUG_ERR_OCR_FAILED       = -5,  // OCR engine unavailable or recognition failed.
    RUG_ERR_INPUT_FAILED     = -6,  // Input synthesis (PostMessage/SendInput) failed.
    RUG_ERR_OUT_OF_MEMORY    = -7,  // Native allocation failed.
    RUG_ERR_UNSUPPORTED      = -8,  // Requested feature/format/mode not supported.
    RUG_ERR_BUFFER_TOO_SMALL = -9,  // Caller-provided buffer is too small.
    RUG_ERR_TIMEOUT          = -10, // Operation exceeded its time budget.
    RUG_ERR_OCR_MODEL_NOT_FOUND = -11  // Model file required by the engine is missing.
} RugStatus;

// -----------------------------------------------------------------------------
// Enumerations
// -----------------------------------------------------------------------------
typedef enum RugPixelFormat {
    RUG_PIXEL_BGRA8 = 0,  // 8-bit BGRA (native WGC output).
    RUG_PIXEL_RGBA8 = 1,  // 8-bit RGBA.
    RUG_PIXEL_GRAY8 = 2   // 8-bit grayscale (OCR pre-processing).
} RugPixelFormat;

typedef enum RugInputMode {
    RUG_INPUT_WIN32_SOFTWARE  = 0,  // Win32 SendInput (foreground) / PostMessage (background).
    RUG_INPUT_HARDWARE_KMBOX  = 1   // KMBox B+/Pro/Net hardware controller.
} RugInputMode;

typedef enum RugMouseButton {
    RUG_MOUSE_LEFT    = 0,
    RUG_MOUSE_RIGHT   = 1,
    RUG_MOUSE_MIDDLE  = 2,
    RUG_MOUSE_XBUTTON1 = 3,  // Side button 1 (browser back).
    RUG_MOUSE_XBUTTON2 = 4   // Side button 2 (browser forward).
} RugMouseButton;

// Mouse movement path shape produced by the humanizer.
typedef enum RugTrajectoryType {
    RUG_TRAJECTORY_STRAIGHT     = 0,  // Linear interpolation with ease-in-out timing.
    RUG_TRAJECTORY_CUBIC_BEZIER = 1   // Cubic Bezier through a perpendicular corridor (default).
} RugTrajectoryType;

// OCR engine back-end selected by Rug_CreateOcrEngine.
typedef enum RugOcrEngineType {
    RUG_OCR_ENGINE_WINRT  = 0,  // Windows.Media.Ocr (built-in, no model file).
    RUG_OCR_ENGINE_PADDLE = 1   // Paddle/ONNX (PP-OCRv5), requires model_path.
} RugOcrEngineType;

// -----------------------------------------------------------------------------
// Opaque handles
// -----------------------------------------------------------------------------
typedef struct RugCapturer*       RugCapturerHandle;
typedef struct RugOcrEngine*      RugOcrEngineHandle;
typedef struct RugOcrResult*      RugOcrResultHandle;
typedef struct RugInputController* RugInputControllerHandle;
typedef struct RugModelList*      RugModelListHandle;

// -----------------------------------------------------------------------------
// Plain-old-data structs (blittable for P/Invoke)
// -----------------------------------------------------------------------------

// Capture configuration. Target window is an HWND carried as void* so it maps
// directly to System.IntPtr on the managed side.
typedef struct RugCaptureConfig {
    void*   targetWindow;  // HWND of the window to capture. Required.
    int32_t ocrUpscale;    // Pre-OCR upscale factor (>= 1). POC used 2.
    int32_t captureCursor; // 0 = exclude cursor (default), non-zero = include.
} RugCaptureConfig;

// A captured frame. `data` is owned by the core; release it with Rug_FreeBuffer.
typedef struct RugFrame {
    uint8_t* data;        // Pixel buffer, core-owned. Free via Rug_FreeBuffer.
    uint32_t dataLength;  // Size of `data` in bytes.
    int32_t  width;       // Pixel width.
    int32_t  height;      // Pixel height.
    int32_t  stride;      // Bytes per row.
    int32_t  format;      // RugPixelFormat value.
} RugFrame;

// One recognized text line. Fixed-size text buffer keeps the struct blittable.
#define RUG_OCR_LINE_TEXT_MAX 256
typedef struct RugOcrLine {
    int32_t x;       // Bounding-box left, in frame pixel coordinates.
    int32_t y;       // Bounding-box top.
    int32_t width;   // Bounding-box width.
    int32_t height;  // Bounding-box height.
    float   confidence;  // Engine confidence in [0,1]. WinRT OCR has none -> 1.0.
    char    text[RUG_OCR_LINE_TEXT_MAX];  // UTF-8, NUL-terminated line text.
} RugOcrLine;

// One template-match hit. Coordinates are logical (caller applies DPI mapping).
// Blittable; the caller allocates the array, so no native free is required.
typedef struct RugMatchBox {
    int32_t x;          // Box left.
    int32_t y;          // Box top.
    int32_t width;      // Box width.
    int32_t height;     // Box height.
    float   confidence; // Match score in [0, 1].
} RugMatchBox;

// One discovered OCR model bundle (from models/ocr/<bundle>/model.json).
// Fixed-size fields keep the struct blittable.
#define RUG_MODEL_ID_MAX      64
#define RUG_MODEL_VERSION_MAX 32
#define RUG_MODEL_ENGINE_MAX  32
typedef struct RugModelInfo {
    char id[RUG_MODEL_ID_MAX];            // unique selector, e.g. "ppocr_v6_tiny"
    char version[RUG_MODEL_VERSION_MAX];  // e.g. "6.0"
    char engine[RUG_MODEL_ENGINE_MAX];    // back-end key, e.g. "paddle"
} RugModelInfo;

// Tuning knobs for the humanized input controller. Blittable; the struct is
// copied by value across the ABI so no ownership transfer occurs.
typedef struct RugHumanizeConfig {
    int32_t minClickHoldMs;   // Lower bound of random click hold time. Default 80.
    int32_t maxClickHoldMs;   // Upper bound of random click hold time. Default 120.
    int32_t minKeyHoldMs;     // Lower bound of random key hold time.   Default 60.
    int32_t maxKeyHoldMs;     // Upper bound of random key hold time.   Default 100.
    float   corridorRatio;    // Bezier control-point offset as a fraction of distance. Default 0.15.
    int32_t enableJitter;     // 0 = disable per-point jitter, non-zero = enable (default).
} RugHumanizeConfig;

// -----------------------------------------------------------------------------
// Memory ownership API
// -----------------------------------------------------------------------------

// Release a core-owned raw buffer (e.g. RugFrame.data). Safe to call with NULL.
// Returns void: deallocation cannot meaningfully fail across the ABI.
RUGCORE_API void RUGCORE_CALL Rug_FreeBuffer(uint8_t* ptr);

// Release a core-owned OCR result and all memory it owns. Safe to call with NULL.
RUGCORE_API void RUGCORE_CALL Rug_FreeOcrResult(RugOcrResultHandle result);

// Release a model list returned by Rug_ScanOcrModels. Safe to call with NULL.
RUGCORE_API void RUGCORE_CALL Rug_FreeModelList(RugModelListHandle list);

// -----------------------------------------------------------------------------
// Image I/O
// -----------------------------------------------------------------------------

// Decode an image file (PNG/JPEG/BMP) into a BGRA8 RugFrame via OpenCV.
// outFrame->data is core-owned; release it with Rug_FreeBuffer.
// Requires OpenCV; without it returns RUG_ERR_UNSUPPORTED.
RUGCORE_API int32_t RUGCORE_CALL Rug_LoadImageFile(const char* path, RugFrame* outFrame);

// -----------------------------------------------------------------------------
// Capture API
// -----------------------------------------------------------------------------

// Create a capturer for the window described by `config`.
// On RUG_OK, *outHandle receives a valid handle.
RUGCORE_API int32_t RUGCORE_CALL Rug_CreateCapturer(const RugCaptureConfig* config,
                                                    RugCapturerHandle* outHandle);

// Destroy a capturer created by Rug_CreateCapturer and free its resources.
RUGCORE_API int32_t RUGCORE_CALL Rug_DestroyCapturer(RugCapturerHandle handle);

// Grab the latest frame from the capture pipeline.
// On RUG_OK, outFrame->data is core-owned; the caller must Rug_FreeBuffer it.
RUGCORE_API int32_t RUGCORE_CALL Rug_GrabFrame(RugCapturerHandle handle,
                                               RugFrame* outFrame);

// -----------------------------------------------------------------------------
// OCR API
// -----------------------------------------------------------------------------

// Create an OCR engine of the given back-end (RugOcrEngineType).
//   - RUG_OCR_ENGINE_WINRT (0):  model_path is an OPTIONAL UTF-8 BCP-47 language
//                                tag (e.g. "zh-Hans", "en"). NULL/empty -> the
//                                user's system (profile) language; an unavailable
//                                tag also falls back to the profile language.
//   - RUG_OCR_ENGINE_PADDLE (1): model_path is a UTF-8 DIRECTORY holding the
//                                PP-OCR model (det.onnx/rec.onnx/det.yml/rec.yml);
//                                missing -> RUG_ERR_OCR_MODEL_NOT_FOUND.
RUGCORE_API int32_t RUGCORE_CALL Rug_CreateOcrEngine(int32_t engine_type,
                                                     const char* model_path,
                                                     RugOcrEngineHandle* out_handle);

// Destroy an OCR engine created by Rug_CreateOcrEngine.
RUGCORE_API int32_t RUGCORE_CALL Rug_DestroyOcrEngine(RugOcrEngineHandle handle);

// Recognize text within `frame`. On RUG_OK, *outResult is core-owned and must be
// released with Rug_FreeOcrResult.
RUGCORE_API int32_t RUGCORE_CALL Rug_RecognizeText(RugOcrEngineHandle handle,
                                                   const RugFrame* frame,
                                                   RugOcrResultHandle* outResult);

// Get the number of recognized lines in a result.
RUGCORE_API int32_t RUGCORE_CALL Rug_OcrResultGetLineCount(RugOcrResultHandle result,
                                                           int32_t* outCount);

// Copy the line at `index` (0-based) into `outLine`.
// Returns RUG_ERR_INVALID_PARAM if index is out of range.
RUGCORE_API int32_t RUGCORE_CALL Rug_OcrResultGetLine(RugOcrResultHandle result,
                                                      int32_t index,
                                                      RugOcrLine* outLine);

// -----------------------------------------------------------------------------
// OCR model discovery (models/ocr/<bundle> + model.json routing)
// -----------------------------------------------------------------------------

// Scan `ocr_dir` (e.g. <repo>/models/ocr) for model bundles. A bundle is an
// immediate subdirectory whose model.json declares kind="ocr" and a supported
// engine, and whose required model files are present. Results are sorted by id.
// On RUG_OK, *out_list is core-owned; release it with Rug_FreeModelList.
RUGCORE_API int32_t RUGCORE_CALL Rug_ScanOcrModels(const char* ocr_dir,
                                                   RugModelListHandle* out_list);

// Number of models in a scanned list.
RUGCORE_API int32_t RUGCORE_CALL Rug_ModelListGetCount(RugModelListHandle list,
                                                       int32_t* out_count);

// Copy the model info at `index` (0-based) into `out_info`.
RUGCORE_API int32_t RUGCORE_CALL Rug_ModelListGetInfo(RugModelListHandle list,
                                                      int32_t index,
                                                      RugModelInfo* out_info);

// Create an OCR engine for the discovered bundle whose model.json id == `id`,
// routing to the back-end named by its `engine` field (only "paddle" today).
// Returns RUG_ERR_OCR_MODEL_NOT_FOUND if no such bundle exists under `ocr_dir`.
RUGCORE_API int32_t RUGCORE_CALL Rug_CreateOcrEngineById(const char* ocr_dir,
                                                         const char* id,
                                                         RugOcrEngineHandle* out_handle);

// -----------------------------------------------------------------------------
// Template matching API
// -----------------------------------------------------------------------------

// Locate every occurrence of a template image inside `frame` whose normalized
// match score is >= `threshold` (0..1). `template_path` is a UTF-8 image path.
// `boxes` is a caller-allocated array; on entry *inout_count is its capacity, on
// return it is the number of boxes written. If more matches exist than capacity,
// the array is filled and RUG_ERR_BUFFER_TOO_SMALL is returned.
// Requires OpenCV; without it returns RUG_ERR_UNSUPPORTED.
RUGCORE_API int32_t RUGCORE_CALL Rug_MatchTemplate(const RugFrame* frame,
                                                   const char* template_path,
                                                   float threshold,
                                                   RugMatchBox* boxes,
                                                   int32_t* inout_count);

// -----------------------------------------------------------------------------
// Input API
// -----------------------------------------------------------------------------
//
// Coordinate space: when a target window is bound (Rug_Input_SetTargetWindow),
// ALL mouse coordinates are CLIENT-SPACE pixels of that window and are clamped to
// its client rect on every emitted point, so the cursor can never leave the bound
// window. Delivery is chosen independently by Rug_Input_SetBackgroundDelivery:
// background (default) = PostMessage to the HWND; foreground = SendInput after a
// ClientToScreen mapping. With no bound window, coordinates are absolute screen
// pixels (foreground SendInput only). The caller is responsible for Per-Monitor V2
// DPI awareness of the host process. `holdTimeMs == 0` on Click/KeyPress selects a
// random duration from the active RugHumanizeConfig.

// Create an input controller operating in `mode` (RugInputMode). `hwnd` (HWND as
// void*) optionally binds the initial target window (NULL = unbound). Delivery
// defaults to background PostMessage; use Rug_Input_SetBackgroundDelivery to switch.
// On RUG_OK, *out_handle receives a valid opaque handle.
RUGCORE_API int32_t RUGCORE_CALL Rug_CreateInputController(int32_t mode,
                                                           void* hwnd,
                                                           RugInputControllerHandle* out_handle);

// Destroy an input controller created by Rug_CreateInputController. Safe with NULL.
RUGCORE_API int32_t RUGCORE_CALL Rug_DestroyInputController(RugInputControllerHandle handle);

// Absolute move to (x, y). `trajectory` is a RugTrajectoryType; when `smooth` is
// non-zero the path is emitted as humanized samples with dynamic delays, else a
// single jump is synthesized.
RUGCORE_API int32_t RUGCORE_CALL Rug_Input_MouseMove(RugInputControllerHandle handle,
                                                     int32_t x,
                                                     int32_t y,
                                                     int32_t trajectory,
                                                     int32_t smooth);

// Relative move by (dx, dy) from the current position, honoring `trajectory`/`smooth`.
RUGCORE_API int32_t RUGCORE_CALL Rug_Input_MouseMoveRelative(RugInputControllerHandle handle,
                                                             int32_t dx,
                                                             int32_t dy,
                                                             int32_t trajectory,
                                                             int32_t smooth);

// Press / release a mouse button (RugMouseButton) without moving.
RUGCORE_API int32_t RUGCORE_CALL Rug_Input_MouseDown(RugInputControllerHandle handle,
                                                     int32_t button);
RUGCORE_API int32_t RUGCORE_CALL Rug_Input_MouseUp(RugInputControllerHandle handle,
                                                   int32_t button);

// Move nowhere; press and release `button`, holding for `holdTimeMs`
// (0 -> random within RugHumanizeConfig click-hold bounds).
RUGCORE_API int32_t RUGCORE_CALL Rug_Input_Click(RugInputControllerHandle handle,
                                                 int32_t button,
                                                 int32_t holdTimeMs);

// Humanized drag: move to (sx, sy), press left, move to (ex, ey), release.
RUGCORE_API int32_t RUGCORE_CALL Rug_Input_DragAndDrop(RugInputControllerHandle handle,
                                                       int32_t sx,
                                                       int32_t sy,
                                                       int32_t ex,
                                                       int32_t ey,
                                                       int32_t trajectory,
                                                       int32_t smooth);

// Keyboard: virtual-key down/up, and a press holding `holdTimeMs`
// (0 -> random within RugHumanizeConfig key-hold bounds).
RUGCORE_API int32_t RUGCORE_CALL Rug_Input_KeyDown(RugInputControllerHandle handle, int32_t vk);
RUGCORE_API int32_t RUGCORE_CALL Rug_Input_KeyUp(RugInputControllerHandle handle, int32_t vk);
RUGCORE_API int32_t RUGCORE_CALL Rug_Input_KeyPress(RugInputControllerHandle handle,
                                                    int32_t vk,
                                                    int32_t holdTimeMs);

// Send a UTF-8, NUL-terminated string as a sequence of Unicode characters.
RUGCORE_API int32_t RUGCORE_CALL Rug_Input_SendText(RugInputControllerHandle handle,
                                                    const char* utf8_text);

// Bind the target window (HWND as void*) used for client-space coordinate mapping
// and confinement. NULL clears the target (coords become absolute screen pixels).
RUGCORE_API int32_t RUGCORE_CALL Rug_Input_SetTargetWindow(RugInputControllerHandle handle,
                                                           void* hwnd);

// Select the delivery mechanism for the bound window: non-zero = background
// PostMessage (no focus, no real cursor movement); zero = foreground SendInput
// (real cursor; the window should be activated by the caller first).
RUGCORE_API int32_t RUGCORE_CALL Rug_Input_SetBackgroundDelivery(RugInputControllerHandle handle,
                                                                 int32_t background);

// Replace the controller's humanization tuning. The struct is copied by value.
RUGCORE_API int32_t RUGCORE_CALL Rug_Input_SetHumanizeConfig(RugInputControllerHandle handle,
                                                             const RugHumanizeConfig* config);

// Dry-run the humanizer for a move from (sx, sy) to (ex, ey) WITHOUT emitting any
// OS input. Writes up to *inout_count samples: `xs`/`ys` receive pixel positions
// and `delays` receives each sample's delay in milliseconds. On entry *inout_count
// is the capacity of all three caller-allocated arrays; on return it is the number
// of samples written. If the plan has more samples than capacity, the arrays are
// filled and RUG_ERR_BUFFER_TOO_SMALL is returned (with *inout_count == capacity).
// Useful for tests asserting the dynamic sampling-interval distribution.
RUGCORE_API int32_t RUGCORE_CALL Rug_Input_PlanTrajectory(RugInputControllerHandle handle,
                                                          int32_t sx,
                                                          int32_t sy,
                                                          int32_t ex,
                                                          int32_t ey,
                                                          int32_t trajectory,
                                                          int32_t* xs,
                                                          int32_t* ys,
                                                          float* delays,
                                                          int32_t* inout_count);

#ifdef __cplusplus
}  // extern "C"
#endif

#endif  // RUGCORE_ABI_H
