#pragma once

#include <cstddef>
#include <cstdint>
#include <vector>

namespace bspeech {

struct VadSettings {
    float onset_db = 12.0f;
    float absolute_floor_dbfs = -50.0f;
    float min_speech_ms = 100.0f;
    float hangover_ms = 450.0f;
    float pre_roll_ms = 200.0f;
};

class Vad {
public:
    enum class Event { None, Onset, Offset };

    Vad(int32_t sample_rate, const VadSettings& settings);

    [[nodiscard]] size_t FrameSamples() const noexcept { return frame_; }
    [[nodiscard]] bool Active() const noexcept { return active_; }
    [[nodiscard]] int64_t OnsetFrame() const noexcept { return onset_frame_; }
    [[nodiscard]] int64_t LastVoicedFrame() const noexcept { return last_voiced_; }
    [[nodiscard]] float NoiseFloorDb() const noexcept { return floor_; }
    [[nodiscard]] bool LastFrameVoiced() const noexcept { return last_frame_voiced_; }

    Event Push(const float* frame) noexcept;

private:
    void Track(float db) noexcept;

    size_t frame_;
    float onset_db_;
    float absolute_floor_;
    int64_t min_speech_frames_;
    int64_t hangover_frames_;
    float floor_;
    bool active_ = false;
    bool last_frame_voiced_ = false;
    int64_t index_ = 0;
    int64_t run_ = 0;
    int64_t run_start_ = 0;
    int64_t silence_ = 0;
    int64_t onset_frame_ = -1;
    int64_t last_voiced_ = -1;
    std::vector<float> block_min_;
    size_t blocks_seen_ = 0;
    size_t block_frames_ = 0;
    float current_min_ = 0.0f;
};

[[nodiscard]] float FrameDbfs(const float* frame, size_t n) noexcept;

struct VadCalibration {
    float noise_dbfs = 0.0f;
    float speech_dbfs = 0.0f;
    VadSettings settings;
};

[[nodiscard]] bool CalibrateVad(const std::vector<float>& samples, int32_t sample_rate, const VadSettings& base,
                                VadCalibration* out);

}
