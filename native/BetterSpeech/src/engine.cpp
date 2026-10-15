#include "engine.h"

#include <algorithm>
#include <cmath>
#include <new>

#include "utf8.h"

namespace bspeech {

namespace {

using Clock = std::chrono::steady_clock;

// A laughter clip is an event, not a recording to play; anything longer is
// refused so the decoded clip stays a small, bounded allocation.
constexpr int64_t kMaxClipSeconds = 30;

// 16 Mi samples is about 11.6 minutes at 24 kHz and 64 MiB of floats: far more
// than any useful playback lead, and still a hard bound.
constexpr int32_t kMaxRingSamples = 16 * 1024 * 1024;

constexpr float kDefaultFadeMs = 5.0f;
constexpr float kMaxFadeMs = 50.0f;
constexpr float kDefaultAudibleDbfs = -45.0f;
constexpr size_t kMinPullSamples = 4096;

int64_t Micros(Clock::duration d) { return std::chrono::duration_cast<std::chrono::microseconds>(d).count(); }

std::string SourceError(const bs_source_v1* source) {
    const char* text = source->last_error(source->self);

    if (text == nullptr || *text == '\0') {
        return "no reason was given";
    }

    return text;
}

bool InRange(float value, float lo, float hi) { return std::isfinite(value) && value >= lo && value <= hi; }

Failure Invalid(std::string message) { return {BS_INVALID_ARG, std::move(message), -1}; }

Failure Cancelled() { return {BS_CANCELLED, "the job was cancelled", -1}; }

void Max(std::atomic<int64_t>& target, int64_t value) {
    int64_t seen = target.load(std::memory_order_relaxed);

    while (value > seen && !target.compare_exchange_weak(seen, value, std::memory_order_relaxed)) {
    }
}

void SetOnce(std::atomic<int64_t>& target, int64_t value) {
    int64_t unset = -1;
    target.compare_exchange_strong(unset, value, std::memory_order_relaxed);
}

class Stopwatch {
public:
    explicit Stopwatch(std::atomic<int64_t>& total) : total_(total), start_(Clock::now()) {}
    ~Stopwatch() { total_.fetch_add(Micros(Clock::now() - start_), std::memory_order_relaxed); }

private:
    std::atomic<int64_t>& total_;
    Clock::time_point start_;
};

}  // namespace

Failure CreateEngineCore(const bs_engine_config& config, std::shared_ptr<EngineCore>* out) {
    auto core = std::make_shared<EngineCore>();
    const bs_source_v1* source = config.source;

    if (source != nullptr) {
        if (source->abi_version != BS_SOURCE_ABI_VERSION || source->struct_size < sizeof(bs_source_v1)) {
            return Invalid("the acoustic source implements source ABI " + std::to_string(source->abi_version) +
                           " with a " + std::to_string(source->struct_size) + "-byte table; this engine needs ABI " +
                           std::to_string(BS_SOURCE_ABI_VERSION));
        }

        if (source->get_caps == nullptr || source->count_units == nullptr || source->begin == nullptr ||
            source->pull == nullptr || source->cancel == nullptr || source->reset == nullptr ||
            source->last_error == nullptr) {
            return Invalid("the acoustic source table is missing a required function");
        }

        if (source->get_caps(source->self, &core->caps) != BS_SOURCE_OK) {
            return {BS_SOURCE, "the acoustic source could not report its capabilities: " + SourceError(source), -1};
        }

        const bs_source_caps& caps = core->caps;

        if (caps.sample_rate < 8000 || caps.sample_rate > 192000 || caps.channels != 1 ||
            caps.max_units_per_chunk < 0 || caps.voice_count < 0 || caps.min_unit_samples < 0) {
            return {BS_SOURCE, "the acoustic source reported capabilities this engine cannot use (" +
                                   std::to_string(caps.sample_rate) + " Hz, " + std::to_string(caps.channels) +
                                   " channels)",
                    -1};
        }

        if (caps.native_events != 0 && source->begin_event == nullptr) {
            return Invalid("the acoustic source claims native events but has no begin_event");
        }

        if (caps.voice_count > 0 && source->voice_id == nullptr) {
            return Invalid("the acoustic source lists voices but has no voice_id");
        }

        for (int32_t i = 0; i < caps.voice_count; ++i) {
            std::string id(64, '\0');
            int32_t length = 0;
            int32_t rc = source->voice_id(source->self, i, id.data(), static_cast<int32_t>(id.size()), &length);

            if (rc != BS_SOURCE_OK && length > static_cast<int32_t>(id.size())) {
                id.assign(static_cast<size_t>(length), '\0');
                rc = source->voice_id(source->self, i, id.data(), static_cast<int32_t>(id.size()), &length);
            }

            if (rc != BS_SOURCE_OK || length < 0 || length > static_cast<int32_t>(id.size())) {
                return {BS_SOURCE, "the acoustic source could not name voice " + std::to_string(i), -1};
            }

            id.resize(static_cast<size_t>(length));
            core->voices.push_back(std::move(id));
        }

        core->source = source;
    } else {
        core->unavailable = "no acoustic model is attached to the speech engine";
    }

    if (config.laugh_clip_path != nullptr && *config.laugh_clip_path != '\0') {
        const std::string path = config.laugh_clip_path;
        WavInfo info;
        std::string error;

        if (!ProbeWav(path, &info, &error)) {
            return {BS_ASSET, "the laughter clip " + path + " cannot be used: " + error, -1};
        }

        const int64_t limit = kMaxClipSeconds * info.sample_rate;
        std::vector<float> clip;

        if (!ReadWavMono(path, limit, &clip, &info, &error)) {
            return {BS_ASSET, "the laughter clip " + path + " cannot be used: " + error, -1};
        }

        core->laugh_clip_rate = info.sample_rate;

        if (core->HasSource() && info.sample_rate != core->caps.sample_rate) {
            dsp::Resampler resampler(static_cast<double>(core->caps.sample_rate) / info.sample_rate);
            std::vector<float> converted;
            converted.reserve(static_cast<size_t>(
                std::ceil(static_cast<double>(clip.size()) * core->caps.sample_rate / info.sample_rate)) + 1);
            resampler.Process(clip.data(), clip.size(), converted);
            resampler.Flush(converted);
            clip = std::move(converted);
        }

        core->laugh_clip = std::move(clip);
    }

    *out = std::move(core);
    return {};
}

void FillCapabilities(const EngineCore& core, bs_capabilities* caps) {
    const uint32_t size = caps->struct_size;
    *caps = bs_capabilities{};
    caps->struct_size = size;

    caps->synthesis_available = core.HasSource() ? 1 : 0;
    caps->gain = BS_CONTROL_ENGINE;
    caps->rate = BS_CONTROL_ENGINE;
    caps->pitch = BS_CONTROL_ENGINE;
    caps->voice = BS_CONTROL_UNAVAILABLE;
    caps->emotion = BS_CONTROL_UNAVAILABLE;
    caps->cloning = BS_CONTROL_UNAVAILABLE;
    caps->laugh = core.laugh_clip.empty() ? BS_EVENT_RENDER_UNAVAILABLE : BS_EVENT_RENDER_CLIP;
    caps->laugh_clip_sample_rate = core.laugh_clip_rate;

    if (!core.HasSource()) {
        return;
    }

    caps->sample_rate = core.caps.sample_rate;
    caps->streaming = core.caps.streaming;
    caps->min_unit_samples = core.caps.min_unit_samples;
    caps->max_units_per_chunk = core.caps.max_units_per_chunk;
    caps->voice_count = core.caps.voice_count;

    if (core.NativeControl(BS_SOURCE_CONTROL_VOICE) && core.caps.voice_count > 0) {
        caps->voice = BS_CONTROL_MODEL;
    }

    if (core.NativeControl(BS_SOURCE_CONTROL_RATE)) {
        caps->rate = BS_CONTROL_MODEL;
    }

    if (core.NativeControl(BS_SOURCE_CONTROL_PITCH)) {
        caps->pitch = BS_CONTROL_MODEL;
    }

    if (core.NativeControl(BS_SOURCE_CONTROL_EMOTION)) {
        caps->emotion = BS_CONTROL_MODEL;
    }

    if (core.NativeControl(BS_SOURCE_CONTROL_CLONING)) {
        caps->cloning = BS_CONTROL_MODEL;
    }

    if (core.NativeEvent(EventKind::Laugh)) {
        caps->laugh = BS_EVENT_RENDER_NATIVE;
    }
}

Job::Job(std::shared_ptr<EngineCore> core) : core_(std::move(core)) {}

Job::~Job() {
    Cancel();

    if (thread_.joinable()) {
        thread_.join();
    }
}

Failure Job::Start(std::shared_ptr<EngineCore> core, const bs_request& request, std::unique_ptr<Job>* out) {
    if (request.struct_size < sizeof(bs_request)) {
        return Invalid("bs_request.struct_size is smaller than the request this engine was built with");
    }

    if (request.text == nullptr && request.text_len > 0) {
        return Invalid("the text pointer is null");
    }

    if (request.language == nullptr || *request.language == '\0') {
        return Invalid("a language is required: it selects the sentence rules and the model's language");
    }

    const bs_voice& voice = request.voice;

    if (!InRange(voice.rate, 0.5f, 2.0f)) {
        return Invalid("the speaking rate must be between 0.5 and 2");
    }

    if (!InRange(voice.pitch_semitones, -12.0f, 12.0f)) {
        return Invalid("the pitch must be between -12 and 12 semitones");
    }

    if (!InRange(voice.gain_db, -24.0f, 12.0f)) {
        return Invalid("the gain must be between -24 and 12 dB");
    }

    if (request.unknown_tag_policy != BS_UNKNOWN_TAG_ERROR && request.unknown_tag_policy != BS_UNKNOWN_TAG_LITERAL) {
        return Invalid("unknown_tag_policy is not a bs_unknown_tag_policy value");
    }

    if (request.playback_ring_samples < 0 || request.playback_ring_samples > kMaxRingSamples) {
        return Invalid("playback_ring_samples must be between 0 and " + std::to_string(kMaxRingSamples));
    }

    if (request.prebuffer_samples < 0 || request.prebuffer_samples > request.playback_ring_samples) {
        return Invalid("prebuffer_samples must be between 0 and playback_ring_samples");
    }

    const bool exporting = request.export_wav_path != nullptr && *request.export_wav_path != '\0';

    if (request.playback_ring_samples == 0 && !exporting) {
        return Invalid("the request asks for neither playback nor export");
    }

    if (request.first_chunk_units < 0 || request.n_threads < 0) {
        return Invalid("first_chunk_units and n_threads cannot be negative");
    }

    const float fade_ms = request.boundary_fade_ms < 0.0f ? kDefaultFadeMs : request.boundary_fade_ms;

    if (!InRange(fade_ms, 0.0f, kMaxFadeMs)) {
        return Invalid("boundary_fade_ms must be at most 50");
    }

    const float audible = request.audible_threshold_dbfs == 0.0f ? kDefaultAudibleDbfs : request.audible_threshold_dbfs;

    if (!InRange(audible, -120.0f, -1.0f)) {
        return Invalid("audible_threshold_dbfs must be between -120 and -1");
    }

    if (!core->HasSource()) {
        return {BS_UNAVAILABLE, core->unavailable, -1};
    }

    const std::string voice_id = voice.voice_id != nullptr ? voice.voice_id : "";

    if (!voice_id.empty()) {
        if (!core->NativeControl(BS_SOURCE_CONTROL_VOICE) || core->voices.empty()) {
            return {BS_UNAVAILABLE, "this model offers no voice selection, so voice '" + voice_id + "' cannot be used",
                    -1};
        }

        if (std::find(core->voices.begin(), core->voices.end(), voice_id) == core->voices.end()) {
            return Invalid("the model has no voice '" + voice_id + "'");
        }
    }

    {
        std::lock_guard lock(core->mutex);

        if (core->job_active) {
            return {BS_STATE,
                    "a speech job is already running on this engine; cancel it and wait for it before starting another",
                    -1};
        }

        core->job_active = true;
    }

    struct Release {
        EngineCore* core;
        ~Release() {
            if (core != nullptr) {
                std::lock_guard lock(core->mutex);
                core->job_active = false;
            }
        }
    } release{core.get()};

    std::unique_ptr<Job> job(new Job(core));
    job->started_ = Clock::now();

    const bs_source_v1* source = core->source;
    PlanSettings settings;
    settings.language = request.language;
    settings.unknown_tags = static_cast<UnknownTags>(request.unknown_tag_policy);
    settings.max_units = core->caps.max_units_per_chunk;
    settings.first_chunk_units = request.first_chunk_units;
    settings.measure = [source](std::string_view text) {
        int32_t units = 0;
        return source->count_units(source->self, text.data(), text.size(), &units) == BS_SOURCE_OK ? units : -1;
    };

    PlanError plan_error;
    const std::string_view text(request.text != nullptr ? request.text : "", request.text_len);

    if (!BuildPlan(text, settings, &job->plan_, &plan_error)) {
        return {BS_INVALID_ARG, plan_error.message, plan_error.offset};
    }

    if (job->plan_.empty()) {
        return Invalid("the text holds nothing to speak");
    }

    const bool laughs = std::any_of(job->plan_.begin(), job->plan_.end(), [](const PlanItem& item) {
        return item.kind == ItemKind::Event && item.event == EventKind::Laugh;
    });

    if (laughs && !core->NativeEvent(EventKind::Laugh) && core->laugh_clip.empty()) {
        return {BS_UNAVAILABLE,
                "the text contains [Laugh], the model does not render laughter itself, and no laughter clip is "
                "configured",
                -1};
    }

    job->counters_.t_plan.store(job->Elapsed(), std::memory_order_relaxed);

    const int32_t rate = core->caps.sample_rate;
    float engine_rate = voice.rate;
    float engine_pitch = voice.pitch_semitones;

    job->source_voice_.rate = 1.0f;
    job->source_voice_.pitch_semitones = 0.0f;

    if (core->NativeControl(BS_SOURCE_CONTROL_RATE)) {
        job->source_voice_.rate = voice.rate;
        engine_rate = 1.0f;
    }

    if (core->NativeControl(BS_SOURCE_CONTROL_PITCH)) {
        job->source_voice_.pitch_semitones = voice.pitch_semitones;
        engine_pitch = 0.0f;
    }

    job->voice_id_ = voice_id;
    job->source_voice_.voice_id = job->voice_id_.empty() ? nullptr : job->voice_id_.c_str();
    job->language_ = request.language;
    job->n_threads_ = request.n_threads;
    job->sample_rate_ = rate;

    job->chain_ = std::make_unique<dsp::VoiceChain>(rate, engine_rate, engine_pitch);
    job->limiter_ = std::make_unique<dsp::GainLimiter>(rate, voice.gain_db);
    job->fader_ = std::make_unique<dsp::EdgeFader>(static_cast<size_t>(std::lround(fade_ms * rate / 1000.0f)));
    job->detector_ = std::make_unique<dsp::AudibleDetector>(rate, audible);

    const size_t pull = std::max<size_t>(kMinPullSamples, static_cast<size_t>(core->caps.min_unit_samples));
    job->pull_buffer_.resize(pull);

    // Rate 0.5 doubles the length and a pitch shift adds a resampler's worth, so
    // four times the pull size covers one pull's output without growing.
    job->stage_a_.reserve(pull * 4);
    job->stage_b_.reserve(pull * 4);

    if (request.playback_ring_samples > 0) {
        job->ring_ = std::make_unique<PcmRing>(static_cast<size_t>(request.playback_ring_samples));
        job->prebuffer_ = static_cast<size_t>(request.prebuffer_samples);
    }

    if (exporting) {
        std::string error;

        if (!job->writer_.Open(request.export_wav_path, rate, &error)) {
            return {BS_IO, std::string("the export to ") + request.export_wav_path + " cannot start: " + error, -1};
        }

        job->exporting_ = true;
    }

    job->running_ = true;

    try {
        job->thread_ = std::thread([raw = job.get()] { raw->Run(); });
    } catch (const std::system_error&) {
        job->running_ = false;
        job->writer_.Abort();
        return {BS_INTERNAL, "the speech thread could not be started", -1};
    }

    // The job thread now owns the latch and clears it when it finishes.
    release.core = nullptr;
    *out = std::move(job);
    return {};
}

int64_t Job::Elapsed() const noexcept { return Micros(Clock::now() - started_); }

Failure Job::SourceFailure(int32_t rc, const char* what) const {
    if (rc == BS_SOURCE_CANCELLED || cancelled_.load(std::memory_order_acquire)) {
        return Cancelled();
    }

    return {BS_SOURCE, std::string("the acoustic model failed while ") + what + ": " + SourceError(core_->source), -1};
}

void Job::Run() noexcept {
    Failure outcome;

    try {
        outcome = Generate();
    } catch (const std::bad_alloc&) {
        outcome = {BS_OUT_OF_MEMORY, "out of memory while generating speech", -1};
    } catch (...) {
        outcome = {BS_INTERNAL, "an internal error stopped the speech job", -1};
    }

    Finish(outcome);
}

Failure Job::Generate() {
    const bs_source_v1* source = core_->source;
    source->reset(source->self);

    for (const PlanItem& item : plan_) {
        if (cancelled_.load(std::memory_order_acquire)) {
            return Cancelled();
        }

        Failure step;

        if (item.kind == ItemKind::Speech) {
            step = Speak(item);
        } else if (core_->NativeEvent(item.event)) {
            step = SpeakEvent(item.event);
        } else {
            step = InsertClip();
        }

        if (step.status != BS_OK) {
            return step;
        }
    }

    if (cancelled_.load(std::memory_order_acquire)) {
        return Cancelled();
    }

    return {};
}

Failure Job::Speak(const PlanItem& item) {
    const bs_source_v1* source = core_->source;
    int32_t rc = 0;

    {
        Stopwatch watch(counters_.source_us);
        rc = source->begin(source->self, item.text.data(), item.text.size(), language_.c_str(), &source_voice_,
                           n_threads_);
    }

    if (rc != BS_SOURCE_OK) {
        return SourceFailure(rc, "starting a chunk");
    }

    counters_.chunks.fetch_add(1, std::memory_order_relaxed);
    fader_->Begin();

    Failure pulled = PullChunk();

    if (pulled.status != BS_OK) {
        return pulled;
    }

    return EndRun(true);
}

Failure Job::SpeakEvent(EventKind kind) {
    const bs_source_v1* source = core_->source;
    int32_t rc = 0;

    {
        Stopwatch watch(counters_.source_us);
        rc = source->begin_event(source->self, static_cast<int32_t>(kind));
    }

    if (rc != BS_SOURCE_OK) {
        return SourceFailure(rc, "starting an event");
    }

    counters_.events.fetch_add(1, std::memory_order_relaxed);
    counters_.laugh_render.store(BS_EVENT_RENDER_NATIVE, std::memory_order_relaxed);
    fader_->Begin();

    Failure pulled = PullChunk();

    if (pulled.status != BS_OK) {
        return pulled;
    }

    return EndRun(true);
}

Failure Job::PullChunk() {
    const bs_source_v1* source = core_->source;
    const auto cap = static_cast<int32_t>(pull_buffer_.size());

    while (true) {
        if (cancelled_.load(std::memory_order_acquire)) {
            return Cancelled();
        }

        int32_t n = 0;
        int32_t done = 0;
        int32_t rc = 0;

        {
            Stopwatch watch(counters_.source_us);
            rc = source->pull(source->self, pull_buffer_.data(), cap, &n, &done);
        }

        if (rc != BS_SOURCE_OK) {
            return SourceFailure(rc, "generating audio");
        }

        if (n < 0 || n > cap) {
            return {BS_SOURCE, "the acoustic model returned " + std::to_string(n) + " samples for a " +
                                   std::to_string(cap) + "-sample buffer",
                    -1};
        }

        if (n > 0) {
            SetOnce(counters_.t_first_source, Elapsed());
            counters_.samples_source.fetch_add(n, std::memory_order_relaxed);

            Failure step = Process(pull_buffer_.data(), static_cast<size_t>(n), true);

            if (step.status != BS_OK) {
                return step;
            }
        }

        if (done != 0) {
            return {};
        }
    }
}

Failure Job::InsertClip() {
    counters_.events.fetch_add(1, std::memory_order_relaxed);
    counters_.laugh_render.store(BS_EVENT_RENDER_CLIP, std::memory_order_relaxed);
    counters_.samples_clip.fetch_add(static_cast<int64_t>(core_->laugh_clip.size()), std::memory_order_relaxed);

    fader_->Begin();

    const std::vector<float>& clip = core_->laugh_clip;
    const size_t block = pull_buffer_.size();

    for (size_t at = 0; at < clip.size(); at += block) {
        if (cancelled_.load(std::memory_order_acquire)) {
            return Cancelled();
        }

        Failure step = Process(clip.data() + at, std::min(block, clip.size() - at), false);

        if (step.status != BS_OK) {
            return step;
        }
    }

    return EndRun(false);
}

Failure Job::Process(const float* data, size_t n, bool voiced) {
    {
        Stopwatch watch(counters_.dsp_us);
        stage_a_.clear();

        if (voiced) {
            chain_->Process(data, n, stage_a_);
        } else {
            stage_a_.insert(stage_a_.end(), data, data + n);
        }

        limiter_->Process(stage_a_.data(), stage_a_.size());
        stage_b_.clear();
        fader_->Process(stage_a_.data(), stage_a_.size(), stage_b_);
    }

    return Emit(stage_b_.data(), stage_b_.size());
}

Failure Job::EndRun(bool voiced) {
    {
        Stopwatch watch(counters_.dsp_us);
        stage_a_.clear();

        if (voiced) {
            chain_->Flush(stage_a_);
        }

        limiter_->Process(stage_a_.data(), stage_a_.size());
        stage_b_.clear();
        fader_->Process(stage_a_.data(), stage_a_.size(), stage_b_);
        fader_->End(stage_b_);
    }

    counters_.limited.store(limiter_->Limited(), std::memory_order_relaxed);
    return Emit(stage_b_.data(), stage_b_.size());
}

Failure Job::Emit(const float* data, size_t n) {
    if (n == 0) {
        return {};
    }

    if (first_audible_index_.load(std::memory_order_relaxed) < 0) {
        const int64_t index = detector_->Feed(data, n);

        if (index >= 0) {
            first_audible_index_.store(index, std::memory_order_release);
            SetOnce(counters_.t_first_audible, Elapsed());
        }
    }

    if (exporting_) {
        Stopwatch watch(counters_.export_us);
        std::string error;

        if (!writer_.Write(data, n, &error)) {
            return {BS_IO, error, -1};
        }
    }

    if (ring_) {
        size_t offset = 0;

        while (offset < n) {
            if (cancelled_.load(std::memory_order_acquire)) {
                return Cancelled();
            }

            const size_t wrote = ring_->TryWrite(data + offset, n - offset);
            offset += wrote;

            const auto buffered = static_cast<int64_t>(ring_->Buffered());
            Max(counters_.peak_buffered, buffered);

            if (buffered >= static_cast<int64_t>(prebuffer_)) {
                SetOnce(counters_.t_prebuffered, Elapsed());
            }

            // Full: the reader is behind by a whole ring, which is the bound on
            // buffered audio doing its job. Wait for it rather than grow.
            if (wrote == 0) {
                std::this_thread::sleep_for(std::chrono::milliseconds(1));
            }
        }
    }

    counters_.samples_out.fetch_add(static_cast<int64_t>(n), std::memory_order_relaxed);
    return {};
}

void Job::Finish(const Failure& result) {
    Failure outcome = result;

    if (outcome.status == BS_OK && exporting_) {
        Stopwatch watch(counters_.export_us);
        std::string error;

        if (!writer_.Finish(&error)) {
            outcome = {BS_IO, error, -1};
        }
    }

    if (outcome.status != BS_OK) {
        writer_.Abort();
    }

    producer_done_.store(true, std::memory_order_release);

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
        core_->job_active = false;
    }

