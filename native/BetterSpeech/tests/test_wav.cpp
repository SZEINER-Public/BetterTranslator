#include <cmath>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <string>
#include <vector>

#include "better_speech.h"
#include "harness.h"
#include "wav.h"
#include "wav_fixtures.h"

using namespace bspeech;

TEST(wav_writer_round_trip) {
    const std::string path = bt::TempPath("round_trip.wav");
    std::filesystem::remove(path);

    std::vector<float> samples(24000);

    for (size_t i = 0; i < samples.size(); ++i) {
        samples[i] = 0.4f * static_cast<float>(std::sin(2.0 * 3.14159265358979 * 440.0 * i / 24000.0));
    }

    WavWriter writer;
    std::string error;
    CHECK(writer.Open(path, 24000, &error));
    CHECK(std::filesystem::exists(path + ".partial"));
    CHECK(!std::filesystem::exists(path));

    for (size_t at = 0; at < samples.size(); at += 1000) {
        CHECK(writer.Write(samples.data() + at, std::min<size_t>(1000, samples.size() - at), &error));
    }

    CHECK(writer.Finish(&error));
    CHECK(std::filesystem::exists(path));
    CHECK(!std::filesystem::exists(path + ".partial"));
    CHECK_EQ(std::filesystem::file_size(path), 44u + 48000u);

    const auto bytes = bt::ReadBytes(path);
    uint32_t riff = 0;
    uint32_t data = 0;
    std::memcpy(&riff, bytes.data() + 4, 4);
    std::memcpy(&data, bytes.data() + 40, 4);
    CHECK_EQ(riff, 36u + 48000u);
    CHECK_EQ(data, 48000u);

    WavInfo info;
    std::vector<float> back;
    CHECK(ReadWavMono(path, 0, &back, &info, &error));
    CHECK_EQ(info.sample_rate, 24000);
    CHECK_EQ(info.channels, 1);
    CHECK_EQ(info.frames, 24000);

    for (size_t i = 0; i < samples.size(); ++i) {
        CHECK_NEAR(back[i], samples[i], 1.0 / 32767.0 + 1e-6);
    }
}

TEST(wav_writer_abort_leaves_nothing) {
    const std::string path = bt::TempPath("aborted.wav");
    std::filesystem::remove(path);

    {
        WavWriter writer;
        std::string error;
        CHECK(writer.Open(path, 24000, &error));
        const std::vector<float> some(4000, 0.1f);
        CHECK(writer.Write(some.data(), some.size(), &error));
        writer.Abort();
    }

    CHECK(!std::filesystem::exists(path));
    CHECK(!std::filesystem::exists(path + ".partial"));

    {
        // Destroyed without Finish: the same as an abort.
        WavWriter writer;
        std::string error;
        CHECK(writer.Open(path, 24000, &error));
    }

    CHECK(!std::filesystem::exists(path + ".partial"));
}

TEST(wav_writer_refuses_missing_folder) {
    WavWriter writer;
    std::string error;
    CHECK(!writer.Open(bt::TempPath("no/such/folder/x.wav"), 24000, &error));
    CHECK(!error.empty());
}

TEST(wav_reader_formats) {
    std::string error;
    WavInfo info;
    std::vector<float> samples;

    // Stereo float, extensible header, at 16 kHz: downmixed to mono.
    const std::string ext = bt::TempPath("ext_float_stereo.wav");
    bt::WriteWav(ext, 16000, 2, 32, true, true, {0.5f, -0.5f, 0.25f, 0.75f}, false);
    CHECK(ReadWavMono(ext, 0, &samples, &info, &error));
    CHECK_EQ(info.channels, 2);
    CHECK(info.is_float);
    CHECK_EQ(samples.size(), 2u);
    CHECK_NEAR(samples[0], 0.0, 1e-7);
    CHECK_NEAR(samples[1], 0.5, 1e-7);

    // 24-bit PCM with an odd-sized LIST chunk in front of the data.
    const std::string pcm24 = bt::TempPath("pcm24_list.wav");
    bt::WriteWav(pcm24, 44100, 1, 24, false, false, {0.5f, -0.25f, 0.0f}, true);
    CHECK(ReadWavMono(pcm24, 0, &samples, &info, &error));
    CHECK_EQ(info.bits_per_sample, 24);
    CHECK_EQ(samples.size(), 3u);
    CHECK_NEAR(samples[0], 0.5, 1e-6);
    CHECK_NEAR(samples[1], -0.25, 1e-6);

    // 32-bit PCM.
    const std::string pcm32 = bt::TempPath("pcm32.wav");
    bt::WriteWav(pcm32, 48000, 1, 32, false, false, {0.125f}, false);
    CHECK(ReadWavMono(pcm32, 0, &samples, &info, &error));
    CHECK_NEAR(samples[0], 0.125, 1e-6);
}

TEST(wav_reader_refuses_broken_files) {
    std::string error;
    WavInfo info;
    std::vector<float> samples;

    const std::string missing = bt::TempPath("does_not_exist.wav");
    std::filesystem::remove(missing);
    CHECK(!ReadWavMono(missing, 0, &samples, &info, &error));

    const std::string text = bt::TempPath("not_a_wav.wav");
    bt::WriteBytes(text, std::vector<uint8_t>{'h', 'e', 'l', 'l', 'o', ' ', 'w', 'o', 'r', 'l', 'd', '!', '!'});
    CHECK(!ReadWavMono(text, 0, &samples, &info, &error));
    CHECK(error.find("RIFF") != std::string::npos);

    // A data chunk claiming more bytes than the file holds: cut off mid-copy.
    const std::string truncated = bt::TempPath("truncated.wav");
    bt::WriteWav(truncated, 24000, 1, 16, false, false, std::vector<float>(1000, 0.1f), false);
    auto bytes = bt::ReadBytes(truncated);
    bytes.resize(bytes.size() - 500);
    bt::WriteBytes(truncated, bytes);
    CHECK(!ReadWavMono(truncated, 0, &samples, &info, &error));
    CHECK(error.find("truncated") != std::string::npos);

    // 8-bit PCM is not a supported clip format.
    const std::string eight = bt::TempPath("pcm8.wav");
    bt::WriteWav(eight, 24000, 1, 8, false, false, {0.1f, 0.2f}, false);
    CHECK(!ReadWavMono(eight, 0, &samples, &info, &error));

    // A header with no audio after it.
    const std::string empty = bt::TempPath("empty_data.wav");
    bt::WriteWav(empty, 24000, 1, 16, false, false, {}, false);
    CHECK(!ReadWavMono(empty, 0, &samples, &info, &error));

    // Longer than the caller allows.
    const std::string longer = bt::TempPath("long.wav");
    bt::WriteWav(longer, 8000, 1, 16, false, false, std::vector<float>(8000, 0.1f), false);
    CHECK(!ReadWavMono(longer, 4000, &samples, &info, &error));
    CHECK(ReadWavMono(longer, 8000, &samples, &info, &error));
}

TEST(abi_wav_probe) {
    const std::string path = bt::TempPath("probe.wav");
    bt::WriteWav(path, 22050, 2, 16, false, false, {0.1f, 0.1f, 0.2f, 0.2f}, false);

    bs_wav_info info{};
    info.struct_size = sizeof info;
    CHECK_EQ(bs_wav_probe(path.c_str(), &info), BS_OK);
    CHECK_EQ(info.sample_rate, 22050);
    CHECK_EQ(info.channels, 2);
    CHECK_EQ(info.frames, 2);

    CHECK_EQ(bs_wav_probe(bt::TempPath("nope.wav").c_str(), &info), BS_ASSET);
}
