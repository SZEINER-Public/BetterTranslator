#include <atomic>
#include <chrono>
#include <cmath>
#include <filesystem>
#include <string>
#include <thread>
#include <vector>

#if defined(__linux__)
#include <sys/resource.h>
#endif

#include "better_speech.h"
#include "dsp.h"
#include "harness.h"
#include "tone_source.h"
#include "wav.h"
#include "wav_fixtures.h"

namespace {

constexpr double kPi = 3.14159265358979323846;

bs_engine* MakeEngine(bt::ToneSource* source, const std::string& clip = "") {
    bs_engine_config config{};
    config.struct_size = sizeof config;
    config.source = source != nullptr ? &source->table : nullptr;
    config.laugh_clip_path = clip.empty() ? nullptr : clip.c_str();

    bs_engine* engine = nullptr;
    const int32_t rc = bs_engine_create(&config, &engine);

    if (rc != BS_OK) {
        bt::Fail(__FILE__, __LINE__, std::string("engine creation failed: ") + bs_last_error());
    }

    return engine;
}

bs_request Request(const std::string& text, const std::string& export_path = "", int32_t ring = 0) {
    bs_request r{};
    r.struct_size = sizeof r;
    r.text = text.data();
    r.text_len = text.size();
    r.language = "en";
    r.voice.rate = 1.0f;
    r.voice.pitch_semitones = 0.0f;
    r.voice.gain_db = 0.0f;
    r.boundary_fade_ms = -1.0f;
    r.export_wav_path = export_path.empty() ? nullptr : export_path.c_str();
    r.playback_ring_samples = ring;
    return r;
}

struct Outcome {
    int32_t start = BS_OK;
    int32_t status = BS_OK;
    bs_job_stats stats{};
    std::string error;
};

Outcome Run(bs_engine* engine, const bs_request& request) {
    Outcome o;
    bs_job* job = nullptr;
    o.start = bs_job_start(engine, &request, &job);

    if (o.start != BS_OK) {
        o.error = bs_last_error();
        return o;
    }

    CHECK_EQ(bs_job_wait(job, 30000, &o.status), BS_OK);
    o.stats.struct_size = sizeof o.stats;
    CHECK_EQ(bs_job_stats_get(job, &o.stats), BS_OK);

    char buf[512];
    int32_t len = 0;

    if (bs_job_error(job, buf, sizeof buf, &len) == BS_OK) {
        o.error.assign(buf, static_cast<size_t>(len));
    }

    bs_job_free(job);
    return o;
}

std::vector<float> ReadExport(const std::string& path) {
    std::vector<float> samples;
    bspeech::WavInfo info;
    std::string error;

    if (!bspeech::ReadWavMono(path, 0, &samples, &info, &error)) {
        bt::Fail(__FILE__, __LINE__, "export unreadable: " + error);
    }

    return samples;
}

double Goertzel(const std::vector<float>& x, size_t from, size_t to, double f, int rate) {
    const double coeff = 2.0 * std::cos(2.0 * kPi * f / rate);
    double s1 = 0;
    double s2 = 0;

    for (size_t i = from; i < to; ++i) {
        const double s0 = x[i] + coeff * s1 - s2;
        s2 = s1;
        s1 = s0;
    }

    return s1 * s1 + s2 * s2 - coeff * s1 * s2;
}

double Dominant(const std::vector<float>& x, size_t from, size_t to, int rate, double lo, double hi, double step) {
    double best = lo;
    double best_power = -1;

    for (double f = lo; f <= hi; f += step) {
        const double p = Goertzel(x, from, to, f, rate);

        if (p > best_power) {
            best_power = p;
            best = f;
        }
    }

    return best;
}

std::string MakeClip(const std::string& name, uint32_t rate, uint16_t channels, size_t frames, double frequency) {
    std::vector<float> samples;

    for (size_t i = 0; i < frames; ++i) {
        const auto v = static_cast<float>(0.3 * std::sin(2.0 * kPi * frequency * static_cast<double>(i) / rate));

        for (uint16_t c = 0; c < channels; ++c) {
            samples.push_back(v);
        }
    }

    const std::string path = bt::TempPath(name);
    bt::WriteWav(path, rate, channels, 16, false, false, samples, false);
    return path;
}

std::string Words(int count) {
    static const char* words[] = {"alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel"};
    std::string text;

    for (int i = 0; i < count; ++i) {
        std::string word = words[i % 8];

        // Sentences of ten words, each opening with a capital, so a long text is
        // many chunks rather than one.
        if (i % 10 == 0) {
            word[0] = static_cast<char>(word[0] - 32);
        }

        text += word;
        text += (i % 10 == 9) ? ". " : " ";
    }

    text += "End.";
    return text;
}

bool Gone(const std::string& path) {
    return !std::filesystem::exists(path) && !std::filesystem::exists(path + ".partial");
}

// Temp files outlive a run, so a test that asserts a file was never written starts
// by removing whatever an earlier run left there.
std::string Fresh(const std::string& name) {
    const std::string path = bt::TempPath(name);
    std::filesystem::remove(path);
    std::filesystem::remove(path + ".partial");
    return path;
}

long PeakRssKb() {
#if defined(__linux__)
    rusage usage{};
    getrusage(RUSAGE_SELF, &usage);
    return usage.ru_maxrss;
#else
    return 0;
#endif
}

std::string OutPath(const std::string& name) {
    const std::string dir = bt::OutputDir();
    return dir.empty() ? bt::TempPath(name) : (std::filesystem::path(dir) / name).string();
}

}  // namespace

