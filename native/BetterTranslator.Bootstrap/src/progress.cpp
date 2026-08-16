#include "progress.h"

#include <windows.h>

#include <commctrl.h>

#include "resource.h"
#include "support.h"

namespace btpay {
namespace {

constexpr wchar_t kWindowClass[] = L"BetterTranslatorBootstrapProgress";
constexpr wchar_t kCaption[] = L"BetterTranslator";
constexpr wchar_t kMessage[] = L"Preparing BetterTranslator for first use.";
constexpr int kWindowWidth = 420;
constexpr int kWindowHeight = 128;
constexpr UINT_PTR kTimerId = 1;
constexpr UINT kTimerInterval = 80;
constexpr int kProgressRange = 1000;

struct WindowState {
    const ExtractionProgress* progress = nullptr;
    HWND bar = nullptr;
    HFONT font = nullptr;
};

LRESULT CALLBACK ProgressWindowProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam) {
    auto* state = reinterpret_cast<WindowState*>(GetWindowLongPtrW(window, GWLP_USERDATA));

    switch (message) {
        case WM_NCCREATE: {
            const auto* create = reinterpret_cast<const CREATESTRUCTW*>(lParam);
            SetWindowLongPtrW(window, GWLP_USERDATA,
                              reinterpret_cast<LONG_PTR>(create->lpCreateParams));
            return DefWindowProcW(window, message, wParam, lParam);
        }
        case WM_PAINT: {
            PAINTSTRUCT paint = {};
            const HDC device = BeginPaint(window, &paint);
            RECT client = {};
            GetClientRect(window, &client);
            FillRect(device, &client, GetSysColorBrush(COLOR_WINDOW));

            RECT text = client;
            text.left += 18;
            text.top += 20;
            text.right -= 18;
            text.bottom = text.top + 24;

            if (state != nullptr && state->font != nullptr) {
                SelectObject(device, state->font);
            }
            SetBkMode(device, TRANSPARENT);
            SetTextColor(device, GetSysColor(COLOR_WINDOWTEXT));
            DrawTextW(device, kMessage, -1, &text, DT_LEFT | DT_SINGLELINE | DT_END_ELLIPSIS);

            EndPaint(window, &paint);
            return 0;
        }
        case WM_TIMER: {
            if (state != nullptr && state->progress != nullptr && state->bar != nullptr) {
                const uint64_t total = state->progress->totalBytes;
                const uint64_t done = state->progress->bytesWritten.load();
                const int value = total == 0 ? 0 : static_cast<int>((done * kProgressRange) / total);
                SendMessageW(state->bar, PBM_SETPOS, static_cast<WPARAM>(value), 0);
            }
            return 0;
        }
        case WM_CLOSE:
            return 0;
        case WM_DESTROY:
            PostQuitMessage(0);
            return 0;
        default:
            return DefWindowProcW(window, message, wParam, lParam);
    }
}

void RunWindow(const ExtractionProgress& progress, const std::atomic<bool>& stopping,
               uint32_t delayMilliseconds) {
    const uint64_t deadline = MonotonicMilliseconds() + delayMilliseconds;
    while (MonotonicMilliseconds() < deadline) {
        if (stopping.load()) {
            return;
        }
        Sleep(20);
    }

    const HINSTANCE instance = GetModuleHandleW(nullptr);

    INITCOMMONCONTROLSEX controls = {};
    controls.dwSize = sizeof(controls);
    controls.dwICC = ICC_PROGRESS_CLASS;
    InitCommonControlsEx(&controls);

    WNDCLASSEXW windowClass = {};
    windowClass.cbSize = sizeof(windowClass);
    windowClass.lpfnWndProc = ProgressWindowProc;
    windowClass.hInstance = instance;
    windowClass.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    windowClass.hbrBackground = GetSysColorBrush(COLOR_WINDOW);
    windowClass.lpszClassName = kWindowClass;
    windowClass.hIcon = LoadIconW(instance, MAKEINTRESOURCEW(IDI_APPLICATION_ICON));
    windowClass.hIconSm = windowClass.hIcon;
    RegisterClassExW(&windowClass);

    WindowState state;
    state.progress = &progress;

    NONCLIENTMETRICSW metrics = {};
    metrics.cbSize = sizeof(metrics);
    if (SystemParametersInfoW(SPI_GETNONCLIENTMETRICS, sizeof(metrics), &metrics, 0) != 0) {
        state.font = CreateFontIndirectW(&metrics.lfMessageFont);
    }

    const int screenWidth = GetSystemMetrics(SM_CXSCREEN);
    const int screenHeight = GetSystemMetrics(SM_CYSCREEN);

    const HWND window = CreateWindowExW(
        WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, kWindowClass, kCaption,
        WS_POPUPWINDOW | WS_CAPTION, (screenWidth - kWindowWidth) / 2, (screenHeight - kWindowHeight) / 2,
        kWindowWidth, kWindowHeight, nullptr, nullptr, instance, &state);

    if (window == nullptr) {
        if (state.font != nullptr) {
            DeleteObject(state.font);
        }
        return;
    }

    state.bar = CreateWindowExW(0, PROGRESS_CLASSW, nullptr, WS_CHILD | WS_VISIBLE | PBS_SMOOTH, 18, 56,
                                kWindowWidth - 52, 12, window, nullptr, instance, nullptr);
    if (state.bar != nullptr) {
        SendMessageW(state.bar, PBM_SETRANGE32, 0, static_cast<LPARAM>(kProgressRange));
    }

    ShowWindow(window, SW_SHOWNOACTIVATE);
    SetTimer(window, kTimerId, kTimerInterval, nullptr);

    MSG message = {};
    while (!stopping.load()) {
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE) != 0) {
            if (message.message == WM_QUIT) {
                break;
            }
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        MsgWaitForMultipleObjects(0, nullptr, FALSE, 40, QS_ALLINPUT);
    }

    KillTimer(window, kTimerId);
    DestroyWindow(window);
    UnregisterClassW(kWindowClass, instance);

    if (state.font != nullptr) {
        DeleteObject(state.font);
    }
}

}

ProgressWindow::~ProgressWindow() { Stop(); }

void ProgressWindow::Start(const ExtractionProgress& progress, uint32_t delayMilliseconds) {
    stopping_.store(false);
    thread_ = std::thread([this, &progress, delayMilliseconds]() {
        RunWindow(progress, stopping_, delayMilliseconds);
    });
}

void ProgressWindow::Stop() noexcept {
    stopping_.store(true);
    if (thread_.joinable()) {
        thread_.join();
    }
}

}
