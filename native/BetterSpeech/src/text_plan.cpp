#include "text_plan.h"

#include <algorithm>
#include <deque>
#include <optional>

#include "utf8.h"

namespace bspeech {

namespace {

// Aliases a writer reaches for. Matching is exact after lowercasing, so a typo is
// an unknown tag rather than a guess.
constexpr std::string_view kLaughNames[] = {"laugh", "laughs", "laughter"};

constexpr size_t kMaxTagName = 32;

// Abbreviations after which a period never ends a sentence: titles and the
// mid-sentence kind ("e.g.") that is followed by a capital as often as not.
constexpr std::string_view kNeverEndsUniversal[] = {"e.g", "i.e", "cf", "vs", "approx", "ca", "fig", "ibid", "viz"};

constexpr std::string_view kNeverEndsEn[] = {
    "mr",  "mrs", "ms",  "dr",  "prof", "st",  "sr",  "jr",  "rev", "gen", "capt", "lt",  "col", "sgt",
    "hon", "mt",  "ft",  "jan", "feb",  "apr", "jun", "jul", "aug", "sep", "sept", "oct", "nov", "dec",
    "vol", "pp",  "ed",  "eds", "dept", "nos"};

constexpr std::string_view kNeverEndsCs[] = {
    "např", "tj",   "tzv", "tzn",  "resp",  "mj",   "popř", "př",  "srov", "str", "čj",  "sv",
    "ing",  "mgr",  "bc",  "doc",  "prof",  "judr", "mudr", "phdr", "rndr", "paedr", "phd", "pí",
    "sl",   "odst", "písm", "obr", "tab",   "kap",  "ul",   "nám", "tel",  "cca", "tis", "mil",
    "mld",  "hod",  "min", "max",  "stol",  "roč"};

constexpr std::string_view kNeverEndsDe[] = {"z.b", "d.h", "u.a", "bzw", "nr",  "str",  "evtl", "ggf",
                                             "vgl", "inkl", "bspw", "hr", "fr", "dr", "prof", "sog"};

// Abbreviations that often do end a sentence: a capital after them is a new one.
constexpr std::string_view kMayEndUniversal[] = {"etc"};
constexpr std::string_view kMayEndEn[] = {"inc", "ltd", "co", "corp"};
constexpr std::string_view kMayEndCs[] = {"atd", "apod", "aj", "atp"};
constexpr std::string_view kMayEndDe[] = {"usw", "etc"};

template <size_t N>
bool Contains(const std::string_view (&list)[N], std::string_view word) {
    return std::find(std::begin(list), std::end(list), word) != std::end(list);
}

std::string PrimaryLanguage(std::string_view tag) {
    std::string out;

    for (const char ch : tag) {
        if (ch == '-' || ch == '_') {
            break;
        }

        out.push_back(static_cast<char>(ch >= 'A' && ch <= 'Z' ? ch + 32 : ch));
    }

    return out;
}

bool NeverEnds(std::string_view lang, std::string_view word) {
    if (Contains(kNeverEndsUniversal, word)) {
        return true;
    }

    if (lang == "en") {
        return Contains(kNeverEndsEn, word);
    }

    if (lang == "cs" || lang == "sk") {
        return Contains(kNeverEndsCs, word);
    }

    if (lang == "de") {
        return Contains(kNeverEndsDe, word);
    }

    return false;
}

bool MayEnd(std::string_view lang, std::string_view word) {
    if (Contains(kMayEndUniversal, word)) {
        return true;
    }

    if (lang == "en") {
        return Contains(kMayEndEn, word);
    }

    if (lang == "cs" || lang == "sk") {
        return Contains(kMayEndCs, word);
    }

    if (lang == "de") {
        return Contains(kMayEndDe, word);
    }

    return false;
}

bool IsTerminator(char32_t c) {
    return c == '.' || c == '!' || c == '?' || c == 0x2026 || c == 0x3002 || c == 0xFF01 || c == 0xFF1F ||
           c == 0x0964 || c == 0x061F || c == 0x203C || (c >= 0x2047 && c <= 0x2049);
}

// Ends a sentence without the whitespace a Latin script would put after it.
bool IsUnspacedTerminator(char32_t c) { return c == 0x3002 || c == 0xFF01 || c == 0xFF1F; }

bool IsExclaimOrQuestion(char32_t c) {
    return c == '!' || c == '?' || c == 0xFF01 || c == 0xFF1F || c == 0x061F || c == 0x203C ||
           (c >= 0x2047 && c <= 0x2049);
}

bool IsCloser(char32_t c) {
    return c == ')' || c == ']' || c == '}' || c == '"' || c == '\'' || c == 0x2018 || c == 0x2019 ||
           c == 0x201C || c == 0x201D || c == 0x00AB || c == 0x00BB || c == 0x300D || c == 0x300F ||
           c == 0x3011 || c == 0xFF09;
}

bool IsClauseSeparator(char32_t c) {
    return c == ',' || c == ';' || c == ':' || c == 0x2013 || c == 0x2014 || c == 0x3001 || c == 0xFF0C ||
           c == 0xFF1B || c == 0xFF1A || c == 0x060C;
}

bool IsAsciiTagStart(char c) { return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'); }

bool IsAsciiTagChar(char c) { return IsAsciiTagStart(c) || (c >= '0' && c <= '9') || c == '_' || c == '-'; }

EventKind LookupEvent(std::string_view lower) {
    if (Contains(kLaughNames, lower)) {
        return EventKind::Laugh;
    }

    return EventKind::None;
}

bool HasWord(std::string_view text, Span span) {
    size_t i = span.start;

    while (i < span.end) {
        size_t length = 0;
        const char32_t cp = utf8::Decode(text, i, &length);

        if (utf8::IsWordChar(cp)) {
            return true;
        }

        i += length;
    }

    return false;
}

Span Trim(std::string_view text, Span span) {
    while (span.start < span.end) {
        size_t length = 0;
        const char32_t cp = utf8::Decode(text, span.start, &length);

        if (!utf8::IsSpace(cp)) {
            break;
        }

        span.start += length;
    }

    while (span.end > span.start) {
        size_t length = 0;
        const char32_t cp = utf8::DecodeBefore(text, span.end, &length);

        if (!utf8::IsSpace(cp)) {
            break;
        }

        span.end -= length;
    }

    return span;
}

void PushSpan(std::vector<Span>& spans, std::string_view text, Span span) {
    span = Trim(text, span);

    if (span.end > span.start) {
        spans.push_back(span);
    }
}

// Letters, digits and inner periods directly before `end`, so "e.g" comes back
// whole from "e.g." and "Dr" from "Dr.".
std::string WordBefore(std::string_view text, size_t end) {
    size_t start = end;

    while (start > 0) {
        size_t length = 0;
        const char32_t cp = utf8::DecodeBefore(text, start, &length);

        if (cp == utf8::kInvalid || !(utf8::IsWordChar(cp) || cp == '.')) {
            break;
        }

        start -= length;
    }

    std::string lower;
    size_t i = start;

    while (i < end) {
        size_t length = 0;
        const char32_t cp = utf8::Decode(text, i, &length);
        utf8::Append(lower, utf8::ToLower(cp));
        i += length;
    }

    while (!lower.empty() && lower.front() == '.') {
        lower.erase(lower.begin());
    }

    return lower;
}

// Whether the period-like run ending a word at `term_start`, followed by `next`,
// closes the sentence.
bool PeriodBreaks(std::string_view text, std::string_view lang, size_t term_start, char32_t next) {
    if (utf8::IsLower(next)) {
        return false;
    }

    const std::string word = WordBefore(text, term_start);

    // A single letter is an initial or a one-letter abbreviation ("J. Smith",
    // "z. B."), and leaving it joined loses nothing.
    if (utf8::CountCodepoints(word) == 1) {
        size_t length = 0;
        const char32_t only = utf8::Decode(word, 0, &length);

        if (utf8::IsUpper(only) || utf8::IsLower(only)) {
            return false;
        }
    }

    if (NeverEnds(lang, word)) {
        return false;
    }

    if (MayEnd(lang, word)) {
        return utf8::IsUpper(next);
    }

    return true;
}

// The end of a blank line starting at `newline`, or npos when the line break is a
// single one.
size_t BlankLineEnd(std::string_view text, size_t newline) {
    size_t i = newline + 1;
    bool second = false;

    while (i < text.size()) {
        const char ch = text[i];

        if (ch == '\n') {
            second = true;
        } else if (ch != ' ' && ch != '\t' && ch != '\r') {
            break;
        }

        ++i;
    }

    return second ? i : std::string_view::npos;
}

}  // namespace

bool ParseEvents(std::string_view input, UnknownTags policy, std::vector<Segment>* out, PlanError* error) {
    out->clear();

    TextRun run;

    auto close_run = [&](size_t end) {
        bool speakable = false;
        size_t i = 0;

        while (i < run.text.size()) {
            size_t length = 0;

            if (!utf8::IsSpace(utf8::Decode(run.text, i, &length))) {
                speakable = true;
                break;
            }

            i += length;
        }

        if (speakable) {
            Segment segment;
            segment.kind = ItemKind::Speech;
            segment.source_offset = run.offsets.front();
            segment.source_length = end - run.offsets.front();
            run.offsets.push_back(static_cast<uint32_t>(end));
            segment.run = std::move(run);
            out->push_back(std::move(segment));
        }

        run = TextRun{};
    };

    auto append = [&](char ch, size_t origin) {
        run.text.push_back(ch);
        run.offsets.push_back(static_cast<uint32_t>(origin));
    };

    size_t i = 0;
    const size_t n = input.size();

    while (i < n) {
        const char ch = input[i];

        if (ch == '\\' && i + 1 < n && (input[i + 1] == '[' || input[i + 1] == ']' || input[i + 1] == '\\')) {
            append(input[i + 1], i);
            i += 2;
            continue;
        }

        if (ch == '[' && i + 1 < n && IsAsciiTagStart(input[i + 1])) {
            size_t k = i + 2;

            while (k < n && k - (i + 1) < kMaxTagName && IsAsciiTagChar(input[k])) {
                ++k;
            }

            if (k < n && input[k] == ']') {
                const std::string_view name = input.substr(i + 1, k - i - 1);
                std::string lower(name);

                for (char& c : lower) {
                    if (c >= 'A' && c <= 'Z') {
                        c = static_cast<char>(c + 32);
                    }
                }

                const EventKind kind = LookupEvent(lower);

                if (kind != EventKind::None) {
                    close_run(i);

                    Segment segment;
                    segment.kind = ItemKind::Event;
                    segment.event = kind;
                    segment.source_offset = i;
                    segment.source_length = k + 1 - i;
                    out->push_back(std::move(segment));

                    i = k + 1;
                    continue;
                }

                if (policy == UnknownTags::Error) {
                    error->message = "unknown tag [" + std::string(name) + "] at byte " + std::to_string(i) +
                                     "; the known tags are [Laugh], [Laughs] and [Laughter]. Write \\[" +
                                     std::string(name) + "] to have it read as text";
                    error->offset = static_cast<int64_t>(i);
                    return false;
                }
            }
        }

        append(ch, i);
        ++i;
    }

    close_run(n);
    return true;
}

std::vector<Span> SplitSentences(std::string_view text, std::string_view language) {
    const std::string lang = PrimaryLanguage(language);
    std::vector<Span> spans;
    size_t start = 0;
    size_t i = 0;

    while (i < text.size()) {
        size_t length = 0;
        const char32_t cp = utf8::Decode(text, i, &length);

        if (cp == '\n') {
            const size_t blank_end = BlankLineEnd(text, i);

            if (blank_end != std::string_view::npos) {
                PushSpan(spans, text, {start, i});
                start = blank_end;
                i = blank_end;
                continue;
            }
        }

        if (!IsTerminator(cp)) {
            i += length;
            continue;
        }

        const size_t term_start = i;
        bool exclaims = false;
        bool unspaced = false;
        size_t j = i;

        while (j < text.size()) {
            size_t l = 0;
            const char32_t t = utf8::Decode(text, j, &l);

            if (!IsTerminator(t)) {
                break;
            }

            exclaims = exclaims || IsExclaimOrQuestion(t);
            unspaced = unspaced || IsUnspacedTerminator(t);
            j += l;
        }

        while (j < text.size()) {
            size_t l = 0;

            if (!IsCloser(utf8::Decode(text, j, &l))) {
                break;
            }

            j += l;
        }

        const size_t end = j;

        if (end >= text.size()) {
            break;
        }

        if (unspaced) {
            PushSpan(spans, text, {start, end});
            start = end;
            i = end;
            continue;
        }

        size_t l = 0;

        if (!utf8::IsSpace(utf8::Decode(text, end, &l))) {
            i = end;
            continue;
        }

        size_t k = end;

        while (k < text.size()) {
            const char32_t s = utf8::Decode(text, k, &l);

            if (!utf8::IsSpace(s)) {
                break;
            }

            k += l;
        }

        if (k >= text.size()) {
            break;
        }

        const char32_t next = utf8::Decode(text, k, &l);
        const bool breaks = exclaims || PeriodBreaks(text, lang, term_start, next);

        if (breaks) {
            PushSpan(spans, text, {start, end});
            start = k;
        }

        i = k;
    }

    PushSpan(spans, text, {start, text.size()});
    return spans;
}

std::string SpeakableText(std::string_view text, Span span) {
    span = Trim(text, span);

    std::string out;
    out.reserve(span.end - span.start);

    bool in_space = false;
    size_t i = span.start;

    while (i < span.end) {
        size_t length = 0;
        const char32_t cp = utf8::Decode(text, i, &length);

        if (utf8::IsSpace(cp)) {
            in_space = true;
        } else {
            if (in_space) {
                out.push_back(' ');
                in_space = false;
            }

            out.append(text.substr(i, length));
        }

        i += length;
    }

    return out;
}

namespace {

class Chunker {
public:
    Chunker(const PlanSettings& settings, std::vector<PlanItem>* out, PlanError* error)
        : settings_(settings), out_(out), error_(error) {}