TEST(job_without_a_model_is_unavailable) {
    bs_engine* engine = MakeEngine(nullptr);

    bs_capabilities caps{};
    caps.struct_size = sizeof caps;
    CHECK_EQ(bs_engine_capabilities(engine, &caps), BS_OK);
    CHECK_EQ(caps.synthesis_available, 0);
    CHECK_EQ(caps.voice, BS_CONTROL_UNAVAILABLE);
    CHECK_EQ(caps.laugh, BS_EVENT_RENDER_UNAVAILABLE);

    char reason[256];
    int32_t len = 0;
    CHECK_EQ(bs_engine_unavailable_reason(engine, reason, sizeof reason, &len), BS_OK);
    CHECK(len > 0);

    int32_t tiny_len = 0;
    CHECK_EQ(bs_engine_unavailable_reason(engine, reason, 3, &tiny_len), BS_BUFFER_TOO_SMALL);
    CHECK_EQ(tiny_len, len);

    const std::string text = "Hello.";
    const std::string never = Fresh("never.wav");
    const auto out = Run(engine, Request(text, never));
    CHECK_EQ(out.start, BS_UNAVAILABLE);
    CHECK(!out.error.empty());
    CHECK(Gone(never));

    bs_engine_free(engine);
}

TEST(engine_rejects_a_bad_source_table) {
    bt::ToneSource source;
    source.table.abi_version = 2;

    bs_engine_config config{};
    config.struct_size = sizeof config;
    config.source = &source.table;
    bs_engine* engine = nullptr;
    CHECK_EQ(bs_engine_create(&config, &engine), BS_INVALID_ARG);
    CHECK(engine == nullptr);

    source.table.abi_version = BS_SOURCE_ABI_VERSION;
    source.table.pull = nullptr;
    CHECK_EQ(bs_engine_create(&config, &engine), BS_INVALID_ARG);

    bt::ToneSource stereo;
    stereo.sample_rate = 4000;
    config.source = &stereo.table;
    CHECK_EQ(bs_engine_create(&config, &engine), BS_SOURCE);
}

TEST(engine_rejects_missing_or_corrupt_clip) {
    bt::ToneSource source;
    bs_engine_config config{};
    config.struct_size = sizeof config;
    config.source = &source.table;
    bs_engine* engine = nullptr;

    const std::string missing = bt::TempPath("no_laugh_here.wav");
    std::filesystem::remove(missing);
    config.laugh_clip_path = missing.c_str();
    CHECK_EQ(bs_engine_create(&config, &engine), BS_ASSET);
    CHECK(std::string(bs_last_error()).find("laughter clip") != std::string::npos);

    const std::string corrupt = bt::TempPath("corrupt_laugh.wav");
    bt::WriteBytes(corrupt, std::vector<uint8_t>(100, 0x42));
    config.laugh_clip_path = corrupt.c_str();
    CHECK_EQ(bs_engine_create(&config, &engine), BS_ASSET);

    const std::string too_long = MakeClip("long_laugh.wav", 8000, 1, 8000 * 31, 500);
    config.laugh_clip_path = too_long.c_str();
    CHECK_EQ(bs_engine_create(&config, &engine), BS_ASSET);
}

