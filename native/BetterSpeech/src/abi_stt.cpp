#include <memory>
#include <string>
#include <vector>

#include "abi_common.h"
#include "better_speech_stt.h"
#include "capture.h"
#include "lexicon.h"
#include "recognize.h"
#include "utf8.h"
#include "vad.h"
#include "wav.h"

struct bs_lexicon {
    explicit bs_lexicon(size_t max_terms) : lexicon(max_terms) {}
    bspeech::Lexicon lexicon;
};

struct bs_dictation {
    bspeech::Dictation dictation;
};

struct bs_rec_engine {
    std::shared_ptr<bspeech::RecEngineCore> core;
};

struct bs_rec_session {
    std::unique_ptr<bspeech::RecognitionSession> session;
};

struct bs_capture {
    std::unique_ptr<bspeech::CaptureDevice> device;
};

using bspeech::abi::CopyOut;
using bspeech::abi::Fail;
using bspeech::abi::Guard;

static_assert(sizeof(bs_vad_settings) == 24);
static_assert(sizeof(bs_rec_capabilities) == 48);
static_assert(sizeof(bs_rec_stats) == 160);
static_assert(sizeof(bs_rec_caps) == 24);
static_assert(sizeof(bs_rec_word) == 32);
static_assert(sizeof(void*) != 8 || sizeof(bs_substitution) == 64);
static_assert(sizeof(void*) != 8 || sizeof(bs_rec_engine_config) == 24);
static_assert(sizeof(void*) != 8 || sizeof(bs_rec_request) == 88);
static_assert(sizeof(void*) != 8 || sizeof(bs_rec_event) == 96);
static_assert(sizeof(void*) != 8 || sizeof(bs_rec_window_options) == 40);
static_assert(sizeof(void*) != 8 || sizeof(bs_recognizer_v1) == 88);

namespace {

constexpr int32_t kMaxLexiconTerms = 65536;
constexpr int64_t kMaxCalibrationSeconds = 600;

void FillSubstitution(const bspeech::Substitution& s, bs_substitution* out) {
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
}

}

