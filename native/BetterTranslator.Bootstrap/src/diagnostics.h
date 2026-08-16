#pragma once

#include <cstdint>
#include <string>

#include "payload.h"
#include "support.h"

namespace btpay {

void ReportLine(const std::wstring& line);

[[nodiscard]] uint32_t RunVerify(const Payload& payload);
[[nodiscard]] uint32_t RunSelfTest(const Payload& payload, const std::wstring& cacheRoot);

}