TEST(capabilities_report_controls_honestly) {
    bt::ToneSource plain;
    bs_engine* engine = MakeEngine(&plain);
    bs_capabilities caps{};
    caps.struct_size = sizeof caps;
    CHECK_EQ(bs_engine_capabilities(engine, &caps), BS_OK);
    CHECK_EQ(caps.synthesis_available, 1);
    CHECK_EQ(caps.sample_rate, 24000);
    CHECK_EQ(caps.rate, BS_CONTROL_ENGINE);
    CHECK_EQ(caps.pitch, BS_CONTROL_ENGINE);
    CHECK_EQ(caps.gain, BS_CONTROL_ENGINE);
    CHECK_EQ(caps.voice, BS_CONTROL_UNAVAILABLE);
    CHECK_EQ(caps.emotion, BS_CONTROL_UNAVAILABLE);
    CHECK_EQ(caps.cloning, BS_CONTROL_UNAVAILABLE);
    CHECK_EQ(caps.laugh, BS_EVENT_RENDER_UNAVAILABLE);
    bs_engine_free(engine);

    const std::string clip = MakeClip("caps_laugh.wav", 16000, 2, 4000, 3000);
    engine = MakeEngine(&plain, clip);
    CHECK_EQ(bs_engine_capabilities(engine, &caps), BS_OK);
    CHECK_EQ(caps.laugh, BS_EVENT_RENDER_CLIP);
    CHECK_EQ(caps.laugh_clip_sample_rate, 16000);
    bs_engine_free(engine);

    bt::ToneSource rich;
    rich.native_events = 1u << BS_EVENT_LAUGH;
    rich.native_controls = BS_SOURCE_CONTROL_VOICE | BS_SOURCE_CONTROL_RATE;
    rich.voices = {"anna", "petr"};
    engine = MakeEngine(&rich);
    CHECK_EQ(bs_engine_capabilities(engine, &caps), BS_OK);
    CHECK_EQ(caps.laugh, BS_EVENT_RENDER_NATIVE);
    CHECK_EQ(caps.voice, BS_CONTROL_MODEL);
    CHECK_EQ(caps.rate, BS_CONTROL_MODEL);
    CHECK_EQ(caps.pitch, BS_CONTROL_ENGINE);
    CHECK_EQ(caps.voice_count, 2);
    bs_engine_free(engine);
}

TEST(job_validates_requests) {
    bt::ToneSource source;
    bs_engine* engine = MakeEngine(&source);
    const std::string text = "Hello there.";
    const std::string path = Fresh("validate.wav");

    auto expect = [&](bs_request r, int32_t status) {
        const auto out = Run(engine, r);
        CHECK_EQ(out.start, status);
        CHECK(!out.error.empty());
    };

    auto r = Request(text, path);
    r.voice.rate = 3.0f;
    expect(r, BS_INVALID_ARG);

    r = Request(text, path);
    r.voice.pitch_semitones = NAN;
    expect(r, BS_INVALID_ARG);

    r = Request(text, path);
    r.voice.gain_db = 20.0f;
    expect(r, BS_INVALID_ARG);

    expect(Request(text), BS_INVALID_ARG);

    r = Request(text, "", 1000);
    r.prebuffer_samples = 2000;
    expect(r, BS_INVALID_ARG);

    r = Request(text, path);
    r.language = "";
    expect(r, BS_INVALID_ARG);

    r = Request(text, path);
    r.voice.voice_id = "anna";
    expect(r, BS_UNAVAILABLE);

    const std::string nothing = "  \n ";
    expect(Request(nothing, path), BS_INVALID_ARG);

    const std::string laugh = "Ha. [Laugh] Ok.";
    expect(Request(laugh, path), BS_UNAVAILABLE);

    const std::string unknown = "Hi [Wink] there.";
    const auto out = Run(engine, Request(unknown, path));
    CHECK_EQ(out.start, BS_INVALID_ARG);
    CHECK_EQ(bs_last_error_offset(), 3);

    const std::string unreachable = bt::TempPath("missing/dir/x.wav");
    r = Request(text, unreachable);
    expect(r, BS_IO);

    CHECK(Gone(path));
    CHECK(source.begun.empty());
    bs_engine_free(engine);
}

TEST(job_speech_laugh_speech_in_order) {
    bt::ToneSource source;
    const std::string clip = MakeClip("order_laugh.wav", 16000, 2, 4000, 3000);
    bs_engine* engine = MakeEngine(&source, clip);

    const std::string text = "Hello. [Laugh] That was unexpected.";
    const std::string path = OutPath("speech_laugh_speech.wav");
    const auto out = Run(engine, Request(text, path));

    CHECK_EQ(out.start, BS_OK);
    CHECK_EQ(out.status, BS_OK);
    CHECK_EQ(source.begun.size(), 2u);
    CHECK_EQ(source.begun[0], std::string("Hello."));
    CHECK_EQ(source.begun[1], std::string("That was unexpected."));
    CHECK_EQ(out.stats.chunks, 2);
    CHECK_EQ(out.stats.events, 1);
    CHECK_EQ(out.stats.laugh_render, BS_EVENT_RENDER_CLIP);
    CHECK_EQ(out.stats.samples_clip, 6000);

    const auto pcm = ReadExport(path);
    CHECK_EQ(pcm.size(), 2400u + 6000u + 7200u);

    // Each region sounds like what was meant to be there, in that order.
    CHECK_NEAR(Dominant(pcm, 240, 2160, 24000, 100, 3500, 25), bt::ToneSource::FrequencyFor(0), 25);
    CHECK_NEAR(Dominant(pcm, 3000, 7800, 24000, 100, 3500, 25), 3000.0, 25);
    CHECK_NEAR(Dominant(pcm, 9000, 15000, 24000, 100, 3500, 25), bt::ToneSource::FrequencyFor(1), 25);

    // Joins are faded rather than cut, so nothing clicks across a boundary.
    CHECK(std::fabs(pcm[2399]) < 0.02f && std::fabs(pcm[2400]) < 0.02f);
    CHECK(std::fabs(pcm[8399]) < 0.02f && std::fabs(pcm[8400]) < 0.02f);

    bs_engine_free(engine);
}

