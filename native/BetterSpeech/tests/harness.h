#pragma once

#include <cmath>
#include <cstdio>
#include <sstream>
#include <string>
#include <vector>

namespace bt {

struct Case {
    const char* name;
    void (*body)();
};

std::vector<Case>& Registry();

struct Registrar {
    Registrar(const char* name, void (*body)()) { Registry().push_back({name, body}); }
};

struct CheckFailed {
    std::string what;
};

[[noreturn]] inline void Fail(const char* file, int line, const std::string& message) {
    std::ostringstream s;
    s << file << ":" << line << ": " << message;
    throw CheckFailed{s.str()};
}

// Where exported files are left for inspection outside the runner; empty when the
// environment does not name a folder.
std::string OutputDir();

std::string TempPath(const std::string& name);

}  // namespace bt

#define TEST(name)                                         \
    static void name();                                    \
    static const bt::Registrar name##_registrar(#name, name); \
    static void name()

#define CHECK(cond)                                                \
    do {                                                           \
        if (!(cond)) {                                             \
            bt::Fail(__FILE__, __LINE__, "CHECK(" #cond ") failed"); \
        }                                                          \
    } while (0)

#define CHECK_EQ(a, b)                                                                              \
    do {                                                                                            \
        const auto check_a_ = (a);                                                                  \
        const auto check_b_ = (b);                                                                  \
        if (!(check_a_ == check_b_)) {                                                              \
            std::ostringstream check_s_;                                                            \
            check_s_ << "CHECK_EQ(" #a ", " #b ") failed: " << check_a_ << " != " << check_b_;      \
            bt::Fail(__FILE__, __LINE__, check_s_.str());                                           \
        }                                                                                           \
    } while (0)

#define CHECK_NEAR(a, b, tolerance)                                                                       \
    do {                                                                                                  \
        const double check_a_ = (a);                                                                      \
        const double check_b_ = (b);                                                                      \
        if (!(std::fabs(check_a_ - check_b_) <= (tolerance))) {                                           \
            std::ostringstream check_s_;                                                                  \
            check_s_ << "CHECK_NEAR(" #a ", " #b ") failed: " << check_a_ << " vs " << check_b_           \
                     << " (tolerance " << (tolerance) << ")";                                            \
            bt::Fail(__FILE__, __LINE__, check_s_.str());                                                 \
        }                                                                                                 \
    } while (0)
