#include "dsp.h"

#include <algorithm>
#include <cmath>
#include <limits>

namespace bspeech::dsp {

namespace {

constexpr double kPi = 3.14159265358979323846;

double BesselI0(double x) {
    double sum = 1.0;
    double term = 1.0;
    const double half = x / 2.0;

    for (int k = 1; k < 64; ++k) {
        term *= (half / k) * (half / k);
        sum += term;

        if (term < sum * 1e-17) {
            break;
        }
    }

    return sum;
}

}  // namespace

Resampler::Resampler(double ratio) : ratio_(ratio), step_(1.0 / ratio) {
    // 16 taps a side at unit ratio. Downsampling narrows the cutoff, so the kernel
    // is widened by the same factor to keep its length in output samples.
    constexpr int kHalfTaps = 16;
    constexpr double kRolloff = 0.92;
    constexpr double kBeta = 8.0;

    const double scale = std::min(1.0, ratio);
    half_ = static_cast<int>(std::ceil(kHalfTaps / scale));
    taps_ = 2 * half_;

    const double cutoff = 0.5 * scale * kRolloff;
    const double norm = BesselI0(kBeta);

    // Laid out phase by phase, each row holding every tap for one fractional
    // position in input order, so an output sample is two contiguous dot products
    // instead of a table lookup and interpolation per tap. Row kPhases closes the
    // last interval.
    rows_.assign(static_cast<size_t>(kPhases + 1) * static_cast<size_t>(taps_), 0.0f);

    for (int p = 0; p <= kPhases; ++p) {
        for (int m = 0; m < taps_; ++m) {
            const double u = half_ - 1 - m + static_cast<double>(p) / kPhases;
            const double x = 2.0 * kPi * cutoff * u;
            const double sinc = std::fabs(x) < 1e-12 ? 1.0 : std::sin(x) / x;
            const double r = u / half_;
            const double w = std::fabs(r) >= 1.0 ? 0.0 : BesselI0(kBeta * std::sqrt(1.0 - r * r)) / norm;
            rows_[static_cast<size_t>(p) * static_cast<size_t>(taps_) + static_cast<size_t>(m)] =
                static_cast<float>(2.0 * cutoff * sinc * w);
        }
    }

    Reset();
}

void Resampler::Reset() {
    buf_.assign(static_cast<size_t>(half_), 0.0f);
    pos_ = half_;
    in_total_ = 0;
    out_total_ = 0;
}

void Resampler::Drain(std::vector<float>& out, uint64_t limit) {
    const auto size = static_cast<int64_t>(buf_.size());

    while (out_total_ < limit) {
        const auto center = static_cast<int64_t>(std::floor(pos_));

        if (center + half_ >= size) {
            break;
        }

        const double scaled = (pos_ - static_cast<double>(center)) * kPhases;
        const auto phase = static_cast<size_t>(scaled);
        const auto f = static_cast<float>(scaled - static_cast<double>(phase));

        const float* x = buf_.data() + (center - half_ + 1);
        const float* ra = rows_.data() + phase * static_cast<size_t>(taps_);
        const float* rb = ra + taps_;

        // Four partial sums per row: independent chains the compiler can keep in
        // flight together, with no reassociation flags needed to get there.
        float a[4] = {0, 0, 0, 0};
        float b[4] = {0, 0, 0, 0};
        int m = 0;

        for (; m + 4 <= taps_; m += 4) {
            for (int l = 0; l < 4; ++l) {
                a[l] += x[m + l] * ra[m + l];
                b[l] += x[m + l] * rb[m + l];
            }
        }

        for (; m < taps_; ++m) {
            a[0] += x[m] * ra[m];
            b[0] += x[m] * rb[m];
        }

        const float lo = (a[0] + a[1]) + (a[2] + a[3]);
        const float hi = (b[0] + b[1]) + (b[2] + b[3]);
        out.push_back(lo + f * (hi - lo));
        ++out_total_;
        pos_ += step_;
    }

    const int64_t keep_from = std::min<int64_t>(static_cast<int64_t>(std::floor(pos_)) - half_ + 1, size);

    if (keep_from > 0) {
        buf_.erase(buf_.begin(), buf_.begin() + keep_from);
        pos_ -= static_cast<double>(keep_from);
    }
}

void Resampler::Process(const float* in, size_t n, std::vector<float>& out) {
    buf_.insert(buf_.end(), in, in + n);
    in_total_ += n;
    Drain(out, std::numeric_limits<uint64_t>::max());
}

void Resampler::Flush(std::vector<float>& out) {
    const auto expected = static_cast<uint64_t>(std::llround(static_cast<double>(in_total_) * ratio_));

    buf_.insert(buf_.end(), static_cast<size_t>(half_) + 2, 0.0f);
    Drain(out, expected);

    while (out_total_ < expected) {
        out.push_back(0.0f);
        ++out_total_;
    }

    Reset();
}

TimeStretch::TimeStretch(int sample_rate, double factor) : factor_(factor) {
    // 25 ms frames at 50% overlap and an 8 ms search: frame lengths in the range
    // WSOLA is normally run at for speech, long enough to hold a pitch period of a
    // low voice and short enough not to smear consonants.
    frame_ = std::max(64, static_cast<int>(std::lround(0.025 * sample_rate)));
    frame_ += frame_ % 2;
    hop_out_ = frame_ / 2;
    tolerance_ = std::max(8, static_cast<int>(std::lround(0.008 * sample_rate)));
    hop_in_ = hop_out_ / factor;

    window_.resize(static_cast<size_t>(frame_));

    for (int m = 0; m < frame_; ++m) {
        window_[static_cast<size_t>(m)] = static_cast<float>(0.5 - 0.5 * std::cos(2.0 * kPi * m / frame_));
    }

    // The first frame has nothing before it to overlap, so its rising half is flat
    // rather than a fade-in the source never had.
    first_window_ = window_;
    std::fill(first_window_.begin(), first_window_.begin() + hop_out_, 1.0f);

    Reset();
}

void TimeStretch::Reset() {
    in_.clear();
    in_base_ = 0;
    in_total_ = 0;
    ola_.assign(static_cast<size_t>(frame_), 0.0f);
    ola_base_ = 0;
    out_total_ = 0;
    frame_index_ = 0;
    prev_ = -1;
}

int64_t TimeStretch::BestOffset(int64_t nominal, int64_t target) const {
    const auto base = static_cast<int64_t>(in_base_);
    const int64_t lo = std::max<int64_t>(-tolerance_, base - nominal);
    const int64_t hi = tolerance_;

    if (lo >= hi) {
        return lo;
    }

    const float* ref = in_.data() + (target - base);
    const int length = hop_out_;

    // Float partial sums: over a few hundred samples of audio in [-1, 1] the error
    // is far below anything that could move the choice of offset.
    auto score = [&](int64_t delta) {
        const float* c = in_.data() + (nominal + delta - base);
        float dot[4] = {0, 0, 0, 0};
        float energy[4] = {0, 0, 0, 0};
        int m = 0;

        for (; m + 4 <= length; m += 4) {
            for (int l = 0; l < 4; ++l) {
                dot[l] += ref[m + l] * c[m + l];
                energy[l] += c[m + l] * c[m + l];
            }
        }

        for (; m < length; ++m) {
            dot[0] += ref[m] * c[m];
            energy[0] += c[m] * c[m];
        }

        const double d = (dot[0] + dot[1]) + (dot[2] + dot[3]);
        const double e = (energy[0] + energy[1]) + (energy[2] + energy[3]);
        return d / std::sqrt(e + 1e-9);
    };

    int64_t best = lo;
    double best_score = -std::numeric_limits<double>::infinity();

    for (int64_t d = lo; d <= hi; d += 2) {
        const double s = score(d);

        if (s > best_score) {
            best_score = s;
            best = d;
        }
    }

    for (const int64_t d : {best - 1, best + 1}) {
        if (d >= lo && d <= hi) {
            const double s = score(d);

            if (s > best_score) {
                best_score = s;
                best = d;
            }
        }
    }

    return best;
}

bool TimeStretch::Step(bool flushing) {
    (void)flushing;

    const uint64_t k = frame_index_;
    const auto nominal = static_cast<int64_t>(std::llround(static_cast<double>(k) * hop_in_));
    const auto available = static_cast<int64_t>(in_base_ + in_.size());
    int64_t chosen = 0;

    if (k == 0) {
        if (available < frame_) {
            return false;
        }
    } else {
        const int64_t target = prev_ + hop_out_;

        // Also waits for input up to the next nominal position, so the output
        // finalised so far never runs ahead of input * factor when compressing.
        const int64_t needed = std::max<int64_t>(
            {nominal + tolerance_ + frame_, target + hop_out_,
             nominal + static_cast<int64_t>(std::ceil(hop_in_))});

        if (needed > available) {
            return false;
        }

        chosen = nominal + BestOffset(nominal, target);
    }

    const uint64_t out_start = k * static_cast<uint64_t>(hop_out_);
    const size_t needed_ola = static_cast<size_t>(out_start - ola_base_) + static_cast<size_t>(frame_);

    if (ola_.size() < needed_ola) {
        ola_.resize(needed_ola, 0.0f);
    }

    const std::vector<float>& w = k == 0 ? first_window_ : window_;
    float* dst = ola_.data() + (out_start - ola_base_);
    const float* src = in_.data() + (chosen - static_cast<int64_t>(in_base_));

    for (int m = 0; m < frame_; ++m) {
        dst[m] += w[static_cast<size_t>(m)] * src[m];
    }

    prev_ = chosen;
    ++frame_index_;

    const auto next_nominal = static_cast<int64_t>(std::llround(static_cast<double>(k + 1) * hop_in_));
    const int64_t keep = std::min<int64_t>(next_nominal - tolerance_, prev_ + hop_out_);

    if (keep > static_cast<int64_t>(in_base_)) {
        const auto drop = std::min<size_t>(static_cast<size_t>(keep - static_cast<int64_t>(in_base_)), in_.size());
        in_.erase(in_.begin(), in_.begin() + static_cast<std::ptrdiff_t>(drop));
        in_base_ += drop;
    }

    return true;
}

void TimeStretch::Emit(std::vector<float>& out, uint64_t upto, uint64_t limit) {
    const uint64_t end = std::min(upto, limit);

    if (end <= ola_base_) {
        return;
    }

    const auto n = std::min<size_t>(static_cast<size_t>(end - ola_base_), ola_.size());
    out.insert(out.end(), ola_.begin(), ola_.begin() + static_cast<std::ptrdiff_t>(n));
    ola_.erase(ola_.begin(), ola_.begin() + static_cast<std::ptrdiff_t>(n));
    ola_base_ += n;
    out_total_ += n;
}

void TimeStretch::Process(const float* in, size_t n, std::vector<float>& out) {
    in_.insert(in_.end(), in, in + n);
    in_total_ += n;

    while (Step(false)) {
        Emit(out, frame_index_ * static_cast<uint64_t>(hop_out_), std::numeric_limits<uint64_t>::max());
    }
}

void TimeStretch::Flush(std::vector<float>& out) {
    const auto expected = static_cast<uint64_t>(std::llround(static_cast<double>(in_total_) * factor_));
    const auto pad = static_cast<size_t>(frame_ + tolerance_ + static_cast<int>(std::ceil(hop_in_)) + hop_out_);

    while (frame_index_ * static_cast<uint64_t>(hop_out_) < expected) {
        if (!Step(true)) {
            in_.insert(in_.end(), pad, 0.0f);
            continue;
        }

        Emit(out, frame_index_ * static_cast<uint64_t>(hop_out_), expected);
    }

    Emit(out, expected, expected);

    while (out_total_ < expected) {
        out.push_back(0.0f);
        ++out_total_;
    }

    Reset();
}

VoiceChain::VoiceChain(int sample_rate, float rate, float pitch_semitones) {
    const bool pitched = std::fabs(pitch_semitones) > 1e-4f;
    const bool paced = std::fabs(rate - 1.0f) > 1e-4f;

    if (!pitched && !paced) {
        return;
    }

    const double pitch = pitched ? std::pow(2.0, pitch_semitones / 12.0) : 1.0;
    const double factor = pitch / rate;

    if (std::fabs(factor - 1.0) > 1e-6) {
        stretch_ = std::make_unique<TimeStretch>(sample_rate, factor);
    }

    if (pitched) {
        resample_ = std::make_unique<Resampler>(1.0 / pitch);
    }
}

void VoiceChain::Process(const float* in, size_t n, std::vector<float>& out) {
    if (Identity()) {
        out.insert(out.end(), in, in + n);
        return;
    }

    if (!resample_) {
        stretch_->Process(in, n, out);
        return;
    }

    if (!stretch_) {
        resample_->Process(in, n, out);
        return;
    }

    scratch_.clear();
    stretch_->Process(in, n, scratch_);
    resample_->Process(scratch_.data(), scratch_.size(), out);
}

void VoiceChain::Flush(std::vector<float>& out) {
    if (Identity()) {
        return;
    }

    if (!resample_) {
        stretch_->Flush(out);
        return;
    }

    if (stretch_) {
        scratch_.clear();
        stretch_->Flush(scratch_);
        resample_->Process(scratch_.data(), scratch_.size(), out);
    }

    resample_->Flush(out);
}

GainLimiter::GainLimiter(int sample_rate, float gain_db, float ceiling)
    : gain_(static_cast<float>(std::pow(10.0, gain_db / 20.0))),
      ceiling_(ceiling),
      release_(static_cast<float>(std::exp(-1.0 / (0.05 * sample_rate)))) {}

void GainLimiter::Process(float* data, size_t n) noexcept {
    for (size_t i = 0; i < n; ++i) {
        float x = data[i] * gain_;

        if (!std::isfinite(x)) {
            x = 0.0f;
        }

        const float a = std::fabs(x);
        const float target = a > ceiling_ ? ceiling_ / a : 1.0f;

        if (target < current_) {
            current_ = target;
        } else {
            current_ = target - (target - current_) * release_;
        }

        data[i] = x * current_;

        if (current_ < 0.9999f) {
            ++limited_;
        }
    }
}

EdgeFader::EdgeFader(size_t fade_samples) : fade_(fade_samples) { held_.reserve(fade_samples); }

void EdgeFader::Begin() {
    held_.clear();
    index_ = 0;
}

float EdgeFader::FadeIn(uint64_t index) const noexcept {
    if (index >= fade_) {
        return 1.0f;
    }

    return static_cast<float>(0.5 - 0.5 * std::cos(kPi * (static_cast<double>(index) + 0.5) / fade_));
}

void EdgeFader::Process(const float* in, size_t n, std::vector<float>& out) {
    if (fade_ == 0) {
        out.insert(out.end(), in, in + n);
        index_ += n;
        return;
    }

    const size_t total = held_.size() + n;

    if (total <= fade_) {
        held_.insert(held_.end(), in, in + n);
        return;
    }

    const size_t emit = total - fade_;
    const size_t from_held = std::min(emit, held_.size());

    for (size_t i = 0; i < from_held; ++i) {
        out.push_back(held_[i] * FadeIn(index_++));
    }

    const size_t from_in = emit - from_held;

    for (size_t i = 0; i < from_in; ++i) {
        out.push_back(in[i] * FadeIn(index_++));
    }

    held_.erase(held_.begin(), held_.begin() + static_cast<std::ptrdiff_t>(from_held));
    held_.insert(held_.end(), in + from_in, in + n);
}

void EdgeFader::End(std::vector<float>& out) {
    const size_t m = held_.size();

    for (size_t j = 0; j < m; ++j) {
        const double distance = static_cast<double>(m - j) - 0.5;
        const double fade_out = fade_ == 0 ? 1.0 : 0.5 - 0.5 * std::cos(kPi * distance / fade_);
        out.push_back(static_cast<float>(held_[j] * FadeIn(index_ + j) * fade_out));
    }

    index_ += m;
    held_.clear();
}

AudibleDetector::AudibleDetector(int sample_rate, float threshold_dbfs)
    : block_(std::max<size_t>(1, static_cast<size_t>(sample_rate / 100))),
      threshold_power_(std::pow(10.0, threshold_dbfs / 10.0)) {}

int64_t AudibleDetector::Feed(const float* data, size_t n) noexcept {
    if (first_ >= 0) {
        return first_;
    }

    for (size_t i = 0; i < n; ++i) {
        acc_ += static_cast<double>(data[i]) * data[i];
        ++count_;
        ++total_;

        if (count_ == block_) {
            if (acc_ / static_cast<double>(block_) >= threshold_power_) {
                first_ = total_ - static_cast<int64_t>(block_);
                return first_;
            }

            acc_ = 0.0;
            count_ = 0;
        }
    }

    return -1;
}

void FloatToInt16(const float* in, size_t n, int16_t* out) noexcept {
    for (size_t i = 0; i < n; ++i) {
        const float v = std::clamp(in[i], -1.0f, 1.0f) * 32767.0f;
        out[i] = static_cast<int16_t>(std::lrint(v));
    }
}

}  // namespace bspeech::dsp
