#pragma once

#include <cstdint>
#include <cstdio>
#include <filesystem>
#include <string>
#include <vector>

namespace bspeech {

struct WavInfo {
    int32_t sample_rate = 0;
    int32_t channels = 0;
    int32_t bits_per_sample = 0;
    bool is_float = false;
    int64_t frames = 0;
};

// The path a UTF-8 string names, on every platform: a narrow path on Windows is in
// the ANSI code page, which cannot spell most of the names this has to open.
[[nodiscard]] std::filesystem::path PathFromUtf8(const std::string& utf8);

// Writes 16-bit mono PCM to <path>.partial while samples arrive, and only on Finish
// fixes the header and renames it to <path>. A failed or cancelled job leaves no
// file that looks finished. Memory use is one fixed conversion buffer.
class WavWriter {
public:
    WavWriter() = default;
    WavWriter(const WavWriter&) = delete;
    WavWriter& operator=(const WavWriter&) = delete;
    ~WavWriter();

    [[nodiscard]] bool Open(const std::string& path_utf8, int32_t sample_rate, std::string* error);
    [[nodiscard]] bool Write(const float* data, size_t n, std::string* error);
    [[nodiscard]] bool Finish(std::string* error);
    void Abort() noexcept;

    [[nodiscard]] bool IsOpen() const noexcept { return file_ != nullptr; }
    [[nodiscard]] uint64_t Frames() const noexcept { return data_bytes_ / 2; }

private:
    std::FILE* file_ = nullptr;
    std::filesystem::path final_;
    std::filesystem::path partial_;
    uint64_t data_bytes_ = 0;
    std::vector<int16_t> buffer_;
};

// Reads a PCM (16, 24 or 32-bit) or IEEE float (32-bit) WAV, plain or
// WAVE_FORMAT_EXTENSIBLE, and downmixes it to mono. Refuses anything malformed,
// truncated or longer than max_frames instead of reading what it can.
[[nodiscard]] bool ReadWavMono(const std::string& path_utf8, int64_t max_frames, std::vector<float>* samples,
                               WavInfo* info, std::string* error);

[[nodiscard]] bool ProbeWav(const std::string& path_utf8, WavInfo* info, std::string* error);

}  // namespace bspeech
