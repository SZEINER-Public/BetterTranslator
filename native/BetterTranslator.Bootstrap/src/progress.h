#pragma once

#include <cstdint>
#include <thread>

#include "extract.h"

namespace btpay {

class ProgressWindow {
public:
    ProgressWindow() = default;
    ProgressWindow(const ProgressWindow&) = delete;
    ProgressWindow& operator=(const ProgressWindow&) = delete;
    ProgressWindow(ProgressWindow&&) = delete;
    ProgressWindow& operator=(ProgressWindow&&) = delete;
    ~ProgressWindow();

    void Start(const ExtractionProgress& progress, uint32_t delayMilliseconds);
    void Stop() noexcept;

private:
    std::thread thread_;
    std::atomic<bool> stopping_{false};
};

}
