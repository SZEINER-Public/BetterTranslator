#pragma once

namespace btpay {

class ParentConsole {
public:
    static bool Attach() noexcept;
    static bool Attached() noexcept;

private:
    ParentConsole() = delete;
};

}
