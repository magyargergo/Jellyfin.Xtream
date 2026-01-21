# Copyright (C) 2025  Gergo Magyar
#
# This program is free software: you can redistribute it and/or modify
# it under the terms of the GNU General Public License as published by
# the Free Software Foundation, either version 3 of the License, or
# (at your option) any later version.

cmake_minimum_required(VERSION 3.16)
project(tsduck_interop VERSION 1.0.0 LANGUAGES CXX)

# C++17 required for structured bindings and other features
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

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

# Find TSDuck using pkg-config
find_package(PkgConfig REQUIRED)
pkg_check_modules(TSDUCK REQUIRED IMPORTED_TARGET tsduck)

# Build shared library
# Using refactored modular source structure
add_library(tsduck_interop SHARED
    src/tsduck_interop.cpp
)

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
        ${TSDUCK_INCLUDE_DIRS}
)

# Link TSDuck
target_link_libraries(tsduck_interop
    PRIVATE
        PkgConfig::TSDUCK
)

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
message(STATUS "TSDuck:")
message(STATUS "  Version:          ${TSDUCK_VERSION}")
message(STATUS "  Include dirs:     ${TSDUCK_INCLUDE_DIRS}")
message(STATUS "  Libraries:        ${TSDUCK_LIBRARIES}")
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
message(STATUS "  src/core/         Constants and types")
message(STATUS "  src/platform/     CPU detection, SIMD search")
message(STATUS "  src/concurrency/  Seqlock, ring buffer")
message(STATUS "  src/mpegts/       Packet utilities")
message(STATUS "  src/analysis/     PCR, IAT, A/V sync trackers")
message(STATUS "  src/restamping/   Timestamp correction")
message(STATUS "  src/context/      TsDuck context and analyzer")
message(STATUS "")
