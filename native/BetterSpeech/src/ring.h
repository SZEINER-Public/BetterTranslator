#pragma once

#include <algorithm>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <vector>

namespace bspeech {

// Single producer, single consumer PCM ring. The storage is allocated once, and
// neither side ever blocks or allocates; the producer decides for itself how to
// wait when the ring is full. Positions only grow, so full and empty never alias.
class PcmRing {
public:
    explicit PcmRing(size_t capacity) : data_(capacity) {}

    PcmRing(const PcmRing&) = delete;
    PcmRing& operator=(const PcmRing&) = delete;

    [[nodiscard]] size_t Capacity() const noexcept { return data_.size(); }

    [[nodiscard]] size_t Buffered() const noexcept {
        return static_cast<size_t>(head_.load(std::memory_order_acquire) - tail_.load(std::memory_order_acquire));
    }

    // Producer side. Returns how many samples fitted.
    size_t TryWrite(const float* in, size_t n) noexcept {
        const uint64_t head = head_.load(std::memory_order_relaxed);
        const uint64_t tail = tail_.load(std::memory_order_acquire);
        const size_t space = data_.size() - static_cast<size_t>(head - tail);
        const size_t take = std::min(n, space);

        Copy(head, in, take);
        head_.store(head + take, std::memory_order_release);
        return take;
    }

    // Consumer side. Returns how many samples were read.
    size_t Read(float* out, size_t n) noexcept {
        const uint64_t tail = tail_.load(std::memory_order_relaxed);
        const uint64_t head = head_.load(std::memory_order_acquire);
        const size_t take = std::min(n, static_cast<size_t>(head - tail));
        const size_t cap = data_.size();

        if (take > 0) {
            const size_t at = static_cast<size_t>(tail % cap);
            const size_t first = std::min(take, cap - at);
            std::memcpy(out, data_.data() + at, first * sizeof(float));
            std::memcpy(out + first, data_.data(), (take - first) * sizeof(float));
        }

        tail_.store(tail + take, std::memory_order_release);
        return take;
    }

    // Consumer side: drops everything buffered.
    void DiscardAll() noexcept { tail_.store(head_.load(std::memory_order_acquire), std::memory_order_release); }

private:
    void Copy(uint64_t head, const float* in, size_t take) noexcept {
        if (take == 0) {
            return;
        }

        const size_t cap = data_.size();
        const size_t at = static_cast<size_t>(head % cap);
        const size_t first = std::min(take, cap - at);
        std::memcpy(data_.data() + at, in, first * sizeof(float));
        std::memcpy(data_.data(), in + first, (take - first) * sizeof(float));
    }

    std::vector<float> data_;
    std::atomic<uint64_t> head_{0};
    std::atomic<uint64_t> tail_{0};
};

}  // namespace bspeech
