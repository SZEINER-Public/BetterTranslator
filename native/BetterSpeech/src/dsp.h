#pragma once

#include <cstddef>
#include <cstdint>
#include <memory>
#include <vector>

namespace bspeech::dsp {

// Streaming resampler for an arbitrary ratio: a Kaiser-windowed sinc evaluated from
// a phase table with linear interpolation between phases. The kernel widens when
// downsampling so its cutoff stays below the output Nyquist.
class Resampler {
public:
    // ratio is output rate over input rate.
    explicit Resampler(double ratio);

    void Process(const float* in, size_t n, std::vector<float>& out);

    // Emits the tail so the total output is round(total input * ratio), then resets.
    void Flush(std::vector<float>& out);

    void Reset();

    [[nodiscard]] double Ratio() const noexcept { return ratio_; }

private:
    void Drain(std::vector<float>& out, uint64_t limit);

    static constexpr int kPhases = 256;

    double ratio_;
    double step_;
    int half_;
    int taps_;
    std::vector<float> rows_;
    std::vector<float> buf_;
    double pos_ = 0.0;
    uint64_t in_total_ = 0;
    uint64_t out_total_ = 0;
};

// Waveform-similarity overlap-add time stretch. factor is output duration over
// input duration; pitch is untouched.
class TimeStretch {
public:
    TimeStretch(int sample_rate, double factor);

    void Process(const float* in, size_t n, std::vector<float>& out);

    // Emits the tail so the total output is round(total input * factor), then resets.
    void Flush(std::vector<float>& out);

    void Reset();

private:
    bool Step(bool flushing);
    void Emit(std::vector<float>& out, uint64_t upto, uint64_t limit);
    [[nodiscard]] int64_t BestOffset(int64_t nominal, int64_t target) const;

    int frame_;
    int hop_out_;
    int tolerance_;
    double hop_in_;
    double factor_;
    std::vector<float> window_;
    std::vector<float> first_window_;

    std::vector<float> in_;
    uint64_t in_base_ = 0;
    uint64_t in_total_ = 0;

    std::vector<float> ola_;
    uint64_t ola_base_ = 0;
    uint64_t out_total_ = 0;

    uint64_t frame_index_ = 0;
    int64_t prev_ = -1;
};

// Speaking rate and pitch, independent of each other: the signal is stretched by
// pitch / rate and then resampled by 1 / pitch, so the duration follows rate alone
// and the pitch follows pitch alone. Rate 1 and pitch 0 pass samples through
// untouched.
class VoiceChain {
public:
    VoiceChain(int sample_rate, float rate, float pitch_semitones);

    [[nodiscard]] bool Identity() const noexcept { return !stretch_ && !resample_; }

    void Process(const float* in, size_t n, std::vector<float>& out);

    // Ends a continuous stretch of audio, emitting what is held back.
    void Flush(std::vector<float>& out);

private:
    std::unique_ptr<TimeStretch> stretch_;
    std::unique_ptr<Resampler> resample_;
    std::vector<float> scratch_;
};

// Gain followed by a peak limiter with instant attack, so no sample leaves above
// the ceiling and a hot voice is turned down rather than clipped.
class GainLimiter {
public:
    GainLimiter(int sample_rate, float gain_db, float ceiling = 0.944f);

    void Process(float* data, size_t n) noexcept;

    [[nodiscard]] int64_t Limited() const noexcept { return limited_; }

private:
    float gain_;
    float ceiling_;
    float release_;
    float current_ = 1.0f;
    int64_t limited_ = 0;
};

// Raised-cosine fade at both ends of a discontinuous run of audio. The last
// fade-length samples are held back until the run ends, since only then is it known
// which samples are last.
class EdgeFader {
public:
    explicit EdgeFader(size_t fade_samples);

    void Begin();
    void Process(const float* in, size_t n, std::vector<float>& out);
    void End(std::vector<float>& out);

private:
    [[nodiscard]] float FadeIn(uint64_t index) const noexcept;

    size_t fade_;
    std::vector<float> held_;
    uint64_t index_ = 0;
};

// Finds the first 10 ms block whose RMS reaches the threshold, as the sample index
// counted from the first sample fed.
class AudibleDetector {
public:
    AudibleDetector(int sample_rate, float threshold_dbfs);

    // Returns the index of the first audible sample once found, else -1.
    int64_t Feed(const float* data, size_t n) noexcept;

    [[nodiscard]] int64_t First() const noexcept { return first_; }

private:
    size_t block_;
    double threshold_power_;
    double acc_ = 0.0;
    size_t count_ = 0;
    int64_t total_ = 0;
    int64_t first_ = -1;
};

void FloatToInt16(const float* in, size_t n, int16_t* out) noexcept;

}  // namespace bspeech::dsp