    {
        std::lock_guard lock(mutex_);
        outcome_ = std::move(outcome);
        finished_ = true;
    }

    done_cv_.notify_all();
}

int32_t Job::Read(float* out, int32_t cap, int32_t* out_real, int32_t* out_flags) noexcept {
    *out_real = 0;
    *out_flags = 0;

    if (!ring_) {
        return BS_STATE;
    }

    const auto want = static_cast<size_t>(cap);

    if (cancelled_.load(std::memory_order_acquire)) {
        ring_->DiscardAll();
        std::fill(out, out + want, 0.0f);
        *out_flags = BS_READ_CANCELLED | BS_READ_FINISHED;
        return BS_OK;
    }

    const bool done = producer_done_.load(std::memory_order_acquire);

    if (!playing_) {
        if (ring_->Buffered() >= prebuffer_ || done) {
            playing_ = true;
        } else {
            std::fill(out, out + want, 0.0f);
            *out_flags = BS_READ_PREBUFFERING;
            return BS_OK;
        }
    }

    const size_t got = ring_->Read(out, want);
    *out_real = static_cast<int32_t>(got);

    const int64_t audible = first_audible_index_.load(std::memory_order_acquire);

    if (audible >= 0 && read_total_ <= static_cast<uint64_t>(audible) &&
        read_total_ + got > static_cast<uint64_t>(audible)) {
        SetOnce(counters_.t_first_audible_read, Elapsed());
    }

    read_total_ += got;

    if (got < want) {
        std::fill(out + got, out + want, 0.0f);

        if (done && ring_->Buffered() == 0) {
            *out_flags |= BS_READ_FINISHED;
        } else {
            *out_flags |= BS_READ_UNDERRUN;
            counters_.underruns.fetch_add(1, std::memory_order_relaxed);
            counters_.underrun_samples.fetch_add(static_cast<int64_t>(want - got), std::memory_order_relaxed);
        }
    }

    return BS_OK;
}

