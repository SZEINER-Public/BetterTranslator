#pragma once

#include <string>
#include <vector>

#include "support.h"

namespace btpay {

struct Options {
    bool clearCache = false;
    bool verify = false;
    bool selfTest = false;
    std::wstring cachePath;
    std::vector<std::wstring> forwarded;

    [[nodiscard]] bool RunsManagedApplication() const noexcept { return !verify && !selfTest; }
};

[[nodiscard]] Status ParseCommandLine(const wchar_t* commandLine, Options& options);

}
