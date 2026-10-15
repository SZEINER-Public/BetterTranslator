// Measures what BetterSpeech itself costs, apart from any model.
//
// Three groups:
//   dsp       the voice chain, limiter and fader per second of 24 kHz audio
//   pipeline  a whole job against a source that returns instantly, so what is
//             left is the engine's own overhead: planning, DSP, buffering, export
//   pacing    a SIMULATION. A tone source sleeps to emulate a model's prefill and
//             real-time factor while a reader consumes at real time. It shows how
//             prebuffer trades start delay against underruns; it says nothing
//             about how fast any real model is.

#include <algorithm>
#include <chrono>
#include <filesystem>
#include <cmath>
#include <cstdio>
#include <random>
#include <string>
#include <thread>
#include <vector>

#include "../tests/tone_source.h"
#include "better_speech.h"
#include "dsp.h"

namespace {

using Clock = std::chrono::steady_clock;
constexpr int kRate = 24000;
constexpr double kPi = 3.14159265358979323846;

// A voiced, speech-shaped test signal: a gliding fundamental with decaying
// harmonics, amplitude-modulated at a syllable rate, plus a little noise.
std::vector<float> SpeechLike(double seconds) {
    std::mt19937 rng(1);
    std::normal_distribution<float> noise(0.0f, 0.01f);
    const auto n = static_cast<size_t>(seconds * kRate);
    std::vector<float> x(n);
    double phase = 0;

    for (size_t i = 0; i < n; ++i) {
        const double t = static_cast<double>(i) / kRate;
        const double f0 = 120.0 + 40.0 * std::sin(2.0 * kPi * 0.3 * t);
        phase += 2.0 * kPi * f0 / kRate;
        double v = 0;

        for (int h = 1; h <= 12; ++h) {
            v += std::sin(h * phase) / (h * 1.5);
        }

        const double envelope = 0.5 + 0.5 * std::sin(2.0 * kPi * 4.0 * t);
        x[i] = static_cast<float>(0.25 * envelope * v) + noise(rng);
    }

    return x;
}

struct Summary {
    double median;
    double p90;
};

Summary Summarise(std::vector<double> v) {
    std::sort(v.begin(), v.end());
    return {v[v.size() / 2], v[std::min(v.size() - 1, static_cast<size_t>(std::ceil(v.size() * 0.9)) - 1)]};
}

void BenchChain(const std::vector<float>& signal, const char* label, float rate, float semitones, int runs) {
    std::vector<double> times;
    std::vector<float> a;
    std::vector<float> b;
    a.reserve(4096 * 4);
    b.reserve(4096 * 4);

    for (int r = 0; r < runs; ++r) {
        bspeech::dsp::VoiceChain chain(kRate, rate, semitones);
        bspeech::dsp::GainLimiter limiter(kRate, 3.0f);
        bspeech::dsp::EdgeFader fader(120);
        fader.Begin();

        const auto start = Clock::now();

        for (size_t at = 0; at < signal.size(); at += 960) {
            a.clear();
            chain.Process(signal.data() + at, std::min<size_t>(960, signal.size() - at), a);
            limiter.Process(a.data(), a.size());
            b.clear();
            fader.Process(a.data(), a.size(), b);
        }

        a.clear();
        chain.Flush(a);
        b.clear();
        fader.Process(a.data(), a.size(), b);
        fader.End(b);

        times.push_back(std::chrono::duration<double>(Clock::now() - start).count());
    }

    const double audio = static_cast<double>(signal.size()) / kRate;
    const Summary s = Summarise(times);
    std::printf("  %-26s %8.3f ms/s audio   p90 %8.3f   RTF %.5f\n", label, 1000.0 * s.median / audio,
                1000.0 * s.p90 / audio, s.median / audio);
}

void BenchResampler(int from, const char* label, int runs) {
    const auto n = static_cast<size_t>(from) * 10;
    std::vector<float> x(n);

    for (size_t i = 0; i < n; ++i) {
        x[i] = 0.3f * static_cast<float>(std::sin(2.0 * kPi * 440.0 * static_cast<double>(i) / from));
    }

    std::vector<double> times;
    std::vector<float> out;
    out.reserve(static_cast<size_t>(kRate) * 11);

    for (int r = 0; r < runs; ++r) {
        bspeech::dsp::Resampler resampler(static_cast<double>(kRate) / from);
        out.clear();
        const auto start = Clock::now();
        resampler.Process(x.data(), x.size(), out);
        resampler.Flush(out);
        times.push_back(std::chrono::duration<double>(Clock::now() - start).count());
    }

    const Summary s = Summarise(times);
    std::printf("  %-26s %8.3f ms/s audio   p90 %8.3f\n", label, 1000.0 * s.median / 10.0, 1000.0 * s.p90 / 10.0);
}

std::string Text(int words) {
    static const char* pool[] = {"alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel"};
    std::string text;

    for (int i = 0; i < words; ++i) {
        std::string w = pool[i % 8];

        if (i % 10 == 0) {
            w[0] = static_cast<char>(w[0] - 32);
        }

        text += w + ((i % 10 == 9) ? ". " : " ");
    }

    return text + "End.";
}

bs_request Request(const std::string& text, const char* path, int32_t ring, int32_t prebuffer) {
    bs_request r{};
    r.struct_size = sizeof r;
    r.text = text.data();
    r.text_len = text.size();
    r.language = "en";
    r.voice.rate = 1.0f;
    r.boundary_fade_ms = -1.0f;
    r.export_wav_path = path;
    r.playback_ring_samples = ring;
    r.prebuffer_samples = prebuffer;
    return r;
}

void BenchPipeline(const char* label, float rate, float semitones, bool with_ring, int runs) {
    const std::string text = Text(599);  // 60 s of audio at 100 ms a word
    const std::string path = (std::filesystem::temp_directory_path() / "bench_pipeline.wav").string();
    std::vector<double> times;

    for (int r = 0; r < runs; ++r) {
        bt::ToneSource source;
        bs_engine_config config{};
        config.struct_size = sizeof config;
        config.source = &source.table;
        bs_engine* engine = nullptr;
        bs_engine_create(&config, &engine);

        auto request = Request(text, path.c_str(), with_ring ? 24000 : 0, 0);
        request.voice.rate = rate;
        request.voice.pitch_semitones = semitones;

        const auto start = Clock::now();
        bs_job* job = nullptr;

        if (bs_job_start(engine, &request, &job) != BS_OK) {
            std::printf("  %s: start failed: %s\n", label, bs_last_error());
            return;
        }

        if (with_ring) {
            std::vector<float> block(4800);

            while (true) {
                int32_t real = 0;
                int32_t flags = 0;
                bs_job_read(job, block.data(), 4800, &real, &flags);

                if ((flags & BS_READ_FINISHED) != 0) {
                    break;
                }
            }
        }

        int32_t status = 0;
        bs_job_wait(job, -1, &status);
        times.push_back(std::chrono::duration<double>(Clock::now() - start).count());
        bs_job_free(job);
        bs_engine_free(engine);
    }

    const double audio = 60.0 / rate;
    const Summary s = Summarise(times);
    std::printf("  %-26s %8.3f ms/s audio   p90 %8.3f   RTF %.5f\n", label, 1000.0 * s.median / audio,
                1000.0 * s.p90 / audio, s.median / audio);
}

void SimulatePacing(double model_rtf, int prefill_ms, double prebuffer_seconds) {
    bt::ToneSource source;
    source.pace = model_rtf;
    source.begin_delay_ms = prefill_ms;

    bs_engine_config config{};
    config.struct_size = sizeof config;
    config.source = &source.table;
    bs_engine* engine = nullptr;
    bs_engine_create(&config, &engine);

    const std::string text = Text(29);  // 3 s of audio in three chunks
    const auto prebuffer = static_cast<int32_t>(prebuffer_seconds * kRate);
    auto request = Request(text, nullptr, 3 * kRate, prebuffer);

    bs_job* job = nullptr;
    bs_job_start(engine, &request, &job);

    // A device pulling 20 ms every 20 ms, on a deadline schedule.
    std::vector<float> block(480);
    auto next = Clock::now();
    bool started = false;
    double start_ms = 0;
    const auto t0 = Clock::now();

    while (true) {
        int32_t real = 0;
        int32_t flags = 0;
        bs_job_read(job, block.data(), 480, &real, &flags);

        if (!started && real > 0) {
            started = true;
            start_ms = std::chrono::duration<double, std::milli>(Clock::now() - t0).count();
        }

        if ((flags & BS_READ_FINISHED) != 0) {
            break;
        }

        next += std::chrono::milliseconds(20);
        std::this_thread::sleep_until(next);
    }

    int32_t status = 0;
    bs_job_wait(job, -1, &status);

    bs_job_stats stats{};
    stats.struct_size = sizeof stats;
    bs_job_stats_get(job, &stats);

    std::printf("  rtf %.2f prefill %3d ms prebuffer %4.2f s | first pcm %6.1f ms  playback starts %6.1f ms  "
                "underruns %3lld (%5.0f ms of silence)\n",
                model_rtf, prefill_ms, prebuffer_seconds, stats.t_first_source_pcm_us / 1000.0, start_ms,
                static_cast<long long>(stats.underruns), stats.underrun_samples * 1000.0 / kRate);

    bs_job_free(job);
    bs_engine_free(engine);
}

}  // namespace

