#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstring>
#include <filesystem>
#include <string>
#include <thread>
#include <vector>

#if defined(__linux__)
#include <unistd.h>
#endif

#include "alloc_count.h"
#include "better_speech_stt.h"
#include "harness.h"
#include "tone_recognizer.h"
#include "wav.h"
#include "wav_fixtures.h"

namespace {

using Clock = std::chrono::steady_clock;

struct Sub {
    int32_t kind = 0;
    bool applied = false;
    float confidence = 0.0f;
    std::string replacement;
    size_t text_offset = 0;
    size_t text_length = 0;
};

struct Event {
    int32_t kind = 0;
    int32_t segment = 0;
    bool segment_end = false;
    int32_t status = 0;
    int64_t start = 0;
    int64_t end = 0;
    float min_confidence = 0.0f;
    std::string text;
    std::string unstable;
    std::string raw;
    std::vector<Sub> subs;
};

bs_rec_engine* MakeEngine(bt::ToneRecognizer* recognizer, const bs_lexicon* lexicon = nullptr) {
    bs_rec_engine_config config{};
    config.struct_size = sizeof config;
    config.recognizer = recognizer != nullptr ? &recognizer->table : nullptr;
    config.lexicon = lexicon;
    bs_rec_engine* engine = nullptr;

    if (bs_rec_engine_create(&config, &engine) != BS_OK) {
        bt::Fail(__FILE__, __LINE__, std::string("engine creation failed: ") + bs_last_error());
    }

    return engine;
}

bs_rec_request Live(int32_t rate, int32_t ring = 48000 * 20) {
    bs_rec_request r{};
    r.struct_size = sizeof r;
    CHECK_EQ(bs_rec_request_defaults(&r), BS_OK);
    r.language = "en";
    r.input_sample_rate = rate;
    r.input_ring_samples = ring;
    return r;
}

bs_rec_request File(const std::string& path) {
    bs_rec_request r{};
    r.struct_size = sizeof r;
    CHECK_EQ(bs_rec_request_defaults(&r), BS_OK);
    r.language = "en";
    r.file_path = path.c_str();
    r.partial_interval_ms = 0;
    return r;
}

bs_rec_session* Start(bs_rec_engine* engine, const bs_rec_request& request) {
    bs_rec_session* session = nullptr;

    if (bs_rec_session_start(engine, &request, &session) != BS_OK) {
        bt::Fail(__FILE__, __LINE__, std::string("session start failed: ") + bs_last_error());
    }

    return session;
}

bool PollOne(bs_rec_session* session, Event* out) {
    bs_rec_event e{};
    e.struct_size = sizeof e;
    CHECK_EQ(bs_rec_session_poll(session, &e), BS_OK);

    if (e.kind == BS_REC_EVENT_NONE) {
        return false;
    }

    out->kind = e.kind;
    out->segment = e.segment;
    out->segment_end = e.segment_end != 0;
    out->status = e.status;
    out->start = e.start_sample;
    out->end = e.end_sample;
    out->min_confidence = e.min_confidence;
    out->text.assign(e.text, e.text_len);
    out->unstable.assign(e.unstable, e.unstable_len);
    out->raw.assign(e.raw, e.raw_len);
    out->subs.clear();

    for (int32_t k = 0; k < e.substitutions; ++k) {
        bs_substitution s{};
        s.struct_size = sizeof s;
        CHECK_EQ(bs_rec_session_substitution(session, k, &s), BS_OK);
        out->subs.push_back({s.kind, s.applied != 0, s.confidence, std::string(s.replacement, s.replacement_len),
                             s.text_offset, s.text_length});
    }

    return true;
}

void PollInto(bs_rec_session* session, std::vector<Event>* events) {
    Event e;

    while ((events->empty() || events->back().kind != BS_REC_EVENT_FINISHED) && PollOne(session, &e)) {
        events->push_back(e);
    }
}

std::vector<Event> Drain(bs_rec_session* session, std::vector<Event> events = {}, int timeout_ms = 120000) {
    const auto deadline = Clock::now() + std::chrono::milliseconds(timeout_ms);

    while (events.empty() || events.back().kind != BS_REC_EVENT_FINISHED) {
        const size_t before = events.size();
        PollInto(session, &events);

        if (events.size() == before) {
            if (Clock::now() > deadline) {
                bt::Fail(__FILE__, __LINE__, "the session did not finish in time");
            }

            std::this_thread::sleep_for(std::chrono::milliseconds(1));
        }
    }

    return events;
}

void PushAll(bs_rec_session* session, const std::vector<float>& interleaved, int32_t channels, int32_t block,
             std::vector<Event>* events = nullptr, int pause_us = 0) {
    const size_t frames = interleaved.size() / static_cast<size_t>(channels);

    for (size_t at = 0; at < frames; at += static_cast<size_t>(block)) {
        const auto n = static_cast<int32_t>(std::min<size_t>(static_cast<size_t>(block), frames - at));
        CHECK_EQ(bs_rec_session_push(session, interleaved.data() + at * static_cast<size_t>(channels), n, channels),
                 BS_OK);

        if (events != nullptr) {
            PollInto(session, events);
        }

        if (pause_us > 0) {
            std::this_thread::sleep_for(std::chrono::microseconds(pause_us));
        }
    }

    bs_rec_session_end_input(session);
}

std::vector<std::string> Segments(const std::vector<Event>& events) {
    std::vector<std::string> segments;

    for (const Event& e : events) {
        if (e.kind != BS_REC_EVENT_FINAL) {
            continue;
        }

        if (segments.size() <= static_cast<size_t>(e.segment)) {
            segments.resize(static_cast<size_t>(e.segment) + 1);
        }

        std::string& s = segments[static_cast<size_t>(e.segment)];

        if (!e.text.empty()) {
            s += (s.empty() ? "" : " ") + e.text;
        }
    }

    return segments;
}

std::string Spoken(const std::vector<std::string>& vocabulary, const std::vector<size_t>& words) {
    std::string out;

    for (const size_t w : words) {
        out += (out.empty() ? "" : " ") + vocabulary[w];
    }

    return out;
}

const Event& Finished(const std::vector<Event>& events) {
    CHECK(!events.empty());
    CHECK_EQ(events.back().kind, static_cast<int32_t>(BS_REC_EVENT_FINISHED));
    return events.back();
}

bs_rec_stats Stats(bs_rec_session* session) {
    bs_rec_stats stats{};
    stats.struct_size = sizeof stats;
    CHECK_EQ(bs_rec_session_stats(session, &stats), BS_OK);
    return stats;
}

std::string Fresh(const std::string& name) {
    const std::string path = bt::TempPath(name);
    std::filesystem::remove(path);
    return path;
}

std::vector<size_t> Cycle(size_t count, size_t period) {
    std::vector<size_t> words;

    for (size_t k = 0; k < count; ++k) {
        words.push_back(k % period);
    }

    return words;
}

void WriteToneFile(const std::string& path, const std::vector<std::vector<size_t>>& utterances, int64_t* frames) {
    bspeech::WavWriter writer;
    std::string error;
    CHECK(writer.Open(path, 16000, &error));
    *frames = 0;

    for (size_t k = 0; k < utterances.size(); ++k) {
        bt::ToneSpeechOptions o;
        o.lead_ms = k == 0 ? 500 : 0;
        const auto chunk = bt::ToneSpeech({utterances[k]}, o);
        CHECK(writer.Write(chunk.data(), chunk.size(), &error));
        *frames += static_cast<int64_t>(chunk.size());
    }

    CHECK(writer.Finish(&error));
}

long ResidentKb() {
#if defined(__linux__)
    std::FILE* f = std::fopen("/proc/self/statm", "r");

    if (f == nullptr) {
        return 0;
    }

    long pages = 0;
    long resident = 0;
    const int read = std::fscanf(f, "%ld %ld", &pages, &resident);
    std::fclose(f);
    return read == 2 ? resident * (sysconf(_SC_PAGESIZE) / 1024) : 0;
#else
    return 0;
#endif
}

}

