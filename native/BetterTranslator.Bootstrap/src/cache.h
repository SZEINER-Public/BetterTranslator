#pragma once

#include <windows.h>

#include <string>

#include "support.h"

namespace btpay {

struct CacheLayout {
    std::wstring root;
    std::wstring runtimeRoot;
    std::wstring active;
    std::wstring staging;
    std::wstring marker;
    std::wstring logPath;
    bool usingFallback = false;
};

[[nodiscard]] std::wstring DefaultCacheRoot();
[[nodiscard]] std::wstring FallbackCacheRoot();
[[nodiscard]] CacheLayout BuildLayout(const std::wstring& root, const std::wstring& hashPrefix);
[[nodiscard]] bool MarkerMatches(const std::wstring& markerPath, const std::wstring& hashHex) noexcept;
[[nodiscard]] Status WriteMarker(const std::wstring& markerPath, const std::wstring& hashHex);

class ExtractionLock {
public:
    ExtractionLock() = default;
    ExtractionLock(const ExtractionLock&) = delete;
    ExtractionLock& operator=(const ExtractionLock&) = delete;
    ExtractionLock(ExtractionLock&&) = delete;
    ExtractionLock& operator=(ExtractionLock&&) = delete;
    ~ExtractionLock();

    [[nodiscard]] Status Acquire(const std::wstring& hashPrefix, DWORD timeoutMilliseconds);
    [[nodiscard]] bool Held() const noexcept { return held_; }
    [[nodiscard]] bool TimedOut() const noexcept { return timedOut_; }
    void Release() noexcept;

private:
    ScopedHandle mutex_;
    bool held_ = false;
    bool timedOut_ = false;
};

void CollectStaleRuntimes(const std::wstring& runtimeRoot, const std::wstring& keepDirectory,
                          uint32_t maximumAgeDays) noexcept;

}
