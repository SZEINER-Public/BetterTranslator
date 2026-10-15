#if !defined(_WIN32)

#include "capture.h"
#include "recognize.h"

namespace bspeech {

bool CaptureBackendAvailable() noexcept { return false; }

Failure OpenCaptureDevice(const std::string&, std::unique_ptr<CaptureDevice>*) {
    return {BS_UNAVAILABLE, "this build has no microphone capture; capture is implemented for Windows through WASAPI",
            -1};
}

Failure CaptureDeviceCount(int32_t* count) {
    *count = 0;
    return {};
}

Failure CaptureDeviceAt(int32_t, std::string*, std::string*) {
    return {BS_UNAVAILABLE, "this build has no microphone capture", -1};
}

}

#endif