TEST(rec_unavailable_without_a_model) {
    bs_rec_engine* engine = MakeEngine(nullptr);
    bs_rec_capabilities caps{};
    caps.struct_size = sizeof caps;
    CHECK_EQ(bs_rec_engine_capabilities(engine, &caps), BS_OK);
    CHECK_EQ(caps.available, 0);
    CHECK_EQ(caps.sample_rate, 0);

    std::vector<char> reason(256);
    int32_t len = 0;
    CHECK_EQ(bs_rec_engine_unavailable_reason(engine, reason.data(), 256, &len), BS_OK);
    CHECK(len > 0);

    const bs_rec_request r = Live(48000, 48000);
    bs_rec_session* session = nullptr;
    CHECK_EQ(bs_rec_session_start(engine, &r, &session), BS_UNAVAILABLE);
    CHECK(session == nullptr);
    CHECK(std::string(bs_last_error()).find("model") != std::string::npos);
    bs_rec_engine_free(engine);
}

TEST(rec_rejects_a_bad_recognizer_table) {
    bt::ToneRecognizer recognizer;
    recognizer.vocabulary = bt::ToneVocabulary(8);
    bs_rec_engine* engine = nullptr;

    bs_recognizer_v1 table = recognizer.table;
    table.abi_version = 2;
    bs_rec_engine_config config{};
    config.struct_size = sizeof config;
    config.recognizer = &table;
    CHECK_EQ(bs_rec_engine_create(&config, &engine), BS_INVALID_ARG);

    table = recognizer.table;
    table.finalize = nullptr;
    CHECK_EQ(bs_rec_engine_create(&config, &engine), BS_INVALID_ARG);

    table = recognizer.table;
    table.struct_size = 16;
    CHECK_EQ(bs_rec_engine_create(&config, &engine), BS_INVALID_ARG);

    table = recognizer.table;
    recognizer.fail_caps = true;
    CHECK_EQ(bs_rec_engine_create(&config, &engine), BS_SOURCE);
    CHECK(std::string(bs_last_error()).find("no capabilities") != std::string::npos);
    recognizer.fail_caps = false;

    recognizer.sample_rate = 4000;
    CHECK_EQ(bs_rec_engine_create(&config, &engine), BS_SOURCE);
    recognizer.sample_rate = 16000;
    recognizer.max_window_samples = 100;
    CHECK_EQ(bs_rec_engine_create(&config, &engine), BS_SOURCE);
    recognizer.max_window_samples = 16000 * 30;

    config.struct_size = 8;
    CHECK_EQ(bs_rec_engine_create(&config, &engine), BS_INVALID_ARG);
    config.struct_size = sizeof config;

    bs_lexicon* lexicon = nullptr;
    CHECK_EQ(bs_lexicon_create(8, &lexicon), BS_OK);
    config.lexicon = lexicon;
    CHECK_EQ(bs_rec_engine_create(&config, &engine), BS_STATE);
    CHECK(engine == nullptr);
    CHECK_EQ(bs_lexicon_freeze(lexicon), BS_OK);
    CHECK_EQ(bs_rec_engine_create(&config, &engine), BS_OK);
    bs_lexicon_free(lexicon);

    bs_rec_capabilities caps{};
    caps.struct_size = sizeof caps;
    CHECK_EQ(bs_rec_engine_capabilities(engine, &caps), BS_OK);
    CHECK_EQ(caps.available, 1);
    CHECK_EQ(caps.sample_rate, 16000);
    CHECK_EQ(caps.word_timestamps, 1);
    CHECK_EQ(caps.bias_context, 1);
    CHECK_EQ(caps.bias_logit, 1);
    CHECK_EQ(caps.bias_trie, 0);
    CHECK_EQ(caps.language_count, 2);

    std::vector<char> code(8);
    int32_t len = 0;
    CHECK_EQ(bs_rec_engine_language(engine, 1, code.data(), 8, &len), BS_OK);
    CHECK_EQ(std::string(code.data(), static_cast<size_t>(len)), std::string("cs"));
    CHECK_EQ(bs_rec_engine_language(engine, 2, code.data(), 8, &len), BS_INVALID_ARG);

    bs_rec_capabilities small{};
    small.struct_size = 8;
    CHECK_EQ(bs_rec_engine_capabilities(engine, &small), BS_INVALID_ARG);
    bs_rec_engine_free(engine);
}

