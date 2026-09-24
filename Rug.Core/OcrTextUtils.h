// =============================================================================
//  OcrTextUtils.h — shared OCR text post-processing (Task 1.3.1).
//  Used by both WinRtOcrEngine and PaddleOcrEngine so CJK spacing handling is
//  identical regardless of back-end.
// =============================================================================

#ifndef OCR_TEXT_UTILS_H
#define OCR_TEXT_UTILS_H
#pragma once

#include <cwctype>
#include <string>
#include <string_view>

namespace rug::core {

// True for CJK code points where Windows/PP-OCR may insert spurious spaces.
inline bool IsCjk(wchar_t c) {
    return (c >= 0x3000 && c <= 0x303F)   // CJK symbols & punctuation
        || (c >= 0x3040 && c <= 0x30FF)   // Hiragana + Katakana
        || (c >= 0x3400 && c <= 0x4DBF)   // CJK Ext A
        || (c >= 0x4E00 && c <= 0x9FFF)   // CJK Unified Ideographs
        || (c >= 0xF900 && c <= 0xFAFF)   // CJK Compatibility Ideographs
        || (c >= 0xFF00 && c <= 0xFFEF);  // Fullwidth forms
}

// Strip the spaces OCR inserts between Han glyphs, collapse other whitespace
// runs to a single space, and trim the trailing space.
inline std::wstring CompactText(std::wstring_view s) {
    std::wstring out;
    const size_t n = s.size();
    size_t i = 0;
    while (i < n) {
        if (!iswspace(s[i])) { out.push_back(s[i]); ++i; continue; }
        size_t j = i;
        while (j < n && iswspace(s[j])) ++j;
        const bool prevCjk = !out.empty() && IsCjk(out.back());
        const bool nextCjk = (j < n) && IsCjk(s[j]);
        if (j < n && !(prevCjk && nextCjk)) out.push_back(L' ');
        i = j;
    }
    return out;
}

}  // namespace rug::core

#endif  // OCR_TEXT_UTILS_H
