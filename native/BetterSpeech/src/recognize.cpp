#include "recognize.h"

#include <algorithm>
#include <cmath>
#include <new>
#include <system_error>

#include "score.h"
#include "utf8.h"

namespace bspeech {

namespace {

using Clock = std::chrono::steady_clock;

constexpr size_t kBlockSamples = 4096;
constexpr size_t kPushScratch = 4096;
constexpr size_t kMaxEvents = 4096;
constexpr size_t kMaxLatencies = 4096;
constexpr size_t kDedupeWords = 16;
constexpr int32_t kMaxRingSamples = 16 * 1024 * 1024;
constexpr int kDecodeAttempts = 4;

int64_t Micros(Clock::duration d) { return std::chrono::duration_cast<std::chrono::microseconds>(d).count(); }

void SetOnce(std::atomic<int64_t>& target, int64_t value) {
    int64_t unset = -1;
    target.compare_exchange_strong(unset, value, std::memory_order_relaxed);
}

void Max(std::atomic<int64_t>& target, int64_t value) {
    int64_t seen = target.load(std::memory_order_relaxed);

    while (value > seen && !target.compare_exchange_weak(seen, value, std::memory_order_relaxed)) {
    }
}

class Stopwatch {
public:
    explicit Stopwatch(std::atomic<int64_t>& total) : total_(total), start_(Clock::now()) {}
    ~Stopwatch() { total_.fetch_add(Micros(Clock::now() - start_), std::memory_order_relaxed); }

private:
    std::atomic<int64_t>& total_;
    Clock::time_point start_;
};

Failure Invalid(std::string message) { return {BS_INVALID_ARG, std::move(message), -1}; }

Failure Cancelled() { return {BS_CANCELLED, "the recognition session was cancelled", -1}; }

bool InRange(float value, float lo, float hi) { return std::isfinite(value) && value >= lo && value <= hi; }

std::string RecError(const bs_recognizer_v1* source) {
    const char* text = source->last_error(source->self);
    return text == nullptr || *text == '\0' ? "no reason was given" : text;
}

std::string Key(std::string_view word) {
    const auto words = ScoringWords(word);
    return words.empty() ? std::string() : words.front();
}

std::string Join(const std::vector<HypWord>& words, size_t from) {
    std::string out;

    for (size_t k = from; k < words.size(); ++k) {
        if (!out.empty()) {
            out.push_back(' ');
        }

        out += words[k].text;
    }

    return out;
}

void TrimContext(std::string* text, size_t keep) {
    if (keep == 0) {
        text->clear();
        return;
    }

    if (text->size() <= keep * 4) {
        return;
    }

    size_t cut = text->size() - keep * 2;

    while (cut < text->size() && (*text)[cut] != ' ') {
        ++cut;
    }

    text->erase(0, std::min(cut + 1, text->size()));
}

std::string Tail(const std::string& text, size_t keep) {
    if (text.size() <= keep) {
        return text;
    }

    size_t cut = text.size() - keep;

    if (text[cut - 1] != ' ') {
        const size_t space = text.find(' ', cut);
        cut = space == std::string::npos ? text.size() : space + 1;
    }

    while (cut < text.size() && (static_cast<unsigned char>(text[cut]) & 0xC0) == 0x80) {
        ++cut;
    }

    return text.substr(cut);
}

}

VadSettings VadFrom(const bs_vad_settings& s) {
    VadSettings v;
    v.onset_db = s.onset_db;
    v.absolute_floor_dbfs = s.absolute_floor_dbfs;
    v.min_speech_ms = s.min_speech_ms;
    v.hangover_ms = s.hangover_ms;
    v.pre_roll_ms = s.pre_roll_ms;
    return v;
}

void VadTo(const VadSettings& v, bs_vad_settings* out) {
    const uint32_t size = out->struct_size;
    *out = bs_vad_settings{};
    out->struct_size = size;
    out->onset_db = v.onset_db;
    out->absolute_floor_dbfs = v.absolute_floor_dbfs;
    out->min_speech_ms = v.min_speech_ms;
    out->hangover_ms = v.hangover_ms;
    out->pre_roll_ms = v.pre_roll_ms;
}

void RecRequestDefaults(bs_rec_request* out) {
    const uint32_t size = out->struct_size;
    *out = bs_rec_request{};
    out->struct_size = size;
    out->mode = BS_DICTATION_PROSE;
    out->input_ring_samples = 48000 * 10;
    out->window_ms = 0;
    out->overlap_ms = 1000;
    out->partial_interval_ms = 500;
    out->min_confidence = 0.6f;
    out->context_chars = 200;
    out->bias_terms = 64;
    out->vad.struct_size = sizeof(bs_vad_settings);
    VadTo(VadSettings{}, &out->vad);
}

Failure CreateRecEngineCore(const bs_rec_engine_config& config, std::shared_ptr<const Lexicon> lexicon,
                            std::shared_ptr<RecEngineCore>* out) {
    auto core = std::make_shared<RecEngineCore>();
    core->lexicon = std::move(lexicon);
    const bs_recognizer_v1* source = config.recognizer;

    if (source == nullptr) {
        core->unavailable = "no speech recognition model is attached to the engine";
        *out = std::move(core);
        return {};
    }

    if (source->abi_version != BS_RECOGNIZER_ABI_VERSION || source->struct_size < sizeof(bs_recognizer_v1)) {
        return Invalid("the recognizer implements recognizer ABI " + std::to_string(source->abi_version) +
                       "; this engine needs ABI " + std::to_string(BS_RECOGNIZER_ABI_VERSION));
    }

    if (source->get_caps == nullptr || source->begin == nullptr || source->feed == nullptr ||
        source->partial == nullptr || source->finalize == nullptr || source->cancel == nullptr ||
        source->reset == nullptr || source->last_error == nullptr) {
        return Invalid("the recognizer table is missing a required function");
    }

    if (source->get_caps(source->self, &core->caps) != BS_REC_OK) {
        return {BS_SOURCE, "the recognizer could not report its capabilities: " + RecError(source), -1};
    }

    const bs_rec_caps& caps = core->caps;

    if (caps.sample_rate < 8000 || caps.sample_rate > 48000 || caps.max_window_samples < caps.sample_rate ||
        caps.max_window_samples > caps.sample_rate * 600 || caps.language_count < 0) {
        return {BS_SOURCE,
                "the recognizer reported capabilities this engine cannot use (" + std::to_string(caps.sample_rate) +
                    " Hz, a " + std::to_string(caps.max_window_samples) + "-sample window)",
                -1};
    }

    if (caps.language_count > 0 && source->language == nullptr) {
        return Invalid("the recognizer lists languages but has no language function");
    }

    for (int32_t i = 0; i < caps.language_count; ++i) {
        std::string code(16, '\0');
        int32_t length = 0;
        int32_t rc = source->language(source->self, i, code.data(), static_cast<int32_t>(code.size()), &length);

        if (rc != BS_REC_OK && length > static_cast<int32_t>(code.size())) {
            code.assign(static_cast<size_t>(length), '\0');
            rc = source->language(source->self, i, code.data(), static_cast<int32_t>(code.size()), &length);
        }

        if (rc != BS_REC_OK || length <= 0 || length > static_cast<int32_t>(code.size())) {
            return {BS_SOURCE, "the recognizer could not name language " + std::to_string(i), -1};
        }

        code.resize(static_cast<size_t>(length));
        core->languages.push_back(std::move(code));
    }

    core->source = source;
    *out = std::move(core);
    return {};
}

void FillRecCapabilities(const RecEngineCore& core, bs_rec_capabilities* caps) {
    const uint32_t size = caps->struct_size;
    *caps = bs_rec_capabilities{};
    caps->struct_size = size;
    caps->lexicon_terms = core.lexicon ? static_cast<int32_t>(core.lexicon->Terms().size()) : 0;
    caps->capture = CaptureBackendAvailable() ? 1 : 0;

    if (!core.HasSource()) {
        return;
    }

    caps->available = 1;
    caps->sample_rate = core.caps.sample_rate;
    caps->max_window_samples = core.caps.max_window_samples;
    caps->streaming = core.caps.streaming;
    caps->word_timestamps = core.caps.word_timestamps;
    caps->bias_context = (core.caps.bias_controls & BS_REC_BIAS_CONTEXT) != 0 ? 1 : 0;
    caps->bias_logit = (core.caps.bias_controls & BS_REC_BIAS_LOGIT) != 0 ? 1 : 0;
    caps->bias_trie = (core.caps.bias_controls & BS_REC_BIAS_TRIE) != 0 ? 1 : 0;
    caps->language_count = static_cast<int32_t>(core.languages.size());
}

RecognitionSession::RecognitionSession(std::shared_ptr<RecEngineCore> core) : core_(std::move(core)) {}

RecognitionSession::~RecognitionSession() {
    Cancel();

    if (thread_.joinable()) {
        thread_.join();
    }
}

Failure RecognitionSession::Start(std::shared_ptr<RecEngineCore> core, const bs_rec_request& request,
                                  std::unique_ptr<RecognitionSession>* out) {
    if (request.struct_size < sizeof(bs_rec_request) || request.vad.struct_size < sizeof(bs_vad_settings)) {
        return Invalid("bs_rec_request.struct_size or its vad.struct_size is smaller than this engine expects");
    }

    if (request.language == nullptr || *request.language == '\0') {
        return Invalid("a language is required");
    }

    if (request.mode != BS_DICTATION_PROSE && request.mode != BS_DICTATION_CODE) {
        return Invalid("mode is not a bs_dictation_mode value");
    }

    const bool file = request.file_path != nullptr && *request.file_path != '\0';
    const bool live = request.input_sample_rate > 0;

    if (file == live) {
        return Invalid("a session reads either a file or live input at input_sample_rate, exactly one of them");
    }

    if (live && (request.input_sample_rate < 8000 || request.input_sample_rate > 192000)) {
        return Invalid("input_sample_rate must be between 8000 and 192000");
    }

    if (live && (request.input_ring_samples < 1024 || request.input_ring_samples > kMaxRingSamples)) {
        return Invalid("input_ring_samples must be between 1024 and " + std::to_string(kMaxRingSamples));
    }

    if (request.window_ms != 0 && request.window_ms < 1000) {
        return Invalid("window_ms must be 0 for the model's own limit, or at least 1000");
    }

    if (request.overlap_ms < 0 || request.partial_interval_ms < 0 ||
        (request.partial_interval_ms > 0 && request.partial_interval_ms < 100) || request.n_threads < 0) {
        return Invalid("overlap_ms, partial_interval_ms and n_threads cannot be negative, and partials run at most "
                       "every 100 ms");
    }

    if (!InRange(request.min_confidence, 0.0f, 1.0f) || request.context_chars < 0 || request.context_chars > 4096 ||
        request.bias_terms < 0 || request.bias_terms > 1024) {
        return Invalid("min_confidence must be in [0, 1], context_chars in [0, 4096] and bias_terms in [0, 1024]");
    }

    const VadSettings vad = VadFrom(request.vad);

    if (!InRange(vad.onset_db, 1.0f, 40.0f) || !InRange(vad.absolute_floor_dbfs, -100.0f, -10.0f) ||
        !InRange(vad.min_speech_ms, 10.0f, 1000.0f) || !InRange(vad.hangover_ms, 50.0f, 5000.0f) ||
        !InRange(vad.pre_roll_ms, vad.min_speech_ms, 2000.0f)) {
        return Invalid("the voice activity settings are out of range; pre_roll_ms must be at least min_speech_ms");
    }

    if (!core->HasSource()) {
        return {BS_UNAVAILABLE, core->unavailable, -1};
    }

    const std::string language = request.language;

    if (!core->languages.empty() &&
        std::find(core->languages.begin(), core->languages.end(), language) == core->languages.end()) {
        return Invalid("the recognizer does not list language '" + language + "'");
    }

    const int32_t rate = core->caps.sample_rate;
    const auto frame = static_cast<size_t>(rate / 100);
    const auto max_window = static_cast<size_t>(core->caps.max_window_samples) - frame;
    const size_t requested = request.window_ms == 0
                                 ? max_window
                                 : static_cast<size_t>(static_cast<int64_t>(request.window_ms) * rate / 1000);
    const size_t window = std::min(requested, max_window);
    const auto overlap = static_cast<size_t>(static_cast<int64_t>(request.overlap_ms) * rate / 1000);

    if (overlap * 2 > window) {
        return Invalid("overlap_ms must be at most half the window");
    }

    if (static_cast<size_t>(std::ceil(vad.pre_roll_ms * static_cast<float>(rate) / 1000.0f)) * 2 > window) {
        return Invalid("pre_roll_ms must be at most half the window");
    }

    {
        std::lock_guard lock(core->mutex);

        if (core->session_active) {
            return {BS_STATE, "a recognition session is already running on this engine; cancel it and wait first",
                    -1};
        }

        core->session_active = true;
    }

    struct Release {
        RecEngineCore* core;
        ~Release() {
            if (core != nullptr) {
                std::lock_guard lock(core->mutex);
                core->session_active = false;
            }
        }
    } release{core.get()};

    std::unique_ptr<RecognitionSession> session(new RecognitionSession(core));
    RecognitionSession& s = *session;
    s.started_ = Clock::now();
    s.language_ = language;
    s.mode_ = static_cast<DictationMode>(request.mode);
    s.n_threads_ = request.n_threads;
    s.min_confidence_ = request.min_confidence;
    s.context_chars_ = static_cast<size_t>(request.context_chars);
    s.model_rate_ = rate;
    s.frame_samples_ = frame;
    s.window_samples_ = window;
    s.overlap_samples_ = overlap;
    s.partial_interval_ = static_cast<size_t>(static_cast<int64_t>(request.partial_interval_ms) * rate / 1000);
    s.vad_settings_ = vad;
    s.preroll_cap_ = static_cast<size_t>(std::ceil(vad.pre_roll_ms * static_cast<float>(rate) / 1000.0f));
    s.live_ = live;

    if (file) {
        WavInfo info;
        std::string error;

        if (!s.reader_.Open(request.file_path, &info, &error)) {
            return {BS_ASSET, std::string("the audio file ") + request.file_path + " cannot be read: " + error, -1};
        }

        s.input_rate_ = info.sample_rate;
    } else {
        s.input_rate_ = request.input_sample_rate;
        s.ring_ = std::make_unique<PcmRing>(static_cast<size_t>(request.input_ring_samples));
        s.push_scratch_.resize(kPushScratch);
    }

    if (core->lexicon && request.bias_terms > 0 &&
        (core->caps.bias_controls & (BS_REC_BIAS_LOGIT | BS_REC_BIAS_TRIE)) != 0) {
        s.bias_terms_ = core->lexicon->TopTerms(static_cast<size_t>(request.bias_terms));

        for (const auto& term : s.bias_terms_) {
            s.bias_ptrs_.push_back(term.c_str());
        }
    }

    const double ratio = static_cast<double>(rate) / s.input_rate_;

    if (s.input_rate_ != rate) {
        s.resampler_ = std::make_unique<dsp::Resampler>(ratio);
    }

    s.vad_ = std::make_unique<Vad>(rate, vad);
    s.in_block_.resize(kBlockSamples);
    s.resampled_.reserve(static_cast<size_t>(std::ceil(kBlockSamples * ratio)) + 256);
    s.frame_acc_.reserve(frame);
    s.preroll_.reserve(s.preroll_cap_ + frame);
    s.window_audio_.reserve(window + frame);
    s.tail_.reserve(window + frame);
    s.text_buf_.resize(4096);
    s.word_buf_.resize(512);
    s.running_ = true;

    try {
        s.thread_ = std::thread([raw = session.get()] { raw->Run(); });
    } catch (const std::system_error&) {
        s.running_ = false;
        return {BS_INTERNAL, "the recognition thread could not be started", -1};
    }

    release.core = nullptr;
    *out = std::move(session);
    return {};
}

int64_t RecognitionSession::Elapsed() const noexcept { return Micros(Clock::now() - started_); }

Failure RecognitionSession::SourceFailure(int32_t rc, const char* what) const {
    if (rc == BS_REC_CANCELLED || cancelled_.load(std::memory_order_acquire)) {
        return Cancelled();
    }

    return {BS_SOURCE, std::string("the recognizer failed while ") + what + ": " + RecError(core_->source), -1};
}

int32_t RecognitionSession::Push(const float* interleaved, int32_t frames, int32_t channels) noexcept {
    if (!live_) {
        return BS_STATE;
    }

    if (cancelled_.load(std::memory_order_acquire)) {
        return BS_CANCELLED;
    }

    if (stopped_.load(std::memory_order_acquire) || ended_.load(std::memory_order_acquire)) {
        return BS_STATE;
    }

    if (frames < 0 || channels < 1 || channels > 8 || (frames > 0 && interleaved == nullptr)) {
        return BS_INVALID_ARG;
    }

    const size_t total = static_cast<size_t>(frames);
    const size_t cap = push_scratch_.size();
    const float scale = 1.0f / static_cast<float>(channels);
    size_t done = 0;

    while (done < total) {
        const size_t take = std::min(cap, total - done);
        const float* src = interleaved + done * static_cast<size_t>(channels);

        for (size_t f = 0; f < take; ++f) {
            float sum = 0.0f;

            for (int32_t c = 0; c < channels; ++c) {
                sum += src[f * static_cast<size_t>(channels) + static_cast<size_t>(c)];
            }

            push_scratch_[f] = sum * scale;
        }

        const size_t wrote = ring_->TryWrite(push_scratch_.data(), take);

        if (wrote < take) {
            overrun_frames_.fetch_add(static_cast<int64_t>(take - wrote), std::memory_order_relaxed);
        }

        done += take;
    }

    input_frames_.fetch_add(frames, std::memory_order_relaxed);
    return BS_OK;
}

void RecognitionSession::EndInput() noexcept { ended_.store(true, std::memory_order_release); }

void RecognitionSession::Cancel() noexcept {
    if (cancelled_.exchange(true, std::memory_order_acq_rel)) {
        return;
    }

    cancel_requested_us_.store(Elapsed(), std::memory_order_release);
    std::lock_guard lock(mutex_);

    for (auto it = events_.begin(); it != events_.end();) {
        it = it->kind == BS_REC_EVENT_FINISHED ? std::next(it) : events_.erase(it);
    }

    if (running_) {
        core_->source->cancel(core_->source->self);
    }
}

void RecognitionSession::Run() noexcept {
    Failure outcome;

    try {
        outcome = Process();
    } catch (const std::bad_alloc&) {
        outcome = {BS_OUT_OF_MEMORY, "out of memory while recognizing speech", -1};
    } catch (...) {
        outcome = {BS_INTERNAL, "an internal error stopped the recognition session", -1};
    }

    Finish(outcome);
}

Failure RecognitionSession::Process() {
    core_->source->reset(core_->source->self);

    while (true) {
        if (cancelled_.load(std::memory_order_acquire)) {
            return Cancelled();
        }

        {
            std::lock_guard lock(mutex_);

            if (overflow_) {
                return {BS_STATE, "results were not read; at most 4096 can wait", -1};
            }
        }

        size_t n = 0;

        if (live_) {
            n = ring_->Read(in_block_.data(), in_block_.size());

            if (n == 0) {
                if (ended_.load(std::memory_order_acquire) && ring_->Buffered() == 0) {
                    break;
                }

                std::this_thread::sleep_for(std::chrono::milliseconds(2));
                continue;
            }
        } else {
            std::string error;

            if (!reader_.Read(in_block_.data(), in_block_.size(), &n, &error)) {
                return {BS_IO, error, -1};
            }

            if (n == 0) {
                break;
            }

            input_frames_.fetch_add(static_cast<int64_t>(n), std::memory_order_relaxed);
        }

        Failure step = Ingest(in_block_.data(), n);

        if (step.status != BS_OK) {
            return step;
        }
    }

    if (resampler_) {
        resampled_.clear();
        resampler_->Flush(resampled_);
        Failure step = Consume(resampled_.data(), resampled_.size());

        if (step.status != BS_OK) {
            return step;
        }
    }

    if (!frame_acc_.empty()) {
        frame_acc_.resize(frame_samples_, 0.0f);
        Failure step = OnFrame(frame_acc_.data());
        frame_acc_.clear();

        if (step.status != BS_OK) {
            return step;
        }
    }

    if (in_utterance_) {
        return EndUtterance();
    }

    return {};
}

Failure RecognitionSession::Ingest(const float* samples, size_t n) {
    if (!resampler_) {
        return Consume(samples, n);
    }

    resampled_.clear();
    resampler_->Process(samples, n, resampled_);
    return Consume(resampled_.data(), resampled_.size());
}

Failure RecognitionSession::Consume(const float* samples, size_t n) {
    size_t at = 0;

    while (at < n) {
        const size_t take = std::min(frame_samples_ - frame_acc_.size(), n - at);
        frame_acc_.insert(frame_acc_.end(), samples + at, samples + at + take);
        at += take;

        if (frame_acc_.size() == frame_samples_) {
            Failure step = OnFrame(frame_acc_.data());
            frame_acc_.clear();

            if (step.status != BS_OK) {
                return step;
            }
        }
    }

    return {};
}

Failure RecognitionSession::OnFrame(const float* frame) {
    if (cancelled_.load(std::memory_order_acquire)) {
        return Cancelled();
    }

    const Vad::Event event = vad_->Push(frame);
    const size_t fs = frame_samples_;
    Failure step;

    if (!in_utterance_) {
        preroll_.insert(preroll_.end(), frame, frame + fs);

        if (preroll_.size() > preroll_cap_) {
            preroll_.erase(preroll_.begin(), preroll_.begin() + static_cast<std::ptrdiff_t>(preroll_.size() - preroll_cap_));
        }

        if (event == Vad::Event::Onset) {
            in_utterance_ = true;
            onset_wall_ = Clock::now();
            last_voiced_wall_ = onset_wall_;
            awaiting_first_partial_ = true;
            SetOnce(counters_.t_first_voiced, Elapsed());
            window_abs_start_ = model_samples_ + static_cast<int64_t>(fs) - static_cast<int64_t>(preroll_.size());
            step = StartWindow(preroll_.data(), preroll_.size());
            preroll_.clear();
        }
    } else {
        step = Feed(frame, fs);

        if (step.status == BS_OK) {
            if (vad_->LastFrameVoiced()) {
                last_voiced_wall_ = Clock::now();
            }

            if (event == Vad::Event::Offset) {
                step = EndUtterance();
            } else if (window_audio_.size() >= window_samples_) {
                step = CutWindow();
            } else if (partial_interval_ > 0) {
                since_partial_ += fs;

                if (since_partial_ >= partial_interval_) {
                    since_partial_ = 0;
                    step = PartialStep();
                }
            }
        }
    }

    model_samples_ += static_cast<int64_t>(fs);
    counters_.model_samples.store(model_samples_, std::memory_order_relaxed);
    return step;
}

Failure RecognitionSession::StartWindow(const float* audio, size_t n) {
    const bs_recognizer_v1* source = core_->source;
    bs_rec_window_options options{};
    options.language = language_.c_str();

    if ((core_->caps.bias_controls & BS_REC_BIAS_CONTEXT) != 0 && context_chars_ > 0) {
        context_ = Tail(committed_, context_chars_);
        options.context = context_.c_str();
        options.context_len = context_.size();
    }

    options.bias_terms = bias_ptrs_.empty() ? nullptr : bias_ptrs_.data();
    options.bias_term_count = static_cast<int32_t>(bias_ptrs_.size());
    options.n_threads = n_threads_;

    const int32_t rc = source->begin(source->self, &options);

    if (rc != BS_REC_OK) {
        return SourceFailure(rc, "starting a window");
    }

    counters_.windows.fetch_add(1, std::memory_order_relaxed);
    window_audio_.clear();
    stable_.clear();
    previous_.clear();
    since_partial_ = 0;

    if (n == 0) {
        return {};
    }

    return Feed(audio, n);
}

Failure RecognitionSession::Feed(const float* audio, size_t n) {
    const bs_recognizer_v1* source = core_->source;
    window_audio_.insert(window_audio_.end(), audio, audio + n);
    Max(counters_.peak_window, static_cast<int64_t>(window_audio_.size()));
    int32_t rc = 0;

    {
        Stopwatch watch(counters_.feed_us);
        rc = source->feed(source->self, audio, static_cast<int32_t>(n));
    }

    return rc == BS_REC_OK ? Failure{} : SourceFailure(rc, "taking audio");
}

Failure RecognitionSession::Decode(bool final, std::vector<HypWord>* words) {
    const bs_recognizer_v1* source = core_->source;
    words->clear();

    for (int attempt = 0; attempt < kDecodeAttempts; ++attempt) {
        int32_t text_len = 0;
        int32_t count = 0;
        const auto text_cap = static_cast<int32_t>(text_buf_.size());
        const auto words_cap = static_cast<int32_t>(word_buf_.size());
        int32_t rc = 0;

        {
            Stopwatch watch(final ? counters_.finalize_us : counters_.partial_us);
            rc = (final ? source->finalize : source->partial)(source->self, text_buf_.data(), text_cap, &text_len,
                                                              word_buf_.data(), words_cap, &count);
        }

        if (rc == BS_REC_BUFFER_TOO_SMALL) {
            if (text_len > text_cap) {
                text_buf_.resize(static_cast<size_t>(text_len));
            }

            if (count > words_cap) {
                word_buf_.resize(static_cast<size_t>(count));
            }

            if (text_len <= text_cap && count <= words_cap) {
                return {BS_SOURCE, "the recognizer asked for larger buffers without saying how large", -1};
            }

            continue;
        }

        if (rc != BS_REC_OK) {
            return SourceFailure(rc, final ? "finalizing a window" : "reading a partial result");
        }

        if (text_len < 0 || text_len > text_cap || count < 0 || count > words_cap) {
            return {BS_SOURCE, "the recognizer returned sizes outside its buffers", -1};
        }

        const std::string_view text(text_buf_.data(), static_cast<size_t>(text_len));
        size_t bad = 0;

        if (!utf8::Valid(text, &bad)) {
            return {BS_SOURCE, "the recognizer returned text that is not UTF-8", -1};
        }

        for (int32_t k = 0; k < count; ++k) {
            const bs_rec_word& w = word_buf_[static_cast<size_t>(k)];

            if (w.text_offset < 0 || w.text_length <= 0 || w.text_offset + w.text_length > text_len) {
                return {BS_SOURCE, "the recognizer returned a word outside its text", -1};
            }

            HypWord word;
            word.text = std::string(text.substr(static_cast<size_t>(w.text_offset), static_cast<size_t>(w.text_length)));
            word.key = Key(word.text);

            if (core_->caps.word_timestamps != 0) {
                word.start = w.start_sample;
                word.end = w.end_sample;
            }

            word.confidence = std::isfinite(w.confidence) ? std::clamp(w.confidence, 0.0f, 1.0f) : 0.0f;
            words->push_back(std::move(word));
        }

        return {};
    }

    return {BS_SOURCE, "the recognizer kept asking for larger buffers", -1};
}

Failure RecognitionSession::PartialStep() {
    std::vector<HypWord> hypothesis;
    Failure step = Decode(false, &hypothesis);

    if (step.status != BS_OK) {
        return step;
    }

    size_t agreed = 0;

    while (agreed < previous_.size() && agreed < hypothesis.size() &&
           previous_[agreed].key == hypothesis[agreed].key) {
        ++agreed;
    }

    bool consistent = hypothesis.size() >= stable_.size();

    for (size_t k = 0; consistent && k < stable_.size(); ++k) {
        consistent = stable_[k].key == hypothesis[k].key;
    }

    if (consistent && agreed > stable_.size()) {
        stable_.insert(stable_.end(), hypothesis.begin() + static_cast<std::ptrdiff_t>(stable_.size()),
                       hypothesis.begin() + static_cast<std::ptrdiff_t>(agreed));
    }

    previous_ = hypothesis;

    RecEvent event;
    event.kind = BS_REC_EVENT_PARTIAL;
    event.segment = segment_;
    event.start = window_abs_start_;
    event.end = window_abs_start_ + static_cast<int64_t>(window_audio_.size());
    event.text = Join(stable_, 0);
    event.unstable = Join(hypothesis, std::min(stable_.size(), hypothesis.size()));
    counters_.partials.fetch_add(1, std::memory_order_relaxed);
    SetOnce(counters_.t_first_partial, Elapsed());

    if (awaiting_first_partial_) {
        awaiting_first_partial_ = false;
        SetOnce(counters_.onset_to_first_partial, Micros(Clock::now() - onset_wall_));
    }

    PushEvent(std::move(event));
    return {};
}

std::vector<HypWord> RecognitionSession::Merge(const std::vector<HypWord>& final_words) const {
    if (stable_.empty()) {
        return final_words;
    }

    size_t k = stable_.size();
    bool agrees = final_words.size() >= k;

    for (size_t i = 0; agrees && i < k; ++i) {
        agrees = final_words[i].key == stable_[i].key;
    }

    if (!agrees) {
        const int64_t stable_end = stable_.back().end;

        if (core_->caps.word_timestamps != 0 && stable_end >= 0) {
            k = final_words.size();

            for (size_t i = 0; i < final_words.size(); ++i) {
                if (final_words[i].start >= stable_end - static_cast<int64_t>(frame_samples_)) {
                    k = i;
                    break;
                }
            }
        } else {
            k = std::min(k, final_words.size());
        }
    }

    std::vector<HypWord> merged = stable_;
    merged.insert(merged.end(), final_words.begin() + static_cast<std::ptrdiff_t>(k), final_words.end());
    return merged;
}

Failure RecognitionSession::Commit(std::vector<HypWord> words, bool segment_end) {
    if (!dedupe_tail_.empty() && !words.empty()) {
        const size_t limit = std::min(dedupe_tail_.size(), words.size());

        for (size_t m = limit; m > 0; --m) {
            bool match = true;

            for (size_t k = 0; k < m; ++k) {
                if (dedupe_tail_[dedupe_tail_.size() - m + k] != words[k].key) {
                    match = false;
                    break;
                }
            }

            if (match) {
                words.erase(words.begin(), words.begin() + static_cast<std::ptrdiff_t>(m));
                break;
            }
        }
    }

    dedupe_tail_.clear();

    if (words.empty() && !segment_end) {
        return {};
    }

    std::vector<InputWord> input;
    float lowest = 1.0f;

    for (const auto& word : words) {
        input.push_back({word.text, word.confidence});
        lowest = std::min(lowest, word.confidence);
    }

    Dictation dictation = Dictate(input, mode_, core_->lexicon.get(), min_confidence_);

    RecEvent event;
    event.kind = BS_REC_EVENT_FINAL;
    event.segment = segment_;
    event.segment_end = segment_end;
    event.min_confidence = words.empty() ? 1.0f : lowest;
    event.start = !words.empty() && words.front().start >= 0 ? window_abs_start_ + words.front().start
                                                              : window_abs_start_;
    event.end = !words.empty() && words.back().end >= 0
                    ? window_abs_start_ + words.back().end
                    : window_abs_start_ + static_cast<int64_t>(window_audio_.size());
    event.text = std::move(dictation.text);
    event.raw = std::move(dictation.raw);
    event.substitutions = std::move(dictation.substitutions);

    if (!event.text.empty()) {
        if (!committed_.empty()) {
            committed_.push_back(' ');
        }

        committed_ += event.text;
        TrimContext(&committed_, context_chars_);
    }

    counters_.finals.fetch_add(1, std::memory_order_relaxed);
    SetOnce(counters_.t_first_final, Elapsed());

    if (segment_end && live_) {
        std::lock_guard lock(mutex_);

        if (latencies_.size() < kMaxLatencies) {
            latencies_.push_back(Micros(Clock::now() - last_voiced_wall_));
        }
    }

    PushEvent(std::move(event));
    return {};
}

Failure RecognitionSession::CutWindow() {
    std::vector<HypWord> final_words;
    Failure step = Decode(true, &final_words);

    if (step.status != BS_OK) {
        return step;
    }

    const std::vector<HypWord> merged = Merge(final_words);
    const size_t fed = window_audio_.size();
    const size_t cut = fed > overlap_samples_ ? fed - overlap_samples_ : fed / 2;
    std::vector<HypWord> commit;
    size_t next_start = cut;

    if (core_->caps.word_timestamps != 0) {
        size_t i = 0;
        int64_t committed_end = 0;

        for (; i < merged.size(); ++i) {
            if (merged[i].end < 0 || merged[i].end > static_cast<int64_t>(cut)) {
                break;
            }

            committed_end = std::max(committed_end, merged[i].end);
            commit.push_back(merged[i]);
        }

        if (i < merged.size() && merged[i].start >= 0) {
            const int64_t lead = std::max<int64_t>(committed_end, merged[i].start - model_rate_ / 10);
            next_start = std::min(cut, static_cast<size_t>(std::max<int64_t>(0, lead)));
        }

        if (next_start == 0) {
            int64_t furthest = static_cast<int64_t>(cut);

            for (const auto& word : merged) {
                furthest = std::max(furthest, word.end >= 0 ? word.end : static_cast<int64_t>(fed));
            }

            commit = merged;
            next_start = static_cast<size_t>(std::min(furthest, static_cast<int64_t>(fed)));
        }
    } else {
        commit = merged;
    }

    std::vector<std::string> tail_keys;

    if (core_->caps.word_timestamps == 0) {
        const size_t keep = std::min(kDedupeWords, commit.size());

        for (size_t k = commit.size() - keep; k < commit.size(); ++k) {
            tail_keys.push_back(commit[k].key);
        }
    }

    step = Commit(std::move(commit), false);

    if (step.status != BS_OK) {
        return step;
    }

    dedupe_tail_ = std::move(tail_keys);
    tail_.assign(window_audio_.begin() + static_cast<std::ptrdiff_t>(next_start), window_audio_.end());
    window_abs_start_ += static_cast<int64_t>(next_start);
    return StartWindow(tail_.data(), tail_.size());
}

Failure RecognitionSession::EndUtterance() {
    std::vector<HypWord> final_words;
    Failure step = Decode(true, &final_words);

    if (step.status != BS_OK) {
        return step;
    }

    step = Commit(Merge(final_words), true);
    in_utterance_ = false;
    window_audio_.clear();
    stable_.clear();
    previous_.clear();
    preroll_.clear();
    since_partial_ = 0;
    ++segment_;
    counters_.segments.fetch_add(1, std::memory_order_relaxed);
    return step;
}

void RecognitionSession::PushEvent(RecEvent event) {
    std::lock_guard lock(mutex_);

    if (cancelled_.load(std::memory_order_acquire) && event.kind != BS_REC_EVENT_FINISHED) {
        return;
    }

    if (event.kind == BS_REC_EVENT_PARTIAL && !events_.empty() && events_.back().kind == BS_REC_EVENT_PARTIAL &&
        events_.back().segment == event.segment) {
        events_.back() = std::move(event);
        return;
    }

    if (events_.size() >= kMaxEvents && event.kind != BS_REC_EVENT_FINISHED) {
        const auto partial = std::find_if(events_.begin(), events_.end(),
                                          [](const RecEvent& e) { return e.kind == BS_REC_EVENT_PARTIAL; });

        if (partial != events_.end()) {
            events_.erase(partial);
        } else {
            overflow_ = true;
            return;
        }
    }

    events_.push_back(std::move(event));
}

void RecognitionSession::Finish(const Failure& result) {
    stopped_.store(true, std::memory_order_release);

    {
        std::lock_guard lock(mutex_);
        running_ = false;
    }

    core_->source->reset(core_->source->self);

    const int64_t finished = Elapsed();
    counters_.t_finished.store(finished, std::memory_order_relaxed);
    const int64_t requested = cancel_requested_us_.load(std::memory_order_acquire);

    if (requested >= 0) {
        counters_.cancel_latency.store(finished - requested, std::memory_order_relaxed);
    }

    {
        std::lock_guard lock(core_->mutex);
        core_->session_active = false;
    }

    RecEvent done;
    done.kind = BS_REC_EVENT_FINISHED;
    done.status = result.status;
    done.segment = segment_;
    done.text = result.message;

    {
        std::lock_guard lock(mutex_);
        outcome_ = result;
        finished_ = true;
        events_.push_back(std::move(done));
    }

    done_cv_.notify_all();
}

int32_t RecognitionSession::Poll(bs_rec_event* out) {
    const uint32_t size = out->struct_size;
    *out = bs_rec_event{};
    out->struct_size = size;

    std::lock_guard lock(mutex_);

    if (events_.empty()) {
        if (!finished_delivered_) {
            out->kind = BS_REC_EVENT_NONE;
            return BS_OK;
        }

        out->kind = BS_REC_EVENT_FINISHED;
        out->status = polled_.status;
        out->text = polled_.text.c_str();
        out->text_len = polled_.text.size();
        out->unstable = "";
        out->raw = "";
        return BS_OK;
    }

    polled_ = std::move(events_.front());
    events_.pop_front();

    if (polled_.kind == BS_REC_EVENT_FINISHED) {
        finished_delivered_ = true;
    }

    out->kind = polled_.kind;
    out->segment = polled_.segment;
    out->segment_end = polled_.segment_end ? 1 : 0;
    out->status = polled_.status;
    out->substitutions = static_cast<int32_t>(polled_.substitutions.size());
    out->start_sample = polled_.start;
    out->end_sample = polled_.end;
    out->min_confidence = polled_.min_confidence;
    out->text = polled_.text.c_str();
    out->text_len = polled_.text.size();
    out->unstable = polled_.unstable.c_str();
    out->unstable_len = polled_.unstable.size();
    out->raw = polled_.raw.c_str();
    out->raw_len = polled_.raw.size();
    return BS_OK;
}

int32_t RecognitionSession::SubstitutionAt(int32_t index, bs_substitution* out) const {
    std::lock_guard lock(mutex_);

    if (index < 0 || static_cast<size_t>(index) >= polled_.substitutions.size()) {
        return BS_INVALID_ARG;
    }

    const Substitution& s = polled_.substitutions[static_cast<size_t>(index)];
    const uint32_t size = out->struct_size;
    *out = bs_substitution{};
    out->struct_size = size;
    out->kind = static_cast<int32_t>(s.kind);
    out->applied = s.applied ? 1 : 0;
    out->confidence = s.confidence;
    out->raw_offset = s.raw_offset;
    out->raw_length = s.raw_length;
    out->text_offset = s.text_offset;
    out->text_length = s.text_length;
    out->replacement = s.replacement.c_str();
    out->replacement_len = s.replacement.size();
    return BS_OK;
}

int32_t RecognitionSession::Wait(int32_t timeout_ms, int32_t* out_status) {
    std::unique_lock lock(mutex_);
    auto finished = [this] { return finished_; };

    if (timeout_ms < 0) {
        done_cv_.wait(lock, finished);
    } else if (!done_cv_.wait_for(lock, std::chrono::milliseconds(timeout_ms), finished)) {
        return BS_TIMEOUT;
    }

    *out_status = outcome_.status;
    return BS_OK;
}

void RecognitionSession::Stats(bs_rec_stats* out) const {
    const uint32_t size = out->struct_size;
    *out = bs_rec_stats{};
    out->struct_size = size;
    std::vector<int64_t> latencies;

    {
        std::lock_guard lock(mutex_);
        out->finished = finished_ ? 1 : 0;
        out->status = finished_ ? outcome_.status : BS_OK;
        latencies = latencies_;
    }

    const auto get = [](const std::atomic<int64_t>& v) { return v.load(std::memory_order_relaxed); };

    out->model_sample_rate = model_rate_;
    out->segments = counters_.segments.load(std::memory_order_relaxed);
    out->finals = counters_.finals.load(std::memory_order_relaxed);
    out->partials = counters_.partials.load(std::memory_order_relaxed);
    out->windows = counters_.windows.load(std::memory_order_relaxed);
    out->input_frames = get(input_frames_);
    out->overrun_frames = get(overrun_frames_);
    out->model_samples = get(counters_.model_samples);
    out->peak_window_samples = get(counters_.peak_window);
    out->t_first_voiced_us = get(counters_.t_first_voiced);
    out->t_first_partial_us = get(counters_.t_first_partial);
    out->t_first_final_us = get(counters_.t_first_final);
    out->t_finished_us = get(counters_.t_finished);
    out->onset_to_first_partial_us = get(counters_.onset_to_first_partial);
    out->feed_us = get(counters_.feed_us);
    out->partial_us = get(counters_.partial_us);
    out->finalize_us = get(counters_.finalize_us);
    out->cancel_latency_us = get(counters_.cancel_latency);
    out->speech_end_to_final_count = static_cast<int64_t>(latencies.size());
    out->speech_end_to_final_median_us = -1;
    out->speech_end_to_final_p90_us = -1;

    if (!latencies.empty()) {
        std::sort(latencies.begin(), latencies.end());
        out->speech_end_to_final_median_us = latencies[latencies.size() / 2];
        const size_t p90 = static_cast<size_t>(std::ceil(static_cast<double>(latencies.size()) * 0.9)) - 1;
        out->speech_end_to_final_p90_us = latencies[std::min(p90, latencies.size() - 1)];
    }
}

std::string RecognitionSession::Error() const {
    std::lock_guard lock(mutex_);
    return outcome_.message;
}

}
