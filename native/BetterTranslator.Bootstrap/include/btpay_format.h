#pragma once

#include <cstdint>

namespace btpay {

inline constexpr uint64_t kContainerMagic = 0x0000315941505442ULL;
inline constexpr uint64_t kFooterMagic = 0x444E455941505442ULL;
inline constexpr uint32_t kFormatVersion = 1;

inline constexpr uint32_t kAlgorithmStore = 0;
inline constexpr uint32_t kAlgorithmXpressHuff = 1;
inline constexpr uint32_t kAlgorithmLzms = 2;

inline constexpr uint32_t kBlockFlagStored = 1;

inline constexpr uint32_t kAttributeReadOnly = 0x00000001;
inline constexpr uint32_t kAttributeHidden = 0x00000002;
inline constexpr uint32_t kAttributeSystem = 0x00000004;
inline constexpr uint32_t kAttributeMask = 0x00000007;

inline constexpr uint32_t kDefaultBlockSize = 4194304;
inline constexpr uint32_t kMaxPathBytes = 32768;
inline constexpr uint32_t kTableAlignment = 8;
inline constexpr uint32_t kHashBytes = 32;
inline constexpr uint32_t kHashPrefixHexChars = 16;

inline constexpr uint32_t kHeaderBytes = 96;
inline constexpr uint32_t kEntryFixedBytes = 72;
inline constexpr uint32_t kBlockBytes = 24;
inline constexpr uint32_t kFooterBytes = 48;

inline constexpr uint32_t kExitSuccess = 0;
inline constexpr uint32_t kExitPayloadSectionMissing = 10;
inline constexpr uint32_t kExitPayloadHashMismatch = 11;
inline constexpr uint32_t kExitDecompressionFailed = 12;
inline constexpr uint32_t kExitCacheWriteFailed = 13;
inline constexpr uint32_t kExitHostfxrLoadFailed = 14;
inline constexpr uint32_t kExitRuntimeInitFailed = 15;
inline constexpr uint32_t kExitExtractionTimeout = 16;
inline constexpr uint32_t kExitFlagUsage = 17;
inline constexpr uint32_t kExitSelfTestFailed = 18;

#pragma pack(push, 1)

struct BtPayHeader {
    uint64_t magic;
    uint32_t formatVersion;
    uint32_t algorithm;
    uint32_t entryCount;
    uint32_t blockCount;
    uint32_t blockSize;
    uint32_t flags;
    uint64_t entryTableOffset;
    uint64_t entryTableBytes;
    uint64_t blockTableOffset;
    uint64_t blockDataOffset;
    uint64_t blockDataBytes;
    uint64_t footerOffset;
    uint64_t totalUncompressedSize;
    uint64_t reserved;
};

struct BtPayEntry {
    uint64_t uncompressedSize;
    uint64_t compressedSize;
    uint64_t dataOffset;
    uint32_t firstBlock;
    uint32_t blockCount;
    uint32_t attributes;
    uint32_t pathBytes;
    uint8_t contentSha256[32];
};

struct BtPayBlock {
    uint64_t dataOffset;
    uint32_t compressedSize;
    uint32_t uncompressedSize;
    uint32_t flags;
    uint32_t reserved;
};

struct BtPayFooter {
    uint64_t magic;
    uint8_t payloadSha256[32];
    uint64_t containerSize;
};

#pragma pack(pop)

static_assert(sizeof(BtPayHeader) == kHeaderBytes);
static_assert(sizeof(BtPayEntry) == kEntryFixedBytes);
static_assert(sizeof(BtPayBlock) == kBlockBytes);
static_assert(sizeof(BtPayFooter) == kFooterBytes);

}
