#include "diagnostics.h"

#include <windows.h>

#include <string>

#include "cache.h"
#include "compression.h"
#include "extract.h"
#include "logging.h"

namespace btpay {
namespace {

constexpr const wchar_t* kRequiredRuntimeFiles[] = {
    L"hostfxr.dll",
    L"hostpolicy.dll",
    L"coreclr.dll",
    L"clrjit.dll",
    L"System.Private.CoreLib.dll",
    L"wpfgfx_cor3.dll",
    L"PresentationNative_cor3.dll",
    L"vcruntime140_cor3.dll",
    L"BetterTranslator.dll",
    L"BetterTranslator.runtimeconfig.json",
    L"BetterTranslator.deps.json",
};

bool ContainsEntry(const Payload& payload, const std::wstring& name) {
    for (const PayloadEntry& entry : payload.Entries()) {
        if (entry.path == name) {
            return true;
        }
    }
    return false;
}

}

void ReportLine(const std::wstring& line) {
    Log::Write(line);

    const std::string utf8 = WideToUtf8(line) + "\r\n";
    const HANDLE output = GetStdHandle(STD_OUTPUT_HANDLE);
    if (output != nullptr && output != INVALID_HANDLE_VALUE) {
        DWORD written = 0;
        WriteFile(output, utf8.data(), static_cast<DWORD>(utf8.size()), &written, nullptr);
    }
}

uint32_t RunVerify(const Payload& payload) {
    const uint64_t started = MonotonicMilliseconds();

    const Status hashed = payload.VerifyContainerHash();
    if (!hashed.Ok()) {
        ReportLine(L"verify FAILED: " + hashed.detail);
        return hashed.code;
    }

    ReportLine(L"container hash    " + payload.HashHex());
    ReportLine(L"format version    " + FormatUnsigned(payload.Header().formatVersion));
    ReportLine(L"algorithm         " + std::wstring(AlgorithmName(payload.Header().algorithm)));
    ReportLine(L"entries           " + FormatUnsigned(payload.Header().entryCount));
    ReportLine(L"blocks            " + FormatUnsigned(payload.Header().blockCount));
    ReportLine(L"container bytes   " + FormatUnsigned(payload.ContainerBytes()));
    ReportLine(L"payload bytes     " + FormatUnsigned(payload.Header().totalUncompressedSize));
    ReportLine(L"verify OK in " + FormatUnsigned(MonotonicMilliseconds() - started) + L" ms");
    return kExitSuccess;
}

uint32_t RunSelfTest(const Payload& payload, const std::wstring& cacheRoot) {
    ReportLine(L"selftest start");

    const uint64_t hashStarted = MonotonicMilliseconds();
    const Status hashed = payload.VerifyContainerHash();
    if (!hashed.Ok()) {
        ReportLine(L"selftest FAILED container hash: " + hashed.detail);
        return kExitSelfTestFailed;
    }
    ReportLine(L"container hash    ok in " + FormatUnsigned(MonotonicMilliseconds() - hashStarted) + L" ms");

    for (const wchar_t* required : kRequiredRuntimeFiles) {
        if (!ContainsEntry(payload, required)) {
            ReportLine(std::wstring(L"selftest FAILED missing payload entry ") + required);
            return kExitSelfTestFailed;
        }
    }
    ReportLine(L"runtime manifest  ok, " + FormatUnsigned(payload.Header().entryCount) + L" entries");

    const uint64_t expandStarted = MonotonicMilliseconds();
    uint32_t verified = 0;
    const Status entries = VerifyEntries(payload, verified);
    if (!entries.Ok()) {
        ReportLine(L"selftest FAILED entry expansion: " + entries.detail);
        return kExitSelfTestFailed;
    }
    ReportLine(L"entry expansion   ok, " + FormatUnsigned(verified) + L" entries in " +
               FormatUnsigned(MonotonicMilliseconds() - expandStarted) + L" ms");

    const CacheLayout layout = BuildLayout(cacheRoot, payload.HashPrefix());
    const Status writable = EnsureDirectoryTree(layout.runtimeRoot, kExitCacheWriteFailed);
    if (!writable.Ok()) {
        ReportLine(L"selftest FAILED cache root: " + writable.detail);
        return kExitSelfTestFailed;
    }
    ReportLine(L"cache root        ok at " + layout.runtimeRoot);
    ReportLine(L"cache warm        " +
               std::wstring(MarkerMatches(layout.marker, payload.HashHex()) ? L"yes" : L"no"));

    ExtractionLock lock;
    const Status acquired = lock.Acquire(payload.HashPrefix(), 0);
    ReportLine(L"extraction lock   " + std::wstring(acquired.Ok() ? L"free" : L"held by another instance"));
    lock.Release();

    ReportLine(L"selftest PASS");
    return kExitSuccess;
}

}