    bool Run(const Segment& segment) {
        const std::string_view text = segment.run.text;
        std::deque<Span> queue;

        for (const Span& s : MergeWordless(text, SplitSentences(text, settings_.language))) {
            queue.push_back(s);
        }

        std::optional<Span> current;

        while (!queue.empty()) {
            Span piece = queue.front();
            queue.pop_front();

            const int32_t budget = BudgetFor(emitted_);

            if (budget <= 0) {
                if (current) {
                    Emit(segment, *current);
                    current.reset();
                }

                Emit(segment, piece);
                continue;
            }

            int32_t units = 0;

            if (!MeasureSpan(text, piece, &units)) {
                return false;
            }

            if (units > budget) {
                if (current) {
                    Emit(segment, *current);
                    current.reset();
                    queue.push_front(piece);
                    continue;
                }

                Span head;

                if (!FitPrefix(text, piece, budget, &head)) {
                    return false;
                }

                Emit(segment, head);

                const Span rest = Trim(text, {head.end, piece.end});

                if (rest.end > rest.start) {
                    queue.push_front(rest);
                }

                continue;
            }

            if (!current) {
                current = piece;
                continue;
            }

            const Span joined{current->start, piece.end};
            int32_t joined_units = 0;

            if (!MeasureSpan(text, joined, &joined_units)) {
                return false;
            }

            if (joined_units <= budget) {
                current = joined;
                continue;
            }

            Emit(segment, *current);
            current.reset();
            queue.push_front(piece);
        }

        if (current) {
            Emit(segment, *current);
        }

        return true;
    }

private:
    int32_t BudgetFor(size_t index) const {
        const int32_t max = settings_.max_units;
        const int32_t first = settings_.first_chunk_units;

        if (index == 0 && first > 0) {
            return max > 0 ? std::min(first, max) : first;
        }

        return max;
    }

