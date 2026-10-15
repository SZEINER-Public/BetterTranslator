# BetterSpeech: the speech engine as one shared library with a flat C ABI.
#
# Outside BetterTranslator.sln on purpose, like the bootstrap: `dotnet build`
# keeps working on a machine with no native toolchain. Visual Studio opens this
# folder as a CMake project; elsewhere:
#
#   cmake -S native/BetterSpeech -B build/speech -DCMAKE_BUILD_TYPE=Release
#   cmake --build build/speech
#   ctest --test-dir build/speech
cmake_minimum_required(VERSION 3.20)

project(BetterSpeech VERSION 0.1.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
    set(CMAKE_BUILD_TYPE Release CACHE STRING "" FORCE)
endif()

option(BS_BUILD_TESTS "Build the test runner" ON)
option(BS_BUILD_BENCH "Build the benchmark" ON)
set(BS_SANITIZE "" CACHE STRING "address, undefined or thread; empty for none")

find_package(Threads REQUIRED)

add_library(better_speech_objects OBJECT
    src/abi.cpp
    src/dsp.cpp
    src/engine.cpp
    src/text_plan.cpp
    src/utf8.cpp
    src/wav.cpp)

target_include_directories(better_speech_objects PUBLIC include src)
target_compile_definitions(better_speech_objects PUBLIC BS_BUILD)
set_target_properties(better_speech_objects PROPERTIES
    POSITION_INDEPENDENT_CODE ON
    CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN ON)

if(MSVC)
    target_compile_options(better_speech_objects PUBLIC /W4 /permissive- /utf-8 /EHsc)
    target_compile_definitions(better_speech_objects PUBLIC _CRT_SECURE_NO_WARNINGS)
else()
    target_compile_options(better_speech_objects PUBLIC -Wall -Wextra -Wpedantic -Wshadow)
endif()

if(BS_SANITIZE)
    target_compile_options(better_speech_objects PUBLIC -fsanitize=${BS_SANITIZE} -fno-omit-frame-pointer -g)
    target_link_options(better_speech_objects PUBLIC -fsanitize=${BS_SANITIZE})
endif()

target_link_libraries(better_speech_objects PUBLIC Threads::Threads)

# Linking the object library carries its objects into each target, so the shared
# library, the tests and the benchmark all build from one compilation.
add_library(BetterSpeech SHARED)
target_link_libraries(BetterSpeech PRIVATE better_speech_objects)
set_target_properties(BetterSpeech PROPERTIES VERSION ${PROJECT_VERSION} SOVERSION 1)

if(BS_BUILD_TESTS)
    enable_testing()
    add_executable(better_speech_tests
        tests/test_main.cpp
        tests/test_text.cpp
        tests/test_dsp.cpp
        tests/test_wav.cpp
        tests/test_job.cpp)
    target_link_libraries(better_speech_tests PRIVATE better_speech_objects)
    add_test(NAME better_speech_tests COMMAND better_speech_tests)
endif()

if(BS_BUILD_BENCH)
    add_executable(better_speech_bench bench/bench_main.cpp)
    target_link_libraries(better_speech_bench PRIVATE better_speech_objects)
endif()
