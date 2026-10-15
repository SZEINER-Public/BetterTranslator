#include "vad.h"

#include <algorithm>
#include <cmath>

namespace bspeech {

namespace {

constexpr float kFastAdapt = 0.3f;
constexpr float kSlowAdapt = 0.01f;
constexpr float kTrackRise = 0.1f;
constexpr size_t kBlockFrames = 10;
constexpr size_t kTrackBlocks = 30;

int64_t Frames(float ms, size_t frame, int32_t rate) {
    const double samples = static_cast<double>(ms) * rate / 1000.0;
    return std::max<int64_t>(1, static_cast<int64_t>(std::ceil(samples / static_cast<double>(frame))));
}

}

float FrameDbfs(const float* frame, size_t n) noexcept {
    double acc = 0.0;

    for (size_t i = 0; i < n; ++i) {
        acc += static_cast<double>(frame[i]) * frame[i];
    }

    return static_cast<float>(10.0 * std::log10(acc / static_cast<double>(std::max<size_t>(n, 1)) + 1e-12));
}

Vad::Vad(int32_t sample_rate, const VadSettings& settings)
    : frame_(std::max<size_t>(1, static_cast<size_t>(sample_rate / 100))),
      onset_db_(settings.onset_db),
      absolute_floor_(settings.absolute_floor_dbfs),
      min_speech_frames_(Frames(settings.min_speech_ms, frame_, sample_rate)),
      hangover_frames_(Frames(settings.hangover_ms, frame_, sample_rate)),
      floor_(settings.absolute_floor_dbfs - 10.0f),
      block_min_(kTrackBlocks, 0.0f) {}

Vad::Event Vad::Push(const float* frame) noexcept {
    const float db = FrameDbfs(frame, frame_);
    Track(db);
    const float threshold = std::max(floor_ + onset_db_, absolute_floor_);
    const bool voiced = db >= threshold;
    Event event = Event::None;
    last_frame_voiced_ = voiced;

    if (!active_) {
        if (voiced) {
            if (run_ == 0) {
                run_start_ = index_;
            }

            ++run_;

            if (run_ >= min_speech_frames_) {
                active_ = true;
                onset_frame_ = run_start_;
                last_voiced_ = index_;
                silence_ = 0;
                event = Event::Onset;
            }
        } else {
            run_ = 0;
            const float rate = db < floor_ ? kFastAdapt : kSlowAdapt;
            floor_ += (db - floor_) * rate;
        }
    } else if (voiced) {
        last_voiced_ = index_;
        silence_ = 0;
    } else if (++silence_ >= hangover_frames_) {
        active_ = false;
        run_ = 0;
        event = Event::Offset;
    }

    ++index_;
    return event;
}

void Vad::Track(float db) noexcept {
    current_min_ = block_frames_ == 0 ? db : std::min(current_min_, db);

    if (++block_frames_ < kBlockFrames) {
        return;
    }

    block_min_[blocks_seen_ % kTrackBlocks] = current_min_;
    ++blocks_seen_;
    block_frames_ = 0;

    if (blocks_seen_ < kTrackBlocks) {
        return;
    }

    const float tracked = *std::min_element(block_min_.begin(), block_min_.end());

    if (tracked > floor_) {
        floor_ += (tracked - floor_) * kTrackRise;
    }
}

bool CalibrateVad(const std::vector<float>& samples, int32_t sample_rate, const VadSettings& base,
                  VadCalibration* out) {
    const size_t frame = std::max<size_t>(1, static_cast<size_t>(sample_rate / 100));

    if (samples.size() < frame * 50) {
        return false;
    }

    std::vector<float> levels;
    levels.reserve(samples.size() / frame);

    for (size_t at = 0; at + frame <= samples.size(); at += frame) {
        levels.push_back(FrameDbfs(samples.data() + at, frame));
    }

    std::sort(levels.begin(), levels.end());
    const float noise = levels[levels.size() / 10];
    const float speech = levels[levels.size() * 9 / 10];

    if (speech - noise < 6.0f) {
        return false;
    }

    out->noise_dbfs = noise;
    out->speech_dbfs = speech;
    out->settings = base;
    out->settings.onset_db = std::clamp((speech - noise) * 0.4f, 6.0f, 20.0f);
    out->settings.absolute_floor_dbfs = std::min(noise + (speech - noise) * 0.25f, speech - 6.0f);
    return true;
}

}