TEST(job_playback_stream_matches_export) {
    bt::ToneSource source;
    bs_engine* engine = MakeEngine(&source);
    const std::string text = Words(30);
    const std::string path = bt::TempPath("stream_vs_file.wav");

    auto r = Request(text, path, 8192);
    r.prebuffer_samples = 2048;

    bs_job* job = nullptr;
    CHECK_EQ(bs_job_start(engine, &r, &job), BS_OK);

    std::vector<float> heard;
    std::vector<float> block(480);
    bool finished = false;
    int flags_seen = 0;

    while (!finished) {
        int32_t real = 0;
        int32_t flags = 0;
        CHECK_EQ(bs_job_read(job, block.data(), static_cast<int32_t>(block.size()), &real, &flags), BS_OK);
        heard.insert(heard.end(), block.begin(), block.begin() + real);
        flags_seen |= flags;
        finished = (flags & BS_READ_FINISHED) != 0;

        if (real == 0) {
            std::this_thread::yield();
        }
    }

    int32_t status = 0;
    CHECK_EQ(bs_job_wait(job, 10000, &status), BS_OK);
    CHECK_EQ(status, BS_OK);
    CHECK((flags_seen & BS_READ_CANCELLED) == 0);

    bs_job_stats stats{};
    stats.struct_size = sizeof stats;
    CHECK_EQ(bs_job_stats_get(job, &stats), BS_OK);
    CHECK(stats.peak_buffered_samples <= 8192);
    CHECK(stats.t_first_audible_read_us >= stats.t_first_audible_pcm_us);
    bs_job_free(job);

    const auto file = ReadExport(path);
    CHECK_EQ(heard.size(), file.size());

    for (size_t i = 0; i < heard.size(); ++i) {
        int16_t q = 0;
        bspeech::dsp::FloatToInt16(&heard[i], 1, &q);
        CHECK_NEAR(file[i], q / 32768.0, 1e-7);
    }

    bs_engine_free(engine);
}

TEST(job_ring_bounds_what_is_buffered) {
    bt::ToneSource source;
    bs_engine* engine = MakeEngine(&source);
    const std::string text = Words(20);

    auto r = Request(text, "", 4096);
    bs_job* job = nullptr;
    CHECK_EQ(bs_job_start(engine, &r, &job), BS_OK);

    size_t total = 0;
    std::vector<float> block(240);

    while (true) {
        int32_t real = 0;
        int32_t flags = 0;
        CHECK_EQ(bs_job_read(job, block.data(), 240, &real, &flags), BS_OK);
        total += static_cast<size_t>(real);

        if ((flags & BS_READ_FINISHED) != 0) {
            break;
        }

        std::this_thread::sleep_for(std::chrono::microseconds(200));
    }

    bs_job_stats stats{};
    stats.struct_size = sizeof stats;
    CHECK_EQ(bs_job_stats_get(job, &stats), BS_OK);
    CHECK(stats.peak_buffered_samples <= 4096);
    CHECK(stats.peak_buffered_samples > 0);
    CHECK_EQ(static_cast<int64_t>(total), stats.samples_out);
    CHECK_EQ(stats.samples_out, 21 * 2400);
    bs_job_free(job);
    bs_engine_free(engine);
}

