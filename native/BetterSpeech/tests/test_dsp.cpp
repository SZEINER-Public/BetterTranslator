#include <algorithm>
#include <cmath>
#include <random>
#include <vector>

#include "dsp.h"
#include "harness.h"

using namespace bspeech::dsp;

namespace {

constexpr double kPi = 3.14159265358979323846;

std::vector<float> Sine(double frequency, int rate, size_t n, float amplitude = 0.5f) {
    std::vector<float> out(n);

    for (size_t i = 0; i < n; ++i) {
        out[i] = amplitude * static_cast<float>(std::sin(2.0 * kPi * frequency * static_cast<double>(i) / rate));
    }

    return out;
}

double Rms(const std::vector<float>& x, size_t from, size_t to) {
    double acc = 0;

    for (size_t i = from; i < to; ++i) {
        acc += static_cast<double>(x[i]) * x[i];
    }

    return std::sqrt(acc / static_cast<double>(to - from));
}

// Power at one frequency over [from, to), normalised per sample.
double Goertzel(const std::vector<float>& x, size_t from, size_t to, double frequency, int rate) {
    const double w = 2.0 * kPi * frequency / rate;
    const double coeff = 2.0 * std::cos(w);
    double s1 = 0;
    double s2 = 0;

    for (size_t i = from; i < to; ++i) {
        const double s0 = x[i] + coeff * s1 - s2;
        s2 = s1;
        s1 = s0;
    }

    const double power = s1 * s1 + s2 * s2 - coeff * s1 * s2;
    const double n = static_cast<double>(to - from);
    return power / (n * n);
}

// The strongest frequency on a 1 Hz grid within [lo, hi].
double Dominant(const std::vector<float>& x, size_t from, size_t to, int rate, double lo, double hi) {
    double best = lo;
    double best_power = -1;

    for (double f = lo; f <= hi; f += 1.0) {
        const double p = Goertzel(x, from, to, f, rate);

        if (p > best_power) {
            best_power = p;
            best = f;
        }
    }

    return best;
}

std::vector<float> RunResampler(Resampler& r, const std::vector<float>& in, size_t block) {
    std::vector<float> out;

    for (size_t at = 0; at < in.size(); at += block) {
        r.Process(in.data() + at, std::min(block, in.size() - at), out);
    }

    r.Flush(out);
    return out;
}

std::vector<float> RunChain(VoiceChain& chain, const std::vector<float>& in, size_t block) {
    std::vector<float> out;

    for (size_t at = 0; at < in.size(); at += block) {
        chain.Process(in.data() + at, std::min(block, in.size() - at), out);
    }

    chain.Flush(out);
    return out;
}

}  // namespace

TEST(resampler_output_length_is_exact) {
    for (const double ratio : {0.5, 1.0 / 1.5, 24000.0 / 44100.0, 1.25, 2.0, 1.0 / 0.7}) {
        Resampler r(ratio);
        const auto in = Sine(440, 24000, 10007);
        const auto out = RunResampler(r, in, 333);
        CHECK_EQ(out.size(), static_cast<size_t>(std::llround(10007 * ratio)));
    }
}

TEST(resampler_reproduces_a_sine) {
    Resampler r(24000.0 / 16000.0);
    const auto in = Sine(1000, 16000, 16000);
    const auto out = RunResampler(r, in, 517);
    const auto ideal = Sine(1000, 24000, out.size());

    double worst = 0;

    for (size_t i = 200; i + 200 < out.size(); ++i) {
        worst = std::max(worst, std::fabs(static_cast<double>(out[i]) - ideal[i]));
    }

    // Better than -60 dB against a 0.5 amplitude tone, away from the edges.
    CHECK(worst < 0.5e-3);
}

TEST(resampler_rejects_content_above_the_new_nyquist) {
    Resampler r(0.5);
    const auto in = Sine(15000, 48000, 48000);
    const auto out = RunResampler(r, in, 1024);
    const double level = Rms(out, 500, out.size() - 500) / (0.5 / std::sqrt(2.0));

    // 15 kHz cannot exist at 24 kHz; what is left of it must be at least 40 dB down.
    CHECK(level < 0.01);
}