TEST(rec_validates_requests) {
    bt::ToneRecognizer recognizer;
    recognizer.vocabulary = bt::ToneVocabulary(8);
    bs_rec_engine* engine = MakeEngine(&recognizer);
    const std::string missing = Fresh("rec_missing.wav");

    struct Mutation {
        const char* name;
        void (*apply)(bs_rec_request*);
    };

    const Mutation mutations[] = {
        {"no language", [](bs_rec_request* r) { r->language = nullptr; }},
        {"empty language", [](bs_rec_request* r) { r->language = ""; }},
        {"unlisted language", [](bs_rec_request* r) { r->language = "de"; }},
        {"bad mode", [](bs_rec_request* r) { r->mode = 5; }},
        {"no input", [](bs_rec_request* r) { r->input_sample_rate = 0; }},
        {"low rate", [](bs_rec_request* r) { r->input_sample_rate = 4000; }},
        {"high rate", [](bs_rec_request* r) { r->input_sample_rate = 384000; }},
        {"tiny ring", [](bs_rec_request* r) { r->input_ring_samples = 100; }},
        {"short window", [](bs_rec_request* r) { r->window_ms = 500; }},
        {"wide overlap", [](bs_rec_request* r) {
             r->window_ms = 2000;
             r->overlap_ms = 1500;
         }},
        {"negative overlap", [](bs_rec_request* r) { r->overlap_ms = -1; }},
        {"fast partials", [](bs_rec_request* r) { r->partial_interval_ms = 50; }},
        {"negative threads", [](bs_rec_request* r) { r->n_threads = -2; }},
        {"confidence", [](bs_rec_request* r) { r->min_confidence = 1.5f; }},
        {"nan confidence", [](bs_rec_request* r) { r->min_confidence = std::nanf(""); }},
        {"context", [](bs_rec_request* r) { r->context_chars = 5000; }},
        {"bias", [](bs_rec_request* r) { r->bias_terms = 2000; }},
        {"onset", [](bs_rec_request* r) { r->vad.onset_db = 0.0f; }},
        {"floor", [](bs_rec_request* r) { r->vad.absolute_floor_dbfs = 5.0f; }},
        {"hangover", [](bs_rec_request* r) { r->vad.hangover_ms = 10.0f; }},
        {"pre roll", [](bs_rec_request* r) { r->vad.pre_roll_ms = 50.0f; }},
        {"pre roll vs window", [](bs_rec_request* r) {
             r->window_ms = 1000;
             r->overlap_ms = 0;
             r->vad.pre_roll_ms = 600.0f;
         }},
        {"struct size", [](bs_rec_request* r) { r->struct_size = 16; }},
        {"vad struct size", [](bs_rec_request* r) { r->vad.struct_size = 4; }},
    };

    for (const Mutation& m : mutations) {
        bs_rec_request r = Live(48000, 48000);
        m.apply(&r);
        bs_rec_session* session = nullptr;
        const int32_t rc = bs_rec_session_start(engine, &r, &session);

        if (rc != BS_INVALID_ARG || session != nullptr) {
            bt::Fail(__FILE__, __LINE__, std::string("accepted a request with ") + m.name);
        }
    }

    bs_rec_request both = Live(48000, 48000);
    both.file_path = missing.c_str();
    bs_rec_session* session = nullptr;
    CHECK_EQ(bs_rec_session_start(engine, &both, &session), BS_INVALID_ARG);

    const bs_rec_request file = File(missing);
    CHECK_EQ(bs_rec_session_start(engine, &file, &session), BS_ASSET);
    CHECK(session == nullptr);

    bs_rec_request ok = Live(48000, 48000);
    session = Start(engine, ok);
    bs_rec_session_cancel(session);
    bs_rec_session_free(session);
    bs_rec_engine_free(engine);
}

TEST(rec_live_48k_stereo_end_to_end) {
    bt::ToneRecognizer recognizer;
    recognizer.vocabulary = bt::ToneVocabulary(40);
    bs_rec_engine* engine = MakeEngine(&recognizer);

    bt::ToneSpeechOptions o;
    o.rate = 48000;
    o.channels = 2;
    const std::vector<std::vector<size_t>> utterances = {{1, 2, 3, 4, 5}, {6, 7, 8}, {30, 31, 32, 33}};
    const auto audio = bt::ToneSpeech(utterances, o);
    bs_rec_session* session = Start(engine, Live(48000));
    PushAll(session, audio, 2, 480);
    int32_t status = 0;
    CHECK_EQ(bs_rec_session_wait(session, -1, &status), BS_OK);
    const auto events = Drain(session);

    CHECK_EQ(Finished(events).status, static_cast<int32_t>(BS_OK));
    const auto segments = Segments(events);
    CHECK_EQ(segments.size(), utterances.size());

    for (size_t k = 0; k < utterances.size(); ++k) {
        CHECK_EQ(segments[k], Spoken(recognizer.vocabulary, utterances[k]));
    }

    int32_t ends = 0;
    std::vector<int32_t> partials_per_segment(utterances.size(), 0);

    for (const Event& e : events) {
        ends += e.kind == BS_REC_EVENT_FINAL && e.segment_end ? 1 : 0;

        if (e.kind == BS_REC_EVENT_PARTIAL) {
            ++partials_per_segment[static_cast<size_t>(e.segment)];
        }
    }

    CHECK_EQ(ends, 3);

    for (const int32_t p : partials_per_segment) {
        CHECK(p <= 1);
    }

    const bs_rec_stats stats = Stats(session);
    CHECK_EQ(stats.finished, 1);
    CHECK_EQ(stats.status, static_cast<int32_t>(BS_OK));
    CHECK_EQ(stats.model_sample_rate, 16000);
    CHECK_EQ(stats.segments, 3);
    CHECK_EQ(stats.input_frames, static_cast<int64_t>(audio.size() / 2));
    CHECK_EQ(stats.overrun_frames, int64_t{0});
    CHECK(stats.partials > 0);
    CHECK_EQ(stats.speech_end_to_final_count, int64_t{3});
    CHECK(stats.speech_end_to_final_median_us >= 0);
    CHECK(stats.speech_end_to_final_p90_us >= stats.speech_end_to_final_median_us);
    CHECK(stats.t_first_voiced_us >= 0 && stats.t_first_voiced_us <= stats.t_first_final_us);
    CHECK(stats.t_first_final_us <= stats.t_finished_us);
    CHECK(stats.onset_to_first_partial_us >= 0);
    CHECK(std::llabs(stats.model_samples - static_cast<int64_t>(audio.size() / 2 / 3)) <= 160);
    CHECK_EQ(stats.cancel_latency_us, int64_t{-1});

    const int64_t first_word = static_cast<int64_t>(o.lead_ms) * 16;
    bool first_final_placed = false;

    for (const Event& e : events) {
        if (e.kind == BS_REC_EVENT_FINAL && e.segment == 0) {
            first_final_placed = std::llabs(e.start - first_word) <= 320;
            break;
        }
    }

    CHECK(first_final_placed);
    CHECK_EQ(recognizer.begun_languages.front(), std::string("en"));
    bs_rec_session_free(session);
    bs_rec_engine_free(engine);
}

