#include <cstring>
#include <exception>
#include <memory>
#include <new>
#include <string>
#include <vector>

#include "abi_common.h"
#include "better_speech.h"
#include "engine.h"
#include "text_plan.h"
#include "wav.h"

// The layouts the managed binding mirrors (BetterTranslator.Runtime/Speech). A
// change here is an ABI change: bump BS_ABI_VERSION and the binding together.
static_assert(sizeof(void*) != 8 || sizeof(bs_plan_options) == 32);
static_assert(sizeof(void*) != 8 || sizeof(bs_plan_item) == 40);
static_assert(sizeof(void*) != 8 || sizeof(bs_engine_config) == 24);
static_assert(sizeof(bs_capabilities) == 60);
static_assert(sizeof(void*) != 8 || sizeof(bs_voice) == 24);
static_assert(sizeof(void*) != 8 || sizeof(bs_request) == 96);
static_assert(sizeof(bs_job_stats) == 168);
static_assert(sizeof(bs_wav_info) == 32);

struct bs_plan {
    std::vector<bspeech::PlanItem> items;
};

struct bs_engine {
    std::shared_ptr<bspeech::EngineCore> core;
};

struct bs_job {
    std::unique_ptr<bspeech::Job> job;
};

using bspeech::abi::CopyOut;
using bspeech::abi::Fail;
using bspeech::abi::Guard;

