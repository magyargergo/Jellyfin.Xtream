# Copyright (C) 2025  Gergo Magyar
#
# This program is free software: you can redistribute it and/or modify
# it under the terms of the GNU General Public License as published by
# the Free Software Foundation, either version 3 of the License, or
# (at your option) any later version.

cmake_minimum_required(VERSION 3.16)
project(tsduck_interop VERSION 1.0.0 LANGUAGES CXX)

# C++20 required for TsDuck v3.43+ headers
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

# Generate compile_commands.json for static analysis tools (clang-tidy, cppcheck)
set(CMAKE_EXPORT_COMPILE_COMMANDS ON)

# Build type defaults
if(NOT CMAKE_BUILD_TYPE)
    set(CMAKE_BUILD_TYPE Release)
endif()

# ============================================================================
# CPU Architecture Detection
# ============================================================================
include(CheckCXXCompilerFlag)

# Detect x86/x64 vs ARM architecture
if(CMAKE_SYSTEM_PROCESSOR MATCHES "^(x86_64|AMD64|amd64|i686|i386)$")
    set(TSDUCK_ARCH_X86 ON)
    set(TSDUCK_ARCH_ARM OFF)
elseif(CMAKE_SYSTEM_PROCESSOR MATCHES "^(aarch64|arm64|ARM64|armv8)$")
    set(TSDUCK_ARCH_ARM ON)
    set(TSDUCK_ARCH_X86 OFF)
elseif(CMAKE_SYSTEM_PROCESSOR MATCHES "^(armv7|arm)$")
    set(TSDUCK_ARCH_ARM ON)
    set(TSDUCK_ARCH_X86 OFF)
else()
    set(TSDUCK_ARCH_X86 OFF)
    set(TSDUCK_ARCH_ARM OFF)
endif()

# Option to enable native optimizations (best for local builds, not portable)
option(ENABLE_NATIVE_OPTIMIZATIONS "Enable -march=native for maximum local performance" OFF)

# Option to enable Link-Time Optimization
option(ENABLE_LTO "Enable Link-Time Optimization" ON)

# Option to enable specific SIMD instruction sets
option(ENABLE_AVX2 "Enable AVX2 instructions (x86_64)" ON)
option(ENABLE_SSE42 "Enable SSE4.2 instructions (x86)" ON)
option(ENABLE_NEON "Enable NEON instructions (ARM)" ON)

# ============================================================================
# SIMD Feature Detection
# ============================================================================
set(TSDUCK_SIMD_FLAGS "")

if(TSDUCK_ARCH_X86)
    # Check for AVX2 support
    if(ENABLE_AVX2)
        if(CMAKE_CXX_COMPILER_ID MATCHES "GNU|Clang")
            check_cxx_compiler_flag("-mavx2" COMPILER_SUPPORTS_AVX2)
            if(COMPILER_SUPPORTS_AVX2)
                list(APPEND TSDUCK_SIMD_FLAGS "-mavx2")
                set(TSDUCK_HAS_AVX2 ON)
            endif()
        elseif(CMAKE_CXX_COMPILER_ID STREQUAL "MSVC")
            # MSVC always supports /arch:AVX2 on x64
            list(APPEND TSDUCK_SIMD_FLAGS "/arch:AVX2")
            set(TSDUCK_HAS_AVX2 ON)
        endif()
    endif()

    # Check for SSE4.2 support
    if(ENABLE_SSE42)
        if(CMAKE_CXX_COMPILER_ID MATCHES "GNU|Clang")
            check_cxx_compiler_flag("-msse4.2" COMPILER_SUPPORTS_SSE42)
            if(COMPILER_SUPPORTS_SSE42)
                list(APPEND TSDUCK_SIMD_FLAGS "-msse4.2")
                set(TSDUCK_HAS_SSE42 ON)
            endif()
        elseif(CMAKE_CXX_COMPILER_ID STREQUAL "MSVC")
            # SSE4.2 is baseline on x64 MSVC
            set(TSDUCK_HAS_SSE42 ON)
        endif()
    endif()

    # Check for BMI2 (bit manipulation) - useful for ctz/popcount
    if(CMAKE_CXX_COMPILER_ID MATCHES "GNU|Clang")
        check_cxx_compiler_flag("-mbmi2" COMPILER_SUPPORTS_BMI2)
        if(COMPILER_SUPPORTS_BMI2)
            list(APPEND TSDUCK_SIMD_FLAGS "-mbmi2")
        endif()
        # POPCNT instruction
        check_cxx_compiler_flag("-mpopcnt" COMPILER_SUPPORTS_POPCNT)
        if(COMPILER_SUPPORTS_POPCNT)
            list(APPEND TSDUCK_SIMD_FLAGS "-mpopcnt")
        endif()
    endif()
