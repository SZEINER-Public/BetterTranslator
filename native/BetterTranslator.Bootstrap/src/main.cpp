#include <windows.h>

#include <string>
#include <thread>

#include "btpay_format.h"
#include "cache.h"
#include "compression.h"
#include "console.h"
#include "diagnostics.h"
#include "extract.h"
#include "logging.h"
#include "options.h"
#include "payload.h"
#include "progress.h"
#include "runtime_host.h"
#include "support.h"

namespace btpay {
namespace {

constexpr wchar_t kManagedAssembly[] = L"BetterTranslator.dll";
constexpr uint32_t kProgressDelayMilliseconds = 400;
constexpr DWORD kLockTimeoutMilliseconds = 300000;
constexpr uint32_t kStaleRuntimeAgeDays = 7;

void ShowFailure(const Status& status) {
    const std::wstring text = L"BetterTranslator could not start.\n\nStage exit code " +
                              FormatUnsigned(status.code) + L"\n" + status.detail;

    Log::Write(L"fatal " + FormatUnsigned(status.code) + L": " + status.detail);
    Log::Flush();

    if (ParentConsole::Attached()) {
        ReportLine(text);
        return;
    }

    MessageBoxW(nullptr, text.c_str(), L"BetterTranslator", MB_OK | MB_ICONERROR);
}

Status PrepareCache(Payload& payload, CacheLayout& layout, bool& extracted) {
    const std::wstring hashHex = payload.HashHex();

    if (MarkerMatches(layout.marker, hashHex)) {
        return Success();
    }

    ExtractionLock lock;
    const Status acquired = lock.Acquire(payload.HashPrefix(), kLockTimeoutMilliseconds);
    if (!acquired.Ok()) {
        if (lock.TimedOut() && MarkerMatches(layout.marker, hashHex)) {
            Log::Write(L"the extraction lock timed out but another instance finished the work");
            return Success();
        }
        return acquired;
    }

    if (MarkerMatches(layout.marker, hashHex)) {
        Log::Write(L"another instance extracted the payload while this one waited");
        return Success();
    }

    const Status container = payload.VerifyContainerHash();
    if (!container.Ok()) {
        return container;
    }

    const Status bound = payload.ReadTables();
    if (!bound.Ok()) {
        return bound;
    }

    ExtractionProgress progress;
    progress.totalBytes = payload.Header().totalUncompressedSize;
    progress.totalFiles = payload.Header().entryCount;

    ProgressWindow window;
    window.Start(progress, kProgressDelayMilliseconds);

    const uint64_t started = MonotonicMilliseconds();
    const Status materialized = MaterializeCache(payload, layout, progress);
    window.Stop();

    if (!materialized.Ok()) {
        return materialized;
    }

    extracted = true;
    Log::Write(L"extracted " + FormatUnsigned(payload.Header().entryCount) + L" entries, " +
               FormatUnsigned(payload.Header().totalUncompressedSize) + L" bytes in " +
               FormatUnsigned(MonotonicMilliseconds() - started) + L" ms to " + layout.active);
    return Success();
}

int Run() {
    ParentConsole::Attach();

    Options options;
    const Status parsed = ParseCommandLine(GetCommandLineW(), options);
    if (!parsed.Ok()) {
        ShowFailure(parsed);
        return static_cast<int>(parsed.code);
    }

    Log::EnableEcho(options.verify || options.selfTest);

    Payload payload;
    const Status located = payload.LocateInCurrentModule();

    std::wstring cacheRoot = options.cachePath.empty() ? DefaultCacheRoot() : options.cachePath;
    if (cacheRoot.empty()) {
        cacheRoot = FallbackCacheRoot();
    }

    CacheLayout layout = BuildLayout(cacheRoot, located.Ok() ? payload.HashPrefix() : L"unknown");
    Log::Open(layout.logPath);

    if (!located.Ok()) {
        ShowFailure(located);
        return static_cast<int>(located.code);
    }

    if (options.clearCache) {
        const bool removed = RemoveTree(layout.runtimeRoot);
        Log::Write(removed ? L"cleared the runtime cache at " + layout.runtimeRoot
                           : L"the runtime cache could not be fully cleared at " + layout.runtimeRoot);
    }

    if (options.verify || options.selfTest) {
        const Status tables = payload.ReadTables();
        if (!tables.Ok()) {
            ShowFailure(tables);
            return static_cast<int>(tables.code);
        }

        const uint32_t code = options.selfTest ? RunSelfTest(payload, cacheRoot) : RunVerify(payload);
        Log::Flush();
        return static_cast<int>(code);
    }

    bool extracted = false;
    Status prepared = PrepareCache(payload, layout, extracted);

    if (!prepared.Ok() && prepared.code == kExitCacheWriteFailed && options.cachePath.empty()) {
        Log::Write(L"the cache under " + cacheRoot + L" is not writable, falling back to the temporary folder");
        cacheRoot = FallbackCacheRoot();
        layout = BuildLayout(cacheRoot, payload.HashPrefix());
        layout.usingFallback = true;
        extracted = false;
        prepared = PrepareCache(payload, layout, extracted);
    }

    if (!prepared.Ok()) {
        ShowFailure(prepared);
        return static_cast<int>(prepared.code);
    }

    std::thread collector;
    if (!extracted) {
        const std::wstring runtimeRoot = layout.runtimeRoot;
        const std::wstring keep = FileNameOf(layout.active);
        collector = std::thread(
            [runtimeRoot, keep]() { CollectStaleRuntimes(runtimeRoot, keep, kStaleRuntimeAgeDays); });
    }

    RuntimeLaunch launch;
    launch.runtimeDirectory = layout.active;
    launch.managedAssembly = JoinPath(layout.active, kManagedAssembly);
    launch.hostPath = ModuleFilePath();
    launch.arguments = options.forwarded;

    Log::Write((extracted ? L"cold start, " : L"warm start, ") + std::wstring(L"bootstrap overhead ") +
               FormatMilliseconds(ProcessAgeMicroseconds()) +
               (layout.usingFallback ? L", temporary folder fallback, runtime at " : L", runtime at ") +
               layout.active);

    int32_t exitCode = 0;
    const Status ran = RunManagedApplication(launch, exitCode);

    if (collector.joinable()) {
        collector.join();
    }

    if (!ran.Ok()) {
        ShowFailure(ran);
        return static_cast<int>(ran.code);
    }

    Log::Write(L"the application exited with code " + FormatUnsigned(static_cast<uint32_t>(exitCode)));
    Log::Flush();
    return static_cast<int>(exitCode);
}

}
}

int APIENTRY wWinMain(_In_ HINSTANCE, _In_opt_ HINSTANCE, _In_ LPWSTR, _In_ int) {
    try {
        return btpay::Run();
    } catch (const std::exception& failure) {
        const std::string what(failure.what());
        btpay::ShowFailure(btpay::Failure(btpay::kExitCacheWriteFailed,
                                          L"unhandled: " + btpay::Utf8ToWide(what.data(), what.size())));
        return static_cast<int>(btpay::kExitCacheWriteFailed);
    } catch (...) {
        btpay::ShowFailure(btpay::Failure(btpay::kExitCacheWriteFailed, L"unhandled bootstrap failure"));
        return static_cast<int>(btpay::kExitCacheWriteFailed);
    }
}
