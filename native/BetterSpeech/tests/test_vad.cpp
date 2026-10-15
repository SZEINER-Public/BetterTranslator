#include <cmath>
#include <filesystem>
#include <string>
#include <vector>

#include "better_speech_stt.h"
#include "harness.h"
#include "lexicon.h"
#include "score.h"
#include "tone_recognizer.h"
#include "vad.h"
#include "wav.h"
#include "wav_fixtures.h"

namespace {

struct Events {
    std::vector<int64_t> onsets;
    std::vector<int64_t> offsets;
};

Events RunVad(const std::vector<float>& mono, int32_t rate, const bspeech::VadSettings& settings = {}) {
    bspeech::Vad vad(rate, settings);
    const size_t frame = vad.FrameSamples();
    Events events;

    for (size_t at = 0; at + frame <= mono.size(); at += frame) {
        const auto e = vad.Push(mono.data() + at);

        if (e == bspeech::Vad::Event::Onset) {
            events.onsets.push_back(static_cast<int64_t>(at / frame));
        } else if (e == bspeech::Vad::Event::Offset) {
            events.offsets.push_back(static_cast<int64_t>(at / frame));
        }
    }

    return events;
}

std::vector<float> Tone(size_t n, double hz, float amplitude, int32_t rate) {
    std::vector<float> out(n);

    for (size_t i = 0; i < n; ++i) {
        out[i] = static_cast<float>(amplitude * std::sin(2.0 * 3.14159265358979323846 * hz * static_cast<double>(i) / rate));
    }

    return out;
}

void Append(std::vector<float>* to, const std::vector<float>& from) { to->insert(to->end(), from.begin(), from.end()); }

std::string Fresh(const std::string& name) {
    const std::string path = bt::TempPath(name);
    std::filesystem::remove(path);
    return path;
}

}

TEST(vad_marks_onset_after_min_speech_and_offset_after_hangover) {
    bt::ToneSpeechOptions o;
    o.lead_ms = 1000;
    o.pause_ms = 1000;
    const auto mono = bt::ToneSpeech({{1, 2, 3}, {4}}, o);
    const Events e = RunVad(mono, 16000);

    CHECK_EQ(e.onsets.size(), size_t{2});
    CHECK_EQ(e.offsets.size(), size_t{2});
    CHECK(e.onsets[0] >= 100 + 9 && e.onsets[0] <= 100 + 11);

    const int64_t last_word_end = 100 + (16 + 6) * 2 + 16;
    CHECK(e.offsets[0] >= last_word_end + 44 && e.offsets[0] <= last_word_end + 47);
    CHECK(e.onsets[1] > e.offsets[0]);
}

TEST(vad_ignores_clicks_shorter_than_min_speech) {
    std::vector<float> mono(16000, 0.0f);
    Append(&mono, Tone(800, 1000.0, 0.5f, 16000));
    Append(&mono, std::vector<float>(16000, 0.0f));
    Append(&mono, Tone(800, 1000.0, 0.5f, 16000));
    Append(&mono, std::vector<float>(16000, 0.0f));
    const Events e = RunVad(mono, 16000);
    CHECK(e.onsets.empty());
    CHECK(e.offsets.empty());
}

TEST(vad_digital_silence_never_triggers) {
    const std::vector<float> zeros(16000 * 10, 0.0f);
    CHECK(RunVad(zeros, 16000).onsets.empty());
    CHECK(bspeech::FrameDbfs(zeros.data(), 160) < -100.0f);
}

TEST(vad_adapts_to_a_steady_hum) {
    std::vector<float> mono = Tone(16000 * 8, 100.0, 0.025f, 16000);
    std::vector<float> speech = bt::ToneSpeech({{5, 6, 7}}, bt::ToneSpeechOptions{});

    for (size_t i = 0; i < speech.size(); ++i) {
        speech[i] += 0.025f * static_cast<float>(std::sin(2.0 * 3.14159265358979323846 * 100.0 *
                                                          static_cast<double>(mono.size() + i) / 16000.0));
    }

    Append(&mono, speech);
    const Events e = RunVad(mono, 16000);

    CHECK(e.onsets.size() >= 2);
    CHECK(e.offsets.size() >= 2);
    CHECK(e.offsets[0] < 500);

    bool speech_found = false;

    for (const int64_t onset : e.onsets) {
        speech_found = speech_found || (onset >= 800 + 50 && onset <= 800 + 62);
    }

    CHECK(speech_found);
}

