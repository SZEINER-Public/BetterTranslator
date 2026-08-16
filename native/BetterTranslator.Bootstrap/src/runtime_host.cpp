#include "runtime_host.h"

#include <windows.h>

#include "btpay_format.h"
#include "logging.h"

namespace btpay {
namespace {

constexpr wchar_t kHostfxrName[] = L"hostfxr.dll";

struct HostfxrInitializeParameters {
    size_t size;
    const wchar_t* host_path;
    const wchar_t* dotnet_root;
};

using HostfxrInitializeForCommandLineFn = int32_t(__cdecl*)(int32_t, const wchar_t**,
                                                            const HostfxrInitializeParameters*, void**);
using HostfxrRunAppFn = int32_t(__cdecl*)(void*);
using HostfxrCloseFn = int32_t(__cdecl*)(void*);
using HostfxrErrorWriterFn = void(__cdecl*)(const wchar_t*);
using HostfxrSetErrorWriterFn = HostfxrErrorWriterFn(__cdecl*)(HostfxrErrorWriterFn);

void __cdecl ForwardHostfxrError(const wchar_t* message) {
    if (message != nullptr) {
        Log::Write(std::wstring(L"hostfxr: ") + message);
    }
}

class HostfxrLibrary {
public:
    HostfxrLibrary() = default;
    HostfxrLibrary(const HostfxrLibrary&) = delete;
    HostfxrLibrary& operator=(const HostfxrLibrary&) = delete;
    HostfxrLibrary(HostfxrLibrary&&) = delete;
    HostfxrLibrary& operator=(HostfxrLibrary&&) = delete;
    ~HostfxrLibrary() {
        if (module_ != nullptr) {
            FreeLibrary(module_);
        }
    }

    [[nodiscard]] Status Load(const std::wstring& path) {
        module_ = LoadLibraryExW(path.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (module_ == nullptr) {
            return Failure(kExitHostfxrLoadFailed,
                           L"LoadLibrary " + path + L": " + FormatSystemError(GetLastError()));
        }

        initialize_ = reinterpret_cast<HostfxrInitializeForCommandLineFn>(
            GetProcAddress(module_, "hostfxr_initialize_for_dotnet_command_line"));
        runApp_ = reinterpret_cast<HostfxrRunAppFn>(GetProcAddress(module_, "hostfxr_run_app"));
        close_ = reinterpret_cast<HostfxrCloseFn>(GetProcAddress(module_, "hostfxr_close"));
        setErrorWriter_ = reinterpret_cast<HostfxrSetErrorWriterFn>(
            GetProcAddress(module_, "hostfxr_set_error_writer"));

        if (initialize_ == nullptr || runApp_ == nullptr || close_ == nullptr) {
            return Failure(kExitHostfxrLoadFailed, L"hostfxr does not export the expected entry points");
        }

        return Success();
    }

    [[nodiscard]] HostfxrInitializeForCommandLineFn Initialize() const noexcept { return initialize_; }
    [[nodiscard]] HostfxrRunAppFn RunApp() const noexcept { return runApp_; }
    [[nodiscard]] HostfxrCloseFn Close() const noexcept { return close_; }
    [[nodiscard]] HostfxrSetErrorWriterFn SetErrorWriter() const noexcept { return setErrorWriter_; }

private:
    HMODULE module_ = nullptr;
    HostfxrInitializeForCommandLineFn initialize_ = nullptr;
    HostfxrRunAppFn runApp_ = nullptr;
    HostfxrCloseFn close_ = nullptr;
    HostfxrSetErrorWriterFn setErrorWriter_ = nullptr;
};

}

Status RunManagedApplication(const RuntimeLaunch& launch, int32_t& exitCode) {
    const std::wstring hostfxrPath = JoinPath(launch.runtimeDirectory, kHostfxrName);
    if (!PathExists(hostfxrPath)) {
        return Failure(kExitHostfxrLoadFailed, L"the payload has no runtime host at " + hostfxrPath);
    }

    HostfxrLibrary hostfxr;
    const Status loaded = hostfxr.Load(hostfxrPath);
    if (!loaded.Ok()) {
        return loaded;
    }

    if (hostfxr.SetErrorWriter() != nullptr) {
        hostfxr.SetErrorWriter()(ForwardHostfxrError);
    }

    std::vector<const wchar_t*> argv;
    argv.reserve(launch.arguments.size() + 1);
    argv.push_back(launch.managedAssembly.c_str());
    for (const std::wstring& argument : launch.arguments) {
        argv.push_back(argument.c_str());
    }

    HostfxrInitializeParameters parameters = {};
    parameters.size = sizeof(parameters);
    parameters.host_path = launch.hostPath.c_str();
    parameters.dotnet_root = launch.runtimeDirectory.c_str();

    void* context = nullptr;
    const int32_t initialized = hostfxr.Initialize()(static_cast<int32_t>(argv.size()), argv.data(),
                                                     &parameters, &context);
    if (initialized < 0 || context == nullptr) {
        if (context != nullptr) {
            hostfxr.Close()(context);
        }
        return Failure(kExitRuntimeInitFailed,
                       L"hostfxr_initialize_for_dotnet_command_line returned " +
                           FormatHex32(static_cast<uint32_t>(initialized)));
    }

    Log::Flush();

    exitCode = hostfxr.RunApp()(context);
    hostfxr.Close()(context);

    return Success();
}

}
