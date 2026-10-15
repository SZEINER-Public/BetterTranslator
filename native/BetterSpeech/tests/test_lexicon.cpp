#include <cstring>
#include <string>
#include <vector>

#include "better_speech_stt.h"
#include "harness.h"
#include "lexicon.h"

namespace {

const char* const kSource =
    "BetterTranslator runs GGUF models through llama.cpp on .NET 10. Locale strings live in i18n.json, and "
    "loadModelAsync reads max_new_tokens from the config. Fix the last commit with git commit --amend.";

bs_lexicon* MakeLexicon() {
    bs_lexicon* lexicon = nullptr;
    CHECK_EQ(bs_lexicon_create(256, &lexicon), BS_OK);
    CHECK_EQ(bs_lexicon_harvest(lexicon, kSource, std::char_traits<char>::length(kSource)), BS_OK);
    CHECK_EQ(bs_lexicon_freeze(lexicon), BS_OK);
    return lexicon;
}

std::string Dictated(const std::string& spoken, int32_t mode, const bs_lexicon* lexicon) {
    bs_dictation* dictation = nullptr;
    CHECK_EQ(bs_dictate(spoken.data(), spoken.size(), mode, lexicon, 0.6f, &dictation), BS_OK);
    const char* text = nullptr;
    size_t len = 0;
    CHECK_EQ(bs_dictation_text(dictation, &text, &len), BS_OK);
    std::string out(text, len);
    bs_dictation_free(dictation);
    return out;
}

struct Pair {
    const char* spoken;
    const char* written;
};

const Pair kPairs[] = {
    {"dash dash amend", "--amend"},
    {"dot net", ".NET"},
    {"llama dot cpp", "llama.cpp"},
    {"i eighteen n dot json", "i18n.json"},
    {"camel case load model async", "loadModelAsync"},
    {"snake case max new tokens", "max_new_tokens"},
    {"open paren close paren", "()"},
    {"G G U F", "GGUF"},
};

void Harvest(bs_lexicon* lexicon, const char* text) {
    CHECK_EQ(bs_lexicon_harvest(lexicon, text, std::strlen(text)), BS_OK);
}

std::vector<bspeech::InputWord> Spoken(const std::string& text, float confidence) {
    auto words = bspeech::WordsOf(text);

    for (auto& word : words) {
        word.confidence = confidence;
    }

    return words;
}

}

TEST(lexicon_every_spoken_pair_in_code_mode) {
    bs_lexicon* lexicon = MakeLexicon();

    for (const Pair& pair : kPairs) {
        CHECK_EQ(Dictated(pair.spoken, BS_DICTATION_CODE, lexicon), std::string(pair.written));
    }

    bs_lexicon_free(lexicon);
}

TEST(lexicon_no_spoken_pair_changes_in_prose_mode) {
    bs_lexicon* lexicon = MakeLexicon();

    for (const Pair& pair : kPairs) {
        CHECK_EQ(Dictated(pair.spoken, BS_DICTATION_PROSE, lexicon), std::string(pair.spoken));
    }

    bs_lexicon_free(lexicon);
}

TEST(lexicon_prose_mode_only_fixes_casing) {
    bs_lexicon* lexicon = MakeLexicon();
    CHECK_EQ(Dictated("we convert gguf models on .net, then ship.", BS_DICTATION_PROSE, lexicon),
             std::string("we convert GGUF models on .NET, then ship."));
    CHECK_EQ(Dictated("the dash dash amend flag", BS_DICTATION_PROSE, lexicon),
             std::string("the dash dash amend flag"));
    CHECK_EQ(Dictated("plain words stay plain", BS_DICTATION_PROSE, lexicon), std::string("plain words stay plain"));
    bs_lexicon_free(lexicon);
}

