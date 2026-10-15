#include <cstddef>
#include <cstdlib>
#include <new>

#include "alloc_count.h"

namespace bt {

thread_local long allocations = 0;

long AllocationsOnThisThread() { return allocations; }

}

void* operator new(std::size_t n) {
    ++bt::allocations;

    if (void* p = std::malloc(n == 0 ? 1 : n)) {
        return p;
    }

    throw std::bad_alloc();
}

void* operator new[](std::size_t n) { return ::operator new(n); }

void operator delete(void* p) noexcept { std::free(p); }

void operator delete[](void* p) noexcept { std::free(p); }

void operator delete(void* p, std::size_t) noexcept { std::free(p); }

void operator delete[](void* p, std::size_t) noexcept { std::free(p); }
