#include "cache.h"

#include <shlobj.h>

#include <vector>

#include "btpay_format.h"
#include "logging.h"

namespace btpay {
namespace {

constexpr wchar_t kVendorFolder[] = L"SZEINER";
constexpr wchar_t kProductFolder[] = L"BetterTranslator";
constexpr wchar_t kRuntimeFolder[] = L"runtime";
constexpr wchar_t kLogFolder[] = L"logs";
constexpr wchar_t kLogFile[] = L"bootstrap.log";
constexpr wchar_t kMarkerFile[] = L".complete";
constexpr wchar_t kStagingSuffix[] = L".staging-";
constexpr uint64_t kTicksPerDay = 864000000000ull;

std::wstring KnownFolder(REFKNOWNFOLDERID folder) {
    PWSTR path = nullptr;
    if (FAILED(SHGetKnownFolderPath(folder, KF_FLAG_DEFAULT, nullptr, &path)) || path == nullptr) {
        return std::wstring();
    }

    std::wstring value(path);
    CoTaskMemFree(path);
    return value;
}

uint64_t ToTicks(const FILETIME& value) noexcept {
    return (static_cast<uint64_t>(value.dwHighDateTime) << 32) | static_cast<uint64_t>(value.dwLowDateTime);
}

}

std::wstring DefaultCacheRoot() {
    std::wstring local = EnvironmentValue(L"LOCALAPPDATA");
    if (local.empty()) {
        local = KnownFolder(FOLDERID_LocalAppData);
    }
    if (local.empty()) {
        return std::wstring();
    }
    return JoinPath(JoinPath(local, kVendorFolder), kProductFolder);
}

std::wstring FallbackCacheRoot() {
    std::wstring temporary(MAX_PATH + 1, L'\0');
    const DWORD written = GetTempPathW(static_cast<DWORD>(temporary.size()), temporary.data());
    if (written == 0 || written > temporary.size()) {
        return std::wstring();
    }
    temporary.resize(written);
    while (!temporary.empty() && (temporary.back() == L'\\' || temporary.back() == L'/')) {
        temporary.pop_back();
    }
    return JoinPath(JoinPath(temporary, kVendorFolder), kProductFolder);
}

CacheLayout BuildLayout(const std::wstring& root, const std::wstring& hashPrefix) {
    CacheLayout layout;
    layout.root = root;
    layout.runtimeRoot = JoinPath(root, kRuntimeFolder);
    layout.active = JoinPath(layout.runtimeRoot, hashPrefix);
    layout.staging = layout.active + kStagingSuffix + FormatUnsigned(GetCurrentProcessId());
    layout.marker = JoinPath(layout.active, kMarkerFile);
    layout.logPath = JoinPath(JoinPath(root, kLogFolder), kLogFile);
    return layout;
}

bool MarkerMatches(const std::wstring& markerPath, const std::wstring& hashHex) noexcept {
    std::vector<uint8_t> contents;
    if (!ReadWholeFile(markerPath, contents)) {
        return false;
    }

    if (contents.size() < hashHex.size()) {
        return false;
    }

    const std::wstring recorded =
        Utf8ToWide(static_cast<const char*>(static_cast<const void*>(contents.data())), hashHex.size());
    return recorded == hashHex;
}

Status WriteMarker(const std::wstring& markerPath, const std::wstring& hashHex) {
    const std::string payload = WideToUtf8(hashHex) + "\n";
    return WriteWholeFile(markerPath, payload.data(), payload.size(), kExitCacheWriteFailed);
}

ExtractionLock::~ExtractionLock() { Release(); }

Status ExtractionLock::Acquire(const std::wstring& hashPrefix, DWORD timeoutMilliseconds) {
    const std::wstring name = L"Local\\BetterTranslator.Bootstrap." + hashPrefix;

    mutex_.Reset(CreateMutexW(nullptr, FALSE, name.c_str()));
    if (!mutex_.Valid()) {
        return Failure(kExitExtractionTimeout,
                       L"CreateMutex " + name + L": " + FormatSystemError(GetLastError()));
    }

    const DWORD waited = WaitForSingleObject(mutex_.Get(), timeoutMilliseconds);
    switch (waited) {
        case WAIT_OBJECT_0:
            held_ = true;
            return Success();
        case WAIT_ABANDONED:
            held_ = true;
            Log::Write(L"extraction mutex was abandoned by a previous owner, reclaiming");
            return Success();
        case WAIT_TIMEOUT:
            timedOut_ = true;
            return Failure(kExitExtractionTimeout,
                           L"another instance held the extraction lock for longer than " +
                               FormatUnsigned(timeoutMilliseconds) + L" ms");
        default:
            return Failure(kExitExtractionTimeout,
                           L"WaitForSingleObject: " + FormatSystemError(GetLastError()));
    }
}

void ExtractionLock::Release() noexcept {
    if (held_ && mutex_.Valid()) {
        ReleaseMutex(mutex_.Get());
    }
    held_ = false;
    mutex_.Reset();
}

void CollectStaleRuntimes(const std::wstring& runtimeRoot, const std::wstring& keepDirectory,
                          uint32_t maximumAgeDays) noexcept {
    FILETIME now = {};
    GetSystemTimeAsFileTime(&now);
    const uint64_t cutoff = ToTicks(now) - (static_cast<uint64_t>(maximumAgeDays) * kTicksPerDay);

    WIN32_FIND_DATAW found = {};
    const ScopedHandle search(FindFirstFileExW(ExtendedPath(JoinPath(runtimeRoot, L"*")).c_str(),
                                               FindExInfoBasic, &found, FindExSearchLimitToDirectories,
                                               nullptr, FIND_FIRST_EX_LARGE_FETCH));
    if (!search.Valid()) {
        return;
    }

    std::vector<std::wstring> doomed;
    do {
        if ((found.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0) {
            continue;
        }

        const std::wstring name(found.cFileName);
        if (name == L"." || name == L".." || name == keepDirectory) {
            continue;
        }

        if (ToTicks(found.ftLastWriteTime) >= cutoff) {
            continue;
        }

        doomed.push_back(JoinPath(runtimeRoot, name));
    } while (FindNextFileW(search.Get(), &found) != 0);

    for (const std::wstring& path : doomed) {
        if (RemoveTree(path)) {
            Log::Write(L"collected stale runtime " + FileNameOf(path));
        }
    }
}

}