endif()

if(TSDUCK_ARCH_ARM)
    if(ENABLE_NEON)
        if(CMAKE_CXX_COMPILER_ID MATCHES "GNU|Clang")
            # ARM64 has NEON by default, ARM32 needs -mfpu=neon
            if(CMAKE_SYSTEM_PROCESSOR MATCHES "^(aarch64|arm64|ARM64)$")
                set(TSDUCK_HAS_NEON ON)
            else()
                check_cxx_compiler_flag("-mfpu=neon" COMPILER_SUPPORTS_NEON)
                if(COMPILER_SUPPORTS_NEON)
                    list(APPEND TSDUCK_SIMD_FLAGS "-mfpu=neon")
                    set(TSDUCK_HAS_NEON ON)
                endif()
            endif()
        endif()
    endif()
endif()

# ============================================================================
# Compiler Optimization Flags
# ============================================================================
if(CMAKE_CXX_COMPILER_ID MATCHES "GNU|Clang")
    # Base flags for all builds
    set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} -Wall -Wextra -Wpedantic")

    # Release optimizations
    set(RELEASE_FLAGS "-O3")

    # Auto-vectorization (enabled by -O3, but explicitly list for clarity)
    string(APPEND RELEASE_FLAGS " -ftree-vectorize")

    # Loop optimizations
    string(APPEND RELEASE_FLAGS " -ftree-loop-vectorize")
    string(APPEND RELEASE_FLAGS " -fvect-cost-model=dynamic")

    # Fast math for floating point (safe for media processing)
    # Enables vectorization of reductions and other FP-heavy code
    string(APPEND RELEASE_FLAGS " -ffast-math")

    # Function sections for dead code elimination
    string(APPEND RELEASE_FLAGS " -ffunction-sections -fdata-sections")

    # Inline aggressively
    string(APPEND RELEASE_FLAGS " -finline-functions")

    # Unroll loops for better SIMD utilization
    string(APPEND RELEASE_FLAGS " -funroll-loops")

    # Native optimization (optional - not portable)
    if(ENABLE_NATIVE_OPTIMIZATIONS)
        string(APPEND RELEASE_FLAGS " -march=native -mtune=native")
    else()
        # Baseline x86-64-v2 for modern servers (SSE4.2, POPCNT, etc.)
        if(TSDUCK_ARCH_X86 AND CMAKE_SIZEOF_VOID_P EQUAL 8)
            check_cxx_compiler_flag("-march=x86-64-v2" COMPILER_SUPPORTS_X64_V2)
            if(COMPILER_SUPPORTS_X64_V2)
                string(APPEND RELEASE_FLAGS " -march=x86-64-v2")
            endif()
        endif()
    endif()

    # Add SIMD flags
    foreach(flag ${TSDUCK_SIMD_FLAGS})
        string(APPEND RELEASE_FLAGS " ${flag}")
    endforeach()

    set(CMAKE_CXX_FLAGS_RELEASE "${RELEASE_FLAGS}")
    set(CMAKE_CXX_FLAGS_DEBUG "-g -O0")

    # Symbol visibility for shared library
    set(CMAKE_CXX_VISIBILITY_PRESET hidden)
    set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)

    # Link-Time Optimization
    if(ENABLE_LTO)
        include(CheckIPOSupported)
        check_ipo_supported(RESULT LTO_SUPPORTED OUTPUT LTO_ERROR)
        if(LTO_SUPPORTED)
            set(CMAKE_INTERPROCEDURAL_OPTIMIZATION_RELEASE ON)
            message(STATUS "LTO enabled for Release builds")
        else()
            message(STATUS "LTO not supported: ${LTO_ERROR}")
        endif()
    endif()

    # Linker optimizations
    if(CMAKE_CXX_COMPILER_ID STREQUAL "GNU")
        set(CMAKE_EXE_LINKER_FLAGS_RELEASE "${CMAKE_EXE_LINKER_FLAGS_RELEASE} -Wl,--gc-sections")
        set(CMAKE_SHARED_LINKER_FLAGS_RELEASE "${CMAKE_SHARED_LINKER_FLAGS_RELEASE} -Wl,--gc-sections")
    elseif(CMAKE_CXX_COMPILER_ID STREQUAL "Clang")
        set(CMAKE_EXE_LINKER_FLAGS_RELEASE "${CMAKE_EXE_LINKER_FLAGS_RELEASE} -Wl,--gc-sections")
        set(CMAKE_SHARED_LINKER_FLAGS_RELEASE "${CMAKE_SHARED_LINKER_FLAGS_RELEASE} -Wl,--gc-sections")
    endif()

