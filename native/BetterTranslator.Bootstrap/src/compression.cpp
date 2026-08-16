#include "compression.h"

#include <windows.h>

#include <compressapi.h>

#include <cstring>

#include "btpay_format.h"

namespace btpay {
namespace {

DWORD ToWindowsAlgorithm(uint32_t algorithm) noexcept {
    switch (algorithm) {
        case kAlgorithmXpressHuff:
            return COMPRESS_ALGORITHM_XPRESS_HUFF;
        case kAlgorithmLzms:
            return COMPRESS_ALGORITHM_LZMS;
        default:
            return 0;
    }
}

}

const wchar_t* AlgorithmName(uint32_t algorithm) noexcept {
    switch (algorithm) {
        case kAlgorithmStore:
            return L"store";
        case kAlgorithmXpressHuff:
            return L"xpress-huff";
        case kAlgorithmLzms:
            return L"lzms";
        default:
            return L"unknown";
    }
}

Decompressor::Decompressor(uint32_t algorithm) : algorithm_(algorithm) {
    if (algorithm == kAlgorithmStore) {
        return;
    }

    const DWORD windowsAlgorithm = ToWindowsAlgorithm(algorithm);
    if (windowsAlgorithm == 0) {
        ready_ = Failure(kExitDecompressionFailed,
                         L"container algorithm " + FormatUnsigned(algorithm) + L" is not supported");
        return;
    }

    DECOMPRESSOR_HANDLE handle = nullptr;
    if (CreateDecompressor(windowsAlgorithm, nullptr, &handle) == FALSE) {
        ready_ = Failure(kExitDecompressionFailed,
                         L"CreateDecompressor: " + FormatSystemError(GetLastError()));
        return;
    }

    handle_ = handle;
}

Decompressor::~Decompressor() {
    if (handle_ != nullptr) {
        CloseDecompressor(static_cast<DECOMPRESSOR_HANDLE>(handle_));
    }
}

Status Decompressor::Expand(const uint8_t* source, size_t sourceBytes, uint8_t* destination,
                            size_t destinationBytes) const {
    if (algorithm_ == kAlgorithmStore) {
        if (sourceBytes != destinationBytes) {
            return Failure(kExitDecompressionFailed, L"a stored block has a mismatched size");
        }
        std::memcpy(destination, source, destinationBytes);
        return Success();
    }

    if (handle_ == nullptr) {
        return ready_.Ok() ? Failure(kExitDecompressionFailed, L"no decompressor is available") : ready_;
    }

    SIZE_T produced = 0;
    if (Decompress(static_cast<DECOMPRESSOR_HANDLE>(handle_), const_cast<uint8_t*>(source), sourceBytes,
                   destination, destinationBytes, &produced) == FALSE) {
        return Failure(kExitDecompressionFailed, L"Decompress: " + FormatSystemError(GetLastError()));
    }

    if (produced != destinationBytes) {
        return Failure(kExitDecompressionFailed,
                       L"a block expanded to " + FormatUnsigned(produced) + L" bytes but " +
                           FormatUnsigned(destinationBytes) + L" were expected");
    }

    return Success();
}

}
