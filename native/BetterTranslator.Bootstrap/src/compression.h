#pragma once

#include <cstdint>
#include <vector>

#include "support.h"

namespace btpay {

class Decompressor {
public:
    explicit Decompressor(uint32_t algorithm);
    Decompressor(const Decompressor&) = delete;
    Decompressor& operator=(const Decompressor&) = delete;
    Decompressor(Decompressor&&) = delete;
    Decompressor& operator=(Decompressor&&) = delete;
    ~Decompressor();

    [[nodiscard]] Status Ready() const { return ready_; }
    [[nodiscard]] Status Expand(const uint8_t* source, size_t sourceBytes, uint8_t* destination,
                                size_t destinationBytes) const;

private:
    void* handle_ = nullptr;
    uint32_t algorithm_ = 0;
    Status ready_;
};

[[nodiscard]] const wchar_t* AlgorithmName(uint32_t algorithm) noexcept;

}