TEST(job_prebuffers_before_playing) {
    bt::ToneSource source;
    source.pace = 0.2;
    bs_engine* engine = MakeEngine(&source);
    const std::string text = Words(5);

    auto r = Request(text, "", 48000);
    r.prebuffer_samples = 4800;
    bs_job* job = nullptr;
    CHECK_EQ(bs_job_start(engine, &r, &job), BS_OK);

    std::vector<float> block(480);
    int32_t real = 0;
    int32_t flags = 0;
    CHECK_EQ(bs_job_read(job, block.data(), 480, &real, &flags), BS_OK);
    CHECK((flags & BS_READ_PREBUFFERING) != 0);
    CHECK_EQ(real, 0);

    for (const float v : block) {
        CHECK_EQ(v, 0.0f);
    }

    int32_t status = 0;
    CHECK_EQ(bs_job_wait(job, 10000, &status), BS_OK);

    bs_job_stats stats{};
    stats.struct_size = sizeof stats;
    CHECK_EQ(bs_job_stats_get(job, &stats), BS_OK);
    CHECK(stats.t_prebuffered_us >= stats.t_first_source_pcm_us);
    CHECK_EQ(stats.underruns, 0);
    bs_job_free(job);
    bs_engine_free(engine);
}

TEST(job_counts_underruns_when_generation_is_slower_than_playback) {
    bt::ToneSource source;
    source.pace = 2.0;  // half real time
    bs_engine* engine = MakeEngine(&source);
    const std::string text = Words(30);

    auto r = Request(text, "", 24000);
    bs_job* job = nullptr;
    CHECK_EQ(bs_job_start(engine, &r, &job), BS_OK);

    std::vector<float> block(480);
    int underrun_flags = 0;
    const auto until = std::chrono::steady_clock::now() + std::chrono::milliseconds(400);

    while (std::chrono::steady_clock::now() < until) {
        int32_t real = 0;
        int32_t flags = 0;
        CHECK_EQ(bs_job_read(job, block.data(), 480, &real, &flags), BS_OK);
        underrun_flags += (flags & BS_READ_UNDERRUN) != 0 ? 1 : 0;
        std::this_thread::sleep_for(std::chrono::milliseconds(20));
    }

    bs_job_cancel(job);
    int32_t status = 0;
    CHECK_EQ(bs_job_wait(job, 5000, &status), BS_OK);
    CHECK_EQ(status, BS_CANCELLED);

    bs_job_stats stats{};
    stats.struct_size = sizeof stats;
    CHECK_EQ(bs_job_stats_get(job, &stats), BS_OK);
    CHECK(underrun_flags > 0);
    CHECK(stats.underruns > 0);
    CHECK(stats.underrun_samples > 0);
    bs_job_free(job);
    bs_engine_free(engine);
}

TEST(job_cancel_is_prompt_and_leaves_nothing_behind) {
    bt::ToneSource source;
    source.pace = 1.0;
    bs_engine* engine = MakeEngine(&source);
    const std::string text = Words(40);
    const std::string path = Fresh("cancelled.wav");

    auto r = Request(text, path, 48000);
    bs_job* first = nullptr;
    CHECK_EQ(bs_job_start(engine, &r, &first), BS_OK);
    std::this_thread::sleep_for(std::chrono::milliseconds(150));

    const auto asked = std::chrono::steady_clock::now();
    bs_job_cancel(first);
    int32_t status = 0;
    CHECK_EQ(bs_job_wait(first, 2000, &status), BS_OK);
    const auto waited = std::chrono::steady_clock::now() - asked;
    CHECK_EQ(status, BS_CANCELLED);

    bs_job_stats stats{};
    stats.struct_size = sizeof stats;
    CHECK_EQ(bs_job_stats_get(first, &stats), BS_OK);
    CHECK(stats.cancel_latency_us >= 0);
    CHECK(stats.cancel_latency_us < 50000);
    CHECK(std::chrono::duration_cast<std::chrono::milliseconds>(waited).count() < 100);
    std::printf("        cancel latency %.2f ms\n", stats.cancel_latency_us / 1000.0);

    CHECK(Gone(path));

    std::vector<float> block(480, 1.0f);
    int32_t real = 0;
    int32_t flags = 0;
    CHECK_EQ(bs_job_read(first, block.data(), 480, &real, &flags), BS_OK);
    CHECK((flags & BS_READ_CANCELLED) != 0);
    CHECK_EQ(real, 0);
    CHECK_EQ(block[0], 0.0f);

    // A second job on the same engine runs cleanly, and a late cancel aimed at the
    // first one does not reach it.
    const int cancels_before = source.cancels.load();
    const std::string short_text = "Short one.";
    const std::string second_path = bt::TempPath("after_cancel.wav");
    source.pace = 0.0;
    auto r2 = Request(short_text, second_path);
    bs_job* second = nullptr;
    CHECK_EQ(bs_job_start(engine, &r2, &second), BS_OK);
    bs_job_cancel(first);
    CHECK_EQ(bs_job_wait(second, 5000, &status), BS_OK);
    CHECK_EQ(status, BS_OK);
    CHECK_EQ(source.cancels.load(), cancels_before);
    CHECK(std::filesystem::exists(second_path));

    bs_job_free(second);
    bs_job_free(first);
    bs_engine_free(engine);
}

