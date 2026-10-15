#include "utf8.h"

namespace bspeech::utf8 {

char32_t Decode(std::string_view text, size_t index, size_t* length) noexcept {
    *length = 1;

    if (index >= text.size()) {
        return kInvalid;
    }

    const auto b0 = static_cast<unsigned char>(text[index]);

    if (b0 < 0x80) {
        return b0;
    }

    size_t need = 0;
    char32_t cp = 0;
    char32_t min = 0;

    if ((b0 & 0xE0) == 0xC0) {
        need = 1;
        cp = b0 & 0x1F;
        min = 0x80;
    } else if ((b0 & 0xF0) == 0xE0) {
        need = 2;
        cp = b0 & 0x0F;
        min = 0x800;
    } else if ((b0 & 0xF8) == 0xF0) {
        need = 3;
        cp = b0 & 0x07;
        min = 0x10000;
    } else {
        return kInvalid;
    }

    if (index + need >= text.size()) {
        return kInvalid;
    }

    for (size_t k = 1; k <= need; ++k) {
        const auto b = static_cast<unsigned char>(text[index + k]);

        if ((b & 0xC0) != 0x80) {
            return kInvalid;
        }

        cp = (cp << 6) | (b & 0x3F);
    }

    if (cp < min || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF)) {
        return kInvalid;
    }

    *length = need + 1;
    return cp;
}

char32_t DecodeBefore(std::string_view text, size_t end, size_t* length) noexcept {
    *length = 1;

    if (end == 0 || end > text.size()) {
        return kInvalid;
    }

    size_t start = end - 1;

    // At most three continuation bytes precede a lead byte.
    while (start > 0 && end - start < 4 && (static_cast<unsigned char>(text[start]) & 0xC0) == 0x80) {
        --start;
    }

    size_t decoded = 0;
    const char32_t cp = Decode(text, start, &decoded);

    if (cp == kInvalid || start + decoded != end) {
        return kInvalid;
    }

    *length = decoded;
    return cp;
}

bool Valid(std::string_view text, size_t* bad_offset) noexcept {
    size_t i = 0;

    while (i < text.size()) {
        size_t length = 0;

        if (Decode(text, i, &length) == kInvalid) {
            *bad_offset = i;
            return false;
        }

        i += length;
    }

    return true;
}

void Append(std::string& out, char32_t c) {
    if (c < 0x80) {
        out.push_back(static_cast<char>(c));
    } else if (c < 0x800) {
        out.push_back(static_cast<char>(0xC0 | (c >> 6)));
        out.push_back(static_cast<char>(0x80 | (c & 0x3F)));
    } else if (c < 0x10000) {
        out.push_back(static_cast<char>(0xE0 | (c >> 12)));
        out.push_back(static_cast<char>(0x80 | ((c >> 6) & 0x3F)));
        out.push_back(static_cast<char>(0x80 | (c & 0x3F)));
    } else {
        out.push_back(static_cast<char>(0xF0 | (c >> 18)));
        out.push_back(static_cast<char>(0x80 | ((c >> 12) & 0x3F)));
        out.push_back(static_cast<char>(0x80 | ((c >> 6) & 0x3F)));
        out.push_back(static_cast<char>(0x80 | (c & 0x3F)));
    }
}

size_t CountCodepoints(std::string_view text) noexcept {
    size_t n = 0;

    for (const char ch : text) {
        if ((static_cast<unsigned char>(ch) & 0xC0) != 0x80) {
            ++n;
        }
    }

    return n;
}

namespace {

// Pairs laid out upper, lower, upper, lower: Latin Extended-A, Cyrillic extensions
// and Latin Extended Additional all use this shape over most of their range.
bool EvenUpper(char32_t c, char32_t first, char32_t last) noexcept {
    return c >= first && c <= last && ((c - first) % 2 == 0);
}

bool OddLower(char32_t c, char32_t first, char32_t last) noexcept {
    return c >= first && c <= last && ((c - first) % 2 == 1);
}

}  // namespace

bool IsUpper(char32_t c) noexcept {
    if (c >= 'A' && c <= 'Z') {
        return true;
    }

    if (c < 0xC0) {
        return false;
    }

    if (c <= 0xDE) {
        return c != 0xD7;
    }

    if (c >= 0x100 && c <= 0x17F) {
        if (c <= 0x137) {
            return EvenUpper(c, 0x100, 0x137);
        }

        if (c >= 0x139 && c <= 0x148) {
            return EvenUpper(c, 0x139, 0x148);
        }

        if (c >= 0x14A && c <= 0x177) {
            return EvenUpper(c, 0x14A, 0x177);
        }

        if (c == 0x178) {
            return true;
        }

        if (c >= 0x179 && c <= 0x17E) {
            return EvenUpper(c, 0x179, 0x17E);
        }

        return false;
    }

    if (c == 0x386 || (c >= 0x388 && c <= 0x38F && c != 0x38B && c != 0x38D)) {
        return true;
    }

    if (c >= 0x391 && c <= 0x3A9) {
        return c != 0x3A2;
    }

    if (c >= 0x400 && c <= 0x42F) {
        return true;
    }

    if (c >= 0x460 && c <= 0x481) {
        return EvenUpper(c, 0x460, 0x481);
    }

    if (c >= 0x48A && c <= 0x4BF) {
        return EvenUpper(c, 0x48A, 0x4BF);
    }

    if (c == 0x4C0) {
        return true;
    }

    if (c >= 0x4C1 && c <= 0x4CE) {
        return EvenUpper(c, 0x4C1, 0x4CE);
    }

    if (c >= 0x4D0 && c <= 0x52F) {
        return EvenUpper(c, 0x4D0, 0x52F);
    }

    if (c == 0x1E9E) {
        return true;
    }

    if (c >= 0x1E00 && c <= 0x1E95) {
        return EvenUpper(c, 0x1E00, 0x1E95);
    }

    if (c >= 0x1EA0 && c <= 0x1EFF) {
        return EvenUpper(c, 0x1EA0, 0x1EFF);
    }

    return false;
}

