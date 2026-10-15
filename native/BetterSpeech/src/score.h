#pragma once

#include <cstddef>
#include <string>
#include <string_view>
#include <vector>

#include "lexicon.h"

namespace bspeech {

struct TranscriptScore {
    size_t reference_words = 0;
    size_t word_errors = 0;
    double word_error_rate = 0.0;
    size_t term_total = 0;
    size_t term_errors = 0;
    double term_error_rate = 0.0;
};

[[nodiscard]] std::vector<std::string> ScoringWords(std::string_view text);

[[nodiscard]] TranscriptScore ScoreTranscript(std::string_view reference, std::string_view hypothesis,
                                              const Lexicon* lexicon);

}
