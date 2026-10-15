#if defined(_WIN32)

#ifndef NOMINMAX
#define NOMINMAX
#endif

#include <windows.h>

#include <algorithm>
#include <cstdint>
#include <atomic>
#include <condition_variable>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include <audioclient.h>
#include <initguid.h>
#include <functiondiscoverykeys_devpkey.h>
#include <mmdeviceapi.h>

#include "capture.h"
#include "recognize.h"

namespace bspeech {

namespace {

constexpr uint16_t kFormatPcm = 1;
constexpr uint16_t kFormatFloat = 3;
constexpr uint16_t kFormatExtensible = 0xFFFE;
constexpr size_t kSubFormatOffset = 24;
constexpr REFERENCE_TIME kBufferDuration = 2000000;

template <class T>
class ComPtr {
public:
    ComPtr() = default;
    ComPtr(const ComPtr&) = delete;
    ComPtr& operator=(const ComPtr&) = delete;
    ~ComPtr() { Reset(); }

    T** Out() {
        Reset();
        return &ptr_;
    }

    void** OutVoid() { return reinterpret_cast<void**>(Out()); }
    T* operator->() const { return ptr_; }
    T* Get() const { return ptr_; }
    explicit operator bool() const { return ptr_ != nullptr; }

    void Reset() {
        if (ptr_ != nullptr) {
            ptr_->Release();
            ptr_ = nullptr;
        }
    }

private:
    T* ptr_ = nullptr;
};

class ComScope {
public:
    ComScope() : hr_(CoInitializeEx(nullptr, COINIT_MULTITHREADED)) {}
    ComScope(const ComScope&) = delete;
    ComScope& operator=(const ComScope&) = delete;

    ~ComScope() {
        if (SUCCEEDED(hr_)) {
            CoUninitialize();
        }
    }

    [[nodiscard]] bool Usable() const { return SUCCEEDED(hr_) || hr_ == RPC_E_CHANGED_MODE; }

private:
    HRESULT hr_;
};

std::string Utf8(const wchar_t* text) {
    if (text == nullptr) {
        return {};
    }

    const int length = WideCharToMultiByte(CP_UTF8, 0, text, -1, nullptr, 0, nullptr, nullptr);

    if (length <= 1) {
        return {};
    }

    std::string out(static_cast<size_t>(length - 1), '\0');
    WideCharToMultiByte(CP_UTF8, 0, text, -1, out.data(), length, nullptr, nullptr);
    return out;
}

std::wstring Wide(const std::string& text) {
    if (text.empty()) {
        return {};
    }

    const int length = MultiByteToWideChar(CP_UTF8, 0, text.c_str(), -1, nullptr, 0);

    if (length <= 1) {
        return {};
    }

    std::wstring out(static_cast<size_t>(length - 1), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, text.c_str(), -1, out.data(), length);
    return out;
}

Failure ComFailure(const char* what, HRESULT hr) {
    char code[16];
    std::snprintf(code, sizeof code, "0x%08lX", static_cast<unsigned long>(hr));
    return {BS_IO, std::string(what) + " failed with HRESULT " + code, -1};
}

Failure Enumerate(ComPtr<IMMDeviceCollection>* collection) {
    ComPtr<IMMDeviceEnumerator> enumerator;
    HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, __uuidof(IMMDeviceEnumerator),
                                  enumerator.OutVoid());

    if (FAILED(hr)) {
        return ComFailure("creating the audio device enumerator", hr);
    }

    hr = enumerator->EnumAudioEndpoints(eCapture, DEVICE_STATE_ACTIVE, collection->Out());
    return FAILED(hr) ? ComFailure("listing microphones", hr) : Failure{};
}

class WasapiCapture final : public CaptureDevice {
public:
    WasapiCapture() = default;
    WasapiCapture(const WasapiCapture&) = delete;
    WasapiCapture& operator=(const WasapiCapture&) = delete;
    ~WasapiCapture() override { Stop(); }

    Failure Open(const std::string& device_id) {
        thread_ = std::thread([this, id = Wide(device_id)] { ThreadMain(id); });
        std::unique_lock lock(mutex_);
        cv_.wait(lock, [this] { return phase_ != Phase::Opening; });

        if (phase_ == Phase::Failed) {
            lock.unlock();
            thread_.join();
            return failure_;
        }

        return {};
    }

    [[nodiscard]] int32_t SampleRate() const noexcept override { return rate_; }
    [[nodiscard]] int32_t Channels() const noexcept override { return channels_; }
    [[nodiscard]] int64_t Frames() const noexcept override { return frames_.load(std::memory_order_relaxed); }
    [[nodiscard]] int64_t Glitches() const noexcept override { return glitches_.load(std::memory_order_relaxed); }

