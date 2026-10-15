#include <cctype>
#include <random>
#include <string>
#include <vector>

#include "better_speech.h"
#include "harness.h"
#include "text_plan.h"
#include "utf8.h"

using namespace bspeech;

namespace {

std::vector<PlanItem> Plan(const std::string& text, int32_t max_units = 0, const std::string& language = "en",
                           UnknownTags tags = UnknownTags::Error, int32_t first = 0) {
    PlanSettings settings;
    settings.language = language;
    settings.unknown_tags = tags;
    settings.max_units = max_units;
    settings.first_chunk_units = first;

    std::vector<PlanItem> items;
    PlanError error;

    if (!BuildPlan(text, settings, &items, &error)) {
        bt::Fail(__FILE__, __LINE__, "plan failed: " + error.message);
    }

    return items;
}

std::vector<std::string> Sentences(const std::string& text, const std::string& language) {
    std::vector<std::string> out;

    for (const Span& s : SplitSentences(text, language)) {
        out.push_back(text.substr(s.start, s.end - s.start));
    }

    return out;
}

std::string Words(const std::string& text) {
    std::string out;
    bool space = true;

    for (const char c : text) {
        const bool is_space = c == ' ' || c == '\n' || c == '\t';

        if (is_space) {
            space = true;
            continue;
        }

        if (space && !out.empty()) {
            out.push_back(' ');
        }

        out.push_back(c);
        space = false;
    }

    return out;
}

}  // namespace

TEST(plan_speech_laugh_speech_in_order) {
    const std::string input = "Hello. [Laugh] That was unexpected.";
    const auto items = Plan(input);

    CHECK_EQ(items.size(), 3u);
    CHECK(items[0].kind == ItemKind::Speech);
    CHECK_EQ(items[0].text, std::string("Hello."));
    CHECK(items[1].kind == ItemKind::Event);
    CHECK(items[1].event == EventKind::Laugh);
    CHECK(items[2].kind == ItemKind::Speech);
    CHECK_EQ(items[2].text, std::string("That was unexpected."));

    CHECK_EQ(input.substr(items[0].source_offset, items[0].source_length), std::string("Hello."));
    CHECK_EQ(input.substr(items[1].source_offset, items[1].source_length), std::string("[Laugh]"));
    CHECK_EQ(input.substr(items[2].source_offset, items[2].source_length), std::string("That was unexpected."));
}

TEST(plan_tag_case_and_aliases) {
    const auto items = Plan("[LAUGH] a [laughs] b [Laughter] c [lAuGh]");
    int events = 0;

    for (const auto& item : items) {
        events += item.kind == ItemKind::Event ? 1 : 0;
    }

    CHECK_EQ(events, 4);
    CHECK_EQ(items.size(), 7u);
}

TEST(plan_escapes_are_text) {
    auto items = Plan("\\[Laugh] is how you write it.");
    CHECK_EQ(items.size(), 1u);
    CHECK_EQ(items[0].text, std::string("[Laugh] is how you write it."));

    items = Plan("a \\\\ b \\] c");
    CHECK_EQ(items.size(), 1u);
    CHECK_EQ(items[0].text, std::string("a \\ b ] c"));

    items = Plan("C:\\path\\file stays.");
    CHECK_EQ(items[0].text, std::string("C:\\path\\file stays."));
}

TEST(plan_unknown_tag_policies) {
    PlanSettings settings;
    settings.language = "en";
    std::vector<PlanItem> items;
    PlanError error;

    CHECK(!BuildPlan("He wrote [sic] there.", settings, &items, &error));
    CHECK_EQ(error.offset, 9);
    CHECK(error.message.find("[sic]") != std::string::npos);

    settings.unknown_tags = UnknownTags::Literal;
    CHECK(BuildPlan("He wrote [sic] there.", settings, &items, &error));
    CHECK_EQ(items.size(), 1u);
    CHECK_EQ(items[0].text, std::string("He wrote [sic] there."));

    // A misspelt tag is an unknown tag, never a guess at laughter.
    settings.unknown_tags = UnknownTags::Error;
    CHECK(!BuildPlan("Ha [Lauhg] ha", settings, &items, &error));
}

