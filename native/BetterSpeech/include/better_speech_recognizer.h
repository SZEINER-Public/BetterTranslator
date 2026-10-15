#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define BS_RECOGNIZER_ABI_VERSION 1u

#define BS_REC_OK 0
#define BS_REC_FAILED (-1)
#define BS_REC_CANCELLED (-2)
#define BS_REC_UNSUPPORTED (-3)
#define BS_REC_BUFFER_TOO_SMALL (-4)

#define BS_REC_BIAS_CONTEXT (1u << 0)
#define BS_REC_BIAS_LOGIT (1u << 1)
#define BS_REC_BIAS_TRIE (1u << 2)

typedef struct bs_rec_caps {
    int32_t sample_rate;
    int32_t max_window_samples;
    int32_t streaming;
    int32_t word_timestamps;
    uint32_t bias_controls;
    int32_t language_count;
} bs_rec_caps;

typedef struct bs_rec_word {
    int32_t text_offset;
    int32_t text_length;
    int64_t start_sample;
    int64_t end_sample;
    float confidence;
} bs_rec_word;

typedef struct bs_rec_window_options {
    const char* language;
    const char* context;
    size_t context_len;
    const char* const* bias_terms;
    int32_t bias_term_count;
    int32_t n_threads;
} bs_rec_window_options;

typedef struct bs_recognizer_v1 {
    uint32_t abi_version;
    uint32_t struct_size;
    void* self;
    int32_t (*get_caps)(void* self, bs_rec_caps* out);
    int32_t (*language)(void* self, int32_t index, char* buf, int32_t cap, int32_t* out_len);
    int32_t (*begin)(void* self, const bs_rec_window_options* options);
    int32_t (*feed)(void* self, const float* samples, int32_t count);
    int32_t (*partial)(void* self, char* text, int32_t text_cap, int32_t* out_text_len, bs_rec_word* words,
                       int32_t words_cap, int32_t* out_words);
    int32_t (*finalize)(void* self, char* text, int32_t text_cap, int32_t* out_text_len, bs_rec_word* words,
                        int32_t words_cap, int32_t* out_words);
    void (*cancel)(void* self);
    void (*reset)(void* self);
    const char* (*last_error)(void* self);
} bs_recognizer_v1;

#ifdef __cplusplus
}
#endif