    Failure Start(RecognitionSession* session) override {
        std::lock_guard lock(mutex_);

        if (phase_ != Phase::Ready) {
            return {BS_STATE, "the microphone is not open, or is already running", -1};
        }

        session_ = session;
        phase_ = Phase::Running;
        cv_.notify_all();
        return {};
    }

    void Stop() noexcept override {
        stop_.store(true, std::memory_order_release);

        {
            std::lock_guard lock(mutex_);

            if (event_ != nullptr) {
                SetEvent(event_);
            }

            cv_.notify_all();
        }

        if (thread_.joinable()) {
            thread_.join();
        }
    }

private:
    enum class Phase { Opening, Ready, Running, Failed, Done };

    void Fail(Failure failure) {
        std::lock_guard lock(mutex_);
        failure_ = std::move(failure);
        phase_ = Phase::Failed;
        cv_.notify_all();
    }

    void ThreadMain(const std::wstring& device_id) {
        ComScope com;

        if (!com.Usable()) {
            Fail({BS_IO, "COM could not be initialized on the capture thread", -1});
            return;
        }

        ComPtr<IMMDeviceEnumerator> enumerator;
        HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
                                      __uuidof(IMMDeviceEnumerator), enumerator.OutVoid());

        if (FAILED(hr)) {
            Fail(ComFailure("creating the audio device enumerator", hr));
            return;
        }

        ComPtr<IMMDevice> device;
        hr = device_id.empty() ? enumerator->GetDefaultAudioEndpoint(eCapture, eConsole, device.Out())
                               : enumerator->GetDevice(device_id.c_str(), device.Out());

        if (FAILED(hr)) {
            Fail(ComFailure(device_id.empty() ? "finding the default microphone" : "finding the chosen microphone",
                            hr));
            return;
        }

        ComPtr<IAudioClient> client;
        hr = device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, client.OutVoid());

        if (FAILED(hr)) {
            Fail(ComFailure("opening the microphone", hr));
            return;
        }

        WAVEFORMATEX* mix = nullptr;
        hr = client->GetMixFormat(&mix);

        if (FAILED(hr) || mix == nullptr) {
            Fail(ComFailure("reading the microphone format", hr));
            return;
        }

        uint16_t format = mix->wFormatTag;

        if (format == kFormatExtensible && mix->cbSize >= 22) {
            uint32_t sub = 0;
            std::memcpy(&sub, reinterpret_cast<const BYTE*>(mix) + kSubFormatOffset, sizeof sub);
            format = static_cast<uint16_t>(sub);
        }

        rate_ = static_cast<int32_t>(mix->nSamplesPerSec);
        channels_ = static_cast<int32_t>(mix->nChannels);
        is_float_ = format == kFormatFloat && mix->wBitsPerSample == 32;
        const bool pcm16 = format == kFormatPcm && mix->wBitsPerSample == 16;

        if ((!is_float_ && !pcm16) || channels_ < 1 || channels_ > 8) {
            CoTaskMemFree(mix);
            Fail({BS_IO, "the microphone's shared format is neither 32-bit float nor 16-bit PCM", -1});
            return;
        }