elseif(CMAKE_CXX_COMPILER_ID STREQUAL "MSVC")
    # MSVC optimizations
    set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} /W4")

    # Release optimizations
    set(RELEASE_FLAGS "/O2")          # Maximum optimization
    string(APPEND RELEASE_FLAGS " /Oi")  # Enable intrinsic functions
    string(APPEND RELEASE_FLAGS " /Ot")  # Favor fast code
    string(APPEND RELEASE_FLAGS " /fp:fast")  # Fast floating point
    string(APPEND RELEASE_FLAGS " /GS-")  # Disable buffer security check (for perf)
    string(APPEND RELEASE_FLAGS " /Gy")   # Enable function-level linking

    # SIMD flags
    foreach(flag ${TSDUCK_SIMD_FLAGS})
        string(APPEND RELEASE_FLAGS " ${flag}")
    endforeach()

    set(CMAKE_CXX_FLAGS_RELEASE "${RELEASE_FLAGS}")
    set(CMAKE_CXX_FLAGS_DEBUG "/Od /Zi")

    # Link-Time Optimization (Whole Program Optimization)
    if(ENABLE_LTO)
        set(CMAKE_CXX_FLAGS_RELEASE "${CMAKE_CXX_FLAGS_RELEASE} /GL")
        set(CMAKE_SHARED_LINKER_FLAGS_RELEASE "${CMAKE_SHARED_LINKER_FLAGS_RELEASE} /LTCG")
        set(CMAKE_EXE_LINKER_FLAGS_RELEASE "${CMAKE_EXE_LINKER_FLAGS_RELEASE} /LTCG")
        message(STATUS "MSVC Whole Program Optimization enabled")
    endif()
endif()

# Find libcurl for HTTP streaming
find_package(CURL REQUIRED)

# Find TsDuck via pkg-config (required for ts::TSPacket API)
find_package(PkgConfig REQUIRED)
pkg_check_modules(TSDUCK REQUIRED tsduck)
message(STATUS "TsDuck found: ${TSDUCK_VERSION}")

# Build shared library
set(TSDUCK_INTEROP_SOURCES
    src/tsduck_interop.cpp
    src/streaming/stream_source.cpp
    src/streaming/stream_pipeline.cpp
)

add_library(tsduck_interop SHARED ${TSDUCK_INTEROP_SOURCES})

# Include directories
target_include_directories(tsduck_interop
    PUBLIC
        $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
        $<INSTALL_INTERFACE:include>
    PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}/src
        ${CMAKE_CURRENT_SOURCE_DIR}/src/core
        ${CMAKE_CURRENT_SOURCE_DIR}/src/platform
        ${CMAKE_CURRENT_SOURCE_DIR}/src/concurrency
        ${CMAKE_CURRENT_SOURCE_DIR}/src/mpegts
        ${CMAKE_CURRENT_SOURCE_DIR}/src/analysis
        ${CMAKE_CURRENT_SOURCE_DIR}/src/restamping
        ${CMAKE_CURRENT_SOURCE_DIR}/src/context
        ${CMAKE_CURRENT_SOURCE_DIR}/src/streaming
        ${CURL_INCLUDE_DIRS}
)