TEST(lexicon_grammar_pairs_need_no_lexicon) {
    CHECK_EQ(Dictated("dash dash amend", BS_DICTATION_CODE, nullptr), std::string("--amend"));
    CHECK_EQ(Dictated("llama dot cpp", BS_DICTATION_CODE, nullptr), std::string("llama.cpp"));
    CHECK_EQ(Dictated("camel case load model async", BS_DICTATION_CODE, nullptr), std::string("loadModelAsync"));
    CHECK_EQ(Dictated("snake case max new tokens", BS_DICTATION_CODE, nullptr), std::string("max_new_tokens"));
    CHECK_EQ(Dictated("open paren close paren", BS_DICTATION_CODE, nullptr), std::string("()"));
    CHECK_EQ(Dictated("G G U F", BS_DICTATION_CODE, nullptr), std::string("GGUF"));
    CHECK_EQ(Dictated("pascal case speech engine", BS_DICTATION_CODE, nullptr), std::string("SpeechEngine"));
    CHECK_EQ(Dictated("kebab case dry run", BS_DICTATION_CODE, nullptr), std::string("dry-run"));
    CHECK_EQ(Dictated("constant case max tokens", BS_DICTATION_CODE, nullptr), std::string("MAX_TOKENS"));
    CHECK_EQ(Dictated("dash dash no dash verify", BS_DICTATION_CODE, nullptr), std::string("--no-verify"));
    CHECK_EQ(Dictated("x equals forty two", BS_DICTATION_CODE, nullptr), std::string("x = 42"));
    CHECK_EQ(Dictated("camel case load model async, now", BS_DICTATION_CODE, nullptr),
             std::string("loadModelAsync, now"));
    CHECK_EQ(Dictated("call foo open paren bar close paren", BS_DICTATION_CODE, nullptr),
             std::string("call foo (bar)"));
}

TEST(lexicon_code_mode_matches_terms_without_commands) {
    bs_lexicon* lexicon = MakeLexicon();
    CHECK_EQ(Dictated("call load model async now", BS_DICTATION_CODE, lexicon), std::string("call loadModelAsync now"));
    CHECK_EQ(Dictated("open better translator", BS_DICTATION_CODE, lexicon), std::string("open BetterTranslator"));
    CHECK_EQ(Dictated("llama dot c p p", BS_DICTATION_CODE, lexicon), std::string("llama.cpp"));
    bs_lexicon_free(lexicon);
}

TEST(lexicon_substitutions_keep_raw_spans) {
    bs_dictation* dictation = nullptr;
    const std::string spoken = "run camel case load model async";
    CHECK_EQ(bs_dictate(spoken.data(), spoken.size(), BS_DICTATION_CODE, nullptr, 0.6f, &dictation), BS_OK);

    const char* text = nullptr;
    size_t len = 0;
    CHECK_EQ(bs_dictation_text(dictation, &text, &len), BS_OK);
    CHECK_EQ(std::string(text, len), std::string("run loadModelAsync"));
    CHECK_EQ(bs_dictation_raw(dictation, &text, &len), BS_OK);
    CHECK_EQ(std::string(text, len), spoken);
    CHECK_EQ(bs_dictation_substitution_count(dictation), 1);

    bs_substitution s{};
    s.struct_size = sizeof s;
    CHECK_EQ(bs_dictation_substitution(dictation, 0, &s), BS_OK);
    CHECK_EQ(s.kind, static_cast<int32_t>(BS_SUBSTITUTION_GRAMMAR));
    CHECK_EQ(s.applied, 1);
    CHECK_EQ(s.raw_offset, size_t{4});
    CHECK_EQ(s.raw_length, std::string("camel case load model async").size());
    CHECK_EQ(s.text_offset, size_t{4});
    CHECK_EQ(s.text_length, std::string("loadModelAsync").size());
    CHECK_EQ(std::string(s.replacement, s.replacement_len), std::string("loadModelAsync"));
    CHECK_EQ(bs_dictation_substitution(dictation, 1, &s), BS_INVALID_ARG);

    bs_substitution small{};
    small.struct_size = 8;
    CHECK_EQ(bs_dictation_substitution(dictation, 0, &small), BS_INVALID_ARG);
    bs_dictation_free(dictation);
}