TEST(rec_resamples_44k_and_48k_files) {
    for (const int32_t rate : {44100, 48000, 22050, 16000}) {
        bt::ToneRecognizer recognizer;
        recognizer.vocabulary = bt::ToneVocabulary(40);
        bs_rec_engine* engine = MakeEngine(&recognizer);

        bt::ToneSpeechOptions o;
        o.rate = rate;
        o.channels = 2;
        const std::vector<std::vector<size_t>> utterances = {{0, 9, 19, 29, 39}, {12, 13}};
        const std::string path = Fresh("rec_file_" + std::to_string(rate) + ".wav");
        bt::WriteWav(path, static_cast<uint32_t>(rate), 2, 16, false, rate == 44100, bt::ToneSpeech(utterances, o),
                     false);

        bs_rec_session* session = Start(engine, File(path));
        const auto events = Drain(session);
        CHECK_EQ(Finished(events).status, static_cast<int32_t>(BS_OK));
        const auto segments = Segments(events);
        CHECK_EQ(segments.size(), size_t{2});
        CHECK_EQ(segments[0], Spoken(recognizer.vocabulary, utterances[0]));
        CHECK_EQ(segments[1], Spoken(recognizer.vocabulary, utterances[1]));

        CHECK_EQ(bs_rec_session_push(session, nullptr, 0, 1), BS_STATE);
        bs_rec_session_free(session);
        bs_rec_engine_free(engine);
    }
}

TEST(rec_partials_only_grow_their_stable_prefix) {
    bt::ToneRecognizer recognizer;
    recognizer.vocabulary = bt::ToneVocabulary(40);
    recognizer.flip_last_partial = true;
    recognizer.partial_delay_ms = 2;
    bs_rec_engine* engine = MakeEngine(&recognizer);

    bt::ToneSpeechOptions o;
    o.rate = 48000;
    const std::vector<std::vector<size_t>> utterances = {{1, 2, 3, 4, 5, 6, 7, 8, 9, 10}, {20, 21, 22, 23, 24, 25}};
    const auto audio = bt::ToneSpeech(utterances, o);
    bs_rec_request r = Live(48000);
    r.partial_interval_ms = 100;
    bs_rec_session* session = Start(engine, r);
    std::vector<Event> events;
    PushAll(session, audio, 1, 480, &events, 1500);
    events = Drain(session, events);
    CHECK_EQ(Finished(events).status, static_cast<int32_t>(BS_OK));

    const auto segments = Segments(events);
    CHECK_EQ(segments.size(), size_t{2});
    CHECK_EQ(segments[0], Spoken(recognizer.vocabulary, utterances[0]));
    CHECK_EQ(segments[1], Spoken(recognizer.vocabulary, utterances[1]));

    int observed = 0;
    bool unstable_seen = false;

    for (int32_t segment = 0; segment < 2; ++segment) {
        std::string last;

        for (const Event& e : events) {
            if (e.segment != segment || e.kind == BS_REC_EVENT_FINISHED) {
                continue;
            }

            const std::string& current = e.kind == BS_REC_EVENT_PARTIAL ? e.text : e.raw;

            if (current.compare(0, last.size(), last) != 0) {
                bt::Fail(__FILE__, __LINE__, "stable text '" + last + "' was revised to '" + current + "'");
            }

            if (e.kind == BS_REC_EVENT_PARTIAL) {
                ++observed;
                unstable_seen = unstable_seen || !e.unstable.empty();
                last = e.text;
            }
        }
    }

    CHECK(observed >= 4);
    CHECK(unstable_seen);
    bs_rec_session_free(session);
    bs_rec_engine_free(engine);
}

TEST(rec_long_speech_has_no_duplicates_or_drops) {
    for (const int32_t timestamps : {1, 0}) {
        for (const int32_t partial_ms : {0, 300}) {
            bt::ToneRecognizer recognizer;
            recognizer.vocabulary = bt::ToneVocabulary(40);
            recognizer.word_timestamps = timestamps;
            bs_rec_engine* engine = MakeEngine(&recognizer);

            const std::vector<size_t> words = Cycle(272, 40);
            const std::string path = Fresh("rec_long.wav");
            bt::WriteWav(path, 16000, 1, 16, false, false, bt::ToneSpeech({words}), false);

            bs_rec_request r = File(path);
            r.window_ms = 4000;
            r.overlap_ms = 1000;
            r.partial_interval_ms = partial_ms;
            bs_rec_session* session = Start(engine, r);
            const auto events = Drain(session);
            CHECK_EQ(Finished(events).status, static_cast<int32_t>(BS_OK));

            const auto segments = Segments(events);
            CHECK_EQ(segments.size(), size_t{1});
            const std::string expected = Spoken(recognizer.vocabulary, words);

            if (segments[0] != expected) {
                bt::Fail(__FILE__, __LINE__,
                         "timestamps " + std::to_string(timestamps) + ", partials " + std::to_string(partial_ms) +
                             ": got\n" + segments[0] + "\nwanted\n" + expected);
            }

            const bs_rec_stats stats = Stats(session);
            CHECK(stats.windows >= 15);
            CHECK(stats.peak_window_samples <= 4000 * 16 + 160);
            CHECK(recognizer.max_fed <= 4000 * 16 + 160);
            CHECK_EQ(stats.finals, stats.windows);
            bs_rec_session_free(session);
            bs_rec_engine_free(engine);
        }
    }
}