# Link libcurl for HTTP streaming
target_link_libraries(tsduck_interop
    PRIVATE
        CURL::libcurl
)

# Link TsDuck for ts::TSPacket API (PCR/PTS/DTS manipulation)
# Use SYSTEM to treat TsDuck headers as system headers (excludes from static analysis)
target_include_directories(tsduck_interop SYSTEM PRIVATE ${TSDUCK_INCLUDE_DIRS})
target_link_libraries(tsduck_interop PRIVATE ${TSDUCK_LIBRARIES})
target_link_directories(tsduck_interop PRIVATE ${TSDUCK_LIBRARY_DIRS})

# Set library properties
set_target_properties(tsduck_interop PROPERTIES
    VERSION ${PROJECT_VERSION}
    SOVERSION 1
    PUBLIC_HEADER include/tsduck_interop.h
)

# Platform-specific settings
if(UNIX AND NOT APPLE)
    # Linux - link pthread
    find_package(Threads REQUIRED)
    target_link_libraries(tsduck_interop PRIVATE Threads::Threads)
endif()

# Installation rules
include(GNUInstallDirs)

install(TARGETS tsduck_interop
    EXPORT tsduck_interop-targets
    LIBRARY DESTINATION ${CMAKE_INSTALL_LIBDIR}
    ARCHIVE DESTINATION ${CMAKE_INSTALL_LIBDIR}
    RUNTIME DESTINATION ${CMAKE_INSTALL_BINDIR}
    PUBLIC_HEADER DESTINATION ${CMAKE_INSTALL_INCLUDEDIR}
)

# Export for CMake find_package
install(EXPORT tsduck_interop-targets
    FILE tsduck_interop-config.cmake
    NAMESPACE TsDuckInterop::
    DESTINATION ${CMAKE_INSTALL_LIBDIR}/cmake/tsduck_interop
)

# Copy to runtime directory for .NET
if(DEFINED DOTNET_RUNTIME_DIR)
    add_custom_command(TARGET tsduck_interop POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy
            $<TARGET_FILE:tsduck_interop>
            "${DOTNET_RUNTIME_DIR}/$<TARGET_FILE_NAME:tsduck_interop>"
        COMMENT "Copying tsduck_interop to .NET runtime directory"
    )
endif()

# ============================================================================
# Testing (optional)
# ============================================================================
option(BUILD_TESTS "Build unit and integration tests" OFF)

if(BUILD_TESTS)
    message(STATUS "Tests enabled - adding tests subdirectory")
    add_subdirectory(tests)
endif()

# ============================================================================
# Configuration Summary
# ============================================================================
message(STATUS "")
message(STATUS "=== TsDuck Interop Library Configuration ===")
message(STATUS "")
message(STATUS "Build Configuration:")
message(STATUS "  Build type:       ${CMAKE_BUILD_TYPE}")
message(STATUS "  C++ Compiler:     ${CMAKE_CXX_COMPILER_ID} ${CMAKE_CXX_COMPILER_VERSION}")
message(STATUS "  Install prefix:   ${CMAKE_INSTALL_PREFIX}")
message(STATUS "")
message(STATUS "libcurl:")
message(STATUS "  Version:          ${CURL_VERSION_STRING}")
message(STATUS "  Include dirs:     ${CURL_INCLUDE_DIRS}")
message(STATUS "  Libraries:        ${CURL_LIBRARIES}")
message(STATUS "")
message(STATUS "TsDuck:")
message(STATUS "  Version:          ${TSDUCK_VERSION}")
message(STATUS "  Include dirs:     ${TSDUCK_INCLUDE_DIRS}")
message(STATUS "  Libraries:        ${TSDUCK_LIBRARIES}")
message(STATUS "  Library dirs:     ${TSDUCK_LIBRARY_DIRS}")
message(STATUS "")
message(STATUS "CPU Architecture:")
message(STATUS "  Processor:        ${CMAKE_SYSTEM_PROCESSOR}")
if(TSDUCK_ARCH_X86)
    message(STATUS "  Architecture:     x86/x64")
