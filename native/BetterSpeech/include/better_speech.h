/*
 * BetterSpeech: the speech engine's flat C ABI.
 *
 * Conventions follow BetterRuntime: a negative int status on failure with text
 * from bs_last_error, opaque handles freed by their own *_free, caller-provided
 * buffers that report BS_BUFFER_TOO_SMALL with the needed length, and structs
 * passed by pointer only. Structs that may grow carry struct_size first; set it
 * to sizeof the struct you compiled against.
 *
 * Ownership in one place:
 *   - bs_engine owns the decoded laughter clip and borrows the acoustic source.
 *   - bs_job owns its playback ring and its export file until it finishes.
 *   - bs_plan owns the item text; bs_plan_item.text is borrowed from it.
 *   - bs_last_error and bs_version return borrowed strings, never freed.
 *   - Every string and buffer passed in is copied or consumed before the call
 *     returns; the caller may free it straight after.
 */
#pragma once

#include <stddef.h>
#include <stdint.h>

#include "better_speech_source.h"

#if defined(_WIN32)
#if defined(BS_BUILD)
#define BS_API __declspec(dllexport)
#else
#define BS_API __declspec(dllimport)
#endif
#else
#define BS_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define BS_ABI_VERSION 1

typedef enum bs_status {
    BS_OK = 0,
    BS_INVALID_ARG = -1,
    BS_UNAVAILABLE = -2,
    BS_ASSET = -3,
    BS_IO = -4,
    BS_CANCELLED = -5,
    BS_BUFFER_TOO_SMALL = -6,
    BS_SOURCE = -7,
    BS_TIMEOUT = -8,
    BS_STATE = -9,
    BS_OUT_OF_MEMORY = -10,
    BS_INTERNAL = -99,
} bs_status;

typedef enum bs_unknown_tag_policy {
    /* An unrecognised [Tag] fails the plan with its byte offset. The default:
     * guessing that "[sic]" is text and "[Lauhg]" is a typo is how a control word
     * ends up read aloud. */
    BS_UNKNOWN_TAG_ERROR = 0,
    /* An unrecognised [Tag] is kept as text, brackets included. */
    BS_UNKNOWN_TAG_LITERAL = 1,
} bs_unknown_tag_policy;

typedef enum bs_item_kind {
    BS_ITEM_SPEECH = 0,
    BS_ITEM_EVENT = 1,
} bs_item_kind;

typedef enum bs_event_kind {
    BS_EVENT_NONE = 0,
    BS_EVENT_LAUGH = 1,
} bs_event_kind;

/* How a control is provided, so a surface never offers a setting that does nothing. */
typedef enum bs_control {
    BS_CONTROL_UNAVAILABLE = 0,
    BS_CONTROL_MODEL = 1,  /* the acoustic model implements it */
    BS_CONTROL_ENGINE = 2, /* BetterSpeech's own signal processing implements it */
} bs_control;

typedef enum bs_event_render {
    BS_EVENT_RENDER_UNAVAILABLE = 0,
    BS_EVENT_RENDER_NATIVE = 1, /* the model produces it */
    BS_EVENT_RENDER_CLIP = 2,   /* an approved local recording is inserted; not the model's voice */
} bs_event_render;

/* Flags returned by bs_job_read. */
#define BS_READ_PREBUFFERING (1 << 0) /* playback has not started; output is silence */
#define BS_READ_UNDERRUN (1 << 1)     /* playback started and the generator fell behind */
#define BS_READ_FINISHED (1 << 2)     /* every generated sample has been read */
#define BS_READ_CANCELLED (1 << 3)    /* the job was cancelled; buffered audio was discarded */

typedef struct bs_plan bs_plan;
typedef struct bs_engine bs_engine;
typedef struct bs_job bs_job;

typedef struct bs_plan_options {
    uint32_t struct_size;
    const char* language;       /* BCP-47; chooses the abbreviation set */
    int32_t unknown_tag_policy; /* bs_unknown_tag_policy */
    int32_t max_units;          /* chunk budget in codepoints for a standalone plan; 0 = one sentence per chunk */
    int32_t first_chunk_units;  /* budget of the first chunk only; 0 = max_units */
} bs_plan_options;

typedef struct bs_plan_item {
    int32_t kind;          /* bs_item_kind */
    int32_t event;         /* bs_event_kind when kind is BS_ITEM_EVENT */
    const char* text;      /* NUL-terminated UTF-8, borrowed from the plan; empty for events */
    size_t text_len;       /* bytes, without the terminator */
    size_t source_offset;  /* byte offset of the item in the original input */
    size_t source_length;  /* bytes the item covers in the original input */
} bs_plan_item;

typedef struct bs_engine_config {
    uint32_t struct_size;
    const bs_source_v1* source;  /* may be NULL: plans and validation work, jobs report BS_UNAVAILABLE */
    const char* laugh_clip_path; /* optional local WAV; missing or corrupt fails creation with BS_ASSET */
} bs_engine_config;

typedef struct bs_capabilities {
    uint32_t struct_size;
    int32_t synthesis_available; /* 0 when no acoustic source is attached */
    int32_t sample_rate;         /* 0 when unavailable */
    int32_t streaming;           /* the source returns PCM before a chunk is complete */
    int32_t min_unit_samples;    /* the smallest generation unit, see bs_source_caps */
    int32_t max_units_per_chunk; /* the model's chunk limit, 0 when none is known */
    int32_t voice_count;
    int32_t voice;   /* bs_control */
    int32_t rate;    /* bs_control */
    int32_t pitch;   /* bs_control */
    int32_t gain;    /* bs_control */
    int32_t emotion; /* bs_control */
    int32_t cloning; /* bs_control */
    int32_t laugh;   /* bs_event_render */
    int32_t laugh_clip_sample_rate; /* the clip's own rate before conversion, 0 without a clip */
} bs_capabilities;

