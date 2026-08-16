#pragma once

#include <atomic>
#include <cstdint>
#include <string>

#include "cache.h"
#include "payload.h"
#include "support.h"

namespace btpay {

struct ExtractionProgress {
    std::atomic<uint64_t> bytesWritten{0};
    std::atomic<uint32_t> filesWritten{0};
    uint64_t totalBytes = 0;
    uint32_t totalFiles = 0;
};

[[nodiscard]] Status ExtractTo(const Payload& payload, const std::wstring& destination,
                               ExtractionProgress& progress);

[[nodiscard]] Status MaterializeCache(const Payload& payload, CacheLayout& layout,
                                      ExtractionProgress& progress);

[[nodiscard]] Status VerifyEntries(const Payload& payload, uint32_t& verifiedCount);

}
