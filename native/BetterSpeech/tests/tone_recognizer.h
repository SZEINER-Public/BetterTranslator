#pragma once

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "better_speech_recognizer.h"

namespace bt {

inline double ToneFrequency(size_t index) { return 300.0 + 70.0 * static_cast<double>(index); }

struct ToneRecognizer {
    int32_t sample_rate = 16000;
    int32_t max_window_samples = 16000 * 30;
    int32_t word_timestamps = 1;
    uint32_t bias_controls = BS_REC_BIAS_CONTEXT | BS_REC_BIAS_LOGIT;
    std::vector<std::string> languages{"en", "cs"};
    std::vector<std::string> vocabulary;
    std::vector<float> confidence;
    int partial_delay_ms = 0;
    int finalize_delay_ms = 0;
    int fail_on_finalize = -1;
    bool fail_caps = false;
    bool flip_last_partial = false;
    int32_t demand_text_cap = 0;

    std::mutex mutex;
    std::vector<std::string> contexts;
    std::vector<std::vector<std::string>> biases;
    std::vector<std::string> begun_languages;
    std::vector<int32_t> threads;
    int64_t max_fed = 0;
    int64_t total_fed = 0;
    std::atomic<int> resets{0};
    std::atomic<int> cancels{0};
    std::atomic<int> finalizes{0};
    std::atomic<int> partials{0};

    std::atomic<bool> cancelled{false};
    std::vector<float> window;
    std::string error;
    bs_recognizer_v1 table{};

    struct Word {
        size_t index;
        int64_t start;
        int64_t end;
    };

    ToneRecognizer() {
        table.abi_version = BS_RECOGNIZER_ABI_VERSION;
        table.struct_size = sizeof(bs_recognizer_v1);
        table.self = this;
        table.get_caps = [](void* self, bs_rec_caps* out) { return static_cast<ToneRecognizer*>(self)->Caps(out); };
        table.language = [](void* self, int32_t index, char* buf, int32_t cap, int32_t* out_len) {
            return static_cast<ToneRecognizer*>(self)->Language(index, buf, cap, out_len);
        };
        table.begin = [](void* self, const bs_rec_window_options* options) {
            return static_cast<ToneRecognizer*>(self)->Begin(options);
        };
        table.feed = [](void* self, const float* samples, int32_t count) {
            return static_cast<ToneRecognizer*>(self)->Feed(samples, count);
        };
        table.partial = [](void* self, char* text, int32_t text_cap, int32_t* out_text_len, bs_rec_word* words,
                           int32_t words_cap, int32_t* out_words) {
            return static_cast<ToneRecognizer*>(self)->Decode(false, text, text_cap, out_text_len, words, words_cap,
                                                              out_words);
        };
        table.finalize = [](void* self, char* text, int32_t text_cap, int32_t* out_text_len, bs_rec_word* words,
                            int32_t words_cap, int32_t* out_words) {
            return static_cast<ToneRecognizer*>(self)->Decode(true, text, text_cap, out_text_len, words, words_cap,
                                                              out_words);
        };
        table.cancel = [](void* self) {
            auto* r = static_cast<ToneRecognizer*>(self);
            r->cancelled.store(true, std::memory_order_release);
            r->cancels.fetch_add(1, std::memory_order_relaxed);
        };
        table.reset = [](void* self) {
            auto* r = static_cast<ToneRecognizer*>(self);
            r->window.clear();
            r->cancelled.store(false, std::memory_order_release);
            r->resets.fetch_add(1, std::memory_order_relaxed);
        };
        table.last_error = [](void* self) { return static_cast<ToneRecognizer*>(self)->error.c_str(); };
    }

    ToneRecognizer(const ToneRecognizer&) = delete;
    ToneRecognizer& operator=(const ToneRecognizer&) = delete;

    int32_t Caps(bs_rec_caps* out) {
        if (fail_caps) {
            error = "the tone model has no capabilities";
            return BS_REC_FAILED;
        }

        out->sample_rate = sample_rate;
        out->max_window_samples = max_window_samples;
        out->streaming = 0;
        out->word_timestamps = word_timestamps;
        out->bias_controls = bias_controls;
        out->language_count = static_cast<int32_t>(languages.size());
        return BS_REC_OK;
    }