void Job::Cancel() noexcept {
    if (cancelled_.exchange(true, std::memory_order_acq_rel)) {
        return;
    }

    cancel_requested_us_.store(Elapsed(), std::memory_order_release);

    std::lock_guard lock(mutex_);

    // Only while this job owns the source: a late cancel must not stop the next job.
    if (running_) {
        core_->source->cancel(core_->source->self);
    }
}

int32_t Job::Wait(int32_t timeout_ms, int32_t* out_status) {
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

void Job::Stats(bs_job_stats* out) const noexcept {
    const uint32_t size = out->struct_size;
    *out = bs_job_stats{};
    out->struct_size = size;

    {
        std::lock_guard lock(mutex_);
        out->finished = finished_ ? 1 : 0;
        out->status = finished_ ? outcome_.status : BS_OK;
    }

    const auto get = [](const std::atomic<int64_t>& v) { return v.load(std::memory_order_relaxed); };

    out->sample_rate = sample_rate_;
    out->chunks = counters_.chunks.load(std::memory_order_relaxed);
    out->events = counters_.events.load(std::memory_order_relaxed);
    out->laugh_render = counters_.laugh_render.load(std::memory_order_relaxed);
    out->samples_out = get(counters_.samples_out);
    out->samples_source = get(counters_.samples_source);
    out->samples_clip = get(counters_.samples_clip);
    out->limited_samples = get(counters_.limited);
    out->underruns = get(counters_.underruns);
    out->underrun_samples = get(counters_.underrun_samples);
    out->peak_buffered_samples = get(counters_.peak_buffered);
    out->t_plan_us = get(counters_.t_plan);
    out->t_first_source_pcm_us = get(counters_.t_first_source);
    out->t_first_audible_pcm_us = get(counters_.t_first_audible);
    out->t_prebuffered_us = get(counters_.t_prebuffered);
    out->t_first_audible_read_us = get(counters_.t_first_audible_read);
    out->t_finished_us = get(counters_.t_finished);
    out->source_us = get(counters_.source_us);
    out->dsp_us = get(counters_.dsp_us);
    out->export_us = get(counters_.export_us);
    out->cancel_latency_us = get(counters_.cancel_latency);
}

std::string Job::Error() const {
    std::lock_guard lock(mutex_);
    return outcome_.message;
}

}  // namespace bspeech