TEST(resampler_streaming_matches_one_shot) {
    std::mt19937 rng(7);
    std::uniform_real_distribution<float> noise(-0.5f, 0.5f);
    std::vector<float> in(20000);

    for (float& v : in) {
        v = noise(rng);
    }

    Resampler a(1.37);
    Resampler b(1.37);
    const auto whole = RunResampler(a, in, in.size());
    std::vector<float> pieces;

    for (size_t at = 0; at < in.size();) {
        const size_t n = std::min<size_t>(1 + rng() % 700, in.size() - at);
        b.Process(in.data() + at, n, pieces);
        at += n;
    }

    b.Flush(pieces);
    CHECK_EQ(whole.size(), pieces.size());

    for (size_t i = 0; i < whole.size(); ++i) {
        CHECK_NEAR(whole[i], pieces[i], 1e-6);
    }
}

TEST(stretch_output_length_is_exact) {
    for (const double factor : {0.25, 0.5, 0.8, 1.25, 2.0, 4.0}) {
        TimeStretch s(24000, factor);
        const auto in = Sine(220, 24000, 24011);
        std::vector<float> out;

        for (size_t at = 0; at < in.size(); at += 1000) {
            s.Process(in.data() + at, std::min<size_t>(1000, in.size() - at), out);
        }

        s.Flush(out);
        CHECK_EQ(out.size(), static_cast<size_t>(std::llround(24011 * factor)));
    }
}

TEST(stretch_keeps_pitch_and_level) {
    for (const double factor : {0.6, 1.5}) {
        TimeStretch s(24000, factor);
        const auto in = Sine(220, 24000, 48000);
        std::vector<float> out;
        s.Process(in.data(), in.size(), out);
        s.Flush(out);

        const size_t from = 2400;
        const size_t to = out.size() - 2400;
        CHECK_NEAR(Dominant(out, from, to, 24000, 150, 400), 220.0, 2.0);

        // Overlap-add of matched frames neither swells nor dips a steady tone.
        CHECK_NEAR(Rms(out, from, to), Rms(in, 2400, in.size() - 2400), 0.03);
    }
}

TEST(chain_identity_passes_samples_through) {
    VoiceChain chain(24000, 1.0f, 0.0f);
    CHECK(chain.Identity());

    const auto in = Sine(300, 24000, 5000);
    const auto out = RunChain(chain, in, 777);
    CHECK(out == in);
}

TEST(chain_rate_changes_duration_not_pitch) {
    VoiceChain chain(24000, 1.5f, 0.0f);
    const auto in = Sine(220, 24000, 48000);
    const auto out = RunChain(chain, in, 960);

    CHECK_NEAR(static_cast<double>(out.size()), 48000.0 / 1.5, 2.0);
    CHECK_NEAR(Dominant(out, 2400, out.size() - 2400, 24000, 150, 400), 220.0, 2.0);
}

TEST(chain_pitch_changes_pitch_not_duration) {
    for (const float semitones : {12.0f, -5.0f, 3.0f}) {
        VoiceChain chain(24000, 1.0f, semitones);
        const auto in = Sine(220, 24000, 48000);
        const auto out = RunChain(chain, in, 960);
        const double expected = 220.0 * std::pow(2.0, semitones / 12.0);

        CHECK_NEAR(static_cast<double>(out.size()), 48000.0, 2.0);
        CHECK_NEAR(Dominant(out, 2400, out.size() - 2400, 24000, 100, 600), expected, 3.0);
    }
}

TEST(chain_rate_and_pitch_are_independent) {
    VoiceChain chain(24000, 1.5f, -5.0f);
    const auto in = Sine(220, 24000, 48000);
    const auto out = RunChain(chain, in, 960);

    CHECK_NEAR(static_cast<double>(out.size()), 48000.0 / 1.5, 2.0);
    CHECK_NEAR(Dominant(out, 2400, out.size() - 2400, 24000, 100, 400), 220.0 * std::pow(2.0, -5.0 / 12.0), 3.0);
}

