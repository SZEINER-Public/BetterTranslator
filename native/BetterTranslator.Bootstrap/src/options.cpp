#include "options.h"

#include <windows.h>

#include <shellapi.h>

#include <cwchar>

#include "btpay_format.h"

namespace btpay {
namespace {

constexpr wchar_t kClearCacheFlag[] = L"--bt-clear-cache";
constexpr wchar_t kCachePathFlag[] = L"--bt-cache-path";
constexpr wchar_t kVerifyFlag[] = L"--bt-verify";
constexpr wchar_t kSelfTestFlag[] = L"--bt-selftest";

class ArgumentVector {
public:
    explicit ArgumentVector(const wchar_t* commandLine) noexcept
        : values_(CommandLineToArgvW(commandLine, &count_)) {}
    ArgumentVector(const ArgumentVector&) = delete;
    ArgumentVector& operator=(const ArgumentVector&) = delete;
    ArgumentVector(ArgumentVector&&) = delete;
    ArgumentVector& operator=(ArgumentVector&&) = delete;
    ~ArgumentVector() {
        if (values_ != nullptr) {
            LocalFree(values_);
        }
    }

    [[nodiscard]] bool Valid() const noexcept { return values_ != nullptr; }
    [[nodiscard]] int Count() const noexcept { return count_; }
    [[nodiscard]] const wchar_t* At(int index) const noexcept { return values_[index]; }

private:
    int count_ = 0;
    LPWSTR* values_ = nullptr;
};

bool StartsWith(const std::wstring& value, const wchar_t* prefix) {
    const std::wstring text(prefix);
    return value.size() >= text.size() && value.compare(0, text.size(), text) == 0;
}

}

Status ParseCommandLine(const wchar_t* commandLine, Options& options) {
    const ArgumentVector arguments(commandLine);
    if (!arguments.Valid()) {
        return Failure(kExitFlagUsage,
                       L"CommandLineToArgv: " + FormatSystemError(GetLastError()));
    }

    for (int index = 1; index < arguments.Count(); ++index) {
        const std::wstring argument(arguments.At(index));

        if (argument == kClearCacheFlag) {
            options.clearCache = true;
            continue;
        }

        if (argument == kVerifyFlag) {
            options.verify = true;
            continue;
        }

        if (argument == kSelfTestFlag) {
            options.selfTest = true;
            continue;
        }

        if (argument == kCachePathFlag) {
            if (index + 1 >= arguments.Count()) {
                return Failure(kExitFlagUsage, std::wstring(kCachePathFlag) + L" needs a directory");
            }
            options.cachePath.assign(arguments.At(++index));
            continue;
        }

        if (StartsWith(argument, L"--bt-cache-path=")) {
            options.cachePath = argument.substr(wcslen(L"--bt-cache-path="));
            if (options.cachePath.empty()) {
                return Failure(kExitFlagUsage, std::wstring(kCachePathFlag) + L" needs a directory");
            }
            continue;
        }

        if (StartsWith(argument, L"--bt-")) {
            return Failure(kExitFlagUsage, L"unknown bootstrap flag " + argument);
        }

        options.forwarded.push_back(argument);
    }

    return Success();
}

}