extern "C" {

BS_API int32_t bs_abi_version(void) { return BS_ABI_VERSION; }

BS_API const char* bs_version(void) { return "BetterSpeech 0.1.0 (ABI 1, source ABI 1)"; }

BS_API const char* bs_last_error(void) { return bspeech::abi::last_error.c_str(); }

BS_API int64_t bs_last_error_offset(void) { return bspeech::abi::last_error_offset; }

BS_API int32_t bs_plan_create(const char* text, size_t text_len, const bs_plan_options* options,
                              bs_plan** out_plan) {
    return Guard([&]() -> int32_t {
        if (out_plan == nullptr) {
            return Fail(BS_INVALID_ARG, "out_plan is null");
        }

        *out_plan = nullptr;

        if (text == nullptr && text_len > 0) {
            return Fail(BS_INVALID_ARG, "the text pointer is null");
        }

        bspeech::PlanSettings settings;

        if (options != nullptr) {
            if (options->struct_size < sizeof(bs_plan_options)) {
                return Fail(BS_INVALID_ARG, "bs_plan_options.struct_size is too small");
            }

            if (options->max_units < 0 || options->first_chunk_units < 0) {
                return Fail(BS_INVALID_ARG, "chunk budgets cannot be negative");
            }

            if (options->unknown_tag_policy != BS_UNKNOWN_TAG_ERROR &&
                options->unknown_tag_policy != BS_UNKNOWN_TAG_LITERAL) {
                return Fail(BS_INVALID_ARG, "unknown_tag_policy is not a bs_unknown_tag_policy value");
            }

            settings.language = options->language != nullptr ? options->language : "";
            settings.unknown_tags = static_cast<bspeech::UnknownTags>(options->unknown_tag_policy);
            settings.max_units = options->max_units;
            settings.first_chunk_units = options->first_chunk_units;
        }

        auto plan = std::make_unique<bs_plan>();
        bspeech::PlanError error;

        if (!bspeech::BuildPlan(std::string_view(text != nullptr ? text : "", text_len), settings, &plan->items,
                                &error)) {
            return Fail(BS_INVALID_ARG, error.message, error.offset);
        }

        *out_plan = plan.release();
        return BS_OK;
    });
}

BS_API size_t bs_plan_count(const bs_plan* plan) { return plan != nullptr ? plan->items.size() : 0; }

BS_API int32_t bs_plan_item_get(const bs_plan* plan, size_t index, bs_plan_item* out_item) {
    if (plan == nullptr || out_item == nullptr) {
        return Fail(BS_INVALID_ARG, "plan or out_item is null");
    }

    if (index >= plan->items.size()) {
        return Fail(BS_INVALID_ARG, "index " + std::to_string(index) + " is past the end of the plan");
    }

    const bspeech::PlanItem& item = plan->items[index];
    out_item->kind = static_cast<int32_t>(item.kind);
    out_item->event = static_cast<int32_t>(item.event);
    out_item->text = item.text.c_str();
    out_item->text_len = item.text.size();
    out_item->source_offset = item.source_offset;
    out_item->source_length = item.source_length;
    return BS_OK;
}

BS_API void bs_plan_free(bs_plan* plan) { delete plan; }

BS_API int32_t bs_engine_create(const bs_engine_config* config, bs_engine** out_engine) {
    return Guard([&]() -> int32_t {
        if (config == nullptr || out_engine == nullptr) {
            return Fail(BS_INVALID_ARG, "config or out_engine is null");
        }

        *out_engine = nullptr;

        if (config->struct_size < sizeof(bs_engine_config)) {
            return Fail(BS_INVALID_ARG, "bs_engine_config.struct_size is too small");
        }

        std::shared_ptr<bspeech::EngineCore> core;
        const bspeech::Failure failure = bspeech::CreateEngineCore(*config, &core);

        if (failure.status != BS_OK) {
            return Fail(failure);
        }

        *out_engine = new bs_engine{std::move(core)};
        return BS_OK;
    });
}

BS_API int32_t bs_engine_capabilities(const bs_engine* engine, bs_capabilities* out_caps) {
    if (engine == nullptr || out_caps == nullptr) {
        return Fail(BS_INVALID_ARG, "engine or out_caps is null");
    }

    if (out_caps->struct_size < sizeof(bs_capabilities)) {
        return Fail(BS_INVALID_ARG, "bs_capabilities.struct_size is too small");
    }

    bspeech::FillCapabilities(*engine->core, out_caps);
    return BS_OK;
}

BS_API int32_t bs_engine_unavailable_reason(const bs_engine* engine, char* buf, int32_t cap, int32_t* out_len) {
    return Guard([&]() -> int32_t {
        if (engine == nullptr) {
            return Fail(BS_INVALID_ARG, "engine is null");
        }

        return CopyOut(engine->core->unavailable, buf, cap, out_len);
    });
}

BS_API void bs_engine_free(bs_engine* engine) { delete engine; }

BS_API int32_t bs_job_start(bs_engine* engine, const bs_request* request, bs_job** out_job) {
    return Guard([&]() -> int32_t {
        if (engine == nullptr || request == nullptr || out_job == nullptr) {
            return Fail(BS_INVALID_ARG, "engine, request or out_job is null");
        }

        *out_job = nullptr;

        std::unique_ptr<bspeech::Job> job;
        const bspeech::Failure failure = bspeech::Job::Start(engine->core, *request, &job);

        if (failure.status != BS_OK) {
            return Fail(failure);
        }

        *out_job = new bs_job{std::move(job)};
        return BS_OK;
    });
}

BS_API int32_t bs_job_read(bs_job* job, float* out, int32_t cap, int32_t* out_real, int32_t* out_flags) {
    if (job == nullptr || out == nullptr || out_real == nullptr || out_flags == nullptr || cap < 0) {
        return BS_INVALID_ARG;
    }

    return job->job->Read(out, cap, out_real, out_flags);
}

BS_API void bs_job_cancel(bs_job* job) {
    if (job != nullptr) {
        job->job->Cancel();
    }
}

BS_API int32_t bs_job_wait(bs_job* job, int32_t timeout_ms, int32_t* out_status) {
    return Guard([&]() -> int32_t {
        if (job == nullptr || out_status == nullptr) {
            return Fail(BS_INVALID_ARG, "job or out_status is null");
        }

        const int32_t rc = job->job->Wait(timeout_ms, out_status);

        if (rc == BS_TIMEOUT) {
            return Fail(BS_TIMEOUT, "the job is still running");
        }

        return rc;
    });
}

BS_API int32_t bs_job_stats_get(const bs_job* job, bs_job_stats* out_stats) {
    if (job == nullptr || out_stats == nullptr) {
        return Fail(BS_INVALID_ARG, "job or out_stats is null");
    }

    if (out_stats->struct_size < sizeof(bs_job_stats)) {
        return Fail(BS_INVALID_ARG, "bs_job_stats.struct_size is too small");
    }

    job->job->Stats(out_stats);
    return BS_OK;
}

BS_API int32_t bs_job_error(const bs_job* job, char* buf, int32_t cap, int32_t* out_len) {
    return Guard([&]() -> int32_t {
        if (job == nullptr) {
            return Fail(BS_INVALID_ARG, "job is null");
        }

        return CopyOut(job->job->Error(), buf, cap, out_len);
    });
}

BS_API void bs_job_free(bs_job* job) { delete job; }

BS_API int32_t bs_wav_probe(const char* path, bs_wav_info* out_info) {
    return Guard([&]() -> int32_t {
        if (path == nullptr || out_info == nullptr) {
            return Fail(BS_INVALID_ARG, "path or out_info is null");
        }

        if (out_info->struct_size < sizeof(bs_wav_info)) {
            return Fail(BS_INVALID_ARG, "bs_wav_info.struct_size is too small");
        }

        bspeech::WavInfo info;
        std::string error;

        if (!bspeech::ProbeWav(path, &info, &error)) {
            return Fail(BS_ASSET, std::string(path) + ": " + error);
        }

        const uint32_t size = out_info->struct_size;
        *out_info = bs_wav_info{};
        out_info->struct_size = size;
        out_info->sample_rate = info.sample_rate;
        out_info->channels = info.channels;
        out_info->bits_per_sample = info.bits_per_sample;
        out_info->is_float = info.is_float ? 1 : 0;
        out_info->frames = info.frames;
        return BS_OK;
    });
}

}  // extern "C"