    int32_t Language(int32_t index, char* buf, int32_t cap, int32_t* out_len) {
        const std::string& code = languages.at(static_cast<size_t>(index));
        *out_len = static_cast<int32_t>(code.size());

        if (cap < static_cast<int32_t>(code.size())) {
            return BS_REC_BUFFER_TOO_SMALL;
        }

        std::memcpy(buf, code.data(), code.size());
        return BS_REC_OK;
    }

    int32_t Begin(const bs_rec_window_options* options) {
        std::lock_guard lock(mutex);
        window.clear();
        contexts.emplace_back(options->context != nullptr ? std::string(options->context, options->context_len)
                                                          : std::string());
        std::vector<std::string> bias;

        for (int32_t k = 0; k < options->bias_term_count; ++k) {
            bias.emplace_back(options->bias_terms[k]);
        }

        biases.push_back(std::move(bias));
        begun_languages.emplace_back(options->language != nullptr ? options->language : "");
        threads.push_back(options->n_threads);
        return BS_REC_OK;
    }

    int32_t Feed(const float* samples, int32_t count) {
        if (cancelled.load(std::memory_order_acquire)) {
            return BS_REC_CANCELLED;
        }

        if (static_cast<int64_t>(window.size()) + count > max_window_samples) {
            error = "the window is longer than the model accepts";
            return BS_REC_FAILED;
        }

        window.insert(window.end(), samples, samples + count);
        std::lock_guard lock(mutex);
        max_fed = std::max<int64_t>(max_fed, static_cast<int64_t>(window.size()));
        total_fed += count;
        return BS_REC_OK;
    }

    bool Pause(int ms) {
        const auto until = std::chrono::steady_clock::now() + std::chrono::milliseconds(ms);

        while (std::chrono::steady_clock::now() < until) {
            if (cancelled.load(std::memory_order_acquire)) {
                return false;
            }

            std::this_thread::sleep_for(std::chrono::milliseconds(1));
        }

        return !cancelled.load(std::memory_order_acquire);
    }

    std::vector<Word> Detect() const {
        const size_t frame = static_cast<size_t>(sample_rate / 100);
        const size_t frames = window.size() / frame;
        std::vector<bool> voiced(frames);

        for (size_t f = 0; f < frames; ++f) {
            double acc = 0.0;

            for (size_t k = 0; k < frame; ++k) {
                const double s = window[f * frame + k];
                acc += s * s;
            }

            voiced[f] = 10.0 * std::log10(acc / static_cast<double>(frame) + 1e-12) > -30.0;
        }

        std::vector<Word> words;
        size_t f = 0;

        while (f < frames) {
            if (!voiced[f]) {
                ++f;
                continue;
            }

            const size_t begin = f;

            while (f < frames && voiced[f]) {
                ++f;
            }

            if (begin == 0 || f == frames || f - begin < 8) {
                continue;
            }

            words.push_back({Classify((begin + 1) * frame, (f - 1) * frame), static_cast<int64_t>(begin * frame),
                             static_cast<int64_t>(f * frame)});
        }

        return words;
    }

    size_t Classify(size_t from, size_t to) const {
        size_t best = 0;
        double best_power = -1.0;

        for (size_t v = 0; v < vocabulary.size(); ++v) {
            const double w = 2.0 * 3.14159265358979323846 * ToneFrequency(v) / sample_rate;
            const double coeff = 2.0 * std::cos(w);
            double s1 = 0.0;
            double s2 = 0.0;

            for (size_t i = from; i < to; ++i) {
                const double s0 = window[i] + coeff * s1 - s2;
                s2 = s1;
                s1 = s0;
            }

            const double power = s1 * s1 + s2 * s2 - coeff * s1 * s2;

            if (power > best_power) {
                best_power = power;
                best = v;
            }
        }

        return best;
    }

