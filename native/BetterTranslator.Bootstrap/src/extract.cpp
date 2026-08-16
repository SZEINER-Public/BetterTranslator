#include "extract.h"

#include <windows.h>

#include <algorithm>
#include <cstring>
#include <functional>
#include <mutex>
#include <set>
#include <thread>
#include <vector>

#include "compression.h"
#include "logging.h"

namespace btpay {
namespace {

constexpr uint32_t kMaximumWorkers = 8;

void DiscardTree(const std::wstring& path) {
    if (!RemoveTree(path)) {
        Log::Write(L"a directory could not be removed: " + path);
    }
}

std::wstring ToWindowsRelativePath(const std::wstring& portablePath) {
    std::wstring converted = portablePath;
    std::replace(converted.begin(), converted.end(), L'/', L'\\');
    return converted;
}

uint32_t WorkerCount(size_t entryCount) noexcept {
    const unsigned int cores = std::thread::hardware_concurrency();
    const uint32_t available = cores == 0 ? 1u : cores;
    const uint32_t capped = std::min(available, kMaximumWorkers);
    return std::max(1u, std::min<uint32_t>(capped, static_cast<uint32_t>(entryCount)));
}

Status CreateEntryDirectories(const Payload& payload, const std::wstring& destination) {
    std::set<std::wstring> directories;
    for (const PayloadEntry& entry : payload.Entries()) {
        const std::wstring relative = ToWindowsRelativePath(entry.path);
        const std::wstring parent = ParentDirectory(relative);
        if (!parent.empty()) {
            directories.insert(parent);
        }
    }

    for (const std::wstring& relative : directories) {
        const Status created = EnsureDirectoryTree(JoinPath(destination, relative), kExitCacheWriteFailed);
        if (!created.Ok()) {
            return created;
        }
    }

    return Success();
}

Status ExpandEntry(const Payload& payload, const Decompressor& decompressor, const PayloadEntry& entry,
                   std::vector<uint8_t>& scratch) {
    scratch.resize(static_cast<size_t>(entry.uncompressedSize));

    size_t offset = 0;
    for (uint32_t index = 0; index < entry.blockCount; ++index) {
        const BtPayBlock& block = payload.Blocks()[entry.firstBlock + index];
        if (offset + block.uncompressedSize > scratch.size()) {
            return Failure(kExitDecompressionFailed, L"block sizes overflow entry " + entry.path);
        }

        const uint8_t* source = payload.BlockData(block);
        uint8_t* target = scratch.data() + offset;

        if ((block.flags & kBlockFlagStored) != 0) {
            if (block.compressedSize != block.uncompressedSize) {
                return Failure(kExitDecompressionFailed, L"a stored block has a mismatched size in " + entry.path);
            }
            std::memcpy(target, source, block.uncompressedSize);
        } else {
            const Status expanded =
                decompressor.Expand(source, block.compressedSize, target, block.uncompressedSize);
            if (!expanded.Ok()) {
                return Failure(expanded.code, expanded.detail + L" in " + entry.path);
            }
        }

        offset += block.uncompressedSize;
    }

    if (offset != scratch.size()) {
        return Failure(kExitDecompressionFailed, L"entry " + entry.path + L" expanded to the wrong size");
    }

    uint8_t digest[32] = {};
    if (!ComputeSha256(scratch.data(), scratch.size(), digest)) {
        return Failure(kExitDecompressionFailed, L"the content hash could not be computed for " + entry.path);
    }

    if (std::memcmp(digest, entry.contentSha256, sizeof(digest)) != 0) {
        return Failure(kExitPayloadHashMismatch, L"entry " + entry.path + L" failed its content hash");
    }

    return Success();
}

Status RunWorkers(const Payload& payload, uint32_t workerCount,
                  const std::function<Status(const Decompressor&, const PayloadEntry&,
                                             std::vector<uint8_t>&)>& body) {
    std::atomic<size_t> cursor{0};
    std::mutex failureGuard;
    Status failure;

    const auto worker = [&]() {
        Decompressor decompressor(payload.Header().algorithm);
        if (!decompressor.Ready().Ok()) {
            const std::lock_guard<std::mutex> guard(failureGuard);
            if (failure.Ok()) {
                failure = decompressor.Ready();
            }
            return;
        }

        std::vector<uint8_t> scratch;
        for (;;) {
            const size_t index = cursor.fetch_add(1);
            if (index >= payload.Entries().size()) {
                return;
            }

            {
                const std::lock_guard<std::mutex> guard(failureGuard);
                if (!failure.Ok()) {
                    return;
                }
            }

            const Status result = body(decompressor, payload.Entries()[index], scratch);
            if (!result.Ok()) {
                const std::lock_guard<std::mutex> guard(failureGuard);
                if (failure.Ok()) {
                    failure = result;
                }
                return;
            }
        }
    };

    std::vector<std::thread> threads;
    threads.reserve(workerCount);
    for (uint32_t index = 0; index < workerCount; ++index) {
        threads.emplace_back(worker);
    }
    for (std::thread& thread : threads) {
        thread.join();
    }

    return failure;
}

}

Status ExtractTo(const Payload& payload, const std::wstring& destination, ExtractionProgress& progress) {
    const Status root = EnsureDirectoryTree(destination, kExitCacheWriteFailed);
    if (!root.Ok()) {
        return root;
    }

    const Status directories = CreateEntryDirectories(payload, destination);
    if (!directories.Ok()) {
        return directories;
    }

    progress.totalFiles = static_cast<uint32_t>(payload.Entries().size());
    progress.totalBytes = payload.Header().totalUncompressedSize;

    return RunWorkers(payload, WorkerCount(payload.Entries().size()),
                      [&](const Decompressor& decompressor, const PayloadEntry& entry,
                          std::vector<uint8_t>& scratch) -> Status {
                          const Status expanded = ExpandEntry(payload, decompressor, entry, scratch);
                          if (!expanded.Ok()) {
                              return expanded;
                          }

                          const std::wstring target =
                              JoinPath(destination, ToWindowsRelativePath(entry.path));
                          const Status written =
                              WriteWholeFile(target, scratch.data(), scratch.size(), kExitCacheWriteFailed);
                          if (!written.Ok()) {
                              return written;
                          }

                          if (entry.attributes != 0) {
                              SetFileAttributesW(ExtendedPath(target).c_str(), entry.attributes);
                          }

                          progress.bytesWritten.fetch_add(entry.uncompressedSize);
                          progress.filesWritten.fetch_add(1);
                          return Success();
                      });
}

Status VerifyEntries(const Payload& payload, uint32_t& verifiedCount) {
    std::atomic<uint32_t> verified{0};

    const Status result =
        RunWorkers(payload, WorkerCount(payload.Entries().size()),
                   [&](const Decompressor& decompressor, const PayloadEntry& entry,
                       std::vector<uint8_t>& scratch) -> Status {
                       const Status expanded = ExpandEntry(payload, decompressor, entry, scratch);
                       if (expanded.Ok()) {
                           verified.fetch_add(1);
                       }
                       return expanded;
                   });

    verifiedCount = verified.load();
    return result;
}

Status MaterializeCache(const Payload& payload, CacheLayout& layout, ExtractionProgress& progress) {
    const Status runtimeRoot = EnsureDirectoryTree(layout.runtimeRoot, kExitCacheWriteFailed);
    if (!runtimeRoot.Ok()) {
        return runtimeRoot;
    }

    if (PathExists(layout.staging) && !RemoveTree(layout.staging)) {
        return Failure(kExitCacheWriteFailed, L"a stale staging directory could not be removed: " + layout.staging);
    }

    const Status extracted = ExtractTo(payload, layout.staging, progress);
    if (!extracted.Ok()) {
        DiscardTree(layout.staging);
        return extracted;
    }

    const std::wstring hashHex = payload.HashHex();
    const Status stagedMarker = WriteMarker(JoinPath(layout.staging, FileNameOf(layout.marker)), hashHex);
    if (!stagedMarker.Ok()) {
        DiscardTree(layout.staging);
        return stagedMarker;
    }

    if (DirectoryExists(layout.active) && !MarkerMatches(layout.marker, hashHex)) {
        Log::Write(L"reclaiming an incomplete runtime directory at " + layout.active);
        if (!RemoveTree(layout.active)) {
            DiscardTree(layout.staging);
            return Failure(kExitCacheWriteFailed,
                           L"an incomplete runtime directory could not be removed: " + layout.active);
        }
    }

    if (MoveFileExW(ExtendedPath(layout.staging).c_str(), ExtendedPath(layout.active).c_str(), 0) == 0) {
        const DWORD error = GetLastError();
        const bool alreadyThere = MarkerMatches(layout.marker, hashHex);
        DiscardTree(layout.staging);
        if (alreadyThere) {
            Log::Write(L"another instance promoted the runtime first, using it");
            return Success();
        }
        return Failure(kExitCacheWriteFailed,
                       L"MoveFileEx " + layout.staging + L" -> " + layout.active + L": " +
                           FormatSystemError(error));
    }

    return WriteMarker(layout.marker, hashHex);
}

}