TEST(job_cancel_before_any_audio) {
    bt::ToneSource source;
    source.pace = 1.0;
    bs_engine* engine = MakeEngine(&source);
    const std::string text = Words(10);
    const std::string path = Fresh("cancel_at_once.wav");

    auto r = Request(text, path);
    bs_job* job = nullptr;
    CHECK_EQ(bs_job_start(engine, &r, &job), BS_OK);
    bs_job_cancel(job);
    int32_t status = 0;
    CHECK_EQ(bs_job_wait(job, 2000, &status), BS_OK);
    CHECK_EQ(status, BS_CANCELLED);
    CHECK(Gone(path));
    bs_job_free(job);
    bs_engine_free(engine);
}

TEST(job_one_at_a_time_per_engine) {
    bt::ToneSource source;
    source.pace = 1.0;
    bs_engine* engine = MakeEngine(&source);
    const std::string text = Words(20);

    auto r = Request(text, "", 48000);
    bs_job* a = nullptr;
    CHECK_EQ(bs_job_start(engine, &r, &a), BS_OK);

    bs_job* b = nullptr;
    CHECK_EQ(bs_job_start(engine, &r, &b), BS_STATE);
    CHECK(b == nullptr);

    bs_job_cancel(a);
    int32_t status = 0;
    CHECK_EQ(bs_job_wait(a, 2000, &status), BS_OK);
    bs_job_free(a);

    source.pace = 0.0;
    CHECK_EQ(bs_job_start(engine, &r, &b), BS_OK);
    std::vector<float> block(4096);

    while (true) {
        int32_t real = 0;
        int32_t flags = 0;
        bs_job_read(b, block.data(), 4096, &real, &flags);

        if ((flags & BS_READ_FINISHED) != 0) {
            break;
        }
    }

    CHECK_EQ(bs_job_wait(b, 5000, &status), BS_OK);
    CHECK_EQ(status, BS_OK);
    bs_job_free(b);
    bs_engine_free(engine);
}

TEST(job_source_failure_is_reported_and_cleaned_up) {
    bt::ToneSource source;
    source.fail_at_chunk = 1;
    bs_engine* engine = MakeEngine(&source);
    const std::string text = "First sentence. Second sentence. Third sentence.";
    const std::string path = Fresh("failed.wav");

    const auto out = Run(engine, Request(text, path));
    CHECK_EQ(out.start, BS_OK);
    CHECK_EQ(out.status, BS_SOURCE);
    CHECK(out.error.find("told to fail") != std::string::npos);
    CHECK(Gone(path));

    bt::ToneSource measuring;
    measuring.max_units = 3;
    measuring.fail_measure = true;
    bs_engine* second = MakeEngine(&measuring);
    const auto measured = Run(second, Request(text, path));
    CHECK_EQ(measured.start, BS_INVALID_ARG);

    bs_engine_free(second);
    bs_engine_free(engine);
}

TEST(job_outlives_its_engine_handle) {
    bt::ToneSource source;
    source.pace = 0.1;
    bs_engine* engine = MakeEngine(&source);
    const std::string text = Words(10);
    const std::string path = bt::TempPath("orphan.wav");

    auto r = Request(text, path);
    bs_job* job = nullptr;
    CHECK_EQ(bs_job_start(engine, &r, &job), BS_OK);
    bs_engine_free(engine);

    int32_t status = 0;
    CHECK_EQ(bs_job_wait(job, 10000, &status), BS_OK);
    CHECK_EQ(status, BS_OK);
    CHECK(std::filesystem::exists(path));
    bs_job_free(job);
}

TEST(job_free_while_running_cancels_and_joins) {
    bt::ToneSource source;
    source.pace = 1.0;
    bs_engine* engine = MakeEngine(&source);
    const std::string text = Words(40);
    const std::string path = Fresh("freed_running.wav");

    auto r = Request(text, path, 24000);
    bs_job* job = nullptr;
    CHECK_EQ(bs_job_start(engine, &r, &job), BS_OK);
    std::this_thread::sleep_for(std::chrono::milliseconds(50));

    const auto start = std::chrono::steady_clock::now();
    bs_job_free(job);
    const auto took = std::chrono::steady_clock::now() - start;
    CHECK(std::chrono::duration_cast<std::chrono::milliseconds>(took).count() < 200);
    CHECK(Gone(path));

    source.pace = 0.0;
    const std::string again = "Again.";
    const auto out = Run(engine, Request(again, path));
    CHECK_EQ(out.status, BS_OK);
    bs_engine_free(engine);
}

