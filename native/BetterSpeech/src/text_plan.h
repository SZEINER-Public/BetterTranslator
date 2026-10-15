#pragma once

#include <cstddef>
#include <cstdint>
#include <functional>
#include <string>
#include <string_view>
#include <vector>

namespace bspeech {

enum class ItemKind : int32_t { Speech = 0, Event = 1 };

enum class EventKind : int32_t { None = 0, Laugh = 1 };

enum class UnknownTags : int32_t { Error = 0, Literal = 1 };

// Inputs above this are refused rather than planned: a plan holds a byte map of its
// input, and an unbounded input would make that map unbounded too.
inline constexpr size_t kMaxInputBytes = 8u * 1024u * 1024u;

struct PlanItem {
    ItemKind kind = ItemKind::Speech;
    EventKind event = EventKind::None;
    std::string text;
    size_t source_offset = 0;
    size_t source_length = 0;
};

struct PlanError {
    std::string message;
    int64_t offset = -1;
};

// The model's measure of a piece of text, normally its token count. Negative means
// the measure itself failed.
using Measure = std::function<int32_t(std::string_view)>;

struct PlanSettings {
    std::string language;
    UnknownTags unknown_tags = UnknownTags::Error;

    // Budget per chunk in `measure` units. 0 means no limit is known, and each
    // sentence becomes its own chunk.
    int32_t max_units = 0;

    // Budget of the first chunk only, so the first audio can be asked for sooner.
    // 0 means the same as max_units.
    int32_t first_chunk_units = 0;

    // Null measures in codepoints.
    Measure measure;
};

// A run of text between events, after escapes are resolved. offsets[i] is the input
// byte that produced text byte i; offsets[text.size()] is where the run ends.
struct TextRun {
    std::string text;
    std::vector<uint32_t> offsets;
};

struct Segment {
    ItemKind kind = ItemKind::Speech;
    EventKind event = EventKind::None;
    TextRun run;
    size_t source_offset = 0;
    size_t source_length = 0;
};

struct Span {
    size_t start = 0;
    size_t end = 0;
};

// Splits the input into text runs and events. The grammar:
//   [Name]   an event when Name, compared case-insensitively, is on the allowlist
//            (laugh, laughs, laughter). Name is an ASCII letter followed by up to 31
//            letters, digits, '_' or '-'; nothing else inside the brackets.
//   \[ \] \\ a literal bracket or backslash. Any other backslash is kept as is.
// A bracket that does not form a tag, such as "[1]", "[ a ]" or one never closed,
// is text. An unknown tag follows UnknownTags.
[[nodiscard]] bool ParseEvents(std::string_view input, UnknownTags policy, std::vector<Segment>* out,
                               PlanError* error);

// Sentence spans over text, trimmed of surrounding whitespace.
[[nodiscard]] std::vector<Span> SplitSentences(std::string_view text, std::string_view language);

// The whole plan: events in place, each run cut into chunks that fit the budget.
[[nodiscard]] bool BuildPlan(std::string_view input, const PlanSettings& settings, std::vector<PlanItem>* out,
                             PlanError* error);

// The text of span as it is sent: trimmed, with each whitespace run collapsed to
// one space.
[[nodiscard]] std::string SpeakableText(std::string_view text, Span span);

}  // namespace bspeech
