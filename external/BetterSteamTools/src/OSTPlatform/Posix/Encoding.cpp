// Steamy POSIX port additions, GPL-3.0; see repository LICENSE.
#include "include/Encoding.h"
#include <cstdint>

namespace OSTPlatform::Encoding {
std::string WideToUtf8(std::wstring_view value) {
    std::string result;
    for (size_t i = 0; i < value.size(); ++i) {
        uint32_t ch = static_cast<uint32_t>(value[i]);
        if constexpr (sizeof(wchar_t) == 2) {
            if (ch >= 0xd800 && ch <= 0xdbff) {
                if (++i >= value.size()) return {};
                const auto low = static_cast<uint32_t>(value[i]);
                if (low < 0xdc00 || low > 0xdfff) return {};
                ch = 0x10000 + ((ch - 0xd800) << 10) + low - 0xdc00;
            }
        }
        if (ch > 0x10ffff || (ch >= 0xd800 && ch <= 0xdfff)) return {};
        if (ch < 0x80) result += static_cast<char>(ch);
        else if (ch < 0x800) {
            result += static_cast<char>(0xc0 | (ch >> 6));
            result += static_cast<char>(0x80 | (ch & 0x3f));
        } else if (ch < 0x10000) {
            result += static_cast<char>(0xe0 | (ch >> 12));
            result += static_cast<char>(0x80 | ((ch >> 6) & 0x3f));
            result += static_cast<char>(0x80 | (ch & 0x3f));
        } else {
            result += static_cast<char>(0xf0 | (ch >> 18));
            result += static_cast<char>(0x80 | ((ch >> 12) & 0x3f));
            result += static_cast<char>(0x80 | ((ch >> 6) & 0x3f));
            result += static_cast<char>(0x80 | (ch & 0x3f));
        }
    }
    return result;
}

std::wstring Utf8ToWide(std::string_view value) {
    std::wstring result;
    for (size_t i = 0; i < value.size();) {
        const auto first = static_cast<uint8_t>(value[i++]);
        uint32_t ch;
        unsigned count;
        if (first < 0x80) { ch = first; count = 0; }
        else if (first >= 0xc2 && first <= 0xdf) { ch = first & 0x1f; count = 1; }
        else if (first >= 0xe0 && first <= 0xef) { ch = first & 0xf; count = 2; }
        else if (first >= 0xf0 && first <= 0xf4) { ch = first & 7; count = 3; }
        else return {};
        if (i + count > value.size()) return {};
        for (unsigned j = 0; j < count; ++j) {
            const auto next = static_cast<uint8_t>(value[i++]);
            if ((next & 0xc0) != 0x80) return {};
            ch = (ch << 6) | (next & 0x3f);
        }
        if ((count == 1 && ch < 0x80) || (count == 2 && ch < 0x800)
            || (count == 3 && ch < 0x10000) || ch > 0x10ffff
            || (ch >= 0xd800 && ch <= 0xdfff)) return {};
        if constexpr (sizeof(wchar_t) == 2) {
            if (ch >= 0x10000) {
                ch -= 0x10000;
                result += static_cast<wchar_t>(0xd800 + (ch >> 10));
                result += static_cast<wchar_t>(0xdc00 + (ch & 0x3ff));
                continue;
            }
        }
        result += static_cast<wchar_t>(ch);
    }
    return result;
}
}
