#include "logging.h"

#include <windows.h>

#include <cstdio>
#include <mutex>
#include <string>
#include <vector>

#include "support.h"

namespace btpay {
namespace {

constexpr uint64_t kRollThresholdBytes = 1048576;

std::mutex g_mutex;
std::wstring g_path;
std::vector<std::wstring> g_pending;
bool g_echo = false;

std::wstring TimeStamp() {
    SYSTEMTIME now = {};
    GetSystemTime(&now);

    wchar_t buffer[32] = {};
    const int written = swprintf(buffer, 32, L"%04u-%02u-%02uT%02u:%02u:%02u.%03uZ", now.wYear,
                                 now.wMonth, now.wDay, now.wHour, now.wMinute, now.wSecond,
                                 now.wMilliseconds);
    if (written <= 0) {
        return std::wstring();
    }
    return std::wstring(buffer, static_cast<size_t>(written));
}

void RollIfLarge(const std::wstring& path) {
    WIN32_FILE_ATTRIBUTE_DATA attributes = {};
    if (GetFileAttributesExW(ExtendedPath(path).c_str(), GetFileExInfoStandard, &attributes) == 0) {
        return;
    }

    const uint64_t size = (static_cast<uint64_t>(attributes.nFileSizeHigh) << 32) |
                          static_cast<uint64_t>(attributes.nFileSizeLow);
    if (size < kRollThresholdBytes) {
        return;
    }

    const std::wstring previous = path + L".1";
    DeleteFileW(ExtendedPath(previous).c_str());
    MoveFileW(ExtendedPath(path).c_str(), ExtendedPath(previous).c_str());
}

}

void Log::Open(const std::wstring& path) {
    const std::lock_guard<std::mutex> guard(g_mutex);
    g_path = path;
}

void Log::EnableEcho(bool enabled) {
    const std::lock_guard<std::mutex> guard(g_mutex);
    g_echo = enabled;
}

void Log::Write(const std::wstring& line) {
    const std::lock_guard<std::mutex> guard(g_mutex);
    g_pending.push_back(TimeStamp() + L" " + line);
    if (g_echo) {
        const std::string utf8 = WideToUtf8(g_pending.back() + L"\r\n");
        DWORD written = 0;
        WriteFile(GetStdHandle(STD_ERROR_HANDLE), utf8.data(), static_cast<DWORD>(utf8.size()), &written,
                  nullptr);
    }
}

void Log::Flush() {
    std::vector<std::wstring> pending;
    std::wstring path;
    {
        const std::lock_guard<std::mutex> guard(g_mutex);
        if (g_path.empty() || g_pending.empty()) {
            return;
        }
        pending.swap(g_pending);
        path = g_path;
    }

    const Status directory = EnsureDirectoryTree(ParentDirectory(path), 1);
    if (!directory.Ok()) {
        return;
    }

    RollIfLarge(path);

    std::string payload;
    for (const std::wstring& line : pending) {
        payload.append(WideToUtf8(line));
        payload.append("\r\n");
    }

    const ScopedHandle file(CreateFileW(ExtendedPath(path).c_str(), FILE_APPEND_DATA,
                                        FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS,
                                        FILE_ATTRIBUTE_NORMAL, nullptr));
    if (!file.Valid()) {
        return;
    }

    DWORD written = 0;
    WriteFile(file.Get(), payload.data(), static_cast<DWORD>(payload.size()), &written, nullptr);
}

}
