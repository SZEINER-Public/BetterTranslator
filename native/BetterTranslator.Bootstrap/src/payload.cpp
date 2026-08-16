#include "payload.h"

#include <windows.h>

#include <cstring>

namespace btpay {
namespace {

constexpr char kSectionName[IMAGE_SIZEOF_SHORT_NAME] = {'.', 'b', 't', 'p', 'a', 'y', '\0', '\0'};

template <typename T>
bool ReadStruct(const uint8_t* base, size_t available, uint64_t offset, T& value) noexcept {
    if (offset > available || available - static_cast<size_t>(offset) < sizeof(T)) {
        return false;
    }
    std::memcpy(&value, base + offset, sizeof(T));
    return true;
}

}

Status Payload::LocateInCurrentModule() {
    const HMODULE module = GetModuleHandleW(nullptr);
    if (module == nullptr) {
        return Failure(kExitPayloadSectionMissing,
                       L"GetModuleHandle: " + FormatSystemError(GetLastError()));
    }

    const auto* image = static_cast<const uint8_t*>(static_cast<const void*>(module));

    IMAGE_DOS_HEADER dos = {};
    std::memcpy(&dos, image, sizeof(dos));
    if (dos.e_magic != IMAGE_DOS_SIGNATURE || dos.e_lfanew <= 0) {
        return Failure(kExitPayloadSectionMissing, L"the module has no DOS header");
    }

    const auto ntOffset = static_cast<size_t>(dos.e_lfanew);

    DWORD signature = 0;
    std::memcpy(&signature, image + ntOffset, sizeof(signature));
    if (signature != IMAGE_NT_SIGNATURE) {
        return Failure(kExitPayloadSectionMissing, L"the module has no PE signature");
    }

    IMAGE_FILE_HEADER fileHeader = {};
    std::memcpy(&fileHeader, image + ntOffset + sizeof(DWORD), sizeof(fileHeader));

    const size_t sectionTableOffset =
        ntOffset + sizeof(DWORD) + sizeof(IMAGE_FILE_HEADER) + fileHeader.SizeOfOptionalHeader;

    for (WORD index = 0; index < fileHeader.NumberOfSections; ++index) {
        IMAGE_SECTION_HEADER section = {};
        std::memcpy(&section, image + sectionTableOffset + (static_cast<size_t>(index) * sizeof(section)),
                    sizeof(section));

        if (std::memcmp(section.Name, kSectionName, IMAGE_SIZEOF_SHORT_NAME) != 0) {
            continue;
        }

        if (section.Misc.VirtualSize < kHeaderBytes + kFooterBytes) {
            return Failure(kExitPayloadSectionMissing, L"the .btpay section is too small to hold a container");
        }

        return BindToBuffer(image + section.VirtualAddress, section.Misc.VirtualSize);
    }

    return Failure(kExitPayloadSectionMissing, L"the .btpay section is not present in this executable");
}

Status Payload::BindToBuffer(const uint8_t* base, size_t bytes) {
    base_ = base;
    bytes_ = bytes;
    return ReadHeaderAndFooter();
}

Status Payload::ReadHeaderAndFooter() {
    if (base_ == nullptr || bytes_ < static_cast<size_t>(kHeaderBytes) + kFooterBytes) {
        return Failure(kExitPayloadSectionMissing, L"the payload buffer is empty");
    }

    if (!ReadStruct(base_, bytes_, 0, header_)) {
        return Failure(kExitPayloadSectionMissing, L"the payload header could not be read");
    }

    if (header_.magic != kContainerMagic) {
        return Failure(kExitPayloadHashMismatch, L"the payload magic does not match");
    }

    if (header_.formatVersion != kFormatVersion) {
        return Failure(kExitPayloadHashMismatch,
                       L"container format version " + FormatUnsigned(header_.formatVersion) +
                           L" is not supported by this bootstrap");
    }

    if (!ReadStruct(base_, bytes_, header_.footerOffset, footer_)) {
        return Failure(kExitPayloadHashMismatch, L"the payload footer is out of range");
    }

    if (footer_.magic != kFooterMagic) {
        return Failure(kExitPayloadHashMismatch, L"the payload footer magic does not match");
    }

    if (footer_.containerSize != header_.footerOffset + kFooterBytes ||
        footer_.containerSize > bytes_) {
        return Failure(kExitPayloadHashMismatch, L"the payload footer reports an inconsistent size");
    }

    containerBytes_ = footer_.containerSize;
    return Success();
}

Status Payload::VerifyContainerHash() const {
    if (base_ == nullptr || containerBytes_ == 0) {
        return Failure(kExitPayloadHashMismatch, L"no payload is bound");
    }

    uint8_t digest[32] = {};
    if (!ComputeSha256(base_, static_cast<size_t>(header_.footerOffset), digest)) {
        return Failure(kExitPayloadHashMismatch, L"the payload hash could not be computed");
    }

    if (std::memcmp(digest, footer_.payloadSha256, sizeof(digest)) != 0) {
        return Failure(kExitPayloadHashMismatch,
                       L"the payload hash is " + BytesToHex(digest, sizeof(digest)) + L" but the footer records " +
                           BytesToHex(footer_.payloadSha256, sizeof(footer_.payloadSha256)));
    }

    return Success();
}

Status Payload::ReadTables() {
    if (base_ == nullptr) {
        return Failure(kExitPayloadSectionMissing, L"no payload is bound");
    }

    if (header_.blockTableOffset + static_cast<uint64_t>(header_.blockCount) * kBlockBytes >
        containerBytes_) {
        return Failure(kExitPayloadHashMismatch, L"the block table is out of range");
    }

    blocks_.resize(header_.blockCount);
    for (uint32_t index = 0; index < header_.blockCount; ++index) {
        const uint64_t offset = header_.blockTableOffset + (static_cast<uint64_t>(index) * kBlockBytes);
        if (!ReadStruct(base_, static_cast<size_t>(containerBytes_), offset, blocks_[index])) {
            return Failure(kExitPayloadHashMismatch, L"a block record is out of range");
        }

        const BtPayBlock& block = blocks_[index];
        if (header_.blockDataOffset + block.dataOffset + block.compressedSize > containerBytes_) {
            return Failure(kExitPayloadHashMismatch, L"block data is out of range");
        }
    }

    if (header_.entryTableOffset + header_.entryTableBytes > containerBytes_) {
        return Failure(kExitPayloadHashMismatch, L"the entry table is out of range");
    }

    entries_.clear();
    entries_.reserve(header_.entryCount);

    uint64_t cursor = header_.entryTableOffset;
    const uint64_t tableEnd = header_.entryTableOffset + header_.entryTableBytes;

    for (uint32_t index = 0; index < header_.entryCount; ++index) {
        BtPayEntry record = {};
        if (cursor + kEntryFixedBytes > tableEnd ||
            !ReadStruct(base_, static_cast<size_t>(containerBytes_), cursor, record)) {
            return Failure(kExitPayloadHashMismatch, L"an entry record is out of range");
        }

        cursor += kEntryFixedBytes;
        if (record.pathBytes == 0 || record.pathBytes > kMaxPathBytes || cursor + record.pathBytes > tableEnd) {
            return Failure(kExitPayloadHashMismatch, L"an entry path is out of range");
        }

        PayloadEntry entry;
        entry.path = Utf8ToWide(static_cast<const char*>(static_cast<const void*>(base_ + cursor)),
                                record.pathBytes);
        if (entry.path.empty()) {
            return Failure(kExitPayloadHashMismatch, L"an entry path is not valid UTF-8");
        }

        cursor += record.pathBytes;
        const uint64_t padding = (kTableAlignment - (cursor % kTableAlignment)) % kTableAlignment;
        cursor += padding;

        if (static_cast<uint64_t>(record.firstBlock) + record.blockCount > header_.blockCount) {
            return Failure(kExitPayloadHashMismatch, L"an entry references a block outside the table");
        }

        if (record.blockCount > 0 && blocks_[record.firstBlock].dataOffset != record.dataOffset) {
            return Failure(kExitPayloadHashMismatch, L"an entry disagrees with its first block offset");
        }

        if (record.blockCount == 0 && record.uncompressedSize != 0) {
            return Failure(kExitPayloadHashMismatch, L"a non-empty entry has no blocks");
        }

        entry.uncompressedSize = record.uncompressedSize;
        entry.compressedSize = record.compressedSize;
        entry.dataOffset = record.dataOffset;
        entry.firstBlock = record.firstBlock;
        entry.blockCount = record.blockCount;
        entry.attributes = record.attributes & kAttributeMask;
        std::memcpy(entry.contentSha256, record.contentSha256, sizeof(entry.contentSha256));

        if (entry.path.find_first_of(L":\\") != std::wstring::npos || entry.path.front() == L'/') {
            return Failure(kExitPayloadHashMismatch, L"an entry path is not relative: " + entry.path);
        }

        if (entry.path == L".." || entry.path.find(L"../") != std::wstring::npos ||
            entry.path.find(L"/..") != std::wstring::npos) {
            return Failure(kExitPayloadHashMismatch, L"an entry path escapes the payload root: " + entry.path);
        }

        entries_.push_back(std::move(entry));
    }

    return Success();
}

const uint8_t* Payload::BlockData(const BtPayBlock& block) const noexcept {
    return base_ + header_.blockDataOffset + block.dataOffset;
}

std::wstring Payload::HashHex() const {
    return BytesToHex(footer_.payloadSha256, sizeof(footer_.payloadSha256));
}

std::wstring Payload::HashPrefix() const { return HashHex().substr(0, kHashPrefixHexChars); }

}