typedef struct bs_voice {
    const char* voice_id;  /* NULL or "" = the model default */
    float rate;            /* speaking rate, 1 = unchanged, [0.5, 2] */
    float pitch_semitones; /* 0 = unchanged, [-12, 12], independent of rate */
    float gain_db;         /* 0 = unchanged, [-24, 12]; a limiter keeps peaks below full scale */
} bs_voice;

typedef struct bs_request {
    uint32_t struct_size;
    const char* text;
    size_t text_len;
    const char* language;
    bs_voice voice;
    int32_t unknown_tag_policy;
    int32_t first_chunk_units;     /* 0 = the model's own limit */
    int32_t n_threads;             /* passed to the source; 0 = the source decides */
    int32_t playback_ring_samples; /* 0 = no playback stream; else the bound on buffered audio */
    int32_t prebuffer_samples;     /* playback stays silent until this much is buffered or the job ends */
    const char* export_wav_path;   /* NULL = no export; written as <path>.partial and renamed on success */
    float boundary_fade_ms;        /* edge fade where audio is discontinuous; negative = engine default */
    float audible_threshold_dbfs;  /* level that counts as audible for the latency figures; 0 = default */
} bs_request;

/* Timings are microseconds from bs_job_start, or -1 when the point was not reached. */
typedef struct bs_job_stats {
    uint32_t struct_size;
    int32_t finished;
    int32_t status; /* the job's outcome once finished */
    int32_t sample_rate;
    int32_t chunks;
    int32_t events;
    int32_t laugh_render; /* bs_event_render actually used */
    int64_t samples_out;
    int64_t samples_source;
    int64_t samples_clip;
    int64_t limited_samples;
    int64_t underruns;
    int64_t underrun_samples;
    int64_t peak_buffered_samples;
    int64_t t_plan_us;
    int64_t t_first_source_pcm_us;
    int64_t t_first_audible_pcm_us;
    int64_t t_prebuffered_us;
    int64_t t_first_audible_read_us; /* first audible sample handed to the reader: a device-submission proxy */
    int64_t t_finished_us;
    int64_t source_us;
    int64_t dsp_us;
    int64_t export_us;
    int64_t cancel_latency_us;
} bs_job_stats;

typedef struct bs_wav_info {
    uint32_t struct_size;
    int32_t sample_rate;
    int32_t channels;
    int32_t bits_per_sample;
    int32_t is_float;
    int64_t frames;
} bs_wav_info;

BS_API int32_t bs_abi_version(void);
BS_API const char* bs_version(void);

/* The last failure on the calling thread. Borrowed, valid until the next call. */
BS_API const char* bs_last_error(void);

/* Byte offset into the input the last failure refers to, or -1. */
BS_API int64_t bs_last_error_offset(void);

BS_API int32_t bs_plan_create(const char* text, size_t text_len, const bs_plan_options* options,
                              bs_plan** out_plan);
BS_API size_t bs_plan_count(const bs_plan* plan);
BS_API int32_t bs_plan_item_get(const bs_plan* plan, size_t index, bs_plan_item* out_item);
BS_API void bs_plan_free(bs_plan* plan);

BS_API int32_t bs_engine_create(const bs_engine_config* config, bs_engine** out_engine);
BS_API int32_t bs_engine_capabilities(const bs_engine* engine, bs_capabilities* out_caps);

/* Why synthesis is unavailable, or an empty string. Same buffer contract as bs_job_error. */
BS_API int32_t bs_engine_unavailable_reason(const bs_engine* engine, char* buf, int32_t cap, int32_t* out_len);

/* Safe while jobs are alive: each job keeps the parts of the engine it uses. */
BS_API void bs_engine_free(bs_engine* engine);

/* Fails with BS_STATE while a job from this engine has not finished: one acoustic
 * context, one job. Cancel the running job and wait for it first. */
BS_API int32_t bs_job_start(bs_engine* engine, const bs_request* request, bs_job** out_job);

/* For the audio callback. Never blocks, never allocates. Fills all of cap with
 * samples, padding with silence; *out_real is how many were generated audio. */
BS_API int32_t bs_job_read(bs_job* job, float* out, int32_t cap, int32_t* out_real, int32_t* out_flags);

/* Thread-safe, idempotent, returns at once. Generation stops at the source's next
 * cancellation point; buffered playback audio is discarded immediately. */
BS_API void bs_job_cancel(bs_job* job);

/* Waits for the generation thread. timeout_ms < 0 waits indefinitely. Returns
 * BS_TIMEOUT if it is still running; otherwise BS_OK with *out_status set to the
 * job's outcome. */
BS_API int32_t bs_job_wait(bs_job* job, int32_t timeout_ms, int32_t* out_status);

BS_API int32_t bs_job_stats_get(const bs_job* job, bs_job_stats* out_stats);

/* The job's failure text without a terminator. BS_BUFFER_TOO_SMALL sets *out_len
 * to the length needed. */
BS_API int32_t bs_job_error(const bs_job* job, char* buf, int32_t cap, int32_t* out_len);

/* Cancels and joins if still running. The reader must have stopped calling
 * bs_job_read before this. */
BS_API void bs_job_free(bs_job* job);

/* Validates a WAV file the way a laughter clip is validated, without keeping it. */
BS_API int32_t bs_wav_probe(const char* path, bs_wav_info* out_info);

#ifdef __cplusplus
}
#endif
