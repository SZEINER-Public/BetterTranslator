#include "support.h"

#include <bcrypt.h>

#include <algorithm>
#include <utility>

namespace btpay {
namespace {

constexpr size_t kFileChunkBytes = 1u << 20;

bool NtSucceeded(NTSTATUS status) noexcept { return status >= 0; }

}

Status Success() noexcept { return Status{}; }

Status Failure(uint32_t code, std::wstring detail) { return Status{code, std::move(detail)}; }

ScopedHandle::ScopedHandle(HANDLE handle) noexcept : handle_(handle) {}

ScopedHandle::ScopedHandle(ScopedHandle&& other) noexcept
    : handle_(std::exchange(other.handle_, INVALID_HANDLE_VALUE)) {}

ScopedHandle& ScopedHandle::operator=(ScopedHandle&& other) noexcept {
    if (this != &other) {
        Reset(std::exchange(other.handle_, INVALID_HANDLE_VALUE));
    }
    return *this;
}

ScopedHandle::~ScopedHandle() { Reset(); }

bool ScopedHandle::Valid() const noexcept {
    return handle_ != INVALID_HANDLE_VALUE && handle_ != nullptr;
}

void ScopedHandle::Reset(HANDLE handle) noexcept {
    if (Valid()) {
        CloseHandle(handle_);
    }
    handle_ = handle;
}

Sha256::Sha256() {
    BCRYPT_ALG_HANDLE algorithm = nullptr;
    if (!NtSucceeded(BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0))) {
        return;
    }

    algorithm_ = algorithm;

    DWORD objectBytes = 0;
    ULONG produced = 0;
    if (!NtSucceeded(BCryptGetProperty(algorithm, BCRYPT_OBJECT_LENGTH,
                                       reinterpret_cast<PUCHAR>(&objectBytes), sizeof(objectBytes),
                                       &produced, 0))) {
        return;
    }

    object_.resize(objectBytes);

    BCRYPT_HASH_HANDLE hash = nullptr;
    if (NtSucceeded(BCryptCreateHash(algorithm, &hash, object_.data(), objectBytes, nullptr, 0, 0))) {
        hash_ = hash;
    }
}

Sha256::~Sha256() {
    if (hash_ != nullptr) {
        BCryptDestroyHash(static_cast<BCRYPT_HASH_HANDLE>(hash_));
    }
    if (algorithm_ != nullptr) {
        BCryptCloseAlgorithmProvider(static_cast<BCRYPT_ALG_HANDLE>(algorithm_), 0);
    }
}

bool Sha256::Update(const void* data, size_t bytes) noexcept {
    if (hash_ == nullptr) {
        return false;
    }

    const auto* cursor = static_cast<const unsigned char*>(data);
    size_t remaining = bytes;
    while (remaining > 0) {
        const ULONG slice = static_cast<ULONG>(std::min<size_t>(remaining, 0x40000000u));
        if (!NtSucceeded(BCryptHashData(static_cast<BCRYPT_HASH_HANDLE>(hash_),
                                        const_cast<PUCHAR>(cursor), slice, 0))) {
            return false;
        }
        cursor += slice;
        remaining -= slice;
    }
    return true;
}

bool Sha256::Finish(uint8_t (&digest)[32]) noexcept {
    if (hash_ == nullptr) {
        return false;
    }
    return NtSucceeded(BCryptFinishHash(static_cast<BCRYPT_HASH_HANDLE>(hash_), digest, 32, 0));
}

bool ComputeSha256(const void* data, size_t bytes, uint8_t (&digest)[32]) noexcept {
    Sha256 hasher;
    if (!hasher.Valid()) {
        return false;
    }
    if (!hasher.Update(data, bytes)) {
        return false;
    }
    return hasher.Finish(digest);
}

std::wstring FormatSystemError(DWORD error) {
    LPWSTR buffer = nullptr;
    const DWORD length = FormatMessageW(
        FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
        nullptr, error, MAKELANGID(LANG_NEUTRAL, SUBLANG_DEFAULT),
        reinterpret_cast<LPWSTR>(&buffer), 0, nullptr);

    std::wstring text;
    if (length != 0 && buffer != nullptr) {
        text.assign(buffer, length);
        while (!text.empty() && (text.back() == L'\r' || text.back() == L'\n' || text.back() == L' ')) {
            text.pop_back();
        }
    }
    if (buffer != nullptr) {
        LocalFree(buffer);
    }

    return FormatHex32(error) + L" " + text;
}

std::wstring BytesToHex(const uint8_t* data, size_t bytes) {
    static constexpr wchar_t kDigits[] = L"0123456789abcdef";
    std::wstring text;
    text.reserve(bytes * 2);
    for (size_t index = 0; index < bytes; ++index) {
        text.push_back(kDigits[(data[index] >> 4) & 0x0F]);
        text.push_back(kDigits[data[index] & 0x0F]);
    }
    return text;
}

