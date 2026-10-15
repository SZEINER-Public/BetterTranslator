#pragma once

// A deterministic stand-in for an acoustic model, compiled into the test runner and
// nowhere else. It produces tones, not speech: each chunk is a sine whose frequency
// encodes the chunk's position, so ordering, gaps and loss are visible in the
// output. It exists to exercise the engine's contract with a source, never to stand
// in for synthesis.

#include <atomic>
#include <chrono>
#include <cmath>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "better_speech.h"

namespace bt {

struct ToneSource {
    // Configuration.
    int32_t sample_rate = 24000;
    int32_t max_units = 0;
    uint32_t native_events = 0;
    uint32_t native_controls = 0;
    std::vector<std::string> voices;
    int32_t window = 960;              // samples per pull, 40 ms at 24 kHz
    int32_t samples_per_unit = 2400;   // 100 ms per word
    double pace = 0.0;                 // wall time per audio second; 0 = as fast as possible
    int begin_delay_ms = 0;            // stands in for prefill before the first sample of a chunk
    int fail_at_chunk = -1;
    bool fail_measure = false;
    float amplitude = 0.5f;

    // Observed.
    std::mutex mutex;
    std::vector<std::string> begun;
    std::vector<int32_t> events_begun;
    std::vector<std::string> languages;
    std::vector<std::string> voice_ids;
    std::vector<int32_t> threads;
    std::atomic<int> resets{0};
    std::atomic<int> cancels{0};

    // Per chunk.
    std::atomic<bool> cancelled{false};
    int chunk = -1;
    int64_t remaining = 0;
    int64_t phase = 0;
    double frequency = 0;
    std::string error;

    bs_source_v1 table{};

    static double FrequencyFor(int chunk_index) { return 300.0 + 150.0 * (chunk_index % 6); }

    static int32_t Words(const char* text, size_t len) {
        int32_t words = 0;
        bool in_word = false;

        for (size_t i = 0; i < len; ++i) {
            const bool space = text[i] == ' ' || text[i] == '\n' || text[i] == '\t';

            if (!space && !in_word) {
                ++words;
            }

            in_word = !space;
        }

        return words;
    }

    ToneSource() {
        table.abi_version = BS_SOURCE_ABI_VERSION;
        table.struct_size = sizeof(bs_source_v1);
        table.self = this;

        table.get_caps = [](void* self, bs_source_caps* out) -> int32_t {
            auto* s = static_cast<ToneSource*>(self);
            *out = bs_source_caps{};
            out->sample_rate = s->sample_rate;
            out->channels = 1;
            out->max_units_per_chunk = s->max_units;
            out->native_events = s->native_events;
            out->native_controls = s->native_controls;
            out->voice_count = static_cast<int32_t>(s->voices.size());
            out->min_unit_samples = s->window;
            out->streaming = 1;
            return BS_SOURCE_OK;
        };

        table.voice_id = [](void* self, int32_t index, char* buf, int32_t cap, int32_t* out_len) -> int32_t {
            auto* s = static_cast<ToneSource*>(self);
            const std::string& id = s->voices.at(static_cast<size_t>(index));
            *out_len = static_cast<int32_t>(id.size());

            if (cap < static_cast<int32_t>(id.size())) {
                return BS_SOURCE_FAILED;
            }

            std::copy(id.begin(), id.end(), buf);
            return BS_SOURCE_OK;
        };

        table.count_units = [](void* self, const char* text, size_t len, int32_t* out) -> int32_t {
            auto* s = static_cast<ToneSource*>(self);

            if (s->fail_measure) {
                s->error = "the test tokenizer was told to fail";
                return BS_SOURCE_FAILED;
            }

            *out = Words(text, len);
            return BS_SOURCE_OK;
        };

        table.begin = [](void* self, const char* text, size_t len, const char* language, const bs_source_voice* voice,
                         int32_t n_threads) -> int32_t {
            auto* s = static_cast<ToneSource*>(self);

            if (s->cancelled.load()) {
                return BS_SOURCE_CANCELLED;
            }

            for (int waited = 0; waited < s->begin_delay_ms; ++waited) {
                if (s->cancelled.load()) {
                    return BS_SOURCE_CANCELLED;
                }

                std::this_thread::sleep_for(std::chrono::milliseconds(1));
            }

            std::lock_guard lock(s->mutex);
            s->begun.emplace_back(text, len);
            s->languages.emplace_back(language);
            s->voice_ids.emplace_back(voice->voice_id != nullptr ? voice->voice_id : "");
            s->threads.push_back(n_threads);
            ++s->chunk;

            if (s->chunk == s->fail_at_chunk) {
                s->error = "the test source was told to fail at this chunk";
                return BS_SOURCE_FAILED;
            }

            s->remaining = static_cast<int64_t>(Words(text, len)) * s->samples_per_unit;
            s->phase = 0;
            s->frequency = FrequencyFor(s->chunk);
            return BS_SOURCE_OK;
        };

        table.begin_event = [](void* self, int32_t kind) -> int32_t {
            auto* s = static_cast<ToneSource*>(self);
            std::lock_guard lock(s->mutex);
            s->events_begun.push_back(kind);
            s->remaining = s->sample_rate / 2;
            s->phase = 0;
            s->frequency = 2500.0;
            return BS_SOURCE_OK;
        };

        table.pull = [](void* self, float* out, int32_t cap, int32_t* out_n, int32_t* out_done) -> int32_t {
            auto* s = static_cast<ToneSource*>(self);
            *out_n = 0;
            *out_done = 0;

            if (s->cancelled.load()) {
                return BS_SOURCE_CANCELLED;
            }

            const auto n = static_cast<int32_t>(std::min<int64_t>({cap, s->window, s->remaining}));

            if (s->pace > 0.0) {
                const double seconds = s->pace * n / s->sample_rate;
                const auto until =
                    std::chrono::steady_clock::now() + std::chrono::duration<double>(seconds);

                // Sleeps in slices so a cancel is seen within a millisecond, the way a
                // model checks between frames.
                while (std::chrono::steady_clock::now() < until) {
                    if (s->cancelled.load()) {
                        return BS_SOURCE_CANCELLED;
                    }

                    std::this_thread::sleep_for(std::chrono::milliseconds(1));
                }
            }

            for (int32_t i = 0; i < n; ++i) {
                out[i] = s->amplitude *
                         static_cast<float>(std::sin(2.0 * 3.14159265358979323846 * s->frequency *
                                                     static_cast<double>(s->phase + i) / s->sample_rate));
            }

            s->phase += n;
            s->remaining -= n;
            *out_n = n;
            *out_done = s->remaining <= 0 ? 1 : 0;
            return BS_SOURCE_OK;
        };

        table.cancel = [](void* self) {
            auto* s = static_cast<ToneSource*>(self);
            s->cancelled.store(true);
            s->cancels.fetch_add(1);
        };

        table.reset = [](void* self) {
            auto* s = static_cast<ToneSource*>(self);
            s->cancelled.store(false);
            s->resets.fetch_add(1);
            s->chunk = -1;
            s->remaining = 0;
        };

        table.last_error = [](void* self) -> const char* { return static_cast<ToneSource*>(self)->error.c_str(); };
    }

    ToneSource(const ToneSource&) = delete;
    ToneSource& operator=(const ToneSource&) = delete;
};

}  // namespace bt
