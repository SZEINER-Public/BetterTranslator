#pragma once

#include <cstdint>
#include <string>
#include <vector>

#include "support.h"

namespace btpay {

struct RuntimeLaunch {
    std::wstring runtimeDirectory;
    std::wstring managedAssembly;
    std::wstring hostPath;
    std::vector<std::wstring> arguments;
};

[[nodiscard]] Status RunManagedApplication(const RuntimeLaunch& launch, int32_t& exitCode);

}