TEST(rec_overrun_is_counted_and_push_never_blocks_or_allocates) {
    bt::ToneRecognizer recognizer;
    recognizer.vocabulary = bt::ToneVocabulary(40);
    recognizer.partial_delay_ms = 20;
    bs_rec_engine* engine = MakeEngine(&recognizer);

    bt::ToneSpeechOptions o;
    o.rate = 48000;
    const auto audio = bt::ToneSpeech({Cycle(30, 40)}, o);
    bs_rec_request r = Live(48000, 1024);
    r.partial_interval_ms = 100;
    bs_rec_session* session = Start(engine, r);

    int64_t slowest_us = 0;
    const long allocations_before = bt::AllocationsOnThisThread();

    for (size_t at = 0; at + 4800 <= audio.size(); at += 4800) {
        const auto t0 = Clock::now();
        CHECK_EQ(bs_rec_session_push(session, audio.data() + at, 4800, 1), BS_OK);
        slowest_us = std::max<int64_t>(
            slowest_us, std::chrono::duration_cast<std::chrono::microseconds>(Clock::now() - t0).count());
    }

    const long allocations = bt::AllocationsOnThisThread() - allocations_before;
    bs_rec_session_end_input(session);
    CHECK_EQ(bs_rec_session_push(session, audio.data(), 10, 1), BS_STATE);
    const auto events = Drain(session);
    CHECK_EQ(Finished(events).status, static_cast<int32_t>(BS_OK));

    const bs_rec_stats stats = Stats(session);
    std::printf("        slowest push %lld us, overrun %lld of %lld frames\n", static_cast<long long>(slowest_us),
                static_cast<long long>(stats.overrun_frames), static_cast<long long>(stats.input_frames));
    CHECK(stats.overrun_frames > 0);
    CHECK_EQ(stats.input_frames, static_cast<int64_t>(audio.size() / 4800 * 4800));
    CHECK(slowest_us < 20000);
    CHECK_EQ(allocations, 0L);
    bs_rec_session_free(session);
    bs_rec_engine_free(engine);
}

TEST(rec_push_rejects_bad_arguments) {
    bt::ToneRecognizer recognizer;
    recognizer.vocabulary = bt::ToneVocabulary(4);
    bs_rec_engine* engine = MakeEngine(&recognizer);
    bs_rec_session* session = Start(engine, Live(16000, 16000));
    const std::vector<float> block(160 * 9, 0.0f);
    CHECK_EQ(bs_rec_session_push(session, block.data(), 160, 0), BS_INVALID_ARG);
    CHECK_EQ(bs_rec_session_push(session, block.data(), 160, 9), BS_INVALID_ARG);
    CHECK_EQ(bs_rec_session_push(session, block.data(), -1, 1), BS_INVALID_ARG);
    CHECK_EQ(bs_rec_session_push(session, nullptr, 160, 1), BS_INVALID_ARG);
    CHECK_EQ(bs_rec_session_push(session, block.data(), 0, 1), BS_OK);
    CHECK_EQ(bs_rec_session_push(nullptr, block.data(), 160, 1), BS_INVALID_ARG);
    bs_rec_session_end_input(session);
    const auto events = Drain(session);
    CHECK_EQ(Finished(events).status, static_cast<int32_t>(BS_OK));
    CHECK(Segments(events).empty());
    bs_rec_session_free(session);
    bs_rec_engine_free(engine);
}

TEST(rec_cancel_is_prompt_and_discards_results) {
    bt::ToneRecognizer recognizer;
    recognizer.vocabulary = bt::ToneVocabulary(40);
    recognizer.partial_delay_ms = 5000;
    bs_rec_engine* engine = MakeEngine(&recognizer);

    bt::ToneSpeechOptions o;
    o.rate = 48000;
    const auto audio = bt::ToneSpeech({{1, 2, 3, 4, 5, 6, 7, 8}}, o);
    bs_rec_request r = Live(48000);
    r.partial_interval_ms = 100;
    bs_rec_session* session = Start(engine, r);
    CHECK_EQ(bs_rec_session_push(session, audio.data(), static_cast<int32_t>(audio.size()), 1), BS_OK);

    const auto deadline = Clock::now() + std::chrono::seconds(30);

    while (recognizer.partials.load() == 0) {
        CHECK(Clock::now() < deadline);
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }

    const auto t0 = Clock::now();
    bs_rec_session_cancel(session);
    int32_t status = 0;
    CHECK_EQ(bs_rec_session_wait(session, 10000, &status), BS_OK);
    const auto waited = std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now() - t0).count();
    CHECK_EQ(status, static_cast<int32_t>(BS_CANCELLED));
    CHECK(waited < 1000);

    const auto events = Drain(session);
    CHECK_EQ(events.size(), size_t{1});
    CHECK_EQ(Finished(events).status, static_cast<int32_t>(BS_CANCELLED));

    const bs_rec_stats stats = Stats(session);
    std::printf("        cancel latency %lld us\n", static_cast<long long>(stats.cancel_latency_us));
    CHECK(stats.cancel_latency_us >= 0 && stats.cancel_latency_us < 500000);
    CHECK(recognizer.cancels.load() >= 1);
    CHECK_EQ(bs_rec_session_push(session, audio.data(), 480, 1), BS_CANCELLED);

    std::vector<char> error(256);
    int32_t len = 0;
    CHECK_EQ(bs_rec_session_error(session, error.data(), 256, &len), BS_OK);
    CHECK(std::string(error.data(), static_cast<size_t>(len)).find("cancelled") != std::string::npos);
    bs_rec_session_free(session);

    bs_rec_session* again = Start(engine, Live(48000, 48000));
    bs_rec_session_cancel(again);
    CHECK_EQ(bs_rec_session_wait(again, 10000, &status), BS_OK);
    CHECK_EQ(status, static_cast<int32_t>(BS_CANCELLED));
    bs_rec_session_free(again);
    bs_rec_engine_free(engine);
}