std::wstring Utf8ToWide(const char* data, size_t bytes) {
    if (bytes == 0) {
        return std::wstring();
    }

    const int required = MultiByteToWideChar(CP_UTF8, 0, data, static_cast<int>(bytes), nullptr, 0);
    if (required <= 0) {
        return std::wstring();
    }

    std::wstring text(static_cast<size_t>(required), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, data, static_cast<int>(bytes), text.data(), required);
    return text;
}

std::string WideToUtf8(const std::wstring& value) {
    if (value.empty()) {
        return std::string();
    }

    const int required = WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()),
                                             nullptr, 0, nullptr, nullptr);
    if (required <= 0) {
        return std::string();
    }

    std::string text(static_cast<size_t>(required), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), text.data(), required,
                        nullptr, nullptr);
    return text;
}

std::wstring FormatUnsigned(uint64_t value) {
    wchar_t buffer[32] = {};
    int position = 31;
    if (value == 0) {
        buffer[--position] = L'0';
    }
    while (value > 0 && position > 0) {
        buffer[--position] = static_cast<wchar_t>(L'0' + (value % 10));
        value /= 10;
    }
    return std::wstring(&buffer[position], static_cast<size_t>(31 - position));
}

std::wstring FormatHex32(uint32_t value) {
    static constexpr wchar_t kDigits[] = L"0123456789abcdef";
    std::wstring text(8, L'0');
    for (int shift = 0; shift < 8; ++shift) {
        text[static_cast<size_t>(7 - shift)] = kDigits[(value >> (shift * 4)) & 0x0Fu];
    }
    return L"0x" + text;
}

std::wstring ModuleFilePath() {
    std::wstring path(MAX_PATH, L'\0');
    for (;;) {
        const DWORD written = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
        if (written == 0) {
            return std::wstring();
        }
        if (written < path.size()) {
            path.resize(written);
            return path;
        }
        path.resize(path.size() * 2);
    }
}

std::wstring ModuleDirectory() { return ParentDirectory(ModuleFilePath()); }

std::wstring EnvironmentValue(const wchar_t* name) {
    const DWORD required = GetEnvironmentVariableW(name, nullptr, 0);
    if (required == 0) {
        return std::wstring();
    }

    std::wstring value(required, L'\0');
    const DWORD written = GetEnvironmentVariableW(name, value.data(), required);
    if (written == 0 || written >= required) {
        return std::wstring();
    }
    value.resize(written);
    return value;
}

std::wstring JoinPath(const std::wstring& left, const std::wstring& right) {
    if (left.empty()) {
        return right;
    }
    if (right.empty()) {
        return left;
    }

    std::wstring joined = left;
    if (joined.back() != L'\\' && joined.back() != L'/') {
        joined.push_back(L'\\');
    }
    joined.append(right);
    return joined;
}

std::wstring ExtendedPath(const std::wstring& path) {
    if (path.size() < 4 || path.compare(0, 4, L"\\\\?\\") != 0) {
        if (path.size() >= 2 && path[1] == L':') {
            return L"\\\\?\\" + path;
        }
    }
    return path;
}

std::wstring ParentDirectory(const std::wstring& path) {
    const size_t separator = path.find_last_of(L"\\/");
    if (separator == std::wstring::npos) {
        return std::wstring();
    }
    return path.substr(0, separator);
}

std::wstring FileNameOf(const std::wstring& path) {
    const size_t separator = path.find_last_of(L"\\/");
    if (separator == std::wstring::npos) {
        return path;
    }
    return path.substr(separator + 1);
}

bool PathExists(const std::wstring& path) noexcept {
    return GetFileAttributesW(ExtendedPath(path).c_str()) != INVALID_FILE_ATTRIBUTES;
}

bool DirectoryExists(const std::wstring& path) noexcept {
    const DWORD attributes = GetFileAttributesW(ExtendedPath(path).c_str());
    return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
}

Status EnsureDirectory(const std::wstring& path, uint32_t failureCode) {
    if (CreateDirectoryW(ExtendedPath(path).c_str(), nullptr) != 0) {
        return Success();
    }

    const DWORD error = GetLastError();
    if (error == ERROR_ALREADY_EXISTS && DirectoryExists(path)) {
        return Success();
    }

    return Failure(failureCode, L"CreateDirectory " + path + L": " + FormatSystemError(error));
}

Status EnsureDirectoryTree(const std::wstring& path, uint32_t failureCode) {
    if (path.empty() || DirectoryExists(path)) {
        return Success();
    }

    const std::wstring parent = ParentDirectory(path);
    if (!parent.empty() && parent != path && !DirectoryExists(parent)) {
        const Status parentStatus = EnsureDirectoryTree(parent, failureCode);
        if (!parentStatus.Ok()) {
            return parentStatus;
        }
    }

    return EnsureDirectory(path, failureCode);
}

