#pragma once

// Writes WAV files of shapes the engine's own writer never produces, so the reader
// is tested against more than its sibling.

#include <cmath>
#include <cstdint>
#include <cstring>
#include <fstream>
#include <iterator>
#include <string>
#include <vector>

namespace bt {

inline std::vector<uint8_t> ReadBytes(const std::string& path) {
    std::ifstream in(path, std::ios::binary);
    return {std::istreambuf_iterator<char>(in), std::istreambuf_iterator<char>()};
}

inline void WriteBytes(const std::string& path, const std::vector<uint8_t>& bytes) {
    std::ofstream out(path, std::ios::binary | std::ios::trunc);
    out.write(reinterpret_cast<const char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
}

// samples are interleaved; bits 8, 16, 24 or 32; float only at 32.
inline void WriteWav(const std::string& path, uint32_t rate, uint16_t channels, uint16_t bits, bool is_float,
                     bool extensible, const std::vector<float>& samples, bool odd_list_chunk) {
    std::vector<uint8_t> b;
    auto put = [&](const void* p, size_t n) {
        const auto* c = static_cast<const uint8_t*>(p);
        b.insert(b.end(), c, c + n);
    };
    auto u16 = [&](uint16_t v) { put(&v, 2); };
    auto u32 = [&](uint32_t v) { put(&v, 4); };

    const uint16_t align = static_cast<uint16_t>(channels * bits / 8);
    const uint32_t data_bytes = static_cast<uint32_t>(samples.size() / (channels ? channels : 1) * align);

    put("RIFF", 4);
    u32(0);
    put("WAVE", 4);
    put("fmt ", 4);
    u32(extensible ? 40 : 16);
    u16(extensible ? 0xFFFE : (is_float ? 3 : 1));
    u16(channels);
    u32(rate);
    u32(rate * align);
    u16(align);
    u16(bits);

    if (extensible) {
        u16(22);
        u16(bits);
        u32(channels == 2 ? 3u : 4u);
        // Subformat GUID: the format tag, then the fixed KSDATAFORMAT tail.
        const uint8_t guid[16] = {static_cast<uint8_t>(is_float ? 3 : 1), 0, 0, 0, 0, 0, 0x10, 0,
                                  0x80, 0, 0, 0xAA, 0, 0x38, 0x9B, 0x71};
        put(guid, 16);
    }

    if (odd_list_chunk) {
        put("LIST", 4);
        u32(5);
        put("INFOx", 5);
        b.push_back(0);  // pad byte
    }

    put("data", 4);
    u32(data_bytes);

    for (const float s : samples) {
        if (is_float) {
            put(&s, 4);
        } else if (bits == 8) {
            b.push_back(static_cast<uint8_t>(128 + std::lround(s * 127)));
        } else if (bits == 16) {
            u16(static_cast<uint16_t>(static_cast<int16_t>(std::lround(s * 32767))));
        } else if (bits == 24) {
            const auto v = static_cast<int32_t>(std::lround(s * 8388607));
            b.push_back(static_cast<uint8_t>(v & 0xFF));
            b.push_back(static_cast<uint8_t>((v >> 8) & 0xFF));
            b.push_back(static_cast<uint8_t>((v >> 16) & 0xFF));
        } else {
            u32(static_cast<uint32_t>(static_cast<int32_t>(std::llround(s * 2147483647.0))));
        }
    }

    const uint32_t riff = static_cast<uint32_t>(b.size() - 8);
    std::memcpy(b.data() + 4, &riff, 4);
    WriteBytes(path, b);
}

}  // namespace bt