        hr = client->Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_EVENTCALLBACK, kBufferDuration, 0, mix,
                                nullptr);
        CoTaskMemFree(mix);

        if (FAILED(hr)) {
            Fail(ComFailure("starting the microphone stream", hr));
            return;
        }

        HANDLE event = CreateEventW(nullptr, FALSE, FALSE, nullptr);

        if (event == nullptr) {
            Fail({BS_IO, "the capture event could not be created", -1});
            return;
        }

        hr = client->SetEventHandle(event);
        UINT32 buffer_frames = 0;

        if (SUCCEEDED(hr)) {
            hr = client->GetBufferSize(&buffer_frames);
        }

        ComPtr<IAudioCaptureClient> capture;

        if (SUCCEEDED(hr)) {
            hr = client->GetService(__uuidof(IAudioCaptureClient), capture.OutVoid());
        }

        if (FAILED(hr)) {
            CloseHandle(event);
            Fail(ComFailure("preparing the capture buffer", hr));
            return;
        }

        std::vector<float> convert(static_cast<size_t>(buffer_frames) * static_cast<size_t>(channels_));

        {
            std::unique_lock lock(mutex_);
            event_ = event;
            phase_ = Phase::Ready;
            cv_.notify_all();
            cv_.wait(lock, [this] { return phase_ == Phase::Running || stop_.load(std::memory_order_acquire); });
        }

        if (!stop_.load(std::memory_order_acquire)) {
            SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL);
            hr = client->Start();

            if (FAILED(hr)) {
                glitches_.fetch_add(1, std::memory_order_relaxed);
            } else {
                Pump(capture.Get(), event, convert);
                client->Stop();
            }
        }

        std::lock_guard lock(mutex_);
        event_ = nullptr;
        CloseHandle(event);
        phase_ = Phase::Done;
    }

    void Pump(IAudioCaptureClient* capture, HANDLE event, std::vector<float>& convert) {
        const auto channels = static_cast<size_t>(channels_);

        while (!stop_.load(std::memory_order_acquire)) {
            if (WaitForSingleObject(event, 200) != WAIT_OBJECT_0) {
                continue;
            }

            UINT32 packet = 0;

            while (!stop_.load(std::memory_order_acquire) && SUCCEEDED(capture->GetNextPacketSize(&packet)) &&
                   packet > 0) {
                BYTE* data = nullptr;
                UINT32 frames = 0;
                DWORD flags = 0;

                if (FAILED(capture->GetBuffer(&data, &frames, &flags, nullptr, nullptr))) {
                    glitches_.fetch_add(1, std::memory_order_relaxed);
                    break;
                }

                if ((flags & AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) != 0) {
                    glitches_.fetch_add(1, std::memory_order_relaxed);
                }

                const size_t samples = static_cast<size_t>(frames) * channels;
                const float* out = nullptr;

                if (samples > convert.size()) {
                    glitches_.fetch_add(1, std::memory_order_relaxed);
                } else if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0) {
                    std::fill(convert.begin(), convert.begin() + static_cast<std::ptrdiff_t>(samples), 0.0f);
                    out = convert.data();
                } else if (is_float_) {
                    out = reinterpret_cast<const float*>(data);
                } else {
                    const auto* pcm = reinterpret_cast<const int16_t*>(data);

                    for (size_t i = 0; i < samples; ++i) {
                        convert[i] = static_cast<float>(pcm[i]) / 32768.0f;
                    }

                    out = convert.data();
                }

                if (out != nullptr && session_ != nullptr) {
                    session_->Push(out, static_cast<int32_t>(frames), channels_);
                    frames_.fetch_add(frames, std::memory_order_relaxed);
                }

                capture->ReleaseBuffer(frames);
            }
        }
    }

    std::thread thread_;
    std::mutex mutex_;
    std::condition_variable cv_;
    Phase phase_ = Phase::Opening;
    Failure failure_;
    RecognitionSession* session_ = nullptr;
    std::atomic<bool> stop_{false};
    HANDLE event_ = nullptr;
    int32_t rate_ = 0;
    int32_t channels_ = 0;
    bool is_float_ = true;
    std::atomic<int64_t> frames_{0};
    std::atomic<int64_t> glitches_{0};
};

}

bool CaptureBackendAvailable() noexcept { return true; }

Failure OpenCaptureDevice(const std::string& device_id, std::unique_ptr<CaptureDevice>* out) {
    auto capture = std::make_unique<WasapiCapture>();
    Failure failure = capture->Open(device_id);

    if (failure.status != BS_OK) {
        return failure;
    }

    *out = std::move(capture);
    return {};
}

Failure CaptureDeviceCount(int32_t* count) {
    *count = 0;
    ComScope com;

    if (!com.Usable()) {
        return {BS_IO, "COM could not be initialized", -1};
    }

    ComPtr<IMMDeviceCollection> collection;
    Failure failure = Enumerate(&collection);

    if (failure.status != BS_OK) {
        return failure;
    }

    UINT n = 0;
    const HRESULT hr = collection->GetCount(&n);

    if (FAILED(hr)) {
        return ComFailure("counting microphones", hr);
    }

    *count = static_cast<int32_t>(n);
    return {};
}

Failure CaptureDeviceAt(int32_t index, std::string* id, std::string* name) {
    ComScope com;

    if (!com.Usable()) {
        return {BS_IO, "COM could not be initialized", -1};
    }

    ComPtr<IMMDeviceCollection> collection;
    Failure failure = Enumerate(&collection);

    if (failure.status != BS_OK) {
        return failure;
    }

    ComPtr<IMMDevice> device;
    HRESULT hr = collection->Item(static_cast<UINT>(index), device.Out());

    if (FAILED(hr)) {
        return {BS_INVALID_ARG, "there is no microphone at index " + std::to_string(index), -1};
    }

    LPWSTR raw_id = nullptr;
    hr = device->GetId(&raw_id);

    if (FAILED(hr)) {
        return ComFailure("reading the microphone id", hr);
    }

    *id = Utf8(raw_id);
    CoTaskMemFree(raw_id);

    ComPtr<IPropertyStore> properties;
    hr = device->OpenPropertyStore(STGM_READ, properties.Out());
    name->clear();

    if (SUCCEEDED(hr)) {
        PROPVARIANT value;
        PropVariantInit(&value);

        if (SUCCEEDED(properties->GetValue(PKEY_Device_FriendlyName, &value)) && value.vt == VT_LPWSTR) {
            *name = Utf8(value.pwszVal);
        }

        PropVariantClear(&value);
    }

    if (name->empty()) {
        *name = *id;
    }

    return {};
}

}

#endif
