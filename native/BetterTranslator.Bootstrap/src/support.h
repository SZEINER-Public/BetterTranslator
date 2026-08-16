#pragma once

#include <windows.h>

#include <cstdint>
#include <string>
#include <vector>

namespace btpay {

struct Status {
    uint32_t code = 0;
    std::wstring detail;

    [[nodiscard]] bool Ok() const noexcept { return code == 0; }
};

[[nodiscard]] Status Success() noexcept;
[[nodiscard]] Status Failure(uint32_t code, std::wstring detail);

class ScopedHandle {
public:
    ScopedHandle() noexcept = default;
    explicit ScopedHandle(HANDLE handle) noexcept;
    ScopedHandle(const ScopedHandle&) = delete;
    ScopedHandle& operator=(const ScopedHandle&) = delete;
    ScopedHandle(ScopedHandle&& other) noexcept;
    ScopedHandle& operator=(ScopedHandle&& other) noexcept;
    ~ScopedHandle();

    [[nodiscard]] HANDLE Get() const noexcept { return handle_; }
    [[nodiscard]] bool Valid() const noexcept;
    void Reset(HANDLE handle = INVALID_HANDLE_VALUE) noexcept;

private:
    HANDLE handle_ = INVALID_HANDLE_VALUE;
};

class Sha256 {
public:
    Sha256();
    Sha256(const Sha256&) = delete;
    Sha256& operator=(const Sha256&) = delete;
    Sha256(Sha256&&) = delete;
    Sha256& operator=(Sha256&&) = delete;
    ~Sha256();

    [[nodiscard]] bool Valid() const noexcept { return hash_ != nullptr; }
    [[nodiscard]] bool Update(const void* data, size_t bytes) noexcept;
    [[nodiscard]] bool Finish(uint8_t (&digest)[32]) noexcept;

private:
    void* algorithm_ = nullptr;
    void* hash_ = nullptr;
    std::vector<uint8_t> object_;
};

[[nodiscard]] bool ComputeSha256(const void* data, size_t bytes, uint8_t (&digest)[32]) noexcept;

[[nodiscard]] std::wstring FormatSystemError(DWORD error);
[[nodiscard]] std::wstring BytesToHex(const uint8_t* data, size_t bytes);
[[nodiscard]] std::wstring Utf8ToWide(const char* data, size_t bytes);
[[nodiscard]] std::string WideToUtf8(const std::wstring& value);
[[nodiscard]] std::wstring FormatUnsigned(uint64_t value);
[[nodiscard]] std::wstring FormatHex32(uint32_t value);

[[nodiscard]] std::wstring ModuleFilePath();
[[nodiscard]] std::wstring ModuleDirectory();
[[nodiscard]] std::wstring EnvironmentValue(const wchar_t* name);
[[nodiscard]] std::wstring JoinPath(const std::wstring& left, const std::wstring& right);
[[nodiscard]] std::wstring ExtendedPath(const std::wstring& path);
[[nodiscard]] std::wstring ParentDirectory(const std::wstring& path);
[[nodiscard]] std::wstring FileNameOf(const std::wstring& path);

[[nodiscard]] bool PathExists(const std::wstring& path) noexcept;
[[nodiscard]] bool DirectoryExists(const std::wstring& path) noexcept;
[[nodiscard]] Status EnsureDirectory(const std::wstring& path, uint32_t failureCode);
[[nodiscard]] Status EnsureDirectoryTree(const std::wstring& path, uint32_t failureCode);
[[nodiscard]] bool RemoveTree(const std::wstring& path) noexcept;
[[nodiscard]] Status WriteWholeFile(const std::wstring& path, const void* data, size_t bytes,
                                    uint32_t failureCode);
[[nodiscard]] bool ReadWholeFile(const std::wstring& path, std::vector<uint8_t>& contents) noexcept;

[[nodiscard]] uint64_t MonotonicMilliseconds() noexcept;
[[nodiscard]] uint64_t ProcessAgeMicroseconds() noexcept;
[[nodiscard]] std::wstring FormatMilliseconds(uint64_t microseconds);

}