TEST(rec_free_while_running_cancels_and_joins) {
    bt::ToneRecognizer recognizer;
    recognizer.vocabulary = bt::ToneVocabulary(40);
    recognizer.finalize_delay_ms = 5000;
    bs_rec_engine* engine = MakeEngine(&recognizer);
    bt::ToneSpeechOptions o;
    const auto audio = bt::ToneSpeech({{1, 2, 3}}, o);
    bs_rec_session* session = Start(engine, Live(16000));
    PushAll(session, audio, 1, 1600);

    const auto deadline = Clock::now() + std::chrono::seconds(30);

    while (recognizer.finalizes.load() == 0) {
        CHECK(Clock::now() < deadline);
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }

    const auto t0 = Clock::now();
    bs_rec_session_free(session);
    CHECK(std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now() - t0).count() < 1000);
    bs_rec_session* next = Start(engine, Live(16000, 16000));
    bs_rec_session_cancel(next);
    bs_rec_session_free(next);
    bs_rec_engine_free(engine);
}

TEST(rec_one_session_at_a_time_and_repeatable) {
    bt::ToneRecognizer recognizer;
    recognizer.vocabulary = bt::ToneVocabulary(40);
    bs_rec_engine* engine = MakeEngine(&recognizer);
    bt::ToneSpeechOptions o;
    o.rate = 48000;
    const auto audio = bt::ToneSpeech({{3, 1, 4, 1, 5}, {9, 2, 6}}, o);

    bs_rec_session* first = Start(engine, Live(48000));
    bs_rec_session* second = nullptr;
    const bs_rec_request r = Live(48000);
    CHECK_EQ(bs_rec_session_start(engine, &r, &second), BS_STATE);
    CHECK(second == nullptr);
    PushAll(first, audio, 1, 960);
    const auto reference = Segments(Drain(first));
    bs_rec_session_free(first);
    CHECK_EQ(reference.size(), size_t{2});

    for (int k = 0; k < 3; ++k) {
        bs_rec_session* session = Start(engine, Live(48000));
        PushAll(session, audio, 1, 480 + 333 * k);
        const auto events = Drain(session);
        CHECK_EQ(Finished(events).status, static_cast<int32_t>(BS_OK));
        const auto segments = Segments(events);
        CHECK_EQ(segments.size(), reference.size());

        for (size_t s = 0; s < segments.size(); ++s) {
            CHECK_EQ(segments[s], reference[s]);
        }

        bs_rec_session_free(session);
    }

    CHECK(recognizer.resets.load() >= 8);
    bs_rec_engine_free(engine);
}

TEST(rec_recognizer_failure_is_reported) {
    bt::ToneRecognizer recognizer;
    recognizer.vocabulary = bt::ToneVocabulary(40);
    recognizer.fail_on_finalize = 0;
    bs_rec_engine* engine = MakeEngine(&recognizer);
    const auto audio = bt::ToneSpeech({{1, 2}, {3, 4}});
    bs_rec_session* session = Start(engine, Live(16000));
    PushAll(session, audio, 1, 160);
    const auto events = Drain(session);
    const Event& done = Finished(events);
    CHECK_EQ(done.status, static_cast<int32_t>(BS_SOURCE));
    CHECK(done.text.find("stopped on purpose") != std::string::npos);
    CHECK(Segments(events).empty());

    int32_t status = 0;
    CHECK_EQ(bs_rec_session_wait(session, 0, &status), BS_OK);
    CHECK_EQ(status, static_cast<int32_t>(BS_SOURCE));
    bs_rec_session_free(session);

    recognizer.fail_on_finalize = -1;
    bs_rec_session* next = Start(engine, Live(16000));
    PushAll(next, audio, 1, 160);
    const auto ok = Drain(next);
    CHECK_EQ(Finished(ok).status, static_cast<int32_t>(BS_OK));
    CHECK_EQ(Segments(ok).size(), size_t{2});
    bs_rec_session_free(next);
    bs_rec_engine_free(engine);
}

TEST(rec_passes_context_bias_language_and_threads) {
    bs_lexicon* lexicon = nullptr;
    CHECK_EQ(bs_lexicon_create(64, &lexicon), BS_OK);
    const char* source = "GGUF llama.cpp loadModelAsync";
    CHECK_EQ(bs_lexicon_harvest(lexicon, source, std::strlen(source)), BS_OK);
    CHECK_EQ(bs_lexicon_freeze(lexicon), BS_OK);

    for (const uint32_t controls : {BS_REC_BIAS_CONTEXT | BS_REC_BIAS_LOGIT, BS_REC_BIAS_CONTEXT, 0u}) {
        bt::ToneRecognizer recognizer;
        recognizer.vocabulary = bt::ToneVocabulary(40);
        recognizer.bias_controls = controls;
        bs_rec_engine* engine = MakeEngine(&recognizer, lexicon);

        bs_rec_capabilities caps{};
        caps.struct_size = sizeof caps;
        CHECK_EQ(bs_rec_engine_capabilities(engine, &caps), BS_OK);
        CHECK_EQ(caps.lexicon_terms, 3);

        bs_rec_request r = Live(16000);
        r.language = "cs";
        r.bias_terms = 2;
        r.n_threads = 3;
        r.context_chars = 8;
        bs_rec_session* session = Start(engine, r);
        PushAll(session, bt::ToneSpeech({{1, 2, 3}, {4, 5}, {6}}), 1, 320);
        const auto events = Drain(session);
        CHECK_EQ(Finished(events).status, static_cast<int32_t>(BS_OK));
        bs_rec_session_free(session);

        std::lock_guard lock(recognizer.mutex);
        CHECK_EQ(recognizer.contexts.size(), size_t{3});
        CHECK_EQ(recognizer.contexts[0], std::string());

        if ((controls & BS_REC_BIAS_CONTEXT) != 0) {
            CHECK_EQ(recognizer.contexts[1], std::string("w1 w2 w3"));
            CHECK_EQ(recognizer.contexts[2], std::string("w3 w4 w5"));
        } else {
            CHECK_EQ(recognizer.contexts[1], std::string());
        }

        if ((controls & BS_REC_BIAS_LOGIT) != 0) {
            CHECK_EQ(recognizer.biases[0].size(), size_t{2});
            CHECK_EQ(recognizer.biases[0][0], std::string("GGUF"));
            CHECK_EQ(recognizer.biases[0][1], std::string("llama.cpp"));
        } else {
            CHECK(recognizer.biases[0].empty());
        }

        CHECK_EQ(recognizer.begun_languages[0], std::string("cs"));
        CHECK_EQ(recognizer.threads[0], 3);
        bs_rec_engine_free(engine);
    }

    bs_lexicon_free(lexicon);
}