TEST(limiter_keeps_peaks_below_ceiling) {
    GainLimiter boost(24000, 12.0f);
    auto loud = Sine(300, 24000, 24000, 0.5f);
    boost.Process(loud.data(), loud.size());

    float peak = 0;

    for (const float v : loud) {
        peak = std::max(peak, std::fabs(v));
    }

    CHECK(peak <= 0.944f + 1e-6f);
    CHECK(boost.Limited() > 0);

    GainLimiter cut(24000, -6.0f);
    auto quiet = Sine(300, 24000, 24000, 0.5f);
    const auto reference = quiet;
    cut.Process(quiet.data(), quiet.size());

    for (size_t i = 0; i < quiet.size(); ++i) {
        CHECK_NEAR(quiet[i], reference[i] * 0.501187, 1e-5);
    }

    CHECK_EQ(cut.Limited(), 0);

    float poison[3] = {NAN, INFINITY, 0.25f};
    GainLimiter guard(24000, 0.0f);
    guard.Process(poison, 3);
    CHECK_EQ(poison[0], 0.0f);
    CHECK_EQ(poison[1], 0.0f);
    CHECK_NEAR(poison[2], 0.25f, 1e-7);
}

TEST(fader_ramps_both_ends_and_keeps_length) {
    EdgeFader fader(120);
    fader.Begin();

    std::vector<float> ones(1000, 1.0f);
    std::vector<float> out;

    for (size_t at = 0; at < ones.size(); at += 37) {
        fader.Process(ones.data() + at, std::min<size_t>(37, ones.size() - at), out);
    }

    fader.End(out);

    CHECK_EQ(out.size(), 1000u);
    CHECK(out.front() < 0.01f);
    CHECK(out.back() < 0.01f);
    CHECK_EQ(out[500], 1.0f);

    for (size_t i = 1; i < 120; ++i) {
        CHECK(out[i] >= out[i - 1]);
    }

    for (size_t i = 881; i < 1000; ++i) {
        CHECK(out[i] <= out[i - 1]);
    }

    fader.Begin();
    out.clear();
    std::vector<float> short_run(50, 1.0f);
    fader.Process(short_run.data(), short_run.size(), out);
    fader.End(out);
    CHECK_EQ(out.size(), 50u);
}

TEST(audible_detector_finds_onset) {
    std::vector<float> x(24000, 0.0f);
    const auto tone = Sine(300, 24000, 12000, 0.3f);
    std::copy(tone.begin(), tone.end(), x.begin() + 9000);

    AudibleDetector detector(24000, -45.0f);
    int64_t found = -1;

    for (size_t at = 0; at < x.size() && found < 0; at += 500) {
        found = detector.Feed(x.data() + at, std::min<size_t>(500, x.size() - at));
    }

    // Block resolution is 10 ms, 240 samples; the onset block may begin up to one
    // block early, since it can straddle the edge.
    CHECK(found >= 9000 - 240);
    CHECK(found <= 9000 + 240);

    AudibleDetector silent(24000, -45.0f);
    const std::vector<float> hiss(24000, 0.001f);
    CHECK_EQ(silent.Feed(hiss.data(), hiss.size()), -1);
}

TEST(float_to_int16_rounds_and_clamps) {
    const float in[6] = {1.0f, -1.0f, 2.0f, -3.0f, 0.5f, 0.0f};
    int16_t out[6];
    FloatToInt16(in, 6, out);

    CHECK_EQ(out[0], 32767);
    CHECK_EQ(out[1], -32767);
    CHECK_EQ(out[2], 32767);
    CHECK_EQ(out[3], -32767);
    CHECK(out[4] == 16383 || out[4] == 16384);
    CHECK_EQ(out[5], 0);
}