    bool MeasureSpan(std::string_view text, Span span, int32_t* units) {
        const std::string speakable = SpeakableText(text, span);

        if (!settings_.measure) {
            *units = static_cast<int32_t>(utf8::CountCodepoints(speakable));
            return true;
        }

        *units = settings_.measure(speakable);

        if (*units < 0) {
            error_->message = "the model could not measure a chunk of the text";
            error_->offset = -1;
            return false;
        }

        return true;
    }

    // Pieces with no word in them ("...", a lone quote) are folded into a
    // neighbour: sent alone they are noise, and dropped they would lose a pause.
    static std::vector<Span> MergeWordless(std::string_view text, const std::vector<Span>& spans) {
        std::vector<Span> out;
        size_t pending = std::string_view::npos;

        for (Span s : spans) {
            if (!HasWord(text, s)) {
                if (!out.empty()) {
                    out.back().end = s.end;
                } else if (pending == std::string_view::npos) {
                    pending = s.start;
                }

                continue;
            }

            if (pending != std::string_view::npos) {
                s.start = pending;
                pending = std::string_view::npos;
            }

            out.push_back(s);
        }

        return out;
    }

    // The longest prefix of span that fits the budget, cut at the coarsest boundary
    // that allows one: a clause, then a word, then a codepoint. Always advances.
    bool FitPrefix(std::string_view text, Span span, int32_t budget, Span* head) {
        std::vector<size_t> clause_cuts;
        std::vector<size_t> word_cuts;
        std::vector<size_t> codepoint_cuts;

        size_t i = span.start;
        bool prev_space = false;

        while (i < span.end) {
            size_t length = 0;
            const char32_t cp = utf8::Decode(text, i, &length);
            const bool space = utf8::IsSpace(cp);

            if (space && !prev_space && i > span.start) {
                word_cuts.push_back(i);
            }

            i += length;

            if (i < span.end) {
                codepoint_cuts.push_back(i);

                if (IsClauseSeparator(cp)) {
                    clause_cuts.push_back(i);
                }
            }

            prev_space = space;
        }

        for (const std::vector<size_t>* cuts : {&clause_cuts, &word_cuts, &codepoint_cuts}) {
            size_t best = 0;
            bool found = false;
            size_t lo = 0;
            size_t hi = cuts->size();

            // Token counts grow with the prefix, so the fitting cuts are a prefix
            // of the list and a binary search finds the last one.
            while (lo < hi) {
                const size_t mid = lo + (hi - lo) / 2;
                int32_t units = 0;

                if (!MeasureSpan(text, {span.start, (*cuts)[mid]}, &units)) {
                    return false;
                }

                if (units <= budget && HasWord(text, {span.start, (*cuts)[mid]})) {
                    best = (*cuts)[mid];
                    found = true;
                    lo = mid + 1;
                } else {
                    hi = mid;
                }
            }

            if (found) {
                *head = {span.start, best};
                return true;
            }
        }

        // Not even one codepoint fits. Send one anyway: a chunk over budget is a
        // degraded chunk, and a chunker that cannot advance never finishes.
        *head = {span.start, codepoint_cuts.empty() ? span.end : codepoint_cuts.front()};
        return true;
    }