elseif(TSDUCK_ARCH_ARM)
    message(STATUS "  Architecture:     ARM")
else()
    message(STATUS "  Architecture:     Other/Unknown")
endif()
message(STATUS "")
message(STATUS "SIMD Features:")
if(TSDUCK_HAS_AVX2)
    message(STATUS "  AVX2:             ENABLED")
else()
    message(STATUS "  AVX2:             disabled")
endif()
if(TSDUCK_HAS_SSE42)
    message(STATUS "  SSE4.2:           ENABLED")
else()
    message(STATUS "  SSE4.2:           disabled")
endif()
if(TSDUCK_HAS_NEON)
    message(STATUS "  NEON:             ENABLED")
else()
    message(STATUS "  NEON:             disabled (not ARM)")
endif()
message(STATUS "  SIMD flags:       ${TSDUCK_SIMD_FLAGS}")
message(STATUS "")
message(STATUS "Optimizations:")
message(STATUS "  Native opts:      ${ENABLE_NATIVE_OPTIMIZATIONS}")
message(STATUS "  LTO:              ${ENABLE_LTO}")
message(STATUS "  Release flags:    ${CMAKE_CXX_FLAGS_RELEASE}")
message(STATUS "")
message(STATUS "Testing:")
message(STATUS "  Build tests:      ${BUILD_TESTS}")
message(STATUS "")
message(STATUS "Project Structure:")
message(STATUS "  src/core/         Constants (derived from TsDuck)")
message(STATUS "  src/platform/     CPU detection, SIMD search")
message(STATUS "  src/concurrency/  Seqlock, ring buffer")
message(STATUS "  src/analysis/     PCR, IAT, A/V sync trackers")
message(STATUS "  src/restamping/   Timestamp correction (via TsDuck)")
message(STATUS "  src/context/      TsDuck context and analyzer")
message(STATUS "  src/streaming/    HTTP streaming, failover, pipeline")
message(STATUS "")

# ============================================================================
# Code Formatting Targets (optional)
# ============================================================================
option(ENABLE_CODE_FORMATTING "Enable code formatting targets" OFF)