bool IsLower(char32_t c) noexcept {
    if (c >= 'a' && c <= 'z') {
        return true;
    }

    if (c < 0xDF) {
        return c == 0xB5 || c == 0xAA || c == 0xBA;
    }

    if (c <= 0xFF) {
        return c != 0xF7;
    }

    if (c >= 0x100 && c <= 0x17F) {
        if (c <= 0x137) {
            return OddLower(c, 0x100, 0x137);
        }

        if (c == 0x138 || c == 0x149 || c == 0x17F) {
            return true;
        }

        if (c >= 0x139 && c <= 0x148) {
            return OddLower(c, 0x139, 0x148);
        }

        if (c >= 0x14A && c <= 0x177) {
            return OddLower(c, 0x14A, 0x177);
        }

        if (c >= 0x179 && c <= 0x17E) {
            return OddLower(c, 0x179, 0x17E);
        }

        return false;
    }

    if (c == 0x390 || (c >= 0x3AC && c <= 0x3CE)) {
        return true;
    }

    if (c >= 0x430 && c <= 0x45F) {
        return true;
    }

    if (c >= 0x460 && c <= 0x481) {
        return OddLower(c, 0x460, 0x481);
    }

    if (c >= 0x48A && c <= 0x4BF) {
        return OddLower(c, 0x48A, 0x4BF);
    }

    if (c >= 0x4C1 && c <= 0x4CE) {
        return OddLower(c, 0x4C1, 0x4CE);
    }

    if (c == 0x4CF) {
        return true;
    }

    if (c >= 0x4D0 && c <= 0x52F) {
        return OddLower(c, 0x4D0, 0x52F);
    }

    if (c >= 0x1E96 && c <= 0x1E9D) {
        return true;
    }

    if (c == 0x1E9F) {
        return true;
    }

    if (c >= 0x1E00 && c <= 0x1E95) {
        return OddLower(c, 0x1E00, 0x1E95);
    }

    if (c >= 0x1EA0 && c <= 0x1EFF) {
        return OddLower(c, 0x1EA0, 0x1EFF);
    }

    return false;
}

char32_t ToLower(char32_t c) noexcept {
    if (!IsUpper(c)) {
        return c;
    }

    if (c <= 'Z') {
        return c + 32;
    }

    if (c <= 0xDE) {
        return c + 32;
    }

    if (c == 0x178) {
        return 0xFF;
    }

    if (c <= 0x17F) {
        return c + 1;
    }

    if (c == 0x386) {
        return 0x3AC;
    }

    if (c >= 0x388 && c <= 0x38A) {
        return c + 37;
    }

    if (c == 0x38C) {
        return 0x3CC;
    }

    if (c == 0x38E || c == 0x38F) {
        return c + 63;
    }

    if (c >= 0x391 && c <= 0x3A9) {
        return c + 32;
    }

    if (c >= 0x400 && c <= 0x40F) {
        return c + 80;
    }

    if (c >= 0x410 && c <= 0x42F) {
        return c + 32;
    }

    if (c == 0x4C0) {
        return 0x4CF;
    }

    if (c == 0x1E9E) {
        return 0xDF;
    }

    return c + 1;
}

bool IsSpace(char32_t c) noexcept {
    return (c >= 0x09 && c <= 0x0D) || c == 0x20 || c == 0x85 || c == 0xA0 || c == 0x1680 ||
           (c >= 0x2000 && c <= 0x200A) || c == 0x2028 || c == 0x2029 || c == 0x202F || c == 0x205F ||
           c == 0x3000;
}

bool IsDigit(char32_t c) noexcept {
    return (c >= '0' && c <= '9') || (c >= 0xFF10 && c <= 0xFF19);
}

bool IsWordChar(char32_t c) noexcept {
    if (IsUpper(c) || IsLower(c) || IsDigit(c)) {
        return true;
    }

    if (c >= 0x3040 && c <= 0x30FF) {
        return true;
    }

    if ((c >= 0x3400 && c <= 0x4DBF) || (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0xF900 && c <= 0xFAFF)) {
        return true;
    }

    if (c >= 0xAC00 && c <= 0xD7AF) {
        return true;
    }

    if (c >= 0x20000 && c <= 0x3134F) {
        return true;
    }

    // Combining marks attach to the letter before them.
    if (c >= 0x300 && c <= 0x36F) {
        return true;
    }

    return false;
}

}  // namespace bspeech::utf8
