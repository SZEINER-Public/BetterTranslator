#include "console.h"

#include <windows.h>

#include <cstdio>

namespace btpay {
namespace {

bool g_attached = false;

bool AlreadyRedirected(DWORD standardHandle) noexcept {
    const HANDLE existing = GetStdHandle(standardHandle);
    if (existing == nullptr || existing == INVALID_HANDLE_VALUE) {
        return false;
    }

    const DWORD type = GetFileType(existing);
    return type == FILE_TYPE_DISK || type == FILE_TYPE_PIPE;
}

void RebindStream(FILE* stream, const wchar_t* device, const wchar_t* mode, DWORD standardHandle,
                  DWORD access) noexcept {
    if (AlreadyRedirected(standardHandle)) {
        return;
    }

    const HANDLE handle = CreateFileW(device, access, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
                                      OPEN_EXISTING, 0, nullptr);
    if (handle == INVALID_HANDLE_VALUE) {
        return;
    }

    SetStdHandle(standardHandle, handle);

    FILE* reopened = nullptr;
    if (_wfreopen_s(&reopened, device, mode, stream) == 0) {
        setvbuf(stream, nullptr, _IONBF, 0);
    }
}

}

bool ParentConsole::Attach() noexcept {
    if (g_attached) {
        return true;
    }

    const bool redirected = AlreadyRedirected(STD_OUTPUT_HANDLE);
    const bool hasConsole = GetConsoleWindow() != nullptr || AttachConsole(ATTACH_PARENT_PROCESS) != 0;

    if (!hasConsole && !redirected) {
        return false;
    }

    if (hasConsole) {
        RebindStream(stdout, L"CONOUT$", L"w", STD_OUTPUT_HANDLE, GENERIC_READ | GENERIC_WRITE);
        RebindStream(stderr, L"CONOUT$", L"w", STD_ERROR_HANDLE, GENERIC_READ | GENERIC_WRITE);
        RebindStream(stdin, L"CONIN$", L"r", STD_INPUT_HANDLE, GENERIC_READ | GENERIC_WRITE);
    }

    g_attached = true;
    return true;
}

bool ParentConsole::Attached() noexcept { return g_attached; }

}