TEST(lexicon_low_confidence_keeps_the_words_and_offers_the_alternative) {
    auto words = Spoken("run camel case load model async", 0.95f);
    words[3].confidence = 0.3f;
    const bspeech::Dictation d = bspeech::Dictate(words, bspeech::DictationMode::Code, nullptr, 0.6f);
    CHECK_EQ(d.text, d.raw);
    CHECK_EQ(d.substitutions.size(), size_t{1});
    CHECK(!d.substitutions[0].applied);
    CHECK_NEAR(d.substitutions[0].confidence, 0.3, 1e-6);
    CHECK_EQ(d.substitutions[0].replacement, std::string("loadModelAsync"));
    CHECK_EQ(d.substitutions[0].text_offset, size_t{4});
    CHECK_EQ(d.substitutions[0].text_length, d.substitutions[0].raw_length);
    CHECK_EQ(d.text.substr(d.substitutions[0].text_offset, d.substitutions[0].text_length),
             std::string("camel case load model async"));

    auto sure = Spoken("run camel case load model async", 0.95f);
    CHECK_EQ(bspeech::Dictate(sure, bspeech::DictationMode::Code, nullptr, 0.6f).text,
             std::string("run loadModelAsync"));
}

TEST(lexicon_offsets_stay_correct_after_earlier_substitutions) {
    bs_lexicon* lexicon = MakeLexicon();
    bs_dictation* dictation = nullptr;
    const std::string spoken = "use G G U F and dash dash amend here";
    CHECK_EQ(bs_dictate(spoken.data(), spoken.size(), BS_DICTATION_CODE, lexicon, 0.6f, &dictation), BS_OK);
    const char* text = nullptr;
    size_t len = 0;
    CHECK_EQ(bs_dictation_text(dictation, &text, &len), BS_OK);
    const std::string out(text, len);
    CHECK_EQ(out, std::string("use GGUF and --amend here"));
    CHECK_EQ(bs_dictation_substitution_count(dictation), 2);

    for (int32_t k = 0; k < 2; ++k) {
        bs_substitution s{};
        s.struct_size = sizeof s;
        CHECK_EQ(bs_dictation_substitution(dictation, k, &s), BS_OK);
        CHECK_EQ(out.substr(s.text_offset, s.text_length), std::string(s.replacement, s.replacement_len));
        CHECK(spoken.substr(s.raw_offset, s.raw_length).find(' ') != std::string::npos);
    }

    bs_dictation_free(dictation);
    bs_lexicon_free(lexicon);
}

TEST(lexicon_fuzzy_match_costs_confidence) {
    bspeech::Lexicon lexicon(16);
    CHECK(lexicon.Add("tokenizer.json", 1.0));
    lexicon.Freeze();

    const auto fuzzy = Spoken("load tokenizr dot json", 1.0f);
    const bspeech::Dictation applied = bspeech::Dictate(fuzzy, bspeech::DictationMode::Code, &lexicon, 0.6f);
    CHECK_EQ(applied.text, std::string("load tokenizer.json"));
    CHECK_EQ(applied.substitutions.size(), size_t{1});
    CHECK_NEAR(applied.substitutions[0].confidence, 0.7, 1e-6);

    const bspeech::Dictation held = bspeech::Dictate(fuzzy, bspeech::DictationMode::Code, &lexicon, 0.8f);
    CHECK(held.substitutions.size() >= 1);
    CHECK(!held.substitutions[0].applied);
    CHECK_EQ(held.substitutions[0].replacement, std::string("tokenizer.json"));

    const auto short_word = Spoken("lama dot cpp", 1.0f);
    bspeech::Lexicon llama(16);
    CHECK(llama.Add("llama.cpp", 1.0));
    llama.Freeze();
    CHECK_EQ(bspeech::Dictate(short_word, bspeech::DictationMode::Code, &llama, 0.6f).text, std::string("lama.cpp"));
}

