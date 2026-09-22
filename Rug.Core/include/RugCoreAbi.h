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
    RUG_INPUT_POSTMESSAGE = 0,  // Background, window-message based (no focus).
    RUG_INPUT_SENDINPUT   = 1   // Foreground, humanized Bezier-curve SendInput.
} RugInputMode;

typedef enum RugMouseButton {
    RUG_MOUSE_LEFT   = 0,
    RUG_MOUSE_RIGHT  = 1,
    RUG_MOUSE_MIDDLE = 2
} RugMouseButton;

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

// -----------------------------------------------------------------------------
// Memory ownership API
// -----------------------------------------------------------------------------

// Release a core-owned raw buffer (e.g. RugFrame.data). Safe to call with NULL.
// Returns void: deallocation cannot meaningfully fail across the ABI.
RUGCORE_API void RUGCORE_CALL Rug_FreeBuffer(uint8_t* ptr);

// Release a core-owned OCR result and all memory it owns. Safe to call with NULL.
RUGCORE_API void RUGCORE_CALL Rug_FreeOcrResult(RugOcrResultHandle result);

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
//   - RUG_OCR_ENGINE_WINRT (0):  model_path is ignored; WinRT uses zh-Hans with
//                                fallback to the user's profile languages.
//   - RUG_OCR_ENGINE_PADDLE (1): model_path is a UTF-8 path to the PP-OCRv5 ONNX
//                                model; missing file -> RUG_ERR_OCR_MODEL_NOT_FOUND.
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

// Create an input controller operating in `mode` (RugInputMode).
RUGCORE_API int32_t RUGCORE_CALL Rug_CreateInputController(int32_t mode,
                                                           RugInputControllerHandle* outHandle);

// Destroy an input controller created by Rug_CreateInputController.
RUGCORE_API int32_t RUGCORE_CALL Rug_DestroyInputController(RugInputControllerHandle handle);

// Synthesize a click at client-space (x, y) on `targetWindow` (HWND as void*).
// `button` is a RugMouseButton value. Coordinates must already be normalized
// against Per-Monitor V2 DPI scaling by the caller.
RUGCORE_API int32_t RUGCORE_CALL Rug_Click(RugInputControllerHandle handle,
                                           void* targetWindow,
                                           int32_t x,
                                           int32_t y,
                                           int32_t button);

#ifdef __cplusplus
}  // extern "C"
#endif

#endif  // RUGCORE_ABI_H
