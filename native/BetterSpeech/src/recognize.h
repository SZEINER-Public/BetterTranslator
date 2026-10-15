#pragma once

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <deque>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "better_speech_stt.h"
#include "dsp.h"
#include "engine.h"
#include "lexicon.h"
#include "ring.h"
#include "vad.h"
#include "wav.h"

namespace bspeech {

struct RecEngineCore {
    const bs_recognizer_v1* source = nullptr;
    bs_rec_caps caps{};
    std::vector<std::string> languages;
    std::shared_ptr<const Lexicon> lexicon;
    std::string unavailable;
    std::mutex mutex;
    bool session_active = false;

    [[nodiscard]] bool HasSource() const noexcept { return source != nullptr; }
};

[[nodiscard]] Failure CreateRecEngineCore(const bs_rec_engine_config& config, std::shared_ptr<const Lexicon> lexicon,
                                          std::shared_ptr<RecEngineCore>* out);

void FillRecCapabilities(const RecEngineCore& core, bs_rec_capabilities* caps);

[[nodiscard]] VadSettings VadFrom(const bs_vad_settings& settings);

void VadTo(const VadSettings& settings, bs_vad_settings* out);

void RecRequestDefaults(bs_rec_request* out);

[[nodiscard]] bool CaptureBackendAvailable() noexcept;

struct HypWord {
    std::string text;
    std::string key;
    int64_t start = -1;
    int64_t end = -1;
    float confidence = 1.0f;
};

struct RecEvent {
    int32_t kind = BS_REC_EVENT_NONE;
    int32_t segment = 0;
    bool segment_end = false;
    int32_t status = BS_OK;
    int64_t start = 0;
    int64_t end = 0;
    float min_confidence = 1.0f;
    std::string text;
    std::string unstable;
    std::string raw;
    std::vector<Substitution> substitutions;
};

class RecognitionSession {
public:
    static Failure Start(std::shared_ptr<RecEngineCore> core, const bs_rec_request& request,
                         std::unique_ptr<RecognitionSession>* out);

    RecognitionSession(const RecognitionSession&) = delete;
    RecognitionSession& operator=(const RecognitionSession&) = delete;
    ~RecognitionSession();

    int32_t Push(const float* interleaved, int32_t frames, int32_t channels) noexcept;
    void EndInput() noexcept;
    void Cancel() noexcept;
    int32_t Poll(bs_rec_event* out);
    int32_t SubstitutionAt(int32_t index, bs_substitution* out) const;
    int32_t Wait(int32_t timeout_ms, int32_t* out_status);
    void Stats(bs_rec_stats* out) const;
    [[nodiscard]] std::string Error() const;

private:
    explicit RecognitionSession(std::shared_ptr<RecEngineCore> core);

    void Run() noexcept;
    Failure Process();
    Failure Ingest(const float* samples, size_t n);
    Failure Consume(const float* samples, size_t n);
    Failure OnFrame(const float* frame);
    Failure StartWindow(const float* audio, size_t n);
    Failure Feed(const float* audio, size_t n);
    Failure Decode(bool final, std::vector<HypWord>* words);
    Failure PartialStep();
    Failure CutWindow();
    Failure EndUtterance();
    Failure Commit(std::vector<HypWord> words, bool segment_end);
    [[nodiscard]] std::vector<HypWord> Merge(const std::vector<HypWord>& final_words) const;
    void PushEvent(RecEvent event);
    void Finish(const Failure& outcome);
    [[nodiscard]] int64_t Elapsed() const noexcept;
    [[nodiscard]] Failure SourceFailure(int32_t rc, const char* what) const;

    std::shared_ptr<RecEngineCore> core_;

    std::string language_;
    DictationMode mode_ = DictationMode::Prose;
    int32_t n_threads_ = 0;
    float min_confidence_ = 0.6f;
    size_t context_chars_ = 0;
    int32_t model_rate_ = 0;
    int32_t input_rate_ = 0;
    size_t frame_samples_ = 0;
    size_t window_samples_ = 0;
    size_t overlap_samples_ = 0;
    size_t partial_interval_ = 0;
    size_t preroll_cap_ = 0;
    VadSettings vad_settings_;
    bool live_ = false;

    std::unique_ptr<PcmRing> ring_;
    std::vector<float> push_scratch_;
    std::atomic<bool> ended_{false};
    std::atomic<bool> stopped_{false};
    std::atomic<int64_t> input_frames_{0};
    std::atomic<int64_t> overrun_frames_{0};

    WavStreamReader reader_;
    std::unique_ptr<dsp::Resampler> resampler_;
    std::unique_ptr<Vad> vad_;
    std::vector<float> in_block_;
    std::vector<float> resampled_;
    std::vector<float> frame_acc_;
    std::vector<float> preroll_;
    std::vector<float> window_audio_;
    std::vector<float> tail_;
    bool in_utterance_ = false;
    size_t since_partial_ = 0;
    bool awaiting_first_partial_ = false;
    int64_t window_abs_start_ = 0;
    int64_t model_samples_ = 0;
    std::vector<HypWord> stable_;
    std::vector<HypWord> previous_;
    std::vector<std::string> dedupe_tail_;
    std::string committed_;
    std::string context_;
    std::vector<std::string> bias_terms_;
    std::vector<const char*> bias_ptrs_;
    std::vector<char> text_buf_;
    std::vector<bs_rec_word> word_buf_;
    int32_t segment_ = 0;

    std::chrono::steady_clock::time_point started_;
    std::chrono::steady_clock::time_point last_voiced_wall_;
    std::chrono::steady_clock::time_point onset_wall_;
    std::thread thread_;
    std::atomic<bool> cancelled_{false};
    std::atomic<int64_t> cancel_requested_us_{-1};

    mutable std::mutex mutex_;
    std::condition_variable done_cv_;
    bool running_ = false;
    bool finished_ = false;
    bool finished_delivered_ = false;
    bool overflow_ = false;
    Failure outcome_;
    std::deque<RecEvent> events_;
    RecEvent polled_;
    std::vector<int64_t> latencies_;

    struct Counters {
        std::atomic<int32_t> segments{0};
        std::atomic<int32_t> finals{0};
        std::atomic<int32_t> partials{0};
        std::atomic<int32_t> windows{0};
        std::atomic<int64_t> model_samples{0};
        std::atomic<int64_t> peak_window{0};
        std::atomic<int64_t> t_first_voiced{-1};
        std::atomic<int64_t> t_first_partial{-1};
        std::atomic<int64_t> t_first_final{-1};
        std::atomic<int64_t> t_finished{-1};
        std::atomic<int64_t> onset_to_first_partial{-1};
        std::atomic<int64_t> feed_us{0};
        std::atomic<int64_t> partial_us{0};
        std::atomic<int64_t> finalize_us{0};
        std::atomic<int64_t> cancel_latency{-1};
    } counters_;
};

}
