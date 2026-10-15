#include "score.h"

#include <algorithm>
#include <unordered_map>

namespace bspeech {

namespace {

std::vector<std::string> StrippedWords(std::string_view text) {
    std::vector<std::string> words;

    for (const InputWord& word : WordsOf(text)) {
        std::string_view core = word.text;

        while (!core.empty() && std::string_view("\"'(").find(core.front()) != std::string_view::npos) {
            core.remove_prefix(1);
        }

        while (!core.empty() && std::string_view(".,!?;:\"')").find(core.back()) != std::string_view::npos) {
            core.remove_suffix(1);
        }

        if (!core.empty()) {
            words.emplace_back(core);
        }
    }

    return words;
}

}

std::vector<std::string> ScoringWords(std::string_view text) {
    std::vector<std::string> words = StrippedWords(text);

    for (auto& word : words) {
        word = LowerUtf8(word);
    }

    return words;
}

TranscriptScore ScoreTranscript(std::string_view reference, std::string_view hypothesis, const Lexicon* lexicon) {
    const auto ref = ScoringWords(reference);
    const auto hyp = ScoringWords(hypothesis);

    std::vector<size_t> previous(hyp.size() + 1);
    std::vector<size_t> current(hyp.size() + 1);

    for (size_t j = 0; j <= hyp.size(); ++j) {
        previous[j] = j;
    }

    for (size_t i = 1; i <= ref.size(); ++i) {
        current[0] = i;

        for (size_t j = 1; j <= hyp.size(); ++j) {
            const size_t substitute = previous[j - 1] + (ref[i - 1] == hyp[j - 1] ? 0 : 1);
            current[j] = std::min({previous[j] + 1, current[j - 1] + 1, substitute});
        }

        std::swap(previous, current);
    }

    TranscriptScore score;
    score.reference_words = ref.size();
    score.word_errors = previous[hyp.size()];
    score.word_error_rate =
        ref.empty() ? (hyp.empty() ? 0.0 : 1.0) : static_cast<double>(score.word_errors) / static_cast<double>(ref.size());

    if (lexicon == nullptr || !lexicon->Frozen()) {
        return score;
    }

    std::unordered_map<std::string, size_t> wanted;
    std::unordered_map<std::string, size_t> found;

    for (const auto& word : StrippedWords(reference)) {
        if (lexicon->CasingFor(LowerUtf8(word)) != nullptr) {
            ++wanted[word];
        }
    }

    for (const auto& word : StrippedWords(hypothesis)) {
        if (wanted.count(word) != 0) {
            ++found[word];
        }
    }

    for (const auto& [term, count] : wanted) {
        score.term_total += count;
        score.term_errors += count - std::min(count, found[term]);
    }

    score.term_error_rate = score.term_total == 0 ? 0.0
                                                  : static_cast<double>(score.term_errors) /
                                                        static_cast<double>(score.term_total);
    return score;
}

}
