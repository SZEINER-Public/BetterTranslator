#pragma once

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "better_speech.h"
#include "dsp.h"
#include "ring.h"
#include "text_plan.h"
#include "wav.h"

namespace bspeech {

struct Failure {
    int32_t status = BS_OK;
    std::string message;
    int64_t offset = -1;
};

// What a job needs from its engine, shared so a job can outlive the handle that
// created it. Immutable after creation apart from the one-job latch.
struct EngineCore {
    const bs_source_v1* source = nullptr;
    bs_source_caps caps{};
    std::vector<std::string> voices;
    std::string unavailable;

    std::vector<float> laugh_clip;  // mono, at caps.sample_rate when a source is attached
    int32_t laugh_clip_rate = 0;

    std::mutex mutex;
    bool job_active = false;

    [[nodiscard]] bool HasSource() const noexcept { return source != nullptr; }
    [[nodiscard]] bool NativeEvent(EventKind kind) const noexcept {
        return HasSource() && (caps.native_events & (1u << static_cast<uint32_t>(kind))) != 0;
    }
    [[nodiscard]] bool NativeControl(uint32_t bit) const noexcept {
        return HasSource() && (caps.native_controls & bit) != 0;
    }
};

[[nodiscard]] Failure CreateEngineCore(const bs_engine_config& config, std::shared_ptr<EngineCore>* out);

void FillCapabilities(const EngineCore& core, bs_capabilities* caps);

class Job {
public:
    static Failure Start(std::shared_ptr<EngineCore> core, const bs_request& request, std::unique_ptr<Job>* out);

    Job(const Job&) = delete;
    Job& operator=(const Job&) = delete;
    ~Job();

    int32_t Read(float* out, int32_t cap, int32_t* out_real, int32_t* out_flags) noexcept;
    void Cancel() noexcept;
    int32_t Wait(int32_t timeout_ms, int32_t* out_status);
    void Stats(bs_job_stats* out) const noexcept;
    [[nodiscard]] std::string Error() const;

private:
    explicit Job(std::shared_ptr<EngineCore> core);

    void Run() noexcept;
    Failure Generate();
    Failure Speak(const PlanItem& item);
    Failure SpeakEvent(EventKind kind);
    Failure PullChunk();
    Failure InsertClip();

    // voiced runs go through the rate and pitch chain; an inserted clip does not,
    // since reshaping a recording does not make it the model's voice.
    Failure Process(const float* data, size_t n, bool voiced);
    Failure EndRun(bool voiced);
    Failure Emit(const float* data, size_t n);
    void Finish(const Failure& outcome);
    [[nodiscard]] int64_t Elapsed() const noexcept;
    [[nodiscard]] Failure SourceFailure(int32_t rc, const char* what) const;

    std::shared_ptr<EngineCore> core_;

    // Fixed at start.
    std::vector<PlanItem> plan_;
    std::string language_;
    std::string voice_id_;
    bs_source_voice source_voice_{};
    int32_t n_threads_ = 0;
    int32_t sample_rate_ = 0;
    size_t prebuffer_ = 0;
    std::unique_ptr<PcmRing> ring_;
    WavWriter writer_;
    bool exporting_ = false;

    // Job thread only.
    std::unique_ptr<dsp::VoiceChain> chain_;
    std::unique_ptr<dsp::GainLimiter> limiter_;
    std::unique_ptr<dsp::EdgeFader> fader_;
    std::unique_ptr<dsp::AudibleDetector> detector_;
    std::vector<float> pull_buffer_;
    std::vector<float> stage_a_;
    std::vector<float> stage_b_;

    std::chrono::steady_clock::time_point started_;
    std::thread thread_;

    // Shared between the job thread, the reader and the controlling thread.
    std::atomic<bool> cancelled_{false};
    std::atomic<bool> producer_done_{false};
    std::atomic<int64_t> first_audible_index_{-1};
    std::atomic<int64_t> cancel_requested_us_{-1};

    // Reader side only, apart from the atomics it publishes.
    bool playing_ = false;
    uint64_t read_total_ = 0;

    mutable std::mutex mutex_;
    std::condition_variable done_cv_;
    bool running_ = false;
    bool finished_ = false;
    Failure outcome_;

    struct Counters {
        std::atomic<int32_t> chunks{0};
        std::atomic<int32_t> events{0};
        std::atomic<int32_t> laugh_render{BS_EVENT_RENDER_UNAVAILABLE};
        std::atomic<int64_t> samples_out{0};
        std::atomic<int64_t> samples_source{0};
        std::atomic<int64_t> samples_clip{0};
        std::atomic<int64_t> limited{0};
        std::atomic<int64_t> underruns{0};
        std::atomic<int64_t> underrun_samples{0};
        std::atomic<int64_t> peak_buffered{0};
        std::atomic<int64_t> t_plan{-1};
        std::atomic<int64_t> t_first_source{-1};
        std::atomic<int64_t> t_first_audible{-1};
        std::atomic<int64_t> t_prebuffered{-1};
        std::atomic<int64_t> t_first_audible_read{-1};
        std::atomic<int64_t> t_finished{-1};
        std::atomic<int64_t> source_us{0};
        std::atomic<int64_t> dsp_us{0};
        std::atomic<int64_t> export_us{0};
        std::atomic<int64_t> cancel_latency{-1};
    } counters_;
};

}  // namespace bspeech