TEST(rec_code_mode_dictates_finals) {
    bs_lexicon* lexicon = nullptr;
    CHECK_EQ(bs_lexicon_create(64, &lexicon), BS_OK);
    const char* source = "GGUF models load through loadModelAsync; use git commit --amend";
    CHECK_EQ(bs_lexicon_harvest(lexicon, source, std::strlen(source)), BS_OK);
    CHECK_EQ(bs_lexicon_freeze(lexicon), BS_OK);

    const std::vector<std::string> vocabulary = {"camel", "case", "load", "model", "async", "dash",
                                                 "amend", "g",    "u",    "f",     "the",   "gguf"};

    for (const int32_t mode : {BS_DICTATION_CODE, BS_DICTATION_PROSE}) {
        bt::ToneRecognizer recognizer;
        recognizer.vocabulary = vocabulary;
        bs_rec_engine* engine = MakeEngine(&recognizer, lexicon);
        bs_rec_request r = Live(16000);
        r.mode = mode;
        bs_rec_session* session = Start(engine, r);
        PushAll(session, bt::ToneSpeech({{0, 1, 2, 3, 4}, {5, 5, 6}, {7, 7, 8, 9}, {10, 11}}), 1, 160);
        const auto events = Drain(session);
        CHECK_EQ(Finished(events).status, static_cast<int32_t>(BS_OK));
        const auto segments = Segments(events);
        CHECK_EQ(segments.size(), size_t{4});

        if (mode == BS_DICTATION_CODE) {
            CHECK_EQ(segments[0], std::string("loadModelAsync"));
            CHECK_EQ(segments[1], std::string("--amend"));
            CHECK_EQ(segments[2], std::string("GGUF"));
            CHECK_EQ(segments[3], std::string("the GGUF"));
        } else {
            CHECK_EQ(segments[0], std::string("camel case load model async"));
            CHECK_EQ(segments[1], std::string("dash dash amend"));
            CHECK_EQ(segments[2], std::string("g g u f"));
            CHECK_EQ(segments[3], std::string("the GGUF"));
        }

        for (const Event& e : events) {
            if (e.kind != BS_REC_EVENT_FINAL || e.segment != 0) {
                continue;
            }

            CHECK_EQ(e.raw, std::string("camel case load model async"));

            if (mode == BS_DICTATION_CODE) {
                CHECK_EQ(e.subs.size(), size_t{1});
                CHECK(e.subs[0].applied);
                CHECK_EQ(e.subs[0].replacement, std::string("loadModelAsync"));
                CHECK_EQ(e.text.substr(e.subs[0].text_offset, e.subs[0].text_length), e.subs[0].replacement);
            } else {
                CHECK(e.subs.empty());
            }
        }

        bs_rec_session_free(session);
        bs_rec_engine_free(engine);
    }

    bt::ToneRecognizer unsure;
    unsure.vocabulary = vocabulary;
    unsure.confidence = std::vector<float>(vocabulary.size(), 0.95f);
    unsure.confidence[2] = 0.3f;
    bs_rec_engine* engine = MakeEngine(&unsure, lexicon);
    bs_rec_request r = Live(16000);
    r.mode = BS_DICTATION_CODE;
    bs_rec_session* session = Start(engine, r);
    PushAll(session, bt::ToneSpeech({{0, 1, 2, 3, 4}}), 1, 160);
    const auto events = Drain(session);
    const auto segments = Segments(events);
    CHECK_EQ(segments.size(), size_t{1});
    CHECK_EQ(segments[0], std::string("camel case load model async"));

    for (const Event& e : events) {
        if (e.kind == BS_REC_EVENT_FINAL) {
            CHECK_NEAR(e.min_confidence, 0.3, 1e-6);
            CHECK_EQ(e.subs.size(), size_t{1});
            CHECK(!e.subs[0].applied);
            CHECK_EQ(e.subs[0].replacement, std::string("loadModelAsync"));
        }
    }

    bs_rec_session_free(session);
    bs_rec_engine_free(engine);
    bs_lexicon_free(lexicon);
}

TEST(rec_regrows_buffers_when_the_model_asks) {
    bt::ToneRecognizer recognizer;
    recognizer.vocabulary = bt::ToneVocabulary(40);
    recognizer.demand_text_cap = 10000;
    bs_rec_engine* engine = MakeEngine(&recognizer);
    bs_rec_session* session = Start(engine, Live(16000));
    PushAll(session, bt::ToneSpeech({{7, 8, 9}}), 1, 160);
    const auto events = Drain(session);
    CHECK_EQ(Finished(events).status, static_cast<int32_t>(BS_OK));
    CHECK_EQ(Segments(events).front(), std::string("w7 w8 w9"));
    bs_rec_session_free(session);
    bs_rec_engine_free(engine);
}

TEST(rec_poll_and_stats_contract) {
    bt::ToneRecognizer recognizer;
    recognizer.vocabulary = bt::ToneVocabulary(40);
    bs_rec_engine* engine = MakeEngine(&recognizer);
    bs_rec_session* session = Start(engine, Live(16000));

    const bs_rec_stats running = Stats(session);
    CHECK_EQ(running.finished, 0);
    CHECK_EQ(running.speech_end_to_final_median_us, int64_t{-1});

    bs_rec_event small{};
    small.struct_size = 8;
    CHECK_EQ(bs_rec_session_poll(session, &small), BS_INVALID_ARG);

    bs_rec_stats tiny{};
    tiny.struct_size = 8;
    CHECK_EQ(bs_rec_session_stats(session, &tiny), BS_INVALID_ARG);

    int32_t status = 0;
    CHECK_EQ(bs_rec_session_wait(session, 0, &status), BS_TIMEOUT);

    PushAll(session, bt::ToneSpeech({{1}}), 1, 160);
    const auto events = Drain(session);
    CHECK_EQ(Finished(events).status, static_cast<int32_t>(BS_OK));

    for (int k = 0; k < 3; ++k) {
        Event again;
        CHECK(PollOne(session, &again));
        CHECK_EQ(again.kind, static_cast<int32_t>(BS_REC_EVENT_FINISHED));
        CHECK_EQ(again.status, static_cast<int32_t>(BS_OK));
    }

    CHECK_EQ(bs_rec_session_wait(session, -1, &status), BS_OK);
    CHECK_EQ(Stats(session).finished, 1);

    bs_substitution s{};
    s.struct_size = sizeof s;
    CHECK_EQ(bs_rec_session_substitution(session, 0, &s), BS_INVALID_ARG);
    bs_rec_session_free(session);
    bs_rec_engine_free(engine);
}

