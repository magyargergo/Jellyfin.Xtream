# Copyright (C) 2025  Gergo Magyar
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Test build for TsDuck interop library
#
# Standalone build (local development):
#   cd tests && cmake -B build && cmake --build build
#
# Docker build (with interop library):
#   cmake -B build -DDOCKER_BUILD=ON -DCMAKE_PREFIX_PATH=/opt/tsduck

cmake_minimum_required(VERSION 3.16)
project(tsduck_interop_tests VERSION 1.0.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Option for Docker builds (enables additional integration tests)
option(DOCKER_BUILD "Build with pre-built interop library" OFF)

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

# Find TsDuck (required for ts::PKT_SIZE, ts::SYNC_BYTE, etc. via constants.hpp)
find_package(PkgConfig REQUIRED)
pkg_check_modules(TSDUCK REQUIRED tsduck)

# Source directories
set(SRC_DIR ${CMAKE_CURRENT_SOURCE_DIR}/../src)
set(INCLUDE_DIR ${CMAKE_CURRENT_SOURCE_DIR}/../include)

# Find interop library for Docker builds
if(DOCKER_BUILD)
    find_library(TSDUCK_INTEROP_LIB tsduck_interop PATHS /install/lib REQUIRED)
    message(STATUS "Docker build: Using pre-built interop library at ${TSDUCK_INTEROP_LIB}")
endif()

# ============================================================================
# Unit tests
# ============================================================================

set(UNIT_TESTS
    test_seqlock
    test_pid_tracker
    test_pcr_analyzer
    test_iat_analyzer
    test_alignment_buffer
    test_keyframe_aligner
    test_failover_manager
    test_quality_switch_trigger
    test_duckcontext
    test_psi_monitor
    test_scte35_monitor
    test_nal_parser
    test_tr101290
)

foreach(test ${UNIT_TESTS})
    add_executable(${test} unit/${test}.cpp)
    target_include_directories(${test} PRIVATE
        ${SRC_DIR} ${INCLUDE_DIR} ${TSDUCK_INCLUDE_DIRS})
    target_link_libraries(${test}
        GTest::gtest_main Threads::Threads ${TSDUCK_LIBRARIES})
    target_link_directories(${test} PRIVATE ${TSDUCK_LIBRARY_DIRS})
endforeach()

# Shared memory channel test (needs implementation file and platform libs)
add_executable(test_shared_memory_channel
    unit/test_shared_memory_channel.cpp
    ${SRC_DIR}/ipc/shared_memory_channel.cpp
            ${SRC_DIR}/registry/channel_registry.cpp
            ${SRC_DIR}/registry/channel_normalizer.cpp
)
target_include_directories(test_shared_memory_channel PRIVATE
    ${SRC_DIR} ${INCLUDE_DIR} ${TSDUCK_INCLUDE_DIRS})
target_link_libraries(test_shared_memory_channel
    GTest::gtest_main Threads::Threads ${TSDUCK_LIBRARIES})
target_link_directories(test_shared_memory_channel PRIVATE ${TSDUCK_LIBRARY_DIRS})
if(UNIX AND NOT APPLE)
    target_link_libraries(test_shared_memory_channel rt)  # For shm_open/sem_open
endif()

# Add shared memory to unit tests list for test discovery
list(APPEND UNIT_TESTS test_shared_memory_channel)

include(GoogleTest)
foreach(test ${UNIT_TESTS})
    gtest_discover_tests(${test}
        PROPERTIES LABELS "unit"
        XML_OUTPUT_DIR ${CMAKE_BINARY_DIR}/test-results
    )
endforeach()

# ============================================================================
# Integration tests (require libcurl)
# ============================================================================

find_package(CURL QUIET)
if(CURL_FOUND)
    # Docker-specific integration tests (require pre-built interop library)
    if(DOCKER_BUILD)
        set(DOCKER_INTEGRATION_TESTS
            test_analyzer_integration
            test_restamper_integration
        )

        foreach(test ${DOCKER_INTEGRATION_TESTS})
            add_executable(${test} integration/${test}.cpp)
            target_include_directories(${test} PRIVATE
                ${SRC_DIR}
                ${INCLUDE_DIR}
                ${TSDUCK_INCLUDE_DIRS}
                /install/include
            )
            target_link_libraries(${test}
                GTest::gtest_main
                Threads::Threads
                ${TSDUCK_INTEROP_LIB}
                ${TSDUCK_LIBRARIES}
            )
            target_link_directories(${test} PRIVATE ${TSDUCK_LIBRARY_DIRS} /install/lib)
            target_compile_options(${test} PRIVATE ${TSDUCK_CFLAGS_OTHER})
        endforeach()

        foreach(test ${DOCKER_INTEGRATION_TESTS})
            gtest_discover_tests(${test}
                PROPERTIES LABELS "integration"
                XML_OUTPUT_DIR ${CMAKE_BINARY_DIR}/test-results
            )
        endforeach()

        message(STATUS "Docker integration tests (analyzer, restamper): ENABLED")
    endif()

    # Streamer integration test (compiles sources directly due to hidden visibility)
    add_executable(test_streamer_integration
        integration/test_streamer_integration.cpp
        ${SRC_DIR}/tsduck_interop.cpp
        ${SRC_DIR}/streaming/stream_source.cpp
        ${SRC_DIR}/streaming/stream_pipeline.cpp
        ${SRC_DIR}/ipc/shared_memory_channel.cpp
            ${SRC_DIR}/registry/channel_registry.cpp
            ${SRC_DIR}/registry/channel_normalizer.cpp
    )
    target_include_directories(test_streamer_integration PRIVATE
        ${SRC_DIR}
        ${INCLUDE_DIR}
        ${CURL_INCLUDE_DIRS}
        ${TSDUCK_INCLUDE_DIRS}
    )
    target_link_libraries(test_streamer_integration
        GTest::gtest_main
        Threads::Threads
        CURL::libcurl
        ${TSDUCK_LIBRARIES}
    )
    target_link_directories(test_streamer_integration PRIVATE ${TSDUCK_LIBRARY_DIRS})
    target_compile_options(test_streamer_integration PRIVATE ${TSDUCK_CFLAGS_OTHER})
    if(UNIX AND NOT APPLE)
        target_link_libraries(test_streamer_integration rt)  # For shm_open/sem_open
    endif()

    gtest_discover_tests(test_streamer_integration
        PROPERTIES LABELS "integration"
        XML_OUTPUT_DIR ${CMAKE_BINARY_DIR}/test-results
    )

    message(STATUS "Streamer integration tests: ENABLED (libcurl found)")

    # ========================================================================
    # Stress tests
    # ========================================================================

    set(STRESS_TESTS
        test_stress
    )

    foreach(test ${STRESS_TESTS})
        add_executable(${test} stress/${test}.cpp)
        target_include_directories(${test} PRIVATE
            ${SRC_DIR} ${INCLUDE_DIR} ${TSDUCK_INCLUDE_DIRS})
        target_link_libraries(${test}
            GTest::gtest_main Threads::Threads ${TSDUCK_LIBRARIES})
        target_link_directories(${test} PRIVATE ${TSDUCK_LIBRARY_DIRS})
    endforeach()

    foreach(test ${STRESS_TESTS})
        gtest_discover_tests(${test}
            PROPERTIES LABELS "stress"
            XML_OUTPUT_DIR ${CMAKE_BINARY_DIR}/test-results
        )
    endforeach()

    message(STATUS "Stress tests: ENABLED")

    # ========================================================================
    # Network simulation tests (require CURL)
    # ========================================================================

    set(NETWORK_TESTS
        test_network_simulation
    )

    foreach(test ${NETWORK_TESTS})
        add_executable(${test} network/${test}.cpp
            ${SRC_DIR}/tsduck_interop.cpp
            ${SRC_DIR}/streaming/stream_source.cpp
            ${SRC_DIR}/streaming/stream_pipeline.cpp
            ${SRC_DIR}/ipc/shared_memory_channel.cpp
            ${SRC_DIR}/registry/channel_registry.cpp
            ${SRC_DIR}/registry/channel_normalizer.cpp
        )
        target_include_directories(${test} PRIVATE
            ${SRC_DIR} ${INCLUDE_DIR} ${CURL_INCLUDE_DIRS} ${TSDUCK_INCLUDE_DIRS}
            ${CMAKE_CURRENT_SOURCE_DIR}/network)
        target_link_libraries(${test}
            GTest::gtest_main Threads::Threads CURL::libcurl ${TSDUCK_LIBRARIES})
        target_link_directories(${test} PRIVATE ${TSDUCK_LIBRARY_DIRS})
        if(WIN32)
            target_link_libraries(${test} ws2_32)
        elseif(UNIX AND NOT APPLE)
            target_link_libraries(${test} rt)  # For shm_open/sem_open
        endif()
    endforeach()

    foreach(test ${NETWORK_TESTS})
        gtest_discover_tests(${test}
            PROPERTIES LABELS "network"
            XML_OUTPUT_DIR ${CMAKE_BINARY_DIR}/test-results
        )
    endforeach()

    message(STATUS "Network simulation tests: ENABLED")
else()
    message(STATUS "Streamer integration tests: DISABLED (libcurl not found)")
    message(STATUS "Stress tests: DISABLED (libcurl not found)")
    message(STATUS "Network simulation tests: DISABLED (libcurl not found)")
endif()

# ============================================================================
# Fuzz tests (optional - require clang with libfuzzer)
# ============================================================================

option(BUILD_FUZZ_TESTS "Build fuzz testing harnesses" OFF)

if(BUILD_FUZZ_TESTS)
    if(CMAKE_CXX_COMPILER_ID STREQUAL "Clang")
        message(STATUS "Fuzz tests: ENABLED (Clang detected)")

        # Alignment buffer fuzzer
        add_executable(fuzz_alignment_buffer fuzz/fuzz_alignment_buffer.cpp)
        target_include_directories(fuzz_alignment_buffer PRIVATE
            ${SRC_DIR} ${INCLUDE_DIR} ${TSDUCK_INCLUDE_DIRS})
        target_compile_options(fuzz_alignment_buffer PRIVATE
            -fsanitize=fuzzer,address,undefined -g -O1)
        target_link_options(fuzz_alignment_buffer PRIVATE
            -fsanitize=fuzzer,address,undefined)
        target_link_libraries(fuzz_alignment_buffer ${TSDUCK_LIBRARIES})
        target_link_directories(fuzz_alignment_buffer PRIVATE ${TSDUCK_LIBRARY_DIRS})

        # PID tracker fuzzer
        add_executable(fuzz_pid_tracker fuzz/fuzz_pid_tracker.cpp)
        target_include_directories(fuzz_pid_tracker PRIVATE
            ${SRC_DIR} ${INCLUDE_DIR} ${TSDUCK_INCLUDE_DIRS})
        target_compile_options(fuzz_pid_tracker PRIVATE
            -fsanitize=fuzzer,address,undefined -g -O1)
        target_link_options(fuzz_pid_tracker PRIVATE
            -fsanitize=fuzzer,address,undefined)
        target_link_libraries(fuzz_pid_tracker ${TSDUCK_LIBRARIES})
        target_link_directories(fuzz_pid_tracker PRIVATE ${TSDUCK_LIBRARY_DIRS})

        # PSI monitor fuzzer
        add_executable(fuzz_psi_monitor fuzz/fuzz_psi_monitor.cpp)
        target_include_directories(fuzz_psi_monitor PRIVATE
            ${SRC_DIR} ${INCLUDE_DIR} ${TSDUCK_INCLUDE_DIRS})
        target_compile_options(fuzz_psi_monitor PRIVATE
            -fsanitize=fuzzer,address,undefined -g -O1)
        target_link_options(fuzz_psi_monitor PRIVATE
            -fsanitize=fuzzer,address,undefined)
        target_link_libraries(fuzz_psi_monitor ${TSDUCK_LIBRARIES})
        target_link_directories(fuzz_psi_monitor PRIVATE ${TSDUCK_LIBRARY_DIRS})

        # Keyframe aligner fuzzer
        add_executable(fuzz_keyframe_aligner fuzz/fuzz_keyframe_aligner.cpp)
        target_include_directories(fuzz_keyframe_aligner PRIVATE
            ${SRC_DIR} ${INCLUDE_DIR} ${TSDUCK_INCLUDE_DIRS})
        target_compile_options(fuzz_keyframe_aligner PRIVATE
            -fsanitize=fuzzer,address,undefined -g -O1)
        target_link_options(fuzz_keyframe_aligner PRIVATE
            -fsanitize=fuzzer,address,undefined)
        target_link_libraries(fuzz_keyframe_aligner ${TSDUCK_LIBRARIES})
        target_link_directories(fuzz_keyframe_aligner PRIVATE ${TSDUCK_LIBRARY_DIRS})

        # SCTE-35 monitor fuzzer
        add_executable(fuzz_scte35_monitor fuzz/fuzz_scte35_monitor.cpp)
        target_include_directories(fuzz_scte35_monitor PRIVATE
            ${SRC_DIR} ${INCLUDE_DIR} ${TSDUCK_INCLUDE_DIRS})
        target_compile_options(fuzz_scte35_monitor PRIVATE
            -fsanitize=fuzzer,address,undefined -g -O1)
        target_link_options(fuzz_scte35_monitor PRIVATE
            -fsanitize=fuzzer,address,undefined)
        target_link_libraries(fuzz_scte35_monitor ${TSDUCK_LIBRARIES})
        target_link_directories(fuzz_scte35_monitor PRIVATE ${TSDUCK_LIBRARY_DIRS})

        # NAL parser fuzzer
        add_executable(fuzz_nal_parser fuzz/fuzz_nal_parser.cpp)
        target_include_directories(fuzz_nal_parser PRIVATE
            ${SRC_DIR} ${INCLUDE_DIR} ${TSDUCK_INCLUDE_DIRS})
        target_compile_options(fuzz_nal_parser PRIVATE
            -fsanitize=fuzzer,address,undefined -g -O1)
        target_link_options(fuzz_nal_parser PRIVATE
            -fsanitize=fuzzer,address,undefined)
        target_link_libraries(fuzz_nal_parser ${TSDUCK_LIBRARIES})
        target_link_directories(fuzz_nal_parser PRIVATE ${TSDUCK_LIBRARY_DIRS})
    else()
        message(STATUS "Fuzz tests: DISABLED (requires Clang)")
    endif()
else()
    message(STATUS "Fuzz tests: DISABLED (set -DBUILD_FUZZ_TESTS=ON to enable)")
endif()
# Build timestamp: 1769634906
