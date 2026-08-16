#pragma once

#include <cstdint>
#include <string>
#include <vector>

#include "btpay_format.h"
#include "support.h"

namespace btpay {

struct PayloadEntry {
    std::wstring path;
    uint64_t uncompressedSize = 0;
    uint64_t compressedSize = 0;
    uint64_t dataOffset = 0;
    uint32_t firstBlock = 0;
    uint32_t blockCount = 0;
    uint32_t attributes = 0;
    uint8_t contentSha256[32] = {};
};

class Payload {
public:
    Payload() = default;
    Payload(const Payload&) = delete;
    Payload& operator=(const Payload&) = delete;
    Payload(Payload&&) = default;
    Payload& operator=(Payload&&) = default;
    ~Payload() = default;

    [[nodiscard]] Status LocateInCurrentModule();
    [[nodiscard]] Status BindToBuffer(const uint8_t* base, size_t bytes);
    [[nodiscard]] Status ReadTables();
    [[nodiscard]] Status VerifyContainerHash() const;

    [[nodiscard]] const BtPayHeader& Header() const noexcept { return header_; }
    [[nodiscard]] const BtPayFooter& Footer() const noexcept { return footer_; }
    [[nodiscard]] const std::vector<PayloadEntry>& Entries() const noexcept { return entries_; }
    [[nodiscard]] const std::vector<BtPayBlock>& Blocks() const noexcept { return blocks_; }
    [[nodiscard]] const uint8_t* BlockData(const BtPayBlock& block) const noexcept;
    [[nodiscard]] std::wstring HashHex() const;
    [[nodiscard]] std::wstring HashPrefix() const;
    [[nodiscard]] uint64_t ContainerBytes() const noexcept { return containerBytes_; }

private:
    [[nodiscard]] Status ReadHeaderAndFooter();

    const uint8_t* base_ = nullptr;
    size_t bytes_ = 0;
    uint64_t containerBytes_ = 0;
    BtPayHeader header_ = {};
    BtPayFooter footer_ = {};
    std::vector<PayloadEntry> entries_;
    std::vector<BtPayBlock> blocks_;
};

}
