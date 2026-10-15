#include "wav.h"

#include <algorithm>
#include <array>
#include <bit>
#include <cmath>
#include <cstring>
#include <system_error>

#include "dsp.h"

namespace bspeech {

static_assert(std::endian::native == std::endian::little, "the WAV code writes host byte order");

namespace {

constexpr uint32_t kHeaderBytes = 44;
constexpr uint64_t kMaxDataBytes = 0xFFFFFFFFull - 36;
constexpr size_t kBufferSamples = 4096;

std::FILE* OpenFile(const std::filesystem::path& path, const char* mode) {
#if defined(_WIN32)
    std::wstring wide_mode(mode, mode + std::strlen(mode));
    return _wfopen(path.c_str(), wide_mode.c_str());
#else
    return std::fopen(path.c_str(), mode);
#endif
}

bool Seek(std::FILE* file, uint64_t offset) {
#if defined(_WIN32)
    return _fseeki64(file, static_cast<__int64>(offset), SEEK_SET) == 0;
#else
    return fseeko(file, static_cast<off_t>(offset), SEEK_SET) == 0;
#endif
}

void Put16(uint8_t* p, uint16_t v) { std::memcpy(p, &v, 2); }

void Put32(uint8_t* p, uint32_t v) { std::memcpy(p, &v, 4); }

uint16_t Get16(const uint8_t* p) {
    uint16_t v = 0;
    std::memcpy(&v, p, 2);
    return v;
}

uint32_t Get32(const uint8_t* p) {
    uint32_t v = 0;
    std::memcpy(&v, p, 4);
    return v;
}

struct Layout {
    WavInfo info;
    int32_t block_align = 0;
    uint64_t data_offset = 0;
    uint64_t data_bytes = 0;
};

bool ReadLayout(std::FILE* file, uint64_t file_size, Layout* layout, std::string* error) {
    uint8_t riff[12];

    if (std::fread(riff, 1, sizeof riff, file) != sizeof riff || std::memcmp(riff, "RIFF", 4) != 0 ||
        std::memcmp(riff + 8, "WAVE", 4) != 0) {
        *error = "not a RIFF/WAVE file";
        return false;
    }

    bool have_fmt = false;
    bool have_data = false;
    uint16_t format = 0;
    uint64_t position = 12;

    while (position + 8 <= file_size && !(have_fmt && have_data)) {
        uint8_t head[8];

        if (!Seek(file, position) ||
            std::fread(head, 1, sizeof head, file) != sizeof head) {
            break;
        }

        const uint32_t size = Get32(head + 4);
        const uint64_t body = position + 8;

        if (std::memcmp(head, "fmt ", 4) == 0) {
            if (size < 16 || body + size > file_size) {
                *error = "the fmt chunk is truncated";
                return false;
            }

            uint8_t fmt[40] = {};
            const size_t take = std::min<size_t>(size, sizeof fmt);

            if (std::fread(fmt, 1, take, file) != take) {
                *error = "the fmt chunk could not be read";
                return false;
            }

            format = Get16(fmt);
            layout->info.channels = Get16(fmt + 2);
            layout->info.sample_rate = static_cast<int32_t>(Get32(fmt + 4));
            layout->block_align = Get16(fmt + 12);
            layout->info.bits_per_sample = Get16(fmt + 14);

            // WAVE_FORMAT_EXTENSIBLE carries the real format in the first two bytes
            // of its subformat GUID.
            if (format == 0xFFFE) {
                if (size < 40) {
                    *error = "an extensible fmt chunk shorter than 40 bytes";
                    return false;
                }

                format = Get16(fmt + 24);
            }

            have_fmt = true;
        } else if (std::memcmp(head, "data", 4) == 0) {
            if (body + size > file_size) {
                *error = "the data chunk runs past the end of the file; it is truncated or was never finished";
                return false;
            }

            layout->data_offset = body;
            layout->data_bytes = size;
            have_data = true;
        }

        position = body + size + (size & 1u);
    }

    if (!have_fmt) {
        *error = "no fmt chunk";
        return false;
    }

    if (!have_data) {
        *error = "no data chunk";
        return false;
    }

    WavInfo& info = layout->info;

    if (format == 1 && (info.bits_per_sample == 16 || info.bits_per_sample == 24 || info.bits_per_sample == 32)) {
        info.is_float = false;
    } else if (format == 3 && info.bits_per_sample == 32) {
        info.is_float = true;
    } else {
        *error = "unsupported sample format " + std::to_string(format) + " at " +
                 std::to_string(info.bits_per_sample) + " bits; use PCM 16, 24 or 32-bit, or float 32-bit";
        return false;
    }

    if (info.channels < 1 || info.channels > 8) {
        *error = "unsupported channel count " + std::to_string(info.channels);
        return false;
    }

    if (info.sample_rate < 8000 || info.sample_rate > 192000) {
        *error = "unsupported sample rate " + std::to_string(info.sample_rate);
        return false;
    }

    if (layout->block_align != info.channels * info.bits_per_sample / 8) {
        *error = "the block alignment does not match the channels and sample width";
        return false;
    }

    info.frames = static_cast<int64_t>(layout->data_bytes / static_cast<uint64_t>(layout->block_align));

    if (info.frames == 0) {
        *error = "the data chunk holds no audio";
        return false;
    }

    return true;
}

float Sample(const uint8_t* p, int32_t bits, bool is_float) {
    if (is_float) {
        float v = 0;
        std::memcpy(&v, p, 4);
        return std::isfinite(v) ? std::clamp(v, -1.0f, 1.0f) : 0.0f;
    }

    switch (bits) {
        case 16:
            return static_cast<float>(static_cast<int16_t>(Get16(p))) / 32768.0f;
        case 24: {
            int32_t v = p[0] | (p[1] << 8) | (p[2] << 16);

            if ((v & 0x800000) != 0) {
                v |= ~0xFFFFFF;
            }

            return static_cast<float>(v) / 8388608.0f;
        }
        default:
            return static_cast<float>(static_cast<int32_t>(Get32(p)) / 2147483648.0);
    }
}

bool FileSize(const std::filesystem::path& path, uint64_t* size, std::string* error) {
    std::error_code ec;

    if (!std::filesystem::is_regular_file(path, ec)) {
        *error = "no such file";
        return false;
    }

    *size = std::filesystem::file_size(path, ec);

    if (ec) {
        *error = "the file size could not be read: " + ec.message();
        return false;
    }

    return true;
}

}  // namespace

std::filesystem::path PathFromUtf8(const std::string& utf8) {
    const std::u8string u8(reinterpret_cast<const char8_t*>(utf8.data()), utf8.size());
    return std::filesystem::path(u8);
}

WavWriter::~WavWriter() { Abort(); }

bool WavWriter::Open(const std::string& path_utf8, int32_t sample_rate, std::string* error) {
    Abort();

    final_ = PathFromUtf8(path_utf8);
    partial_ = final_;
    partial_ += ".partial";

    std::error_code ec;
    const auto parent = final_.parent_path();

    if (!parent.empty() && !std::filesystem::is_directory(parent, ec)) {
        *error = "the export folder does not exist";
        return false;
    }

    file_ = OpenFile(partial_, "wb");

    if (file_ == nullptr) {
        *error = "the export file could not be created";
        return false;
    }

    uint8_t header[kHeaderBytes] = {};
    std::memcpy(header, "RIFF", 4);
    Put32(header + 4, 36);
    std::memcpy(header + 8, "WAVE", 4);
    std::memcpy(header + 12, "fmt ", 4);
    Put32(header + 16, 16);
    Put16(header + 20, 1);
    Put16(header + 22, 1);
    Put32(header + 24, static_cast<uint32_t>(sample_rate));
    Put32(header + 28, static_cast<uint32_t>(sample_rate) * 2);
    Put16(header + 32, 2);
    Put16(header + 34, 16);
    std::memcpy(header + 36, "data", 4);
    Put32(header + 40, 0);

    if (std::fwrite(header, 1, sizeof header, file_) != sizeof header) {
        *error = "the export header could not be written";
        Abort();
        return false;
    }

    buffer_.resize(kBufferSamples);
    data_bytes_ = 0;
    return true;
}

bool WavWriter::Write(const float* data, size_t n, std::string* error) {
    if (file_ == nullptr) {
        *error = "the export file is not open";
        return false;
    }

    if (data_bytes_ + static_cast<uint64_t>(n) * 2 > kMaxDataBytes) {
        *error = "the export would pass the 4 GiB size limit of a WAV file";
        return false;
    }

    while (n > 0) {
        const size_t take = std::min(n, buffer_.size());
        dsp::FloatToInt16(data, take, buffer_.data());

        if (std::fwrite(buffer_.data(), sizeof(int16_t), take, file_) != take) {
            *error = "writing the export failed; the disk may be full";
            return false;
        }

        data_bytes_ += static_cast<uint64_t>(take) * 2;
        data += take;
        n -= take;
    }

    return true;
}

bool WavWriter::Finish(std::string* error) {
    if (file_ == nullptr) {
        *error = "the export file is not open";
        return false;
    }

    uint8_t riff[4];
    uint8_t data[4];
    Put32(riff, static_cast<uint32_t>(36 + data_bytes_));
    Put32(data, static_cast<uint32_t>(data_bytes_));

    const bool patched = std::fseek(file_, 4, SEEK_SET) == 0 && std::fwrite(riff, 1, 4, file_) == 4 &&
                         std::fseek(file_, 40, SEEK_SET) == 0 && std::fwrite(data, 1, 4, file_) == 4 &&
                         std::fflush(file_) == 0;

    const bool closed = std::fclose(file_) == 0;
    file_ = nullptr;

    if (!patched || !closed) {
        *error = "finishing the export header failed";
        std::error_code ec;
        std::filesystem::remove(partial_, ec);
        return false;
    }

    std::error_code ec;
    std::filesystem::rename(partial_, final_, ec);

    if (ec) {
        *error = "the finished export could not be moved into place: " + ec.message();
        std::filesystem::remove(partial_, ec);
        return false;
    }

    return true;
}

void WavWriter::Abort() noexcept {
    if (file_ == nullptr) {
        return;
    }

    std::fclose(file_);
    file_ = nullptr;

    std::error_code ec;
    std::filesystem::remove(partial_, ec);
}

bool ProbeWav(const std::string& path_utf8, WavInfo* info, std::string* error) {
    const auto path = PathFromUtf8(path_utf8);
    uint64_t size = 0;

    if (!FileSize(path, &size, error)) {
        return false;
    }

    std::FILE* file = OpenFile(path, "rb");

    if (file == nullptr) {
        *error = "the file could not be opened";
        return false;
    }

    Layout layout;
    const bool ok = ReadLayout(file, size, &layout, error);
    std::fclose(file);

    if (ok) {
        *info = layout.info;
    }

    return ok;
}

bool ReadWavMono(const std::string& path_utf8, int64_t max_frames, std::vector<float>* samples, WavInfo* info,
                 std::string* error) {
    const auto path = PathFromUtf8(path_utf8);
    uint64_t size = 0;

    if (!FileSize(path, &size, error)) {
        return false;
    }

    std::FILE* file = OpenFile(path, "rb");

    if (file == nullptr) {
        *error = "the file could not be opened";
        return false;
    }

    Layout layout;

    if (!ReadLayout(file, size, &layout, error)) {
        std::fclose(file);
        return false;
    }

    if (max_frames > 0 && layout.info.frames > max_frames) {
        *error = "the clip is " + std::to_string(layout.info.frames) + " frames long; the limit is " +
                 std::to_string(max_frames);
        std::fclose(file);
        return false;
    }

    if (!Seek(file, layout.data_offset)) {
        *error = "seeking to the audio failed";
        std::fclose(file);
        return false;
    }

    const int32_t channels = layout.info.channels;
    const int32_t bytes = layout.info.bits_per_sample / 8;
    const auto frames = static_cast<size_t>(layout.info.frames);

    samples->assign(frames, 0.0f);

    std::vector<uint8_t> block(static_cast<size_t>(layout.block_align) * 1024);
    size_t done = 0;

    while (done < frames) {
        const size_t take = std::min<size_t>(1024, frames - done);
        const size_t want = take * static_cast<size_t>(layout.block_align);

        if (std::fread(block.data(), 1, want, file) != want) {
            *error = "the audio could not be read";
            std::fclose(file);
            return false;
        }

        for (size_t f = 0; f < take; ++f) {
            float sum = 0.0f;
            const uint8_t* frame = block.data() + f * static_cast<size_t>(layout.block_align);

            for (int32_t c = 0; c < channels; ++c) {
                sum += Sample(frame + static_cast<size_t>(c * bytes), layout.info.bits_per_sample,
                              layout.info.is_float);
            }

            (*samples)[done + f] = sum / static_cast<float>(channels);
        }

        done += take;
    }

    std::fclose(file);
    *info = layout.info;
    return true;
}

}  // namespace bspeech
