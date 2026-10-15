#pragma once

#include <cstdint>
#include <cstring>
#include <exception>
#include <new>
#include <string>

#include "better_speech.h"
#include "engine.h"

namespace bspeech::abi {

inline thread_local std::string last_error;
inline thread_local int64_t last_error_offset = -1;

inline int32_t Fail(int32_t status, std::string message, int64_t offset = -1) {
    last_error = std::move(message);
    last_error_offset = offset;
    return status;
}

inline int32_t Fail(const Failure& failure) { return Fail(failure.status, failure.message, failure.offset); }

template <class F>
int32_t Guard(F&& body) noexcept {
    try {
        return body();
    } catch (const std::bad_alloc&) {
        return Fail(BS_OUT_OF_MEMORY, "out of memory");
    } catch (const std::exception& e) {
        return Fail(BS_INTERNAL, e.what());
    } catch (...) {
        return Fail(BS_INTERNAL, "internal error");
    }
}

inline int32_t CopyOut(const std::string& text, char* buf, int32_t cap, int32_t* out_len) {
    if (out_len == nullptr) {
        return Fail(BS_INVALID_ARG, "out_len is null");
    }

    *out_len = static_cast<int32_t>(text.size());

    if (text.empty()) {
        return BS_OK;
    }

    if (buf == nullptr || cap < static_cast<int32_t>(text.size())) {
        return Fail(BS_BUFFER_TOO_SMALL, "the buffer is smaller than the text");
    }

    std::memcpy(buf, text.data(), text.size());
    return BS_OK;
}

}