    int32_t Decode(bool final, char* text, int32_t text_cap, int32_t* out_text_len, bs_rec_word* out_words,
                   int32_t words_cap, int32_t* out_words_count) {
        const int call = final ? finalizes.fetch_add(1, std::memory_order_relaxed)
                               : partials.fetch_add(1, std::memory_order_relaxed);

        if (!Pause(final ? finalize_delay_ms : partial_delay_ms)) {
            return BS_REC_CANCELLED;
        }

        if (final && call == fail_on_finalize) {
            error = "the tone model stopped on purpose";
            return BS_REC_FAILED;
        }

        std::vector<Word> words = Detect();

        if (!final && flip_last_partial && (call % 2) == 1 && !words.empty()) {
            words.back().index = (words.back().index + 1) % vocabulary.size();
        }

        std::string joined;
        std::vector<bs_rec_word> spans;

        for (const Word& w : words) {
            if (!joined.empty()) {
                joined.push_back(' ');
            }

            bs_rec_word span{};
            span.text_offset = static_cast<int32_t>(joined.size());
            span.text_length = static_cast<int32_t>(vocabulary[w.index].size());
            span.start_sample = w.start;
            span.end_sample = w.end;
            span.confidence = w.index < confidence.size() ? confidence[w.index] : 0.95f;
            joined += vocabulary[w.index];
            spans.push_back(span);
        }

        *out_text_len = std::max(static_cast<int32_t>(joined.size()), demand_text_cap);
        *out_words_count = static_cast<int32_t>(spans.size());

        if (text_cap < *out_text_len || words_cap < *out_words_count) {
            return BS_REC_BUFFER_TOO_SMALL;
        }

        *out_text_len = static_cast<int32_t>(joined.size());
        std::memcpy(text, joined.data(), joined.size());
        std::copy(spans.begin(), spans.end(), out_words);
        return BS_REC_OK;
    }
};

struct ToneSpeechOptions {
    int32_t rate = 16000;
    int32_t channels = 1;
    int word_ms = 160;
    int gap_ms = 60;
    int pause_ms = 900;
    int lead_ms = 500;
    float amplitude = 0.3f;
    float noise = 3e-4f;
};

inline std::vector<float> ToneSpeech(const std::vector<std::vector<size_t>>& utterances,
                                     const ToneSpeechOptions& o = {}) {
    std::vector<float> mono;
    const auto ms = [&](int v) { return static_cast<size_t>(static_cast<int64_t>(v) * o.rate / 1000); };
    const size_t ramp = ms(5);
    uint32_t seed = 12345u;

    const auto silence = [&](size_t n) { mono.insert(mono.end(), n, 0.0f); };

    silence(ms(o.lead_ms));

    for (size_t u = 0; u < utterances.size(); ++u) {
        for (size_t k = 0; k < utterances[u].size(); ++k) {
            const double step = 2.0 * 3.14159265358979323846 * ToneFrequency(utterances[u][k]) / o.rate;
            const size_t n = ms(o.word_ms);

            for (size_t i = 0; i < n; ++i) {
                double gain = 1.0;

                if (i < ramp) {
                    gain = 0.5 - 0.5 * std::cos(3.14159265358979323846 * static_cast<double>(i) / ramp);
                } else if (i + ramp > n) {
                    gain = 0.5 - 0.5 * std::cos(3.14159265358979323846 * static_cast<double>(n - i) / ramp);
                }

                mono.push_back(static_cast<float>(o.amplitude * gain * std::sin(step * static_cast<double>(i))));
            }

            silence(ms(k + 1 < utterances[u].size() ? o.gap_ms : o.pause_ms));
        }
    }

    for (float& s : mono) {
        seed = seed * 1664525u + 1013904223u;
        s += o.noise * (static_cast<float>(seed >> 8) / 8388608.0f - 1.0f);
    }

    if (o.channels == 1) {
        return mono;
    }

    std::vector<float> interleaved;
    interleaved.reserve(mono.size() * static_cast<size_t>(o.channels));

    for (const float s : mono) {
        for (int32_t c = 0; c < o.channels; ++c) {
            interleaved.push_back(c == 0 ? s : s * 0.8f);
        }
    }

    return interleaved;
}

inline std::vector<std::string> ToneVocabulary(size_t n) {
    std::vector<std::string> words;

    for (size_t k = 0; k < n; ++k) {
        words.push_back("w" + std::to_string(k));
    }

    return words;
}

}
