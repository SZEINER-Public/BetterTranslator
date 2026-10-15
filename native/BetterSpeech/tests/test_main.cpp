#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <filesystem>

#include "harness.h"

namespace bt {

std::vector<Case>& Registry() {
    static std::vector<Case> cases;
    return cases;
}

std::string OutputDir() {
    const char* dir = std::getenv("BS_TEST_OUT");
    return dir != nullptr ? dir : "";
}

std::string TempPath(const std::string& name) {
    const auto dir = std::filesystem::temp_directory_path() / "better_speech_tests";
    std::filesystem::create_directories(dir);
    return (dir / name).string();
}

}  // namespace bt

int main(int argc, char** argv) {
    // Line-buffered, so a sanitizer that ends the process at exit cannot swallow
    // the results it is reporting on.
    std::setvbuf(stdout, nullptr, _IOLBF, 0);

    const char* filter = argc > 1 ? argv[1] : nullptr;
    int passed = 0;
    int failed = 0;

    for (const bt::Case& c : bt::Registry()) {
        if (filter != nullptr && std::strstr(c.name, filter) == nullptr) {
            continue;
        }

        const auto start = std::chrono::steady_clock::now();

        try {
            c.body();
            ++passed;
            const auto ms =
                std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - start);
            std::printf("  pass  %-56s %6lld ms\n", c.name, static_cast<long long>(ms.count()));
        } catch (const bt::CheckFailed& f) {
            ++failed;
            std::printf("  FAIL  %s\n        %s\n", c.name, f.what.c_str());
        } catch (const std::exception& e) {
            ++failed;
            std::printf("  FAIL  %s\n        exception: %s\n", c.name, e.what());
        }
    }

    std::printf("\n%d passed, %d failed\n", passed, failed);
    return failed == 0 ? 0 : 1;
}