bool RemoveTree(const std::wstring& path) noexcept {
    const DWORD attributes = GetFileAttributesW(ExtendedPath(path).c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES) {
        return true;
    }

    if ((attributes & FILE_ATTRIBUTE_DIRECTORY) == 0) {
        if ((attributes & FILE_ATTRIBUTE_READONLY) != 0) {
            SetFileAttributesW(ExtendedPath(path).c_str(), FILE_ATTRIBUTE_NORMAL);
        }
        return DeleteFileW(ExtendedPath(path).c_str()) != 0;
    }

    WIN32_FIND_DATAW found = {};
    const ScopedHandle search(FindFirstFileExW(ExtendedPath(JoinPath(path, L"*")).c_str(),
                                               FindExInfoBasic, &found, FindExSearchNameMatch, nullptr,
                                               FIND_FIRST_EX_LARGE_FETCH));
    bool removed = true;
    if (search.Valid()) {
        do {
            const std::wstring name(found.cFileName);
            if (name == L"." || name == L"..") {
                continue;
            }
            removed = RemoveTree(JoinPath(path, name)) && removed;
        } while (FindNextFileW(search.Get(), &found) != 0);
    }

    if ((attributes & FILE_ATTRIBUTE_READONLY) != 0) {
        SetFileAttributesW(ExtendedPath(path).c_str(), FILE_ATTRIBUTE_DIRECTORY);
    }
    return RemoveDirectoryW(ExtendedPath(path).c_str()) != 0 && removed;
}

Status WriteWholeFile(const std::wstring& path, const void* data, size_t bytes, uint32_t failureCode) {
    ScopedHandle file(CreateFileW(ExtendedPath(path).c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr,
                                  CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr));
    if (!file.Valid()) {
        return Failure(failureCode, L"CreateFile " + path + L": " + FormatSystemError(GetLastError()));
    }

    const auto* cursor = static_cast<const unsigned char*>(data);
    size_t remaining = bytes;
    while (remaining > 0) {
        const DWORD slice = static_cast<DWORD>(std::min<size_t>(remaining, kFileChunkBytes));
        DWORD written = 0;
        if (WriteFile(file.Get(), cursor, slice, &written, nullptr) == 0 || written != slice) {
            return Failure(failureCode, L"WriteFile " + path + L": " + FormatSystemError(GetLastError()));
        }
        cursor += written;
        remaining -= written;
    }

    return Success();
}

bool ReadWholeFile(const std::wstring& path, std::vector<uint8_t>& contents) noexcept {
    const ScopedHandle file(CreateFileW(ExtendedPath(path).c_str(), GENERIC_READ,
                                        FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING,
                                        FILE_ATTRIBUTE_NORMAL, nullptr));
    if (!file.Valid()) {
        return false;
    }

    LARGE_INTEGER size = {};
    if (GetFileSizeEx(file.Get(), &size) == 0 || size.QuadPart < 0) {
        return false;
    }

    contents.resize(static_cast<size_t>(size.QuadPart));
    size_t offset = 0;
    while (offset < contents.size()) {
        const DWORD slice = static_cast<DWORD>(std::min<size_t>(contents.size() - offset, kFileChunkBytes));
        DWORD read = 0;
        if (ReadFile(file.Get(), contents.data() + offset, slice, &read, nullptr) == 0 || read == 0) {
            return false;
        }
        offset += read;
    }

    return true;
}

uint64_t MonotonicMilliseconds() noexcept {
    LARGE_INTEGER frequency = {};
    LARGE_INTEGER counter = {};
    if (QueryPerformanceFrequency(&frequency) == 0 || QueryPerformanceCounter(&counter) == 0 ||
        frequency.QuadPart == 0) {
        return GetTickCount64();
    }
    return static_cast<uint64_t>(counter.QuadPart) * 1000ull / static_cast<uint64_t>(frequency.QuadPart);
}

uint64_t ProcessAgeMicroseconds() noexcept {
    FILETIME creation = {};
    FILETIME exited = {};
    FILETIME kernel = {};
    FILETIME user = {};
    if (GetProcessTimes(GetCurrentProcess(), &creation, &exited, &kernel, &user) == 0) {
        return 0;
    }

    FILETIME now = {};
    GetSystemTimePreciseAsFileTime(&now);

    const uint64_t started =
        (static_cast<uint64_t>(creation.dwHighDateTime) << 32) | static_cast<uint64_t>(creation.dwLowDateTime);
    const uint64_t current =
        (static_cast<uint64_t>(now.dwHighDateTime) << 32) | static_cast<uint64_t>(now.dwLowDateTime);
    if (current <= started) {
        return 0;
    }

    return (current - started) / 10ull;
}

std::wstring FormatMilliseconds(uint64_t microseconds) {
    const uint64_t whole = microseconds / 1000ull;
    const uint64_t fraction = microseconds % 1000ull;

    std::wstring digits = FormatUnsigned(fraction);
    while (digits.size() < 3) {
        digits.insert(digits.begin(), L'0');
    }

    return FormatUnsigned(whole) + L"." + digits + L" ms";
}

}