    void Emit(const Segment& segment, Span span) {
        const std::string_view text = segment.run.text;
        span = Trim(text, span);

        if (span.end <= span.start) {
            return;
        }

        PlanItem item;
        item.kind = ItemKind::Speech;
        item.text = SpeakableText(text, span);
        item.source_offset = segment.run.offsets[span.start];
        item.source_length = segment.run.offsets[span.end] - segment.run.offsets[span.start];
        out_->push_back(std::move(item));
        ++emitted_;
    }

    const PlanSettings& settings_;
    std::vector<PlanItem>* out_;
    PlanError* error_;
    size_t emitted_ = 0;
};

}  // namespace

bool BuildPlan(std::string_view input, const PlanSettings& settings, std::vector<PlanItem>* out,
               PlanError* error) {
    out->clear();

    if (input.size() > kMaxInputBytes) {
        error->message = "the text is " + std::to_string(input.size()) + " bytes; the limit is " +
                         std::to_string(kMaxInputBytes);
        error->offset = -1;
        return false;
    }

    size_t bad = 0;

    if (!utf8::Valid(input, &bad)) {
        error->message = "the text is not valid UTF-8 at byte " + std::to_string(bad);
        error->offset = static_cast<int64_t>(bad);
        return false;
    }

    std::vector<Segment> segments;

    if (!ParseEvents(input, settings.unknown_tags, &segments, error)) {
        return false;
    }

    Chunker chunker(settings, out, error);

    for (const Segment& segment : segments) {
        if (segment.kind == ItemKind::Event) {
            PlanItem item;
            item.kind = ItemKind::Event;
            item.event = segment.event;
            item.source_offset = segment.source_offset;
            item.source_length = segment.source_length;
            out->push_back(std::move(item));
            continue;
        }

        if (!chunker.Run(segment)) {
            return false;
        }
    }

    return true;
}

}  // namespace bspeech