TEST(job_applies_rate_pitch_and_gain) {
    bt::ToneSource source;
    bs_engine* engine = MakeEngine(&source);
    const std::string text = Words(20);
    const std::string path = bt::TempPath("voice.wav");

    auto r = Request(text, path);
    r.voice.rate = 2.0f;
    auto out = Run(engine, r);
    CHECK_EQ(out.status, BS_OK);
    CHECK_NEAR(static_cast<double>(out.stats.samples_out), out.stats.samples_source / 2.0, 8.0);

    r = Request(text, path);
    r.voice.pitch_semitones = 12.0f;
    out = Run(engine, r);
    CHECK_EQ(out.status, BS_OK);
    CHECK_NEAR(static_cast<double>(out.stats.samples_out), static_cast<double>(out.stats.samples_source), 8.0);

    const auto pitched = ReadExport(path);
    CHECK_NEAR(Dominant(pitched, 400, 2000, 24000, 100, 1500, 5), 2.0 * bt::ToneSource::FrequencyFor(0), 10);

    r = Request(text, path);
    r.voice.gain_db = 12.0f;
    out = Run(engine, r);
    CHECK_EQ(out.status, BS_OK);
    CHECK(out.stats.limited_samples > 0);

    float peak = 0;

    for (const float v : ReadExport(path)) {
        peak = std::max(peak, std::fabs(v));
    }

    CHECK(peak <= 0.945f);
    bs_engine_free(engine);
}

TEST(job_event_only_input_uses_the_clip) {
    bt::ToneSource source;
    const std::string clip = MakeClip("only_laugh.wav", 24000, 1, 7000, 2000);
    bs_engine* engine = MakeEngine(&source, clip);
    const std::string text = "[Laugh]";
    const std::string path = bt::TempPath("only_laugh_out.wav");

    const auto out = Run(engine, Request(text, path));
    CHECK_EQ(out.status, BS_OK);
    CHECK(source.begun.empty());
    CHECK_EQ(out.stats.samples_out, 7000);
    CHECK_EQ(ReadExport(path).size(), 7000u);
    bs_engine_free(engine);
}

TEST(job_native_laughter_is_the_models) {
    bt::ToneSource source;
    source.native_events = 1u << BS_EVENT_LAUGH;
    bs_engine* engine = MakeEngine(&source);
    const std::string text = "Ha. [Laugh] Ok.";
    const std::string path = bt::TempPath("native_laugh.wav");

    const auto out = Run(engine, Request(text, path));
    CHECK_EQ(out.status, BS_OK);
    CHECK_EQ(source.events_begun.size(), 1u);
    CHECK_EQ(source.events_begun[0], BS_EVENT_LAUGH);
    CHECK_EQ(out.stats.laugh_render, BS_EVENT_RENDER_NATIVE);
    CHECK_EQ(out.stats.samples_clip, 0);
    bs_engine_free(engine);
}

TEST(job_chunks_follow_the_models_budget) {
    bt::ToneSource source;
    source.max_units = 4;
    bs_engine* engine = MakeEngine(&source);
    const std::string text =
        "One two three four five six seven eight nine ten. Short. Another sentence with quite a few words in it.";
    const std::string path = bt::TempPath("budget.wav");

    const auto out = Run(engine, Request(text, path));
    CHECK_EQ(out.status, BS_OK);

    for (const auto& chunk : source.begun) {
        CHECK(bt::ToneSource::Words(chunk.data(), chunk.size()) <= 4);
    }

    std::string joined;

    for (const auto& chunk : source.begun) {
        joined += (joined.empty() ? "" : " ") + chunk;
    }

    CHECK_EQ(joined, text);
    bs_engine_free(engine);
}

TEST(job_voice_selection_and_native_rate) {
    bt::ToneSource source;
    source.voices = {"anna", "petr"};
    source.native_controls = BS_SOURCE_CONTROL_VOICE | BS_SOURCE_CONTROL_RATE;
    bs_engine* engine = MakeEngine(&source);
    const std::string text = "Hello there friend.";
    const std::string path = bt::TempPath("voices.wav");

    auto r = Request(text, path);
    r.voice.voice_id = "petr";
    r.voice.rate = 1.5f;
    r.n_threads = 3;
    auto out = Run(engine, r);
    CHECK_EQ(out.status, BS_OK);
    CHECK_EQ(source.voice_ids.back(), std::string("petr"));
    CHECK_EQ(source.threads.back(), 3);

    // The model handled rate, so the engine did not stretch on top of it.
    CHECK_EQ(out.stats.samples_out, out.stats.samples_source);

    r.voice.voice_id = "nobody";
    out = Run(engine, r);
    CHECK_EQ(out.start, BS_INVALID_ARG);
    bs_engine_free(engine);
}