TEST(plan_brackets_that_are_not_tags) {
    for (const std::string text : {"See [1] here.", "Pick [ x ] now.", "Unclosed [Laugh at the end", "Empty [] pair.",
                                   "[9lives] starts with a digit."}) {
        const auto items = Plan(text);
        CHECK_EQ(items.size(), 1u);
        CHECK(items[0].kind == ItemKind::Speech);
        CHECK_EQ(items[0].text, text);
    }
}

TEST(plan_event_only_and_repeated) {
    auto items = Plan("[Laugh]");
    CHECK_EQ(items.size(), 1u);
    CHECK(items[0].kind == ItemKind::Event);

    items = Plan("[Laugh][Laugh]  [Laugh]\n");
    CHECK_EQ(items.size(), 3u);

    for (const auto& item : items) {
        CHECK(item.kind == ItemKind::Event);
    }

    // Punctuation with no word in it is not sent on its own.
    items = Plan("[Laugh]. ");
    CHECK_EQ(items.size(), 1u);

    items = Plan("Hello. Hello. Hello.");
    CHECK_EQ(items.size(), 3u);

    for (const auto& item : items) {
        CHECK_EQ(item.text, std::string("Hello."));
    }
}

TEST(plan_empty_and_whitespace) {
    CHECK(Plan("").empty());
    CHECK(Plan("  \n\t ").empty());
}

TEST(plan_rejects_invalid_utf8) {
    PlanSettings settings;
    std::vector<PlanItem> items;
    PlanError error;
    const std::string bad = std::string("ok ") + static_cast<char>(0xC3) + "(";

    CHECK(!BuildPlan(bad, settings, &items, &error));
    CHECK_EQ(error.offset, 3);

    const std::string surrogate = "x\xED\xA0\x80y";
    CHECK(!BuildPlan(surrogate, settings, &items, &error));

    const std::string overlong = "x\xC0\xAFy";
    CHECK(!BuildPlan(overlong, settings, &items, &error));
}

TEST(sentences_english_rules) {
    auto s = Sentences("Dr. Smith arrived. He sat down! Was it late? Yes.", "en");
    CHECK_EQ(s.size(), 4u);
    CHECK_EQ(s[0], std::string("Dr. Smith arrived."));

    s = Sentences("Use e.g. Python here. Next one.", "en-US");
    CHECK_EQ(s.size(), 2u);

    s = Sentences("Version 3.14 shipped. Next one.", "en");
    CHECK_EQ(s.size(), 2u);

    s = Sentences("Wait... what happened? Nothing.", "en");
    CHECK_EQ(s.size(), 2u);
    CHECK_EQ(s[0], std::string("Wait... what happened?"));

    s = Sentences("He said \"Stop.\" Then he left.", "en");
    CHECK_EQ(s.size(), 2u);
    CHECK_EQ(s[0], std::string("He said \"Stop.\""));

    s = Sentences("J. R. Tolkien wrote it. It sold.", "en");
    CHECK_EQ(s.size(), 2u);

    s = Sentences("Built on .NET and i18n.json today.", "en");
    CHECK_EQ(s.size(), 1u);

    s = Sentences("We shipped Apple stuff etc. Then we rested.", "en");
    CHECK_EQ(s.size(), 2u);
}

TEST(sentences_czech_rules) {
    const std::string text =
        "Dne 1. ledna jsme začali, např. v Praze. Přišli Ing. Novák a Mgr. Dvořák. Bylo to fajn atd. Pak jsme šli "
        "domů.";
    const auto s = Sentences(text, "cs");

    CHECK_EQ(s.size(), 4u);
    CHECK_EQ(s[0], std::string("Dne 1. ledna jsme začali, např. v Praze."));
    CHECK_EQ(s[1], std::string("Přišli Ing. Novák a Mgr. Dvořák."));
    CHECK_EQ(s[2], std::string("Bylo to fajn atd."));

    const auto diacritic = Sentences("Je to tak. Řekl jsem to. Šlo to.", "cs-CZ");
    CHECK_EQ(diacritic.size(), 3u);
}