TEST(vad_calibration_sets_thresholds_between_noise_and_speech) {
    bt::ToneSpeechOptions o;
    o.noise = 0.003f;
    o.lead_ms = 2000;
    o.pause_ms = 2000;
    const auto mono = bt::ToneSpeech({{1, 2, 3, 4, 5, 6, 7, 8}, {9, 10, 11, 12, 13, 14}}, o);
    const std::string path = Fresh("calibrate.wav");
    bt::WriteWav(path, 16000, 1, 16, false, false, mono, false);

    bs_vad_settings settings{};
    settings.struct_size = sizeof settings;
    CHECK_EQ(bs_vad_defaults(&settings), BS_OK);
    float noise = 0.0f;
    float speech = 0.0f;
    CHECK_EQ(bs_vad_calibrate(path.c_str(), &settings, &noise, &speech), BS_OK);
    CHECK(noise < -50.0f);
    CHECK(speech > -20.0f);
    CHECK(settings.absolute_floor_dbfs > noise && settings.absolute_floor_dbfs < speech);
    CHECK(settings.onset_db >= 6.0f && settings.onset_db <= 20.0f);
    CHECK_NEAR(settings.hangover_ms, 450.0, 1e-6);

    bspeech::VadSettings tuned;
    tuned.onset_db = settings.onset_db;
    tuned.absolute_floor_dbfs = settings.absolute_floor_dbfs;
    CHECK_EQ(RunVad(mono, 16000, tuned).onsets.size(), size_t{2});

    const std::string flat = Fresh("calibrate_flat.wav");
    bt::WriteWav(flat, 16000, 1, 16, false, false, Tone(16000 * 2, 440.0, 0.2f, 16000), false);
    CHECK_EQ(bs_vad_calibrate(flat.c_str(), &settings, &noise, &speech), BS_ASSET);

    const std::string tiny = Fresh("calibrate_tiny.wav");
    bt::WriteWav(tiny, 16000, 1, 16, false, false, std::vector<float>(1000, 0.1f), false);
    CHECK_EQ(bs_vad_calibrate(tiny.c_str(), &settings, &noise, &speech), BS_ASSET);
    CHECK_EQ(bs_vad_calibrate(Fresh("calibrate_missing.wav").c_str(), &settings, &noise, &speech), BS_ASSET);

    bs_vad_settings small{};
    small.struct_size = 4;
    CHECK_EQ(bs_vad_defaults(&small), BS_INVALID_ARG);
}

TEST(score_counts_word_and_term_errors) {
    bspeech::Lexicon lexicon(16);
    CHECK(lexicon.Add("GGUF", 1.0));
    CHECK(lexicon.Add("llama.cpp", 1.0));
    lexicon.Freeze();

    const auto exact = bspeech::ScoreTranscript("Load the GGUF file with llama.cpp.", "load the GGUF file with llama.cpp",
                                                &lexicon);
    CHECK_EQ(exact.reference_words, size_t{6});
    CHECK_EQ(exact.word_errors, size_t{0});
    CHECK_EQ(exact.term_total, size_t{2});
    CHECK_EQ(exact.term_errors, size_t{0});

    const auto wrong = bspeech::ScoreTranscript("Load the GGUF file with llama.cpp", "load a gguf with lama dot cpp",
                                                &lexicon);
    CHECK_EQ(wrong.word_errors, size_t{5});
    CHECK_NEAR(wrong.word_error_rate, 5.0 / 6.0, 1e-9);
    CHECK_EQ(wrong.term_total, size_t{2});
    CHECK_EQ(wrong.term_errors, size_t{2});
    CHECK_NEAR(wrong.term_error_rate, 1.0, 1e-9);

    const auto empty = bspeech::ScoreTranscript("", "", nullptr);
    CHECK_EQ(empty.reference_words, size_t{0});
    CHECK_NEAR(empty.word_error_rate, 0.0, 1e-9);

    const auto words = bspeech::ScoringWords("\"Hello,\" (world)!  ok?");
    CHECK_EQ(words.size(), size_t{3});
    CHECK_EQ(words[0], std::string("hello"));
    CHECK_EQ(words[1], std::string("world"));
    CHECK_EQ(words[2], std::string("ok"));
}

TEST(stream_reader_reads_every_frame_in_blocks) {
    for (const bool extensible : {false, true}) {
        const uint16_t channels = 2;
        std::vector<float> interleaved;

        for (int i = 0; i < 10007; ++i) {
            const float v = static_cast<float>(std::sin(i * 0.01) * 0.5);
            interleaved.push_back(v);
            interleaved.push_back(-v);
        }

        const std::string path = Fresh(extensible ? "stream_ext.wav" : "stream.wav");
        bt::WriteWav(path, 44100, channels, 24, false, extensible, interleaved, true);

        bspeech::WavStreamReader reader;
        bspeech::WavInfo info;
        std::string error;
        CHECK(reader.Open(path, &info, &error));
        CHECK_EQ(info.sample_rate, 44100);
        CHECK_EQ(info.channels, 2);
        CHECK_EQ(info.frames, int64_t{10007});

        std::vector<float> block(1000);
        size_t total = 0;
        double peak = 0.0;

        while (true) {
            size_t n = 0;
            CHECK(reader.Read(block.data(), block.size(), &n, &error));

            if (n == 0) {
                break;
            }

            for (size_t k = 0; k < n; ++k) {
                peak = std::max(peak, std::fabs(static_cast<double>(block[k])));
            }

            total += n;
        }

        CHECK_EQ(total, size_t{10007});
        CHECK(peak < 1e-3);
    }
}

TEST(stream_reader_rejects_corrupt_and_truncated_files) {
    const std::string path = Fresh("stream_corrupt.wav");
    bt::WriteWav(path, 16000, 1, 16, false, false, std::vector<float>(4000, 0.25f), false);
    auto bytes = bt::ReadBytes(path);

    auto garbled = bytes;
    garbled[0] = 'X';
    bt::WriteBytes(path, garbled);
    bspeech::WavStreamReader a;
    bspeech::WavInfo info;
    std::string error;
    CHECK(!a.Open(path, &info, &error));
    CHECK(!error.empty());

    auto truncated = bytes;
    truncated.resize(truncated.size() - 1000);
    bt::WriteBytes(path, truncated);
    bspeech::WavStreamReader b;
    error.clear();
    CHECK(!b.Open(path, &info, &error));
    CHECK(!error.empty());

    auto bad_format = bytes;
    bad_format[20] = 7;
    bt::WriteBytes(path, bad_format);
    bspeech::WavStreamReader c;
    CHECK(!c.Open(path, &info, &error));

    bspeech::WavStreamReader d;
    CHECK(!d.Open(Fresh("stream_missing.wav"), &info, &error));
}
