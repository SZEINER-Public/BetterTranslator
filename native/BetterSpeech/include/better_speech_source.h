/*
 * The contract an acoustic model meets to be driven by BetterSpeech.
 *
 * BetterSpeech owns everything around the model: the event grammar, chunking,
 * scheduling, voice processing, buffering, export and timing. What it cannot own
 * is the step from text to PCM, because that is model execution and it belongs
 * beside llama.cpp in the BetterRuntime fork. This table is the seam between the
 * two. It is plain C function pointers, so the fork can hand BetterSpeech a
 * pointer to one and no managed delegate ever crosses a native boundary.
 *
 * The intended producer is a set of br_tts_* exports in BetterRuntime driving
 * libmtmd's mtmd_gen_audio_process (GEN_CODE per frame, GEN_WAV per window with
 * the decoder state carried between windows). That producer does not exist yet;
 * see the blockers recorded in the repository README.
 *
 * Threading: BetterSpeech calls every function from one job thread at a time,
 * except cancel, which may be called from any thread while pull is blocked.
 * Ownership: the table and its self pointer belong to the producer and must
 * outlive every engine created over them. Memory returned through out
 * parameters is caller-provided; the source never hands back buffers it owns.
 */
#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define BS_SOURCE_ABI_VERSION 1u

/* Status codes a source returns. Zero is success, anything negative is a failure
 * whose text is available from last_error. */
#define BS_SOURCE_OK 0
#define BS_SOURCE_FAILED (-1)
#define BS_SOURCE_CANCELLED (-2)
#define BS_SOURCE_UNSUPPORTED (-3)

/* Bits for bs_source_caps.native_controls. A bit is set only when the model
 * itself implements the control; BetterSpeech never passes a value to a control
 * the model did not claim. */
#define BS_SOURCE_CONTROL_VOICE (1u << 0)
#define BS_SOURCE_CONTROL_RATE (1u << 1)
#define BS_SOURCE_CONTROL_PITCH (1u << 2)
#define BS_SOURCE_CONTROL_EMOTION (1u << 3)
#define BS_SOURCE_CONTROL_CLONING (1u << 4)

typedef struct bs_source_caps {
    int32_t sample_rate;          /* Hz, fixed for the lifetime of the source */
    int32_t channels;             /* must be 1 */
    int32_t max_units_per_chunk;  /* the model's input limit in count_units units; 0 = none known */
    uint32_t native_events;       /* bit (1u << bs_event_kind) per event the model renders itself */
    uint32_t native_controls;     /* BS_SOURCE_CONTROL_* */
    int32_t voice_count;          /* voices selectable by index, 0 when the model has no voice list */
    int32_t min_unit_samples;     /* the smallest PCM unit pull can produce, for latency reporting */
    int32_t streaming;            /* 1 = pull returns PCM as windows decode; 0 = only once a chunk is done */
} bs_source_caps;

typedef struct bs_source_voice {
    const char* voice_id;   /* NULL = the model default; only set when BS_SOURCE_CONTROL_VOICE */
    float rate;             /* only meaningful when BS_SOURCE_CONTROL_RATE, else always 1 */
    float pitch_semitones;  /* only meaningful when BS_SOURCE_CONTROL_PITCH, else always 0 */
} bs_source_voice;

typedef struct bs_source_v1 {
    uint32_t abi_version; /* BS_SOURCE_ABI_VERSION */
    uint32_t struct_size; /* sizeof(bs_source_v1) */
    void* self;

    int32_t (*get_caps)(void* self, bs_source_caps* out);

    /* Writes the UTF-8 id of voice `index` into buf without a terminator. Returns
     * BS_SOURCE_OK, or BS_SOURCE_FAILED with *out_len set to the required length
     * when cap is too small. */
    int32_t (*voice_id)(void* self, int32_t index, char* buf, int32_t cap, int32_t* out_len);

    /* The model's own measure of a piece of text, normally its token count. The
     * chunker packs against this, never against a character estimate. */
    int32_t (*count_units)(void* self, const char* utf8, size_t len, int32_t* out_units);

    /* Starts one chunk. Prefill happens here or on the first pull. n_threads is
     * the share of the CPU this job may use; 0 lets the source decide. */
    int32_t (*begin)(void* self, const char* utf8, size_t len, const char* language,
                     const bs_source_voice* voice, int32_t n_threads);

    /* Starts a model-native event such as laughter. Only called for events whose
     * bit is set in native_events. */
    int32_t (*begin_event)(void* self, int32_t event_kind);

    /* Blocks until PCM is available, the chunk ends, or cancel is called. Writes
     * at most cap mono float samples in [-1, 1]. *out_done is set once the chunk
     * has produced its last sample; a call may return samples and done together. */
    int32_t (*pull)(void* self, float* out, int32_t cap, int32_t* out_n, int32_t* out_done);

    /* Asks the running begin or pull to stop at the next point the model can stop
     * at, typically a frame boundary, and keeps refusing begin and pull with
     * BS_SOURCE_CANCELLED until reset. Thread-safe, idempotent, never blocks. */
    void (*cancel)(void* self);

    /* Drops per-job state (KV cache, decoder state, cancel flag) and keeps the
     * weights, so the next job starts clean. */
    void (*reset)(void* self);

    /* Borrowed, valid until the next call on this source. */
    const char* (*last_error)(void* self);
} bs_source_v1;

#ifdef __cplusplus
}
#endif