TEST(lexicon_harvest_classifies_terms) {
    CHECK(bspeech::ClassifyToken("--amend") == bspeech::TermKind::Flag);
    CHECK(bspeech::ClassifyToken("GGUF") == bspeech::TermKind::Acronym);
    CHECK(bspeech::ClassifyToken("llama.cpp") == bspeech::TermKind::Dotted);
    CHECK(bspeech::ClassifyToken("i18n.json") == bspeech::TermKind::Dotted);
    CHECK(bspeech::ClassifyToken("loadModelAsync") == bspeech::TermKind::Identifier);
    CHECK(bspeech::ClassifyToken("max_new_tokens") == bspeech::TermKind::Identifier);
    CHECK(bspeech::ClassifyToken("XMLHttpRequest") == bspeech::TermKind::Identifier);
    CHECK(bspeech::ClassifyToken("v2") == bspeech::TermKind::Identifier);
    CHECK(bspeech::ClassifyToken("model") == bspeech::TermKind::Word);
    CHECK(bspeech::ClassifyToken("https://example.com") == bspeech::TermKind::Word);

    bs_lexicon* lexicon = MakeLexicon();
    std::vector<std::string> terms;
    std::vector<char> buf(64);

    for (int32_t k = 0; k < bs_lexicon_count(lexicon); ++k) {
        int32_t len = 0;
        CHECK_EQ(bs_lexicon_term(lexicon, k, buf.data(), static_cast<int32_t>(buf.size()), &len), BS_OK);
        terms.emplace_back(buf.data(), static_cast<size_t>(len));
    }

    for (const char* expected : {"BetterTranslator", "GGUF", "llama.cpp", ".NET", "i18n.json", "loadModelAsync",
                                 "max_new_tokens", "--amend"}) {
        bool found = false;

        for (const auto& term : terms) {
            found = found || term == expected;
        }

        if (!found) {
            bt::Fail(__FILE__, __LINE__, std::string("the lexicon did not harvest ") + expected);
        }
    }

    for (const auto& term : terms) {
        CHECK(term != "runs" && term != "models" && term != "config");
    }

    int32_t len = 0;
    CHECK_EQ(bs_lexicon_term(lexicon, 0, buf.data(), 1, &len), BS_BUFFER_TOO_SMALL);
    CHECK(len > 1);
    CHECK_EQ(bs_lexicon_term(lexicon, bs_lexicon_count(lexicon), buf.data(), 64, &len), BS_INVALID_ARG);
    bs_lexicon_free(lexicon);
}

TEST(lexicon_spoken_variants_cover_letters_and_numbers) {
    const auto has = [](const std::vector<std::vector<std::string>>& variants, const std::vector<std::string>& v) {
        for (const auto& candidate : variants) {
            if (candidate == v) {
                return true;
            }
        }

        return false;
    };

    const auto gguf = bspeech::SpokenVariants("GGUF", 8);
    CHECK(has(gguf, {"gguf"}));
    CHECK(has(gguf, {"g", "g", "u", "f"}));

    const auto i18n = bspeech::SpokenVariants("i18n.json", 8);
    CHECK(has(i18n, {"i", "eighteen", "n", "dot", "json"}));
    CHECK(has(i18n, {"i", "one", "eight", "n", "dot", "json"}));

    const auto amend = bspeech::SpokenVariants("--amend", 8);
    CHECK(has(amend, {"dash", "dash", "amend"}));
    CHECK(has(amend, {"hyphen", "hyphen", "amend"}));

    const auto camel = bspeech::SpokenVariants("loadModelAsync", 8);
    CHECK(has(camel, {"load", "model", "async"}));

    const auto http = bspeech::SpokenVariants("XMLHttpRequest", 8);
    CHECK(has(http, {"xml", "http", "request"}));

    CHECK(bspeech::SpokenVariants("a\xE2\x82\xAC" "b", 8).empty());
}