TEST(sentences_unspaced_scripts_and_lines) {
    auto s = Sentences("你好。今天天气很好！是吗？", "zh");
    CHECK_EQ(s.size(), 3u);
    CHECK_EQ(s[0], std::string("你好。"));

    s = Sentences("Title line\n\nThe body starts here", "en");
    CHECK_EQ(s.size(), 2u);

    s = Sentences("a wrapped line\ncontinues here", "en");
    CHECK_EQ(s.size(), 1u);
    CHECK_EQ(SpeakableText("a wrapped line\ncontinues here", {0, 29}), std::string("a wrapped line continues here"));
}

TEST(utf8_case_tables) {
    CHECK(utf8::IsUpper(U'Ř'));
    CHECK(utf8::IsLower(U'ř'));
    CHECK(utf8::ToLower(U'Ř') == U'ř');
    CHECK(utf8::ToLower(U'Ž') == U'ž');
    CHECK(utf8::ToLower(U'Ĺ') == U'ĺ');
    CHECK(utf8::ToLower(U'Ÿ') == U'ÿ');
    CHECK(utf8::ToLower(U'Д') == U'д');
    CHECK(utf8::ToLower(U'Ё') == U'ё');
    CHECK(utf8::ToLower(U'Ω') == U'ω');
    CHECK(utf8::IsUpper(U'Ü') && utf8::IsLower(U'ü'));
    CHECK(!utf8::IsUpper(U'中') && !utf8::IsLower(U'中'));
    CHECK(utf8::IsWordChar(U'中'));
    CHECK(!utf8::IsWordChar(U'。'));
}

TEST(chunks_pack_to_budget_without_losing_words) {
    const std::string text =
        "The first sentence is short. The second one is a little longer than that. Third. And the fourth "
        "sentence closes the paragraph.";
    const auto items = Plan(text, 60);

    std::string joined;

    for (const auto& item : items) {
        CHECK(utf8::CountCodepoints(item.text) <= 60);
        joined += (joined.empty() ? "" : " ") + item.text;
    }

    CHECK_EQ(Words(joined), Words(text));
    CHECK(items.size() >= 2);
    CHECK(items.size() < 4);
}

TEST(chunks_split_long_sentence_at_clauses_then_words) {
    const std::string text =
        "When the build finished, the tests ran in parallel, the coverage report was written, and the "
        "release notes were drafted for the team.";
    auto items = Plan(text, 50);
    std::string joined;

    for (const auto& item : items) {
        CHECK(utf8::CountCodepoints(item.text) <= 50);
        joined += (joined.empty() ? "" : " ") + item.text;
    }

    CHECK_EQ(Words(joined), Words(text));
    CHECK_EQ(items[0].text, std::string("When the build finished,"));

    const std::string no_commas =
        "This sentence has no clause punctuation at all and so it has to be cut between words instead of anywhere";
    items = Plan(no_commas, 30);
    joined.clear();

    for (const auto& item : items) {
        CHECK(utf8::CountCodepoints(item.text) <= 30);
        CHECK(item.text.front() != ' ' && item.text.back() != ' ');
        joined += (joined.empty() ? "" : " ") + item.text;
    }

    CHECK_EQ(joined, no_commas);
}

TEST(chunks_split_unspaced_run_at_codepoints) {
    const std::string text = "这是一个没有任何标点符号而且非常非常长的句子所以只能按字切开";
    const auto items = Plan(text, 10, "zh");
    std::string joined;

    for (const auto& item : items) {
        CHECK(utf8::CountCodepoints(item.text) <= 10);
        joined += item.text;
    }

    CHECK_EQ(joined, text);
}

TEST(chunks_first_chunk_budget) {
    const std::string text =
        "Good morning, everyone, and welcome to the quarterly review. We have a lot to cover today. Let us begin.";
    const auto items = Plan(text, 200, "en", UnknownTags::Error, 20);

    CHECK(utf8::CountCodepoints(items[0].text) <= 20);
    CHECK_EQ(items[0].text, std::string("Good morning,"));
    CHECK(items.size() >= 2);
}

TEST(chunks_keep_event_positions) {
    const auto items = Plan("One. Two. [Laugh] Three. Four.", 100);

    CHECK_EQ(items.size(), 3u);
    CHECK_EQ(items[0].text, std::string("One. Two."));
    CHECK(items[1].kind == ItemKind::Event);
    CHECK_EQ(items[2].text, std::string("Three. Four."));
}