TEST(rec_file_mode_memory_is_bounded) {
    bt::ToneRecognizer recognizer;
    recognizer.vocabulary = bt::ToneVocabulary(40);
    bs_rec_engine* engine = MakeEngine(&recognizer);
    std::vector<long> growth;

    for (const size_t words : {size_t{45}, size_t{818}}) {
        const std::string path = Fresh("rec_bounded.wav");
        std::vector<std::vector<size_t>> utterances;

        for (size_t k = 0; k < words; k += 9) {
            std::vector<size_t> u;

            for (size_t j = k; j < std::min(words, k + 9); ++j) {
                u.push_back(j % 40);
            }

            utterances.push_back(u);
        }

        int64_t frames = 0;
        WriteToneFile(path, utterances, &frames);
        bs_rec_request r = File(path);
        r.window_ms = 8000;
        const long before = ResidentKb();
        bs_rec_session* session = Start(engine, r);
        const auto events = Drain(session);
        const long after = ResidentKb();
        CHECK_EQ(Finished(events).status, static_cast<int32_t>(BS_OK));
        const auto segments = Segments(events);
        CHECK_EQ(segments.size(), utterances.size());
        CHECK_EQ(segments.back(), Spoken(recognizer.vocabulary, utterances.back()));

        const bs_rec_stats stats = Stats(session);
        CHECK_EQ(stats.input_frames, frames);
        CHECK(stats.peak_window_samples <= 8000 * 16 + 160);
        growth.push_back(after - before);
        std::printf("        %6.1f s file, %zu segments, resident growth %ld KiB, engine %.4f s per audio second\n",
                    static_cast<double>(frames) / 16000.0, segments.size(), after - before,
                    (static_cast<double>(stats.t_finished_us) / 1e6) / (static_cast<double>(frames) / 16000.0));
        bs_rec_session_free(session);
    }

#if defined(__linux__) && !defined(__SANITIZE_ADDRESS__) && !defined(__SANITIZE_THREAD__)
    CHECK(growth[1] - growth[0] < 2 * 1024);
#endif
    bs_rec_engine_free(engine);
}

TEST(rec_corrupt_files_fail_cleanly) {
    bt::ToneRecognizer recognizer;
    recognizer.vocabulary = bt::ToneVocabulary(40);
    bs_rec_engine* engine = MakeEngine(&recognizer);
    const std::string path = Fresh("rec_corrupt.wav");
    bt::WriteWav(path, 16000, 1, 16, false, false, bt::ToneSpeech({{1, 2}}), false);
    auto bytes = bt::ReadBytes(path);
    bytes.resize(bytes.size() / 2);
    bt::WriteBytes(path, bytes);

    const bs_rec_request r = File(path);
    bs_rec_session* session = nullptr;
    CHECK_EQ(bs_rec_session_start(engine, &r, &session), BS_ASSET);
    CHECK(session == nullptr);
    CHECK(std::string(bs_last_error()).find(path) != std::string::npos);

    bt::WriteBytes(path, std::vector<uint8_t>(64, 0x41));
    CHECK_EQ(bs_rec_session_start(engine, &r, &session), BS_ASSET);

    bt::WriteWav(path, 16000, 1, 16, false, false, bt::ToneSpeech({{1, 2}}), false);
    session = Start(engine, r);
    CHECK_EQ(Finished(Drain(session)).status, static_cast<int32_t>(BS_OK));
    bs_rec_session_free(session);
    bs_rec_engine_free(engine);
}

TEST(rec_capture_backend_reports_its_platform) {
    int32_t count = -1;
    CHECK_EQ(bs_capture_device_count(&count), BS_OK);
    bs_capture* capture = nullptr;
    bs_rec_engine* engine = MakeEngine(nullptr);
    bs_rec_capabilities caps{};
    caps.struct_size = sizeof caps;
    CHECK_EQ(bs_rec_engine_capabilities(engine, &caps), BS_OK);
#if defined(_WIN32)
    CHECK_EQ(caps.capture, 1);
    CHECK(count >= 0);
#else
    CHECK_EQ(caps.capture, 0);
    CHECK_EQ(count, 0);
    CHECK_EQ(bs_capture_open(nullptr, &capture), BS_UNAVAILABLE);
    CHECK(capture == nullptr);
    CHECK(std::string(bs_last_error()).find("WASAPI") != std::string::npos);
    char id[8];
    char name[8];
    int32_t id_len = 0;
    int32_t name_len = 0;
    CHECK_EQ(bs_capture_device(0, id, 8, &id_len, name, 8, &name_len), BS_UNAVAILABLE);
#endif
    CHECK_EQ(bs_capture_start(nullptr, nullptr), BS_INVALID_ARG);
    bs_capture_close(nullptr);
    bs_rec_engine_free(engine);
}

TEST(rec_abi_versions_and_layouts) {
    CHECK_EQ(bs_stt_abi_version(), BS_STT_ABI_VERSION);
    CHECK_EQ(sizeof(bs_rec_capabilities), size_t{48});
    CHECK_EQ(sizeof(bs_rec_stats), size_t{160});
    CHECK_EQ(sizeof(bs_vad_settings), size_t{24});
    CHECK_EQ(sizeof(bs_rec_caps), size_t{24});
    CHECK_EQ(sizeof(bs_rec_word), size_t{32});

    if (sizeof(void*) == 8) {
        CHECK_EQ(sizeof(bs_substitution), size_t{64});
        CHECK_EQ(sizeof(bs_rec_engine_config), size_t{24});
        CHECK_EQ(sizeof(bs_rec_request), size_t{88});
        CHECK_EQ(sizeof(bs_rec_event), size_t{96});
        CHECK_EQ(sizeof(bs_rec_window_options), size_t{40});
        CHECK_EQ(sizeof(bs_recognizer_v1), size_t{88});
    }
}
