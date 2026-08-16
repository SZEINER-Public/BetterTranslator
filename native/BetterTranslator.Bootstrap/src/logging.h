#pragma once

#include <string>

namespace btpay {

class Log {
public:
    static void Open(const std::wstring& path);
    static void Write(const std::wstring& line);
    static void Flush();
    static void EnableEcho(bool enabled);

private:
    Log() = delete;
};

}