extern "C" {

BS_API int32_t bs_stt_abi_version(void) { return BS_STT_ABI_VERSION; }

BS_API int32_t bs_lexicon_create(int32_t max_terms, bs_lexicon** out_lexicon) {
    return Guard([&]() -> int32_t {
        if (out_lexicon == nullptr) {
            return Fail(BS_INVALID_ARG, "out_lexicon is null");
        }

        *out_lexicon = nullptr;

        if (max_terms < 1 || max_terms > kMaxLexiconTerms) {
            return Fail(BS_INVALID_ARG, "max_terms must be between 1 and " + std::to_string(kMaxLexiconTerms));
        }

        *out_lexicon = new bs_lexicon(static_cast<size_t>(max_terms));
        return BS_OK;
    });
}

BS_API int32_t bs_lexicon_add(bs_lexicon* lexicon, const char* term, size_t term_len, float weight) {
    return Guard([&]() -> int32_t {
        if (lexicon == nullptr || term == nullptr) {
            return Fail(BS_INVALID_ARG, "lexicon or term is null");
        }

        if (lexicon->lexicon.Frozen()) {
            return Fail(BS_STATE, "the lexicon is frozen; terms are added before bs_lexicon_freeze");
        }

        if (!lexicon->lexicon.Add(std::string_view(term, term_len), static_cast<double>(weight))) {
            return Fail(BS_INVALID_ARG, "the term is empty, longer than 128 bytes, or not UTF-8");
        }

        return BS_OK;
    });
}

BS_API int32_t bs_lexicon_harvest(bs_lexicon* lexicon, const char* text, size_t text_len) {
    return Guard([&]() -> int32_t {
        if (lexicon == nullptr || (text == nullptr && text_len > 0)) {
            return Fail(BS_INVALID_ARG, "lexicon or text is null");
        }

        if (lexicon->lexicon.Frozen()) {
            return Fail(BS_STATE, "the lexicon is frozen; text is harvested before bs_lexicon_freeze");
        }

        lexicon->lexicon.Harvest(std::string_view(text != nullptr ? text : "", text_len));
        return BS_OK;
    });
}

BS_API int32_t bs_lexicon_freeze(bs_lexicon* lexicon) {
    return Guard([&]() -> int32_t {
        if (lexicon == nullptr) {
            return Fail(BS_INVALID_ARG, "lexicon is null");
        }

        lexicon->lexicon.Freeze();
        return BS_OK;
    });
}

BS_API int32_t bs_lexicon_count(const bs_lexicon* lexicon) {
    return lexicon != nullptr && lexicon->lexicon.Frozen() ? static_cast<int32_t>(lexicon->lexicon.Terms().size())
                                                           : 0;
}

BS_API int32_t bs_lexicon_term(const bs_lexicon* lexicon, int32_t index, char* buf, int32_t cap, int32_t* out_len) {
    return Guard([&]() -> int32_t {
        if (lexicon == nullptr || !lexicon->lexicon.Frozen()) {
            return Fail(BS_STATE, "the lexicon is null or not frozen");
        }

        const auto& terms = lexicon->lexicon.Terms();

        if (index < 0 || static_cast<size_t>(index) >= terms.size()) {
            return Fail(BS_INVALID_ARG, "index " + std::to_string(index) + " is past the end of the lexicon");
        }

        return CopyOut(terms[static_cast<size_t>(index)].written, buf, cap, out_len);
    });
}

BS_API uint64_t bs_lexicon_version(const bs_lexicon* lexicon) {
    return lexicon != nullptr ? lexicon->lexicon.Version() : 0;
}

BS_API void bs_lexicon_free(bs_lexicon* lexicon) { delete lexicon; }

BS_API int32_t bs_dictate(const char* text, size_t text_len, int32_t mode, const bs_lexicon* lexicon,
                          float min_confidence, bs_dictation** out_dictation) {
    return Guard([&]() -> int32_t {
        if (out_dictation == nullptr || (text == nullptr && text_len > 0)) {
            return Fail(BS_INVALID_ARG, "out_dictation or text is null");
        }

        *out_dictation = nullptr;

        if (mode != BS_DICTATION_PROSE && mode != BS_DICTATION_CODE) {
            return Fail(BS_INVALID_ARG, "mode is not a bs_dictation_mode value");
        }

        if (lexicon != nullptr && !lexicon->lexicon.Frozen()) {
            return Fail(BS_STATE, "the lexicon must be frozen before it is used");
        }

        if (!(min_confidence >= 0.0f && min_confidence <= 1.0f)) {
            return Fail(BS_INVALID_ARG, "min_confidence must be in [0, 1]");
        }

        const std::string_view view(text != nullptr ? text : "", text_len);
        size_t bad = 0;

        if (!bspeech::utf8::Valid(view, &bad)) {
            return Fail(BS_INVALID_ARG, "the text is not valid UTF-8 at byte " + std::to_string(bad),
                        static_cast<int64_t>(bad));
        }

        auto result = std::make_unique<bs_dictation>();
        result->dictation = bspeech::Dictate(bspeech::WordsOf(view), static_cast<bspeech::DictationMode>(mode),
                                             lexicon != nullptr ? &lexicon->lexicon : nullptr, min_confidence);
        *out_dictation = result.release();
        return BS_OK;
    });
}

BS_API int32_t bs_dictation_text(const bs_dictation* dictation, const char** out_text, size_t* out_len) {
    if (dictation == nullptr || out_text == nullptr || out_len == nullptr) {
        return Fail(BS_INVALID_ARG, "dictation or an out pointer is null");
    }

    *out_text = dictation->dictation.text.c_str();
    *out_len = dictation->dictation.text.size();
    return BS_OK;
}

BS_API int32_t bs_dictation_raw(const bs_dictation* dictation, const char** out_text, size_t* out_len) {
    if (dictation == nullptr || out_text == nullptr || out_len == nullptr) {
        return Fail(BS_INVALID_ARG, "dictation or an out pointer is null");
    }

    *out_text = dictation->dictation.raw.c_str();
    *out_len = dictation->dictation.raw.size();
    return BS_OK;
}

BS_API int32_t bs_dictation_substitution_count(const bs_dictation* dictation) {
    return dictation != nullptr ? static_cast<int32_t>(dictation->dictation.substitutions.size()) : 0;
}

BS_API int32_t bs_dictation_substitution(const bs_dictation* dictation, int32_t index, bs_substitution* out) {
    if (dictation == nullptr || out == nullptr || out->struct_size < sizeof(bs_substitution)) {
        return Fail(BS_INVALID_ARG, "dictation or out is null, or out.struct_size is too small");
    }

    const auto& subs = dictation->dictation.substitutions;

    if (index < 0 || static_cast<size_t>(index) >= subs.size()) {
        return Fail(BS_INVALID_ARG, "index " + std::to_string(index) + " is past the last substitution");
    }

    FillSubstitution(subs[static_cast<size_t>(index)], out);
    return BS_OK;
}

BS_API void bs_dictation_free(bs_dictation* dictation) { delete dictation; }

BS_API int32_t bs_vad_defaults(bs_vad_settings* out_settings) {
    if (out_settings == nullptr || out_settings->struct_size < sizeof(bs_vad_settings)) {
        return Fail(BS_INVALID_ARG, "out_settings is null or its struct_size is too small");
    }

    bspeech::VadTo(bspeech::VadSettings{}, out_settings);
    return BS_OK;
}

BS_API int32_t bs_vad_calibrate(const char* wav_path, bs_vad_settings* in_out_settings, float* out_noise_dbfs,
                                float* out_speech_dbfs) {
    return Guard([&]() -> int32_t {
        if (wav_path == nullptr || in_out_settings == nullptr || out_noise_dbfs == nullptr ||
            out_speech_dbfs == nullptr || in_out_settings->struct_size < sizeof(bs_vad_settings)) {
            return Fail(BS_INVALID_ARG, "a pointer is null or the settings struct_size is too small");
        }

        bspeech::WavInfo info;
        std::string error;

        if (!bspeech::ProbeWav(wav_path, &info, &error)) {
            return Fail(BS_ASSET, std::string(wav_path) + ": " + error);
        }

        std::vector<float> samples;

        if (!bspeech::ReadWavMono(wav_path, kMaxCalibrationSeconds * info.sample_rate, &samples, &info, &error)) {
            return Fail(BS_ASSET, std::string(wav_path) + ": " + error);
        }

        bspeech::VadCalibration calibration;

        if (!bspeech::CalibrateVad(samples, info.sample_rate, bspeech::VadFrom(*in_out_settings), &calibration)) {
            return Fail(BS_ASSET, std::string(wav_path) +
                                      " is shorter than half a second, or its speech is not 6 dB above its silence");
        }

        bspeech::VadTo(calibration.settings, in_out_settings);
        *out_noise_dbfs = calibration.noise_dbfs;
        *out_speech_dbfs = calibration.speech_dbfs;
        return BS_OK;
    });
}

BS_API int32_t bs_rec_engine_create(const bs_rec_engine_config* config, bs_rec_engine** out_engine) {
    return Guard([&]() -> int32_t {
        if (config == nullptr || out_engine == nullptr) {
            return Fail(BS_INVALID_ARG, "config or out_engine is null");
        }

        *out_engine = nullptr;

        if (config->struct_size < sizeof(bs_rec_engine_config)) {
            return Fail(BS_INVALID_ARG, "bs_rec_engine_config.struct_size is too small");
        }

        std::shared_ptr<const bspeech::Lexicon> lexicon;

        if (config->lexicon != nullptr) {
            if (!config->lexicon->lexicon.Frozen()) {
                return Fail(BS_STATE, "the lexicon must be frozen before an engine uses it");
            }

            lexicon = std::make_shared<const bspeech::Lexicon>(config->lexicon->lexicon);
        }

        std::shared_ptr<bspeech::RecEngineCore> core;
        const bspeech::Failure failure = bspeech::CreateRecEngineCore(*config, std::move(lexicon), &core);

        if (failure.status != BS_OK) {
            return Fail(failure);
        }

        *out_engine = new bs_rec_engine{std::move(core)};
        return BS_OK;
    });
}

BS_API int32_t bs_rec_engine_capabilities(const bs_rec_engine* engine, bs_rec_capabilities* out_caps) {
    if (engine == nullptr || out_caps == nullptr || out_caps->struct_size < sizeof(bs_rec_capabilities)) {
        return Fail(BS_INVALID_ARG, "engine or out_caps is null, or out_caps.struct_size is too small");
    }

    bspeech::FillRecCapabilities(*engine->core, out_caps);
    return BS_OK;
}

BS_API int32_t bs_rec_engine_unavailable_reason(const bs_rec_engine* engine, char* buf, int32_t cap,
                                                int32_t* out_len) {
    return Guard([&]() -> int32_t {
        if (engine == nullptr) {
            return Fail(BS_INVALID_ARG, "engine is null");
        }

        return CopyOut(engine->core->unavailable, buf, cap, out_len);
    });
}

BS_API int32_t bs_rec_engine_language(const bs_rec_engine* engine, int32_t index, char* buf, int32_t cap,
                                      int32_t* out_len) {
    return Guard([&]() -> int32_t {
        if (engine == nullptr) {
            return Fail(BS_INVALID_ARG, "engine is null");
        }

        const auto& languages = engine->core->languages;

        if (index < 0 || static_cast<size_t>(index) >= languages.size()) {
            return Fail(BS_INVALID_ARG, "index " + std::to_string(index) + " is past the last language");
        }

        return CopyOut(languages[static_cast<size_t>(index)], buf, cap, out_len);
    });
}

BS_API void bs_rec_engine_free(bs_rec_engine* engine) { delete engine; }

BS_API int32_t bs_rec_request_defaults(bs_rec_request* out_request) {
    if (out_request == nullptr || out_request->struct_size < sizeof(bs_rec_request)) {
        return Fail(BS_INVALID_ARG, "out_request is null or its struct_size is too small");
    }

    bspeech::RecRequestDefaults(out_request);
    return BS_OK;
}

BS_API int32_t bs_rec_session_start(bs_rec_engine* engine, const bs_rec_request* request,
                                    bs_rec_session** out_session) {
    return Guard([&]() -> int32_t {
        if (engine == nullptr || request == nullptr || out_session == nullptr) {
            return Fail(BS_INVALID_ARG, "engine, request or out_session is null");
        }

        *out_session = nullptr;
        std::unique_ptr<bspeech::RecognitionSession> session;
        const bspeech::Failure failure = bspeech::RecognitionSession::Start(engine->core, *request, &session);

        if (failure.status != BS_OK) {
            return Fail(failure);
        }

        *out_session = new bs_rec_session{std::move(session)};
        return BS_OK;
    });
}

BS_API int32_t bs_rec_session_push(bs_rec_session* session, const float* interleaved, int32_t frames,
                                   int32_t channels) {
    if (session == nullptr) {
        return BS_INVALID_ARG;
    }

    return session->session->Push(interleaved, frames, channels);
}

BS_API void bs_rec_session_end_input(bs_rec_session* session) {
    if (session != nullptr) {
        session->session->EndInput();
    }
}

BS_API void bs_rec_session_cancel(bs_rec_session* session) {
    if (session != nullptr) {
        session->session->Cancel();
    }
}

BS_API int32_t bs_rec_session_poll(bs_rec_session* session, bs_rec_event* out_event) {
    return Guard([&]() -> int32_t {
        if (session == nullptr || out_event == nullptr || out_event->struct_size < sizeof(bs_rec_event)) {
            return Fail(BS_INVALID_ARG, "session or out_event is null, or out_event.struct_size is too small");
        }

        return session->session->Poll(out_event);
    });
}

BS_API int32_t bs_rec_session_substitution(const bs_rec_session* session, int32_t index, bs_substitution* out) {
    if (session == nullptr || out == nullptr || out->struct_size < sizeof(bs_substitution)) {
        return Fail(BS_INVALID_ARG, "session or out is null, or out.struct_size is too small");
    }

    const int32_t rc = session->session->SubstitutionAt(index, out);
    return rc == BS_OK ? BS_OK : Fail(rc, "index " + std::to_string(index) + " is past the last substitution");
}

BS_API int32_t bs_rec_session_wait(bs_rec_session* session, int32_t timeout_ms, int32_t* out_status) {
    return Guard([&]() -> int32_t {
        if (session == nullptr || out_status == nullptr) {
            return Fail(BS_INVALID_ARG, "session or out_status is null");
        }

        const int32_t rc = session->session->Wait(timeout_ms, out_status);
        return rc == BS_TIMEOUT ? Fail(BS_TIMEOUT, "the session is still running") : rc;
    });
}

BS_API int32_t bs_rec_session_stats(const bs_rec_session* session, bs_rec_stats* out_stats) {
    return Guard([&]() -> int32_t {
        if (session == nullptr || out_stats == nullptr || out_stats->struct_size < sizeof(bs_rec_stats)) {
            return Fail(BS_INVALID_ARG, "session or out_stats is null, or out_stats.struct_size is too small");
        }

        session->session->Stats(out_stats);
        return BS_OK;
    });
}

BS_API int32_t bs_rec_session_error(const bs_rec_session* session, char* buf, int32_t cap, int32_t* out_len) {
    return Guard([&]() -> int32_t {
        if (session == nullptr) {
            return Fail(BS_INVALID_ARG, "session is null");
        }

        return CopyOut(session->session->Error(), buf, cap, out_len);
    });
}

BS_API void bs_rec_session_free(bs_rec_session* session) { delete session; }

BS_API int32_t bs_capture_device_count(int32_t* out_count) {
    return Guard([&]() -> int32_t {
        if (out_count == nullptr) {
            return Fail(BS_INVALID_ARG, "out_count is null");
        }

        const bspeech::Failure failure = bspeech::CaptureDeviceCount(out_count);
        return failure.status == BS_OK ? BS_OK : Fail(failure);
    });
}

BS_API int32_t bs_capture_device(int32_t index, char* id, int32_t id_cap, int32_t* id_len, char* name,
                                 int32_t name_cap, int32_t* name_len) {
    return Guard([&]() -> int32_t {
        std::string device_id;
        std::string device_name;
        const bspeech::Failure failure = bspeech::CaptureDeviceAt(index, &device_id, &device_name);

        if (failure.status != BS_OK) {
            return Fail(failure);
        }

        const int32_t rc = CopyOut(device_id, id, id_cap, id_len);
        const int32_t rn = CopyOut(device_name, name, name_cap, name_len);
        return rc != BS_OK ? rc : rn;
    });
}

BS_API int32_t bs_capture_open(const char* device_id, bs_capture** out_capture) {
    return Guard([&]() -> int32_t {
        if (out_capture == nullptr) {
            return Fail(BS_INVALID_ARG, "out_capture is null");
        }

        *out_capture = nullptr;
        std::unique_ptr<bspeech::CaptureDevice> device;
        const bspeech::Failure failure =
            bspeech::OpenCaptureDevice(device_id != nullptr ? device_id : "", &device);

        if (failure.status != BS_OK) {
            return Fail(failure);
        }

        *out_capture = new bs_capture{std::move(device)};
        return BS_OK;
    });
}

BS_API int32_t bs_capture_format(const bs_capture* capture, int32_t* out_sample_rate, int32_t* out_channels) {
    if (capture == nullptr || out_sample_rate == nullptr || out_channels == nullptr) {
        return Fail(BS_INVALID_ARG, "capture or an out pointer is null");
    }

    *out_sample_rate = capture->device->SampleRate();
    *out_channels = capture->device->Channels();
    return BS_OK;
}

BS_API int32_t bs_capture_start(bs_capture* capture, bs_rec_session* session) {
    return Guard([&]() -> int32_t {
        if (capture == nullptr || session == nullptr) {
            return Fail(BS_INVALID_ARG, "capture or session is null");
        }

        const bspeech::Failure failure = capture->device->Start(session->session.get());
        return failure.status == BS_OK ? BS_OK : Fail(failure);
    });
}

BS_API int32_t bs_capture_stats(const bs_capture* capture, int64_t* out_frames, int64_t* out_glitches) {
    if (capture == nullptr || out_frames == nullptr || out_glitches == nullptr) {
        return Fail(BS_INVALID_ARG, "capture or an out pointer is null");
    }

    *out_frames = capture->device->Frames();
    *out_glitches = capture->device->Glitches();
    return BS_OK;
}

BS_API void bs_capture_close(bs_capture* capture) {
    if (capture != nullptr) {
        capture->device->Stop();
        delete capture;
    }
}

}