if(ENABLE_CODE_FORMATTING)
    find_program(CLANG_FORMAT_EXE NAMES clang-format clang-format-18 clang-format-17 clang-format-16)
    if(CLANG_FORMAT_EXE)
        message(STATUS "clang-format found: ${CLANG_FORMAT_EXE}")

        # Collect source files for formatting
        file(GLOB_RECURSE FORMAT_SOURCES
            ${CMAKE_CURRENT_SOURCE_DIR}/src/*.cpp
            ${CMAKE_CURRENT_SOURCE_DIR}/src/*.hpp
            ${CMAKE_CURRENT_SOURCE_DIR}/include/*.h
        )

        # Create format target (applies formatting in-place)
        add_custom_target(format
            COMMAND ${CLANG_FORMAT_EXE}
                -i
                --style=file
                ${FORMAT_SOURCES}
            WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}
            COMMENT "Formatting source files with clang-format..."
            VERBATIM
        )

        # Create format-check target (checks formatting without modifying)
        add_custom_target(format-check
            COMMAND ${CLANG_FORMAT_EXE}
                --dry-run
                --Werror
                --style=file
                ${FORMAT_SOURCES}
            WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}
            COMMENT "Checking source file formatting..."
            VERBATIM
        )

        # Create format-diff target (shows diff of what would change)
        add_custom_target(format-diff
            COMMAND ${CMAKE_COMMAND} -E echo "Showing formatting differences..."
            COMMAND ${CLANG_FORMAT_EXE}
                --style=file
                ${FORMAT_SOURCES}
                | diff -u ${FORMAT_SOURCES} - || true
            WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}
            COMMENT "Showing formatting differences..."
            VERBATIM
        )
    else()
        message(STATUS "clang-format not found - formatting targets disabled")
    endif()
endif()

message(STATUS "Code Formatting:")
message(STATUS "  Enabled:          ${ENABLE_CODE_FORMATTING}")
if(ENABLE_CODE_FORMATTING AND CLANG_FORMAT_EXE)
    message(STATUS "  clang-format:     ${CLANG_FORMAT_EXE}")
elseif(ENABLE_CODE_FORMATTING)
    message(STATUS "  clang-format:     not found")
endif()
message(STATUS "")

# ============================================================================
# Static Analysis Targets (optional)
# ============================================================================
option(ENABLE_STATIC_ANALYSIS "Enable static analysis targets" OFF)

if(ENABLE_STATIC_ANALYSIS)
    # Find clang-tidy
    find_program(CLANG_TIDY_EXE NAMES clang-tidy clang-tidy-18 clang-tidy-17 clang-tidy-16)
    if(CLANG_TIDY_EXE)
        message(STATUS "clang-tidy found: ${CLANG_TIDY_EXE}")

        # Collect source files for analysis
        # Note: We only analyze C++ files (.cpp, .hpp), not C headers (.h)
        # The tsduck_interop.h is a C API header for P/Invoke that must remain C-compatible
        file(GLOB_RECURSE TSDUCK_SOURCES
            ${CMAKE_CURRENT_SOURCE_DIR}/src/*.cpp
            ${CMAKE_CURRENT_SOURCE_DIR}/src/*.hpp
        )

        # Create clang-tidy target
        # Use --extra-arg=-isystem to treat TsDuck and CURL as system headers
        # Use --extra-arg-before to inject -Qunused-arguments before compile_commands.json flags
        # This silently ignores GCC-specific flags like -ftree-loop-vectorize
        add_custom_target(clang-tidy
            COMMAND ${CLANG_TIDY_EXE}
                -p ${CMAKE_BINARY_DIR}
                --config-file=${CMAKE_CURRENT_SOURCE_DIR}/.clang-tidy
                "--extra-arg-before=-Qunused-arguments"
                "--extra-arg=-isystem${TSDUCK_INCLUDE_DIRS}"
                "--extra-arg=-isystem${CURL_INCLUDE_DIRS}"
                "--extra-arg=-Wno-unknown-warning-option"
                ${TSDUCK_SOURCES}
            WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}
            COMMENT "Running clang-tidy static analysis..."
            VERBATIM
        )

        # Create clang-tidy-fix target (auto-fixes issues)
        add_custom_target(clang-tidy-fix
            COMMAND ${CLANG_TIDY_EXE}
                -p ${CMAKE_BINARY_DIR}
                --config-file=${CMAKE_CURRENT_SOURCE_DIR}/.clang-tidy
                "--extra-arg-before=-Qunused-arguments"
                "--extra-arg=-isystem${TSDUCK_INCLUDE_DIRS}"
                "--extra-arg=-isystem${CURL_INCLUDE_DIRS}"
                "--extra-arg=-Wno-unknown-warning-option"
                --fix
                --fix-errors
                ${TSDUCK_SOURCES}
            WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}
            COMMENT "Running clang-tidy with auto-fix..."
            VERBATIM
        )
    else()
        message(STATUS "clang-tidy not found - clang-tidy target disabled")
    endif()

    # Find cppcheck
    find_program(CPPCHECK_EXE NAMES cppcheck)
    if(CPPCHECK_EXE)
        message(STATUS "cppcheck found: ${CPPCHECK_EXE}")

        # Create cppcheck target
        # Exclude TsDuck and system header directories from analysis
        add_custom_target(cppcheck
            COMMAND ${CPPCHECK_EXE}
                --enable=all
                --std=c++20
                --language=c++
                --project=${CMAKE_BINARY_DIR}/compile_commands.json
                --suppressions-list=${CMAKE_CURRENT_SOURCE_DIR}/.cppcheck-suppressions
                --error-exitcode=1
                --inline-suppr
                --quiet
                -i ${CMAKE_CURRENT_SOURCE_DIR}/tests
                -i /opt/tsduck
                -i /usr/include
                -i /usr/local/include
                --template="{file}:{line}: [{severity}] {id}: {message}"
            WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}
            COMMENT "Running cppcheck static analysis..."
            VERBATIM
        )

        # Create cppcheck-xml target (for CI integration)
        add_custom_target(cppcheck-xml
            COMMAND ${CPPCHECK_EXE}
                --enable=all
                --std=c++20
                --language=c++
                --project=${CMAKE_BINARY_DIR}/compile_commands.json
                --suppressions-list=${CMAKE_CURRENT_SOURCE_DIR}/.cppcheck-suppressions
                --inline-suppr
                -i ${CMAKE_CURRENT_SOURCE_DIR}/tests
                -i /opt/tsduck
                -i /usr/include
                -i /usr/local/include
                --xml
                --output-file=${CMAKE_BINARY_DIR}/cppcheck-report.xml
            WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}
            COMMENT "Running cppcheck with XML output..."
            VERBATIM
        )
    else()
        message(STATUS "cppcheck not found - cppcheck target disabled")
    endif()

    # Find flawfinder (security-focused analysis)
    find_program(FLAWFINDER_EXE NAMES flawfinder)
    if(FLAWFINDER_EXE)
        message(STATUS "flawfinder found: ${FLAWFINDER_EXE}")

        # Create flawfinder target
        add_custom_target(flawfinder
            COMMAND ${FLAWFINDER_EXE}
                --minlevel=1
                --context
                --columns
                ${CMAKE_CURRENT_SOURCE_DIR}/src
                ${CMAKE_CURRENT_SOURCE_DIR}/include
            WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}
            COMMENT "Running flawfinder security analysis..."
            VERBATIM
        )

        # Create flawfinder-html target (for CI integration)
        add_custom_target(flawfinder-html
            COMMAND ${FLAWFINDER_EXE}
                --minlevel=1
                --context
                --columns
                --html
                ${CMAKE_CURRENT_SOURCE_DIR}/src
                ${CMAKE_CURRENT_SOURCE_DIR}/include
                > ${CMAKE_BINARY_DIR}/flawfinder-report.html
            WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}
            COMMENT "Running flawfinder with HTML output..."
            VERBATIM
        )
    else()
        message(STATUS "flawfinder not found - flawfinder target disabled")
    endif()

    # Combined static analysis target
    set(STATIC_ANALYSIS_DEPS)
    if(CLANG_TIDY_EXE)
        list(APPEND STATIC_ANALYSIS_DEPS clang-tidy)
    endif()
    if(CPPCHECK_EXE)
        list(APPEND STATIC_ANALYSIS_DEPS cppcheck)
    endif()
    if(FLAWFINDER_EXE)
        list(APPEND STATIC_ANALYSIS_DEPS flawfinder)
    endif()

    if(STATIC_ANALYSIS_DEPS)
        add_custom_target(static-analysis
            DEPENDS ${STATIC_ANALYSIS_DEPS}
            COMMENT "Running all static analysis tools..."
        )
    endif()
endif()

message(STATUS "Static Analysis:")
message(STATUS "  Enabled:          ${ENABLE_STATIC_ANALYSIS}")
if(ENABLE_STATIC_ANALYSIS)
    if(CLANG_TIDY_EXE)
        message(STATUS "  clang-tidy:       ${CLANG_TIDY_EXE}")
    else()
        message(STATUS "  clang-tidy:       not found")
    endif()
    if(CPPCHECK_EXE)
        message(STATUS "  cppcheck:         ${CPPCHECK_EXE}")
    else()
        message(STATUS "  cppcheck:         not found")
    endif()
    if(FLAWFINDER_EXE)
        message(STATUS "  flawfinder:       ${FLAWFINDER_EXE}")
    else()
        message(STATUS "  flawfinder:       not found")
    endif()
endif()
message(STATUS "")
