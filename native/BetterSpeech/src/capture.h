#pragma once

#include <cstdint>
#include <memory>
#include <string>

#include "engine.h"

namespace bspeech {

class RecognitionSession;

class CaptureDevice {
public:
    virtual ~CaptureDevice() = default;

    [[nodiscard]] virtual int32_t SampleRate() const noexcept = 0;
    [[nodiscard]] virtual int32_t Channels() const noexcept = 0;
    [[nodiscard]] virtual Failure Start(RecognitionSession* session) = 0;
    virtual void Stop() noexcept = 0;
    [[nodiscard]] virtual int64_t Frames() const noexcept = 0;
    [[nodiscard]] virtual int64_t Glitches() const noexcept = 0;
};

[[nodiscard]] Failure OpenCaptureDevice(const std::string& device_id, std::unique_ptr<CaptureDevice>* out);

[[nodiscard]] Failure CaptureDeviceCount(int32_t* count);

[[nodiscard]] Failure CaptureDeviceAt(int32_t index, std::string* id, std::string* name);

}
