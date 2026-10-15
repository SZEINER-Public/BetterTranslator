#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <string>
#include <thread>
#include <vector>

#include "../tests/tone_recognizer.h"
#include "better_speech_stt.h"
#include "lexicon.h"

namespace {

using Clock = std::chrono::steady_clock;

double Us(Clock::duration d) { return std::chrono::duration<double, std::micro>(d).count(); }

struct Summary {
    double median = 0;
    double p90 = 0;
};

Summary Summarize(std::vector<double> v) {
    std::sort(v.begin(), v.end());
    return {v[v.size() / 2], v[std::min(v.size() - 1, v.size() * 9 / 10)]};
}

std::string Identifier(size_t k) {
    static const char* const parts[] = {"load", "model", "token", "cache", "stream", "voice", "batch", "queue",
                                        "speech", "window", "index", "graph", "kernel", "buffer", "sample", "event"};
    std::string a = parts[k % 16];
    std::string b = parts[(k / 16) % 16];
    std::string c = parts[(k / 256) % 16];
    b[0] = static_cast<char>(b[0] - 32);
    c[0] = static_cast<char>(c[0] - 32);
    return a + b + c + std::to_string(k / 4096);
}

void BenchLexicon() {
    std::printf("lexicon\n");

    for (const size_t n : {size_t{1000}, size_t{8000}, size_t{32000}}) {
        std::string corpus;

        for (size_t k = 0; k < n; ++k) {
            corpus += Identifier(k);
            corpus += k % 3 == 0 ? " uses --dry-run with llama.cpp and GGUF.\n" : " ";
        }

        bspeech::Lexicon lexicon(n + 16);
        const auto t0 = Clock::now();
        lexicon.Harvest(corpus);
        lexicon.Freeze();
        const double build_ms = Us(Clock::now() - t0) / 1000.0;

        std::vector<double> per;
        const std::vector<std::string> phrases = {"call load model token zero now", "use dash dash dry run here",
                                                  "camel case max new tokens", "open the G G U F file",
                                                  "llama dot cpp runs speech window index one"};

        for (int round = 0; round < 2000; ++round) {
            const auto words = bspeech::WordsOf(phrases[static_cast<size_t>(round) % phrases.size()]);
            const auto t1 = Clock::now();
            const auto d = bspeech::Dictate(words, bspeech::DictationMode::Code, &lexicon, 0.6f);
            per.push_back(Us(Clock::now() - t1));

            if (d.text.empty()) {
                std::printf("empty\n");
            }
        }

        const Summary s = Summarize(per);
        std::printf("  %6zu terms  build %8.1f ms   code-mode dictation per phrase  median %6.1f us  p90 %6.1f us\n",
                    lexicon.Terms().size(), build_ms, s.median, s.p90);
    }
}

void BenchPipeline() {
    std::printf("pipeline (tone recognizer: engine overhead = wall minus model calls)\n");

    for (const int32_t rate : {16000, 48000}) {
        bt::ToneRecognizer recognizer;
        recognizer.vocabulary = bt::ToneVocabulary(40);
        bs_rec_engine_config config{};
        config.struct_size = sizeof config;
        config.recognizer = &recognizer.table;
        bs_rec_engine* engine = nullptr;

        if (bs_rec_engine_create(&config, &engine) != BS_OK) {
            std::printf("engine: %s\n", bs_last_error());
            return;
        }

        bs_rec_request r{};
        r.struct_size = sizeof r;
        bs_rec_request_defaults(&r);
        r.language = "en";
        r.input_sample_rate = rate;
        r.input_ring_samples = rate * 30;
        r.window_ms = 8000;
        bs_rec_session* session = nullptr;

        if (bs_rec_session_start(engine, &r, &session) != BS_OK) {
            std::printf("session: %s\n", bs_last_error());
            bs_rec_engine_free(engine);
            return;
        }

        bt::ToneSpeechOptions o;
        o.rate = rate;
        o.channels = 2;
        int64_t frames = 0;
        size_t events = 0;
        const auto t0 = Clock::now();

        for (int u = 0; u < 120; ++u) {
            std::vector<size_t> words;

            for (size_t k = 0; k < 9; ++k) {
                words.push_back((static_cast<size_t>(u) * 9 + k) % 40);
            }

            o.lead_ms = u == 0 ? 500 : 0;
            const auto audio = bt::ToneSpeech({words}, o);
            const auto n = static_cast<int32_t>(audio.size() / 2);

            while (true) {
                bs_rec_stats st{};
                st.struct_size = sizeof st;
                bs_rec_session_stats(session, &st);

                if (frames - st.model_samples * rate / 16000 < rate * 20) {
                    break;
                }

                std::this_thread::sleep_for(std::chrono::milliseconds(1));
            }

            bs_rec_session_push(session, audio.data(), n, 2);
            frames += n;
            bs_rec_event e{};
            e.struct_size = sizeof e;

            while (bs_rec_session_poll(session, &e) == BS_OK && e.kind != BS_REC_EVENT_NONE) {
                ++events;
            }
        }

        bs_rec_session_end_input(session);
        int32_t status = 0;
        bs_rec_session_wait(session, -1, &status);
        const double wall_us = Us(Clock::now() - t0);
        bs_rec_stats st{};
        st.struct_size = sizeof st;
        bs_rec_session_stats(session, &st);
        const double audio_s = static_cast<double>(frames) / rate;
        const double model_us = static_cast<double>(st.feed_us + st.partial_us + st.finalize_us);
        std::printf("  %5d Hz stereo  %6.1f s audio  %3d segments  overrun %lld  engine %7.3f ms per audio second  "
                    "speech end to final after hangover median %6.2f ms p90 %6.2f ms  events %zu  status %d\n",
                    rate, audio_s, st.segments, static_cast<long long>(st.overrun_frames),
                    (wall_us - model_us) / 1000.0 / audio_s, st.speech_end_to_final_median_us / 1000.0,
                    st.speech_end_to_final_p90_us / 1000.0, events, status);
        bs_rec_session_free(session);
        bs_rec_engine_free(engine);
    }
}

void BenchCancel() {
    std::printf("cancel (a model call that would take 5 s)\n");
    std::vector<double> latencies;

    for (int round = 0; round < 10; ++round) {
        bt::ToneRecognizer recognizer;
        recognizer.vocabulary = bt::ToneVocabulary(40);
        recognizer.finalize_delay_ms = 5000;
        bs_rec_engine_config config{};
        config.struct_size = sizeof config;
        config.recognizer = &recognizer.table;
        bs_rec_engine* engine = nullptr;
        bs_rec_engine_create(&config, &engine);
        bs_rec_request r{};
        r.struct_size = sizeof r;
        bs_rec_request_defaults(&r);
        r.language = "en";
        r.input_sample_rate = 16000;
        bs_rec_session* session = nullptr;
        bs_rec_session_start(engine, &r, &session);
        const auto audio = bt::ToneSpeech({{1, 2, 3}});
        bs_rec_session_push(session, audio.data(), static_cast<int32_t>(audio.size()), 1);

        while (recognizer.finalizes.load() == 0) {
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
        }

        bs_rec_session_cancel(session);
        int32_t status = 0;
        bs_rec_session_wait(session, -1, &status);
        bs_rec_stats st{};
        st.struct_size = sizeof st;
        bs_rec_session_stats(session, &st);
        latencies.push_back(static_cast<double>(st.cancel_latency_us));
        bs_rec_session_free(session);
        bs_rec_engine_free(engine);
    }

    const Summary s = Summarize(latencies);
    std::printf("  cancel to finished  median %.2f ms  p90 %.2f ms\n", s.median / 1000.0, s.p90 / 1000.0);
}

}

int main(int argc, char** argv) {
    const std::string only = argc > 1 ? argv[1] : "";

    if (only.empty() || only == "lexicon") {
        BenchLexicon();
    }

    if (only.empty() || only == "pipeline") {
        BenchPipeline();
    }

    if (only.empty() || only == "cancel") {
        BenchCancel();
    }

    return 0;
}
