# Copyright (C) 2025  Gergo Magyar
# SPDX-License-Identifier: GPL-3.0-or-later
# Standalone unit test build (no TSDuck dependency)

cmake_minimum_required(VERSION 3.16)
project(tsduck_interop_tests VERSION 1.0.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Ensure C++17 for MSVC
if(MSVC)
    add_compile_options(/std:c++17)
endif()

# Fetch GoogleTest
include(FetchContent)
FetchContent_Declare(
    googletest
    GIT_REPOSITORY https://github.com/google/googletest.git
    GIT_TAG        v1.14.0
)

set(gtest_force_shared_crt ON CACHE BOOL "" FORCE)
FetchContent_MakeAvailable(googletest)

enable_testing()
find_package(Threads REQUIRED)

# Source directories
set(SRC_DIR ${CMAKE_CURRENT_SOURCE_DIR}/../src)
set(INCLUDE_DIR ${CMAKE_CURRENT_SOURCE_DIR}/../include)

# Unit tests (no TSDuck dependency)
set(UNIT_TESTS
    test_seqlock
    test_packet_utils
    test_pid_tracker
    test_pcr_analyzer
    test_iat_analyzer
)

foreach(test ${UNIT_TESTS})
    add_executable(${test} unit/${test}.cpp)
    target_include_directories(${test} PRIVATE ${SRC_DIR} ${INCLUDE_DIR})
    target_link_libraries(${test} GTest::gtest_main Threads::Threads)
endforeach()

include(GoogleTest)
foreach(test ${UNIT_TESTS})
    gtest_discover_tests(${test})
endforeach()