TEST(lexicon_ordering_cap_and_version) {
    bs_lexicon* a = nullptr;
    CHECK_EQ(bs_lexicon_create(3, &a), BS_OK);
    CHECK_EQ(bs_lexicon_add(a, "zeta_term", 9, 0.5f), BS_OK);
    CHECK_EQ(bs_lexicon_add(a, "AlphaTerm", 9, 2.0f), BS_OK);
    Harvest(a, "betaTerm betaTerm betaTerm gammaTerm");
    CHECK_EQ(bs_lexicon_count(a), 0);
    CHECK_EQ(bs_lexicon_freeze(a), BS_OK);
    CHECK_EQ(bs_lexicon_count(a), 3);

    std::vector<char> buf(32);
    int32_t len = 0;
    CHECK_EQ(bs_lexicon_term(a, 0, buf.data(), 32, &len), BS_OK);
    CHECK_EQ(std::string(buf.data(), static_cast<size_t>(len)), std::string("AlphaTerm"));
    CHECK_EQ(bs_lexicon_term(a, 1, buf.data(), 32, &len), BS_OK);
    CHECK_EQ(std::string(buf.data(), static_cast<size_t>(len)), std::string("zeta_term"));
    CHECK_EQ(bs_lexicon_term(a, 2, buf.data(), 32, &len), BS_OK);
    CHECK_EQ(std::string(buf.data(), static_cast<size_t>(len)), std::string("betaTerm"));

    CHECK_EQ(bs_lexicon_add(a, "late", 4, 1.0f), BS_STATE);
    CHECK_EQ(bs_lexicon_harvest(a, "lateTerm", 8), BS_STATE);

    bs_lexicon* b = nullptr;
    CHECK_EQ(bs_lexicon_create(3, &b), BS_OK);
    Harvest(b, "gammaTerm betaTerm betaTerm betaTerm");
    CHECK_EQ(bs_lexicon_add(b, "AlphaTerm", 9, 2.0f), BS_OK);
    CHECK_EQ(bs_lexicon_add(b, "zeta_term", 9, 0.5f), BS_OK);
    CHECK_EQ(bs_lexicon_freeze(b), BS_OK);
    CHECK(bs_lexicon_version(a) != 0);
    CHECK_EQ(bs_lexicon_version(a), bs_lexicon_version(b));

    bs_lexicon* c = nullptr;
    CHECK_EQ(bs_lexicon_create(3, &c), BS_OK);
    CHECK_EQ(bs_lexicon_add(c, "AlphaTerm", 9, 2.0f), BS_OK);
    CHECK_EQ(bs_lexicon_freeze(c), BS_OK);
    CHECK(bs_lexicon_version(a) != bs_lexicon_version(c));

    bs_lexicon_free(a);
    bs_lexicon_free(b);
    bs_lexicon_free(c);
}

TEST(lexicon_rejects_bad_input) {
    bs_lexicon* lexicon = nullptr;
    CHECK_EQ(bs_lexicon_create(0, &lexicon), BS_INVALID_ARG);
    CHECK_EQ(bs_lexicon_create(70000, &lexicon), BS_INVALID_ARG);
    CHECK_EQ(bs_lexicon_create(8, nullptr), BS_INVALID_ARG);
    CHECK_EQ(bs_lexicon_create(8, &lexicon), BS_OK);
    CHECK_EQ(bs_lexicon_add(lexicon, "", 0, 1.0f), BS_INVALID_ARG);
    CHECK_EQ(bs_lexicon_add(lexicon, "\xC3", 1, 1.0f), BS_INVALID_ARG);
    const std::string long_term(200, 'a');
    CHECK_EQ(bs_lexicon_add(lexicon, long_term.data(), long_term.size(), 1.0f), BS_INVALID_ARG);
    CHECK_EQ(bs_lexicon_harvest(lexicon, nullptr, 3), BS_INVALID_ARG);

    bs_dictation* dictation = nullptr;
    CHECK_EQ(bs_dictate("x", 1, BS_DICTATION_CODE, lexicon, 0.6f, &dictation), BS_STATE);
    CHECK_EQ(bs_lexicon_freeze(lexicon), BS_OK);
    CHECK_EQ(bs_dictate("x", 1, 7, lexicon, 0.6f, &dictation), BS_INVALID_ARG);
    CHECK_EQ(bs_dictate("x", 1, BS_DICTATION_CODE, lexicon, 1.5f, &dictation), BS_INVALID_ARG);
    CHECK_EQ(bs_dictate("a\xFF", 2, BS_DICTATION_CODE, lexicon, 0.6f, &dictation), BS_INVALID_ARG);
    CHECK_EQ(bs_last_error_offset(), int64_t{1});
    CHECK(dictation == nullptr);
    CHECK_EQ(bs_dictate("", 0, BS_DICTATION_CODE, lexicon, 0.6f, &dictation), BS_OK);
    CHECK_EQ(bs_dictation_substitution_count(dictation), 0);
    bs_dictation_free(dictation);
    bs_lexicon_free(lexicon);
}