TEST(chunks_measure_failure_is_reported) {
    PlanSettings settings;
    settings.max_units = 10;
    settings.measure = [](std::string_view) { return -1; };
    std::vector<PlanItem> items;
    PlanError error;

    CHECK(!BuildPlan("Some text here.", settings, &items, &error));
}

TEST(plan_property_random_texts_keep_every_word_and_event) {
    std::mt19937 rng(12345);
    const std::vector<std::string> words = {"alpha", "beta",  "gamma", "Dr.", "e.g.", "Praha", "3.14",
                                            "über",  "Řekl", "x",     "longwordwithoutbreaks"};
    const std::vector<std::string> tails = {"", "", "", ".", ",", "!", "?", "...", ";"};

    for (int round = 0; round < 300; ++round) {
        std::string text;
        std::vector<std::string> expected_words;
        int expected_events = 0;
        const int count = 1 + static_cast<int>(rng() % 40);

        for (int i = 0; i < count; ++i) {
            if (rng() % 9 == 0) {
                text += " [Laugh] ";
                ++expected_events;
                continue;
            }

            const std::string w = words[rng() % words.size()] + tails[rng() % tails.size()];
            text += w + (rng() % 7 == 0 ? "\n" : " ");
        }

        const int32_t budget = static_cast<int32_t>(rng() % 3 == 0 ? 0 : 8 + rng() % 60);
        const auto items = Plan(text, budget, rng() % 2 ? "en" : "cs");

        std::string spoken;
        int events = 0;

        for (const auto& item : items) {
            if (item.kind == ItemKind::Event) {
                ++events;
                spoken += " ";
                continue;
            }

            CHECK(!item.text.empty());

            if (budget > 0) {
                // A single word longer than the budget is the one case allowed to be cut.
                CHECK(utf8::CountCodepoints(item.text) <= static_cast<size_t>(budget));
            }

            spoken += item.text + " ";
        }

        CHECK_EQ(events, expected_events);

        // Concatenating the chunks restores the input's non-space characters exactly.
        std::string a;
        std::string b;

        for (const char c : text) {
            if (c != ' ' && c != '\n') {
                a.push_back(c);
            }
        }

        for (const char c : spoken) {
            if (c != ' ') {
                b.push_back(c);
            }
        }

        // The event tags are not spoken; remove them from the expectation.
        std::string stripped;

        for (size_t i = 0; i < a.size();) {
            if (a.compare(i, 7, "[Laugh]") == 0) {
                i += 7;
            } else {
                stripped.push_back(a[i++]);
            }
        }

        // Pure punctuation directly after an event is dropped as unspeakable, which
        // is the only difference allowed.
        std::string filtered_expected;
        std::string filtered_spoken;

        for (const char c : stripped) {
            if (std::isalnum(static_cast<unsigned char>(c)) || static_cast<unsigned char>(c) >= 0x80) {
                filtered_expected.push_back(c);
            }
        }

        for (const char c : b) {
            if (std::isalnum(static_cast<unsigned char>(c)) || static_cast<unsigned char>(c) >= 0x80) {
                filtered_spoken.push_back(c);
            }
        }

        CHECK_EQ(filtered_spoken, filtered_expected);
    }
}

TEST(abi_plan_round_trip) {
    const std::string input = "Hello. [Laugh] That was unexpected.";
    bs_plan_options options{};
    options.struct_size = sizeof options;
    options.language = "en";

    bs_plan* plan = nullptr;
    CHECK_EQ(bs_plan_create(input.data(), input.size(), &options, &plan), BS_OK);
    CHECK_EQ(bs_plan_count(plan), 3u);

    bs_plan_item item{};
    CHECK_EQ(bs_plan_item_get(plan, 1, &item), BS_OK);
    CHECK_EQ(item.kind, BS_ITEM_EVENT);
    CHECK_EQ(item.event, BS_EVENT_LAUGH);
    CHECK_EQ(bs_plan_item_get(plan, 3, &item), BS_INVALID_ARG);
    bs_plan_free(plan);

    const std::string bad = "Hi [Wink] there";
    CHECK_EQ(bs_plan_create(bad.data(), bad.size(), &options, &plan), BS_INVALID_ARG);
    CHECK(plan == nullptr);
    CHECK_EQ(bs_last_error_offset(), 3);
    CHECK(std::string(bs_last_error()).find("[Wink]") != std::string::npos);
}
