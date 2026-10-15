#pragma once

#include <cstddef>
#include <cstdint>
#include <memory>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

namespace bspeech {

enum class TermKind : int32_t { Word = 0, Identifier = 1, Flag = 2, Acronym = 3, Dotted = 4 };

struct LexiconTerm {
    std::string written;
    std::string lower;
    TermKind kind = TermKind::Word;
    double weight = 0.0;
    uint32_t count = 0;
};

[[nodiscard]] TermKind ClassifyToken(std::string_view token);

[[nodiscard]] std::vector<std::vector<std::string>> SpokenVariants(std::string_view written, size_t max_variants);

[[nodiscard]] std::string LowerUtf8(std::string_view text);

class Lexicon {
public:
    explicit Lexicon(size_t max_terms);

    bool Add(std::string_view term, double weight);
    void Harvest(std::string_view text);
    void Freeze();

    [[nodiscard]] bool Frozen() const noexcept { return frozen_; }
    [[nodiscard]] const std::vector<LexiconTerm>& Terms() const noexcept { return terms_; }
    [[nodiscard]] std::vector<std::string> TopTerms(size_t n) const;
    [[nodiscard]] uint64_t Version() const noexcept { return version_; }

    [[nodiscard]] const LexiconTerm* CasingFor(std::string_view lower) const;
    [[nodiscard]] size_t MatchSpoken(const std::vector<std::string>& words, size_t start, const LexiconTerm** term,
                                     bool* fuzzy) const;

private:
    struct Node {
        std::vector<std::pair<std::string, int32_t>> children;
        int32_t term = -1;
    };

    void Insert(const std::vector<std::string>& words, int32_t term);
    void Walk(int32_t node, const std::vector<std::string>& words, size_t at, size_t depth, bool fuzzy_used,
              size_t* best_len, int32_t* best_term, bool* best_fuzzy) const;

    size_t max_terms_;
    bool frozen_ = false;
    std::unordered_map<std::string, LexiconTerm> pending_;
    std::vector<LexiconTerm> terms_;
    std::unordered_map<std::string, size_t> by_lower_;
    std::vector<Node> nodes_;
    uint64_t version_ = 0;
};

enum class DictationMode : int32_t { Prose = 0, Code = 1 };

enum class SubstitutionKind : int32_t { Casing = 0, Lexicon = 1, Grammar = 2, Letters = 3 };

struct InputWord {
    std::string text;
    float confidence = 1.0f;
};

struct Substitution {
    SubstitutionKind kind = SubstitutionKind::Casing;
    bool applied = true;
    float confidence = 1.0f;
    size_t raw_offset = 0;
    size_t raw_length = 0;
    size_t text_offset = 0;
    size_t text_length = 0;
    std::string replacement;
};

struct Dictation {
    std::string raw;
    std::string text;
    std::vector<Substitution> substitutions;
};

[[nodiscard]] std::vector<InputWord> WordsOf(std::string_view text);

[[nodiscard]] Dictation Dictate(const std::vector<InputWord>& words, DictationMode mode, const Lexicon* lexicon,
                                float min_confidence);

}
