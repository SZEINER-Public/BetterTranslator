#pragma once

#include <stddef.h>
#include <stdint.h>

#include "better_speech.h"
#include "better_speech_recognizer.h"

#ifdef __cplusplus
extern "C" {
#endif

#define BS_STT_ABI_VERSION 1

typedef enum bs_dictation_mode {
    BS_DICTATION_PROSE = 0,
    BS_DICTATION_CODE = 1,
} bs_dictation_mode;

typedef enum bs_substitution_kind {
    BS_SUBSTITUTION_CASING = 0,
    BS_SUBSTITUTION_LEXICON = 1,
    BS_SUBSTITUTION_GRAMMAR = 2,
    BS_SUBSTITUTION_LETTERS = 3,
} bs_substitution_kind;

typedef enum bs_rec_event_kind {
    BS_REC_EVENT_NONE = 0,
    BS_REC_EVENT_PARTIAL = 1,
    BS_REC_EVENT_FINAL = 2,
    BS_REC_EVENT_FINISHED = 3,
} bs_rec_event_kind;

typedef struct bs_lexicon bs_lexicon;
typedef struct bs_dictation bs_dictation;
typedef struct bs_rec_engine bs_rec_engine;
typedef struct bs_rec_session bs_rec_session;
typedef struct bs_capture bs_capture;

typedef struct bs_substitution {
    uint32_t struct_size;
    int32_t kind;
    int32_t applied;
    float confidence;
    size_t raw_offset;
    size_t raw_length;
    size_t text_offset;
    size_t text_length;
    const char* replacement;
    size_t replacement_len;
} bs_substitution;

typedef struct bs_vad_settings {
    uint32_t struct_size;
    float onset_db;
    float absolute_floor_dbfs;
    float min_speech_ms;
    float hangover_ms;
    float pre_roll_ms;
} bs_vad_settings;

typedef struct bs_rec_engine_config {
    uint32_t struct_size;
    const bs_recognizer_v1* recognizer;
    const bs_lexicon* lexicon;
} bs_rec_engine_config;

typedef struct bs_rec_capabilities {
    uint32_t struct_size;
    int32_t available;
    int32_t sample_rate;
    int32_t max_window_samples;
    int32_t streaming;
    int32_t word_timestamps;
    int32_t bias_context;
    int32_t bias_logit;
    int32_t bias_trie;
    int32_t language_count;
    int32_t lexicon_terms;
    int32_t capture;
} bs_rec_capabilities;

typedef struct bs_rec_request {
    uint32_t struct_size;
    const char* language;
    int32_t mode;
    int32_t n_threads;
    const char* file_path;
    int32_t input_sample_rate;
    int32_t input_ring_samples;
    int32_t window_ms;
    int32_t overlap_ms;
    int32_t partial_interval_ms;
    float min_confidence;
    int32_t context_chars;
    int32_t bias_terms;
    bs_vad_settings vad;
} bs_rec_request;

typedef struct bs_rec_event {
    uint32_t struct_size;
    int32_t kind;
    int32_t segment;
    int32_t segment_end;
    int32_t status;
    int32_t substitutions;
    int64_t start_sample;
    int64_t end_sample;
    float min_confidence;
    const char* text;
    size_t text_len;
    const char* unstable;
    size_t unstable_len;
    const char* raw;
    size_t raw_len;
} bs_rec_event;

typedef struct bs_rec_stats {
    uint32_t struct_size;
    int32_t finished;
    int32_t status;
    int32_t model_sample_rate;
    int32_t segments;
    int32_t finals;
    int32_t partials;
    int32_t windows;
    int64_t input_frames;
    int64_t overrun_frames;
    int64_t model_samples;
    int64_t peak_window_samples;
    int64_t t_first_voiced_us;
    int64_t t_first_partial_us;
    int64_t t_first_final_us;
    int64_t t_finished_us;
    int64_t speech_end_to_final_count;
    int64_t speech_end_to_final_median_us;
    int64_t speech_end_to_final_p90_us;
    int64_t onset_to_first_partial_us;
    int64_t feed_us;
    int64_t partial_us;
    int64_t finalize_us;
    int64_t cancel_latency_us;
} bs_rec_stats;

BS_API int32_t bs_stt_abi_version(void);

BS_API int32_t bs_lexicon_create(int32_t max_terms, bs_lexicon** out_lexicon);
BS_API int32_t bs_lexicon_add(bs_lexicon* lexicon, const char* term, size_t term_len, float weight);
BS_API int32_t bs_lexicon_harvest(bs_lexicon* lexicon, const char* text, size_t text_len);
BS_API int32_t bs_lexicon_freeze(bs_lexicon* lexicon);
BS_API int32_t bs_lexicon_count(const bs_lexicon* lexicon);
BS_API int32_t bs_lexicon_term(const bs_lexicon* lexicon, int32_t index, char* buf, int32_t cap, int32_t* out_len);
BS_API uint64_t bs_lexicon_version(const bs_lexicon* lexicon);
BS_API void bs_lexicon_free(bs_lexicon* lexicon);

BS_API int32_t bs_dictate(const char* text, size_t text_len, int32_t mode, const bs_lexicon* lexicon,
                          float min_confidence, bs_dictation** out_dictation);
BS_API int32_t bs_dictation_text(const bs_dictation* dictation, const char** out_text, size_t* out_len);
BS_API int32_t bs_dictation_raw(const bs_dictation* dictation, const char** out_text, size_t* out_len);
BS_API int32_t bs_dictation_substitution_count(const bs_dictation* dictation);
BS_API int32_t bs_dictation_substitution(const bs_dictation* dictation, int32_t index, bs_substitution* out);
BS_API void bs_dictation_free(bs_dictation* dictation);

BS_API int32_t bs_vad_defaults(bs_vad_settings* out_settings);
BS_API int32_t bs_vad_calibrate(const char* wav_path, bs_vad_settings* in_out_settings, float* out_noise_dbfs,
                                float* out_speech_dbfs);

BS_API int32_t bs_rec_engine_create(const bs_rec_engine_config* config, bs_rec_engine** out_engine);
BS_API int32_t bs_rec_engine_capabilities(const bs_rec_engine* engine, bs_rec_capabilities* out_caps);
BS_API int32_t bs_rec_engine_unavailable_reason(const bs_rec_engine* engine, char* buf, int32_t cap,
                                                int32_t* out_len);
BS_API int32_t bs_rec_engine_language(const bs_rec_engine* engine, int32_t index, char* buf, int32_t cap,
                                      int32_t* out_len);
BS_API void bs_rec_engine_free(bs_rec_engine* engine);

BS_API int32_t bs_rec_request_defaults(bs_rec_request* out_request);
BS_API int32_t bs_rec_session_start(bs_rec_engine* engine, const bs_rec_request* request,
                                    bs_rec_session** out_session);
BS_API int32_t bs_rec_session_push(bs_rec_session* session, const float* interleaved, int32_t frames,
                                   int32_t channels);
BS_API void bs_rec_session_end_input(bs_rec_session* session);
BS_API void bs_rec_session_cancel(bs_rec_session* session);
BS_API int32_t bs_rec_session_poll(bs_rec_session* session, bs_rec_event* out_event);
BS_API int32_t bs_rec_session_substitution(const bs_rec_session* session, int32_t index, bs_substitution* out);
BS_API int32_t bs_rec_session_wait(bs_rec_session* session, int32_t timeout_ms, int32_t* out_status);
BS_API int32_t bs_rec_session_stats(const bs_rec_session* session, bs_rec_stats* out_stats);
BS_API int32_t bs_rec_session_error(const bs_rec_session* session, char* buf, int32_t cap, int32_t* out_len);
BS_API void bs_rec_session_free(bs_rec_session* session);

BS_API int32_t bs_capture_device_count(int32_t* out_count);
BS_API int32_t bs_capture_device(int32_t index, char* id, int32_t id_cap, int32_t* id_len, char* name,
                                 int32_t name_cap, int32_t* name_len);
BS_API int32_t bs_capture_open(const char* device_id, bs_capture** out_capture);
BS_API int32_t bs_capture_format(const bs_capture* capture, int32_t* out_sample_rate, int32_t* out_channels);
BS_API int32_t bs_capture_start(bs_capture* capture, bs_rec_session* session);
BS_API int32_t bs_capture_stats(const bs_capture* capture, int64_t* out_frames, int64_t* out_glitches);
BS_API void bs_capture_close(bs_capture* capture);

#ifdef __cplusplus
}
#endif