int main(int argc, char** argv) {
    const bool skip_pacing = argc > 1 && std::string(argv[1]) == "--no-pacing";
    const int runs = 9;

    std::printf("BetterSpeech benchmark, %d runs each, medians with p90\n", runs);
    std::printf("hardware threads: %u\n\n", std::thread::hardware_concurrency());

    const auto signal = SpeechLike(20.0);

    std::printf("dsp: voice chain + limiter + fader, 960-sample pulls\n");
    BenchChain(signal, "identity", 1.0f, 0.0f, runs);
    BenchChain(signal, "rate 0.8", 0.8f, 0.0f, runs);
    BenchChain(signal, "rate 1.25", 1.25f, 0.0f, runs);
    BenchChain(signal, "rate 2.0", 2.0f, 0.0f, runs);
    BenchChain(signal, "pitch +3 st", 1.0f, 3.0f, runs);
    BenchChain(signal, "pitch -3 st", 1.0f, -3.0f, runs);
    BenchChain(signal, "rate 1.2, pitch +2 st", 1.2f, 2.0f, runs);

    std::printf("\nresampler to 24 kHz, one-time clip conversion\n");
    BenchResampler(16000, "16 kHz", runs);
    BenchResampler(44100, "44.1 kHz", runs);
    BenchResampler(48000, "48 kHz", runs);

    std::printf("\npipeline: 60 s of audio from an instant source\n");
    BenchPipeline("export only", 1.0f, 0.0f, false, runs);
    BenchPipeline("export + playback ring", 1.0f, 0.0f, true, runs);
    BenchPipeline("export, rate 1.2 pitch +2", 1.2f, 2.0f, false, runs);

    if (!skip_pacing) {
        std::printf("\npacing SIMULATION (a sleeping tone source, not a model), 3 s of audio, real-time reader\n");

        for (const double rtf : {0.7, 1.15}) {
            for (const double prebuffer : {0.0, 0.25, 0.5, 1.0}) {
                SimulatePacing(rtf, 300, prebuffer);
            }
        }
    }

    return 0;
}
