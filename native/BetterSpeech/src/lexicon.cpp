#include "lexicon.h"

#include <algorithm>
#include <array>
#include <functional>

#include "utf8.h"

namespace bspeech {

namespace {

constexpr std::array<const char*, 20> kOnes = {"zero",    "one",     "two",       "three",    "four",
                                               "five",    "six",     "seven",     "eight",    "nine",
                                               "ten",     "eleven",  "twelve",    "thirteen", "fourteen",
                                               "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"};

constexpr std::array<const char*, 10> kTens = {"",      "",      "twenty",  "thirty", "forty",
                                               "fifty", "sixty", "seventy", "eighty", "ninety"};

constexpr size_t kMaxTermBytes = 128;
constexpr size_t kMaxHarvestBytes = 64;
constexpr size_t kMaxVariants = 8;
constexpr size_t kMaxCaseWords = 8;
constexpr float kFuzzyPenalty = 0.7f;

bool AsciiAlpha(char c) { return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'); }

bool AsciiDigit(char c) { return c >= '0' && c <= '9'; }

bool AsciiUpper(char c) { return c >= 'A' && c <= 'Z'; }

bool AsciiLower(char c) { return c >= 'a' && c <= 'z'; }

char AsciiToUpper(char c) { return AsciiLower(c) ? static_cast<char>(c - 32) : c; }

bool HasVowel(std::string_view lower) { return lower.find_first_of("aeiouy") != std::string_view::npos; }

std::vector<std::string> Spelled(std::string_view lower) {
    std::vector<std::string> letters;
    size_t i = 0;

    while (i < lower.size()) {
        size_t length = 0;
        (void)utf8::Decode(lower, i, &length);
        letters.emplace_back(lower.substr(i, length));
        i += length;
    }

    return letters;
}

std::vector<std::vector<std::string>> NumberAlternatives(std::string_view digits) {
    std::vector<std::vector<std::string>> alternatives;

    if (digits.size() <= 2 && !(digits.size() == 2 && digits[0] == '0')) {
        const int value = std::stoi(std::string(digits));

        if (value < 20) {
            alternatives.push_back({kOnes[static_cast<size_t>(value)]});
        } else if (value % 10 == 0) {
            alternatives.push_back({kTens[static_cast<size_t>(value / 10)]});
        } else {
            alternatives.push_back({kTens[static_cast<size_t>(value / 10)], kOnes[static_cast<size_t>(value % 10)]});
        }
    }

    if (digits.size() > 1 || alternatives.empty()) {
        std::vector<std::string> each;

        for (const char d : digits) {
            each.emplace_back(kOnes[static_cast<size_t>(d - '0')]);
        }

        alternatives.push_back(std::move(each));
    }

    return alternatives;
}

std::vector<std::vector<std::string>> SymbolAlternatives(char c) {
    switch (c) {
        case '.':
            return {{"dot"}};
        case '-':
            return {{"dash"}, {"hyphen"}};
        case '_':
            return {{"underscore"}};
        case '/':
            return {{"slash"}};
        case '\\':
            return {{"backslash"}};
        case '#':
            return {{"sharp"}, {"hash"}};
        case '+':
            return {{"plus"}};
        case '@':
            return {{"at"}};
        case ':':
            return {{"colon"}};
        default:
            return {};
    }
}

std::vector<std::string> SplitHumps(std::string_view run) {
    std::vector<std::string> humps;
    std::string current;
    std::vector<std::pair<size_t, char32_t>> cps;
    size_t i = 0;

    while (i < run.size()) {
        size_t length = 0;
        cps.emplace_back(i, utf8::Decode(run, i, &length));
        i += length;
    }

    for (size_t k = 0; k < cps.size(); ++k) {
        const char32_t c = cps[k].second;
        const size_t begin = cps[k].first;
        const size_t end = k + 1 < cps.size() ? cps[k + 1].first : run.size();

        if (!current.empty() && k > 0) {
            const char32_t prev = cps[k - 1].second;
            const bool lower_to_upper = utf8::IsLower(prev) && utf8::IsUpper(c);
            const bool acronym_end = utf8::IsUpper(prev) && utf8::IsUpper(c) && k + 1 < cps.size() &&
                                     utf8::IsLower(cps[k + 1].second);

            if (lower_to_upper || acronym_end) {
                humps.push_back(current);
                current.clear();
            }
        }

        current.append(run.substr(begin, end - begin));
    }

    if (!current.empty()) {
        humps.push_back(current);
    }

    return humps;
}

bool AllUpper(std::string_view text) {
    size_t i = 0;
    size_t letters = 0;

    while (i < text.size()) {
        size_t length = 0;
        const char32_t c = utf8::Decode(text, i, &length);

        if (utf8::IsLower(c)) {
            return false;
        }

        letters += utf8::IsUpper(c) ? 1 : 0;
        i += length;
    }

    return letters > 0;
}

std::vector<std::vector<std::string>> LetterAlternatives(std::string_view run) {
    const std::vector<std::string> humps = SplitHumps(run);
    std::vector<std::vector<std::string>> alternatives;

    if (humps.size() > 1) {
        std::vector<std::string> words;

        for (const auto& hump : humps) {
            words.push_back(LowerUtf8(hump));
        }

        alternatives.push_back(words);
        alternatives.push_back({LowerUtf8(run)});
        return alternatives;
    }

    const std::string lower = LowerUtf8(run);
    const size_t count = utf8::CountCodepoints(lower);

    if (count >= 2 && AllUpper(run)) {
        alternatives.push_back({lower});
        alternatives.push_back(Spelled(lower));
    } else if (count == 1) {
        alternatives.push_back({lower});

        if (lower == "c") {
            alternatives.push_back({"see"});
        }
    } else if (count <= 4 && !HasVowel(lower)) {
        alternatives.push_back(Spelled(lower));
        alternatives.push_back({lower});
    } else {
        alternatives.push_back({lower});
    }

    return alternatives;
}

using Alternatives = std::vector<std::vector<std::string>>;

bool Pieces(std::string_view written, std::vector<Alternatives>* pieces) {
    size_t i = 0;

    while (i < written.size()) {
        size_t length = 0;
        const char32_t c = utf8::Decode(written, i, &length);

        if (utf8::IsSpace(c)) {
            i += length;
            continue;
        }

        if (utf8::IsUpper(c) || utf8::IsLower(c)) {
            size_t j = i;

            while (j < written.size()) {
                size_t l = 0;
                const char32_t d = utf8::Decode(written, j, &l);

                if (!(utf8::IsUpper(d) || utf8::IsLower(d))) {
                    break;
                }

                j += l;
            }

            pieces->push_back(LetterAlternatives(written.substr(i, j - i)));
            i = j;
            continue;
        }

        if (AsciiDigit(static_cast<char>(c))) {
            size_t j = i;

            while (j < written.size() && AsciiDigit(written[j])) {
                ++j;
            }

            pieces->push_back(NumberAlternatives(written.substr(i, j - i)));
            i = j;
            continue;
        }

        if (c < 0x80) {
            Alternatives symbol = SymbolAlternatives(static_cast<char>(c));

            if (symbol.empty()) {
                return false;
            }

            pieces->push_back(std::move(symbol));
            i += length;
            continue;
        }

        return false;
    }

    return !pieces->empty();
}

uint64_t Fnv(uint64_t hash, std::string_view text) {
    for (const char ch : text) {
        hash ^= static_cast<unsigned char>(ch);
        hash *= 1099511628211ull;
    }

    return hash;
}

bool WithinOneEdit(std::string_view a, std::string_view b) {
    if (a == b) {
        return true;
    }

    const size_t la = a.size();
    const size_t lb = b.size();

    if (la > lb + 1 || lb > la + 1) {
        return false;
    }

    size_t i = 0;
    size_t j = 0;
    bool edited = false;

    while (i < la && j < lb) {
        if (a[i] == b[j]) {
            ++i;
            ++j;
            continue;
        }

        if (edited) {
            return false;
        }

        edited = true;

        if (la > lb) {
            ++i;
        } else if (lb > la) {
            ++j;
        } else {
            ++i;
            ++j;
        }
    }

    return !edited || (i == la && j == lb);
}

bool IsDelimiter(char c) {
    switch (c) {
        case ',':
        case ';':
        case '(':
        case ')':
        case '[':
        case ']':
        case '{':
        case '}':
        case '"':
        case '\'':
        case '`':
        case '<':
        case '>':
        case '=':
        case '|':
        case '*':
        case '&':
        case '!':
        case '?':
        case ':':
            return true;
        default:
            return false;
    }
}

std::string_view CleanToken(std::string_view token) {
    const bool dot_name = token.size() > 2 && token[0] == '.' && AsciiAlpha(token[1]);

    if (token.rfind("--", 0) != 0 && !dot_name) {
        while (!token.empty() && std::string_view("-/#@.").find(token.front()) != std::string_view::npos) {
            token.remove_prefix(1);
        }
    }

    while (!token.empty() && std::string_view(".-/").find(token.back()) != std::string_view::npos) {
        token.remove_suffix(1);
    }

    return token;
}

}

std::string LowerUtf8(std::string_view text) {
    std::string out;
    out.reserve(text.size());
    size_t i = 0;

    while (i < text.size()) {
        size_t length = 0;
        const char32_t c = utf8::Decode(text, i, &length);

        if (c == utf8::kInvalid) {
            out.append(text.substr(i, length));
        } else {
            utf8::Append(out, utf8::ToLower(c));
        }

        i += length;
    }

    return out;
}

TermKind ClassifyToken(std::string_view token) {
    if (token.size() > 2 && token.rfind("--", 0) == 0 && AsciiAlpha(token[2])) {
        return TermKind::Flag;
    }

    if (token.find("://") != std::string_view::npos) {
        return TermKind::Word;
    }

    bool letter = false;
    bool digit = false;
    bool underscore = false;
    bool camel = false;
    bool lower_seen = false;
    bool upper_seen = false;
    size_t letters = 0;

    for (size_t k = 0; k < token.size(); ++k) {
        const char c = token[k];

        if (AsciiAlpha(c) || static_cast<unsigned char>(c) >= 0x80) {
            letter = true;
            ++letters;
        }

        digit = digit || AsciiDigit(c);
        underscore = underscore || c == '_';

        if (AsciiUpper(c)) {
            if (lower_seen) {
                camel = true;
            }

            upper_seen = true;
        }

        if (AsciiLower(c)) {
            if (k >= 2 && AsciiUpper(token[k - 1]) && AsciiUpper(token[k - 2])) {
                camel = true;
            }

            lower_seen = true;
        }
    }

    if (!letter) {
        return TermKind::Word;
    }

    const size_t dot = token.find('.');

    if (dot != std::string_view::npos && dot > 0 && dot + 1 < token.size()) {
        size_t longest = 0;
        size_t start = 0;

        for (size_t k = 0; k <= token.size(); ++k) {
            if (k == token.size() || token[k] == '.') {
                longest = std::max(longest, k - start);
                start = k + 1;
            }
        }

        if (longest >= 2 && letters >= 2) {
            return TermKind::Dotted;
        }
    }

    if (underscore) {
        return TermKind::Identifier;
    }

    if (camel) {
        return TermKind::Identifier;
    }

    if (upper_seen && !lower_seen && letters >= 2 && letters <= 8) {
        return TermKind::Acronym;
    }

    if (digit) {
        return TermKind::Identifier;
    }

    return TermKind::Word;
}

std::vector<std::vector<std::string>> SpokenVariants(std::string_view written, size_t max_variants) {
    std::vector<Alternatives> pieces;

    if (!Pieces(written, &pieces)) {
        return {};
    }

    std::vector<std::vector<std::string>> variants{{}};

    for (const Alternatives& piece : pieces) {
        std::vector<std::vector<std::string>> next;

        for (const auto& partial : variants) {
            for (const auto& alternative : piece) {
                if (next.size() >= max_variants) {
                    break;
                }

                std::vector<std::string> extended = partial;
                extended.insert(extended.end(), alternative.begin(), alternative.end());
                next.push_back(std::move(extended));
            }
        }

        variants = std::move(next);
    }

    std::sort(variants.begin(), variants.end());
    variants.erase(std::unique(variants.begin(), variants.end()), variants.end());
    return variants;
}

Lexicon::Lexicon(size_t max_terms) : max_terms_(max_terms) {}

bool Lexicon::Add(std::string_view term, double weight) {
    while (!term.empty() && (term.front() == ' ' || term.front() == '\t')) {
        term.remove_prefix(1);
    }

    while (!term.empty() && (term.back() == ' ' || term.back() == '\t' || term.back() == '\r' || term.back() == '\n')) {
        term.remove_suffix(1);
    }

    size_t bad = 0;

    if (frozen_ || term.empty() || term.size() > kMaxTermBytes || !utf8::Valid(term, &bad)) {
        return false;
    }

    const std::string lower = LowerUtf8(term);
    auto [it, inserted] = pending_.try_emplace(lower);

    if (inserted) {
        it->second.written = std::string(term);
        it->second.lower = lower;
        it->second.kind = ClassifyToken(term);
    }

    it->second.weight = std::max(it->second.weight, weight);
    ++it->second.count;
    return true;
}

void Lexicon::Harvest(std::string_view text) {
    if (frozen_) {
        return;
    }

    size_t i = 0;
    const size_t limit = std::max<size_t>(max_terms_ * 16, 256);

    while (i < text.size()) {
        while (i < text.size() &&
               (text[i] == ' ' || text[i] == '\t' || text[i] == '\r' || text[i] == '\n' || IsDelimiter(text[i]))) {
            ++i;
        }

        size_t j = i;

        while (j < text.size() && !(text[j] == ' ' || text[j] == '\t' || text[j] == '\r' || text[j] == '\n' ||
                                    IsDelimiter(text[j]))) {
            ++j;
        }

        const std::string_view token = CleanToken(text.substr(i, j - i));
        i = j;

        size_t bad = 0;

        if (token.size() < 2 || token.size() > kMaxHarvestBytes || !utf8::Valid(token, &bad)) {
            continue;
        }

        const TermKind kind = ClassifyToken(token);

        if (kind == TermKind::Word) {
            continue;
        }

        const std::string lower = LowerUtf8(token);
        auto found = pending_.find(lower);

        if (found == pending_.end()) {
            if (pending_.size() >= limit) {
                continue;
            }

            LexiconTerm term;
            term.written = std::string(token);
            term.lower = lower;
            term.kind = kind;
            found = pending_.emplace(lower, std::move(term)).first;
        }

        ++found->second.count;
    }
}

void Lexicon::Freeze() {
    if (frozen_) {
        return;
    }

    terms_.clear();
    terms_.reserve(pending_.size());

    for (auto& [lower, term] : pending_) {
        terms_.push_back(std::move(term));
    }

    pending_.clear();

    std::sort(terms_.begin(), terms_.end(), [](const LexiconTerm& a, const LexiconTerm& b) {
        if (a.weight != b.weight) {
            return a.weight > b.weight;
        }

        if (a.count != b.count) {
            return a.count > b.count;
        }

        return a.lower < b.lower;
    });

    if (terms_.size() > max_terms_) {
        terms_.resize(max_terms_);
    }

    nodes_.assign(1, Node{});
    uint64_t hash = 1469598103934665603ull;

    for (size_t k = 0; k < terms_.size(); ++k) {
        by_lower_.emplace(terms_[k].lower, k);
        hash = Fnv(hash, terms_[k].written);
        hash = Fnv(hash, "\n");

        for (const auto& variant : SpokenVariants(terms_[k].written, kMaxVariants)) {
            Insert(variant, static_cast<int32_t>(k));
        }
    }

    version_ = Fnv(hash, std::to_string(max_terms_));
    frozen_ = true;
}

std::vector<std::string> Lexicon::TopTerms(size_t n) const {
    std::vector<std::string> top;

    for (size_t k = 0; k < terms_.size() && top.size() < n; ++k) {
        top.push_back(terms_[k].written);
    }

    return top;
}

const LexiconTerm* Lexicon::CasingFor(std::string_view lower) const {
    const auto found = by_lower_.find(std::string(lower));

    if (found == by_lower_.end()) {
        return nullptr;
    }

    const LexiconTerm& term = terms_[found->second];
    return term.kind == TermKind::Word ? nullptr : &term;
}

void Lexicon::Insert(const std::vector<std::string>& words, int32_t term) {
    if (words.empty()) {
        return;
    }

    int32_t node = 0;

    for (const auto& word : words) {
        int32_t next = -1;

        for (const auto& [edge, child] : nodes_[static_cast<size_t>(node)].children) {
            if (edge == word) {
                next = child;
                break;
            }
        }

        if (next < 0) {
            next = static_cast<int32_t>(nodes_.size());
            nodes_[static_cast<size_t>(node)].children.emplace_back(word, next);
            nodes_.push_back(Node{});
        }

        node = next;
    }

    if (nodes_[static_cast<size_t>(node)].term < 0) {
        nodes_[static_cast<size_t>(node)].term = term;
    }
}

void Lexicon::Walk(int32_t node, const std::vector<std::string>& words, size_t at, size_t depth, bool fuzzy_used,
                   size_t* best_len, int32_t* best_term, bool* best_fuzzy) const {
    const Node& current = nodes_[static_cast<size_t>(node)];

    if (current.term >= 0 && depth > 0) {
        const bool better = depth > *best_len || (depth == *best_len && *best_fuzzy && !fuzzy_used);

        if (better) {
            *best_len = depth;
            *best_term = current.term;
            *best_fuzzy = fuzzy_used;
        }
    }

    if (at >= words.size()) {
        return;
    }

    const std::string& word = words[at];

    for (const auto& [edge, child] : current.children) {
        if (edge == word) {
            Walk(child, words, at + 1, depth + 1, fuzzy_used, best_len, best_term, best_fuzzy);
        } else if (!fuzzy_used && edge.size() >= 5 && word.size() >= 5 && WithinOneEdit(edge, word)) {
            Walk(child, words, at + 1, depth + 1, true, best_len, best_term, best_fuzzy);
        }
    }
}

size_t Lexicon::MatchSpoken(const std::vector<std::string>& words, size_t start, const LexiconTerm** term,
                            bool* fuzzy) const {
    *term = nullptr;
    *fuzzy = false;

    if (!frozen_ || nodes_.empty() || start >= words.size()) {
        return 0;
    }

    size_t best_len = 0;
    int32_t best_term = -1;
    bool best_fuzzy = false;
    Walk(0, words, start, 0, false, &best_len, &best_term, &best_fuzzy);

    if (best_term < 0) {
        return 0;
    }

    *term = &terms_[static_cast<size_t>(best_term)];
    *fuzzy = best_fuzzy;
    return best_len;
}

std::vector<InputWord> WordsOf(std::string_view text) {
    std::vector<InputWord> words;
    size_t i = 0;

    while (i < text.size()) {
        while (i < text.size() && (text[i] == ' ' || text[i] == '\t' || text[i] == '\r' || text[i] == '\n')) {
            ++i;
        }

        size_t j = i;

        while (j < text.size() && !(text[j] == ' ' || text[j] == '\t' || text[j] == '\r' || text[j] == '\n')) {
            ++j;
        }

        if (j > i) {
            words.push_back({std::string(text.substr(i, j - i)), 1.0f});
        }

        i = j;
    }

    return words;
}

namespace {

struct Split {
    std::string lead;
    std::string core;
    std::string trail;
    std::string lower;
};

Split SplitWord(const std::string& word) {
    Split s;
    size_t begin = 0;
    size_t end = word.size();

    while (begin < end && std::string_view("(\"'[{").find(word[begin]) != std::string_view::npos) {
        ++begin;
    }

    while (end > begin && std::string_view(".,!?;:\")]}'").find(word[end - 1]) != std::string_view::npos) {
        --end;
    }

    s.lead = word.substr(0, begin);
    s.core = word.substr(begin, end - begin);
    s.trail = word.substr(end);
    s.lower = LowerUtf8(s.core);
    return s;
}

struct Symbol {
    std::vector<std::string> spoken;
    const char* written;
    bool attach_left;
    bool attach_right;
};

const std::vector<Symbol>& Symbols() {
    static const std::vector<Symbol> symbols = {
        {{"open", "parenthesis"}, "(", false, true},
        {{"close", "parenthesis"}, ")", true, false},
        {{"open", "paren"}, "(", false, true},
        {{"close", "paren"}, ")", true, false},
        {{"open", "bracket"}, "[", false, true},
        {{"close", "bracket"}, "]", true, false},
        {{"open", "brace"}, "{", false, true},
        {{"close", "brace"}, "}", true, false},
        {{"open", "angle"}, "<", false, true},
        {{"close", "angle"}, ">", true, false},
        {{"double", "equals"}, "==", false, false},
        {{"double", "quote"}, "\"", false, false},
        {{"single", "quote"}, "'", false, false},
        {{"plus", "plus"}, "++", true, false},
        {{"fat", "arrow"}, "=>", false, false},
        {{"less", "than"}, "<", false, false},
        {{"greater", "than"}, ">", false, false},
        {{"at", "sign"}, "@", false, true},
        {{"new", "line"}, "\n", true, true},
        {{"dot"}, ".", true, true},
        {{"dash"}, "-", true, true},
        {{"hyphen"}, "-", true, true},
        {{"underscore"}, "_", true, true},
        {{"slash"}, "/", true, true},
        {{"backslash"}, "\\", true, true},
        {{"comma"}, ",", true, false},
        {{"colon"}, ":", true, false},
        {{"semicolon"}, ";", true, false},
        {{"equals"}, "=", false, false},
        {{"plus"}, "+", false, false},
        {{"minus"}, "-", false, false},
        {{"star"}, "*", false, false},
        {{"asterisk"}, "*", false, false},
        {{"hash"}, "#", false, true},
        {{"quote"}, "\"", false, false},
        {{"backtick"}, "`", false, false},
        {{"pipe"}, "|", false, false},
        {{"ampersand"}, "&", false, false},
        {{"arrow"}, "->", false, false},
    };
    return symbols;
}

enum class CaseStyle { Camel, Pascal, Snake, Kebab, Constant };

bool CaseCommand(const std::vector<Split>& w, size_t i, CaseStyle* style) {
    if (i + 1 >= w.size() || w[i + 1].lower != "case") {
        return false;
    }

    const std::string& first = w[i].lower;

    if (first == "camel") {
        *style = CaseStyle::Camel;
    } else if (first == "pascal") {
        *style = CaseStyle::Pascal;
    } else if (first == "snake") {
        *style = CaseStyle::Snake;
    } else if (first == "kebab") {
        *style = CaseStyle::Kebab;
    } else if (first == "constant") {
        *style = CaseStyle::Constant;
    } else {
        return false;
    }

    return true;
}

bool PlainAlpha(const std::string& lower) {
    if (lower.empty()) {
        return false;
    }

    size_t i = 0;

    while (i < lower.size()) {
        size_t length = 0;
        const char32_t c = utf8::Decode(lower, i, &length);

        if (!(utf8::IsLower(c) || utf8::IsUpper(c) || AsciiDigit(static_cast<char>(c)))) {
            return false;
        }

        i += length;
    }

    return true;
}

size_t MatchSymbol(const std::vector<Split>& w, size_t i, const Symbol** found) {
    for (const Symbol& symbol : Symbols()) {
        if (i + symbol.spoken.size() > w.size()) {
            continue;
        }

        bool match = true;

        for (size_t k = 0; k < symbol.spoken.size(); ++k) {
            if (w[i + k].lower != symbol.spoken[k]) {
                match = false;
                break;
            }
        }

        if (match) {
            *found = &symbol;
            return symbol.spoken.size();
        }
    }

    return 0;
}

bool StartsCommand(const std::vector<Split>& w, size_t i) {
    CaseStyle style{};
    const Symbol* symbol = nullptr;
    return CaseCommand(w, i, &style) || MatchSymbol(w, i, &symbol) > 0;
}

std::string Capitalized(const std::string& lower) {
    if (lower.empty()) {
        return lower;
    }

    std::string out = lower;
    out[0] = AsciiToUpper(out[0]);
    return out;
}

std::string Styled(const std::vector<std::string>& words, CaseStyle style) {
    std::string out;

    for (size_t k = 0; k < words.size(); ++k) {
        const std::string& word = words[k];

        switch (style) {
            case CaseStyle::Camel:
                out += k == 0 ? word : Capitalized(word);
                break;
            case CaseStyle::Pascal:
                out += Capitalized(word);
                break;
            case CaseStyle::Snake:
                out += (k == 0 ? "" : "_") + word;
                break;
            case CaseStyle::Kebab:
                out += (k == 0 ? "" : "-") + word;
                break;
            case CaseStyle::Constant: {
                std::string upper = word;

                for (char& c : upper) {
                    c = AsciiToUpper(c);
                }

                out += (k == 0 ? "" : "_") + upper;
                break;
            }
        }
    }

    return out;
}

int NumberWord(const std::string& lower) {
    for (size_t k = 0; k < kOnes.size(); ++k) {
        if (lower == kOnes[k]) {
            return static_cast<int>(k);
        }
    }

    for (size_t k = 2; k < kTens.size(); ++k) {
        if (lower == kTens[k]) {
            return static_cast<int>(k * 10);
        }
    }

    return -1;
}

struct Token {
    std::string text;
    bool attach_left = false;
    bool attach_right = false;
    size_t first = 0;
    size_t last = 0;
    bool substituted = false;
    bool applied = true;
    SubstitutionKind kind = SubstitutionKind::Casing;
    float confidence = 1.0f;
    std::string replacement;
};

float MinConfidence(const std::vector<InputWord>& words, size_t first, size_t count) {
    float lowest = 1.0f;

    for (size_t k = first; k < first + count; ++k) {
        lowest = std::min(lowest, words[k].confidence);
    }

    return lowest;
}

}

Dictation Dictate(const std::vector<InputWord>& words, DictationMode mode, const Lexicon* lexicon,
                  float min_confidence) {
    Dictation result;
    std::vector<size_t> raw_offsets;
    raw_offsets.reserve(words.size() + 1);

    for (size_t k = 0; k < words.size(); ++k) {
        if (k > 0) {
            result.raw.push_back(' ');
        }

        raw_offsets.push_back(result.raw.size());
        result.raw += words[k].text;
    }

    raw_offsets.push_back(result.raw.size());

    std::vector<Split> w;
    std::vector<std::string> lowers;
    w.reserve(words.size());

    for (const auto& word : words) {
        w.push_back(SplitWord(word.text));
        lowers.push_back(w.back().lower);
    }

    const bool usable_lexicon = lexicon != nullptr && lexicon->Frozen();
    std::vector<Token> tokens;

    auto plain = [&](size_t k) {
        Token t;
        t.text = words[k].text;
        t.first = k;
        t.last = k;
        tokens.push_back(std::move(t));
    };

    auto emit = [&](size_t first, size_t count, std::string produced, SubstitutionKind kind, float penalty,
                    bool attach_left, bool attach_right) {
        const float confidence = MinConfidence(words, first, count) * penalty;

        if (confidence < min_confidence) {
            for (size_t k = first; k < first + count; ++k) {
                plain(k);
            }

            Token& marker = tokens[tokens.size() - count];
            marker.substituted = true;
            marker.applied = false;
            marker.kind = kind;
            marker.confidence = confidence;
            marker.replacement = produced;
            marker.last = first + count - 1;
            return;
        }

        Token t;
        t.text = w[first].lead + produced + w[first + count - 1].trail;
        t.attach_left = attach_left && w[first].lead.empty();
        t.attach_right = attach_right && w[first + count - 1].trail.empty();
        t.first = first;
        t.last = first + count - 1;
        t.substituted = true;
        t.kind = kind;
        t.confidence = confidence;
        t.replacement = produced;
        tokens.push_back(std::move(t));
    };

    size_t i = 0;

    while (i < words.size()) {
        if (mode == DictationMode::Prose) {
            const LexiconTerm* term = usable_lexicon ? lexicon->CasingFor(w[i].lower) : nullptr;

            if (term != nullptr && term->written != w[i].core) {
                emit(i, 1, term->written, SubstitutionKind::Casing, 1.0f, false, false);
            } else {
                plain(i);
            }

            ++i;
            continue;
        }

        CaseStyle style{};

        if (CaseCommand(w, i, &style)) {
            std::vector<std::string> parts;
            size_t j = i + 2;

            while (j < w.size() && parts.size() < kMaxCaseWords && PlainAlpha(w[j].lower) && !StartsCommand(w, j)) {
                parts.push_back(w[j].lower);
                const bool ends = !w[j].trail.empty();
                ++j;

                if (ends) {
                    break;
                }
            }

            if (!parts.empty()) {
                Token t;
                const float confidence = MinConfidence(words, i, j - i);

                if (confidence < min_confidence) {
                    const size_t before = tokens.size();

                    for (size_t k = i; k < j; ++k) {
                        plain(k);
                    }

                    tokens[before].substituted = true;
                    tokens[before].applied = false;
                    tokens[before].kind = SubstitutionKind::Grammar;
                    tokens[before].confidence = confidence;
                    tokens[before].replacement = Styled(parts, style);
                    tokens[before].last = j - 1;
                } else {
                    t.text = Styled(parts, style) + w[j - 1].trail;
                    t.first = i;
                    t.last = j - 1;
                    t.substituted = true;
                    t.kind = SubstitutionKind::Grammar;
                    t.confidence = confidence;
                    t.replacement = Styled(parts, style);
                    tokens.push_back(std::move(t));
                }

                i = j;
                continue;
            }
        }

        if (usable_lexicon) {
            const LexiconTerm* term = nullptr;
            bool fuzzy = false;
            const size_t matched = lexicon->MatchSpoken(lowers, i, &term, &fuzzy);

            if (matched > 0 && !(matched == 1 && term->kind == TermKind::Word)) {
                emit(i, matched, term->written, SubstitutionKind::Lexicon, fuzzy ? kFuzzyPenalty : 1.0f, false,
                     false);
                i += matched;
                continue;
            }
        }

        if (i + 2 < w.size() && w[i].lower == "dash" && w[i + 1].lower == "dash" && w[i + 1].trail.empty() &&
            PlainAlpha(w[i + 2].lower)) {
            std::string flag = "--" + w[i + 2].lower;
            size_t j = i + 3;

            while (j + 1 < w.size() && (w[j].lower == "dash" || w[j].lower == "hyphen") && w[j - 1].trail.empty() &&
                   PlainAlpha(w[j + 1].lower)) {
                flag += "-" + w[j + 1].lower;
                j += 2;
            }

            emit(i, j - i, flag, SubstitutionKind::Grammar, 1.0f, false, false);
            i = j;
            continue;
        }

        if (i + 1 < w.size() && utf8::CountCodepoints(w[i].lower) == 1 && AsciiAlpha(w[i].lower[0]) &&
            utf8::CountCodepoints(w[i + 1].lower) == 1 && AsciiAlpha(w[i + 1].lower[0])) {
            size_t j = i;
            std::string joined;

            while (j < w.size() && utf8::CountCodepoints(w[j].lower) == 1 && AsciiAlpha(w[j].lower[0])) {
                joined.push_back(AsciiToUpper(w[j].lower[0]));
                const bool ends = !w[j].trail.empty();
                ++j;

                if (ends) {
                    break;
                }
            }

            const LexiconTerm* term = usable_lexicon ? lexicon->CasingFor(LowerUtf8(joined)) : nullptr;
            emit(i, j - i, term != nullptr ? term->written : joined, SubstitutionKind::Letters, 1.0f, false, false);
            i = j;
            continue;
        }

        const Symbol* symbol = nullptr;
        const size_t symbol_words = MatchSymbol(w, i, &symbol);

        if (symbol_words > 0) {
            emit(i, symbol_words, symbol->written, SubstitutionKind::Grammar, 1.0f, symbol->attach_left,
                 symbol->attach_right);
            i += symbol_words;
            continue;
        }

        const int number = NumberWord(w[i].lower);

        if (number >= 0) {
            size_t count = 1;
            int value = number;

            if (number >= 20 && i + 1 < w.size() && w[i].trail.empty()) {
                const int ones = NumberWord(w[i + 1].lower);

                if (ones >= 1 && ones <= 9) {
                    value += ones;
                    count = 2;
                }
            }

            emit(i, count, std::to_string(value), SubstitutionKind::Grammar, 1.0f, false, false);
            i += count;
            continue;
        }

        plain(i);
        ++i;
    }

    for (size_t k = 0; k < tokens.size(); ++k) {
        Token& t = tokens[k];
        const bool joined = k > 0 && (t.attach_left || tokens[k - 1].attach_right);

        if (k > 0 && !joined) {
            result.text.push_back(' ');
        }

        const size_t start = result.text.size();
        result.text += t.text;

        if (!t.substituted) {
            continue;
        }

        Substitution s;
        s.kind = t.kind;
        s.applied = t.applied;
        s.confidence = t.confidence;
        s.raw_offset = raw_offsets[t.first];
        s.raw_length = raw_offsets[t.last] + words[t.last].text.size() - raw_offsets[t.first];
        s.text_offset = start;
        s.text_length = t.applied ? t.text.size() : s.raw_length;
        s.replacement = t.replacement;
        result.substitutions.push_back(std::move(s));
    }

    return result;
}

}
