#pragma once

#include <cstddef>
#include <string>
#include <string_view>

namespace bspeech::utf8 {

inline constexpr char32_t kInvalid = 0xFFFFFFFFu;

// Decodes the codepoint starting at text[index]. Returns kInvalid for a malformed,
// overlong or surrogate sequence, with *length set to 1 so a caller can step over it.
[[nodiscard]] char32_t Decode(std::string_view text, size_t index, size_t* length) noexcept;

// Decodes the codepoint that ends at text[end - 1], for walking backwards.
[[nodiscard]] char32_t DecodeBefore(std::string_view text, size_t end, size_t* length) noexcept;

// Whether the whole text is well-formed UTF-8. *bad_offset receives the first bad byte.
[[nodiscard]] bool Valid(std::string_view text, size_t* bad_offset) noexcept;

void Append(std::string& out, char32_t codepoint);

[[nodiscard]] size_t CountCodepoints(std::string_view text) noexcept;

// Case classification covers Latin (Basic, Latin-1, Extended-A, Extended Additional),
// Greek and Cyrillic. Every other script is uncased: neither upper nor lower.
[[nodiscard]] bool IsUpper(char32_t c) noexcept;
[[nodiscard]] bool IsLower(char32_t c) noexcept;
[[nodiscard]] char32_t ToLower(char32_t c) noexcept;

[[nodiscard]] bool IsSpace(char32_t c) noexcept;
[[nodiscard]] bool IsDigit(char32_t c) noexcept;

// A letter in the scripts above, or any codepoint at or above U+3040 that is not
// punctuation or a symbol: kana, CJK ideographs and Hangul.
[[nodiscard]] bool IsWordChar(char32_t c) noexcept;

}  // namespace bspeech::utf8