TEST(job_output_is_identical_under_cpu_contention) {
    const std::string text = Words(60);

    auto render = [&](const std::string& path) {
        bt::ToneSource source;
        bs_engine* engine = MakeEngine(&source);
        auto r = Request(text, path);
        r.voice.rate = 1.2f;
        r.voice.pitch_semitones = 2.0f;
        const auto out = Run(engine, r);
        CHECK_EQ(out.status, BS_OK);
        bs_engine_free(engine);
        return ReadExport(path);
    };

    const auto calm = render(bt::TempPath("calm.wav"));

    // Stands in for a translation generation holding every other core.
    std::atomic<bool> stop{false};
    std::vector<std::thread> busy;
    const unsigned others = std::max(1u, std::thread::hardware_concurrency());

    for (unsigned i = 0; i < others; ++i) {
        busy.emplace_back([&stop] {
            volatile double x = 1.0;

            while (!stop.load(std::memory_order_relaxed)) {
                x = x * 1.0000001 + 1e-9;
            }
        });
    }

    const auto contended = render(bt::TempPath("contended.wav"));
    stop.store(true);

    for (auto& t : busy) {
        t.join();
    }

    CHECK(calm == contended);
}

TEST(job_timings_are_ordered) {
    bt::ToneSource source;
    source.pace = 0.05;
    bs_engine* engine = MakeEngine(&source);
    const std::string text = Words(8);
    const std::string path = bt::TempPath("timings.wav");

    const auto out = Run(engine, Request(text, path));
    CHECK_EQ(out.status, BS_OK);

    const auto& s = out.stats;
    CHECK(s.t_plan_us >= 0);
    CHECK(s.t_first_source_pcm_us >= s.t_plan_us);
    CHECK(s.t_first_audible_pcm_us >= s.t_first_source_pcm_us);
    CHECK(s.t_finished_us >= s.t_first_audible_pcm_us);
    CHECK(s.source_us > 0);
    CHECK(s.dsp_us >= 0);
    CHECK_EQ(s.t_first_audible_read_us, -1);  // nothing read: export only
    CHECK_EQ(s.cancel_latency_us, -1);
    bs_engine_free(engine);
}

TEST(job_long_form_exports_with_bounded_memory) {
    bt::ToneSource source;
    bs_engine* engine = MakeEngine(&source);

    struct Length {
        const char* name;
        int words;  // 100 ms each, plus the trailing "end."
    };

    long before = 0;

    for (const Length length : {Length{"tone_5s.wav", 49}, Length{"tone_30s.wav", 299}, Length{"tone_5min.wav", 2999}}) {
        const std::string text = Words(length.words);
        const std::string path = OutPath(length.name);
        auto r = Request(text, path, 24000);

        bs_job* job = nullptr;
        CHECK_EQ(bs_job_start(engine, &r, &job), BS_OK);

        // A reader that keeps up, as a device would, so the playback path is part
        // of what is measured.
        std::vector<float> block(4800);

        while (true) {
            int32_t real = 0;
            int32_t flags = 0;
            bs_job_read(job, block.data(), 4800, &real, &flags);

            if ((flags & BS_READ_FINISHED) != 0) {
                break;
            }

            if (real == 0) {
                std::this_thread::yield();
            }
        }

        int32_t status = 0;
        CHECK_EQ(bs_job_wait(job, 60000, &status), BS_OK);
        CHECK_EQ(status, BS_OK);

        bs_job_stats stats{};
        stats.struct_size = sizeof stats;
        CHECK_EQ(bs_job_stats_get(job, &stats), BS_OK);
        bs_job_free(job);

        const int64_t expected = static_cast<int64_t>(length.words + 1) * 2400;
        CHECK_EQ(stats.samples_out, expected);

        bspeech::WavInfo info;
        std::string error;
        CHECK(bspeech::ProbeWav(path, &info, &error));
        CHECK_EQ(info.frames, expected);
        CHECK(stats.peak_buffered_samples <= 24000);

        const long rss = PeakRssKb();

        if (before == 0) {
            before = rss;
        }

        std::printf("        %-14s %7.1f s audio, peak RSS %ld KiB, rtf %.4f\n", length.name, expected / 24000.0, rss,
                    (stats.t_finished_us / 1e6) / (expected / 24000.0));
    }

    // Five minutes held in memory would be 28 MiB as float and 14 MiB as 16-bit.
    // The peak may not grow by anything like that between the short and long job.
    const long growth = PeakRssKb() - before;
    std::printf("        peak RSS growth from 5 s to 5 min: %ld KiB\n", growth);
#if defined(__linux__)
    CHECK(growth < 6 * 1024);
#endif
    bs_engine_free(engine);
}
