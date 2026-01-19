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

# Compiler flags
if(CMAKE_CXX_COMPILER_ID MATCHES "GNU|Clang")
    set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} -Wall -Wextra -Wpedantic")
    set(CMAKE_CXX_FLAGS_RELEASE "${CMAKE_CXX_FLAGS_RELEASE} -O3")
    set(CMAKE_CXX_FLAGS_DEBUG "${CMAKE_CXX_FLAGS_DEBUG} -g -O0")

    # Symbol visibility for shared library
    set(CMAKE_CXX_VISIBILITY_PRESET hidden)
    set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)
endif()

# Find TSDuck using pkg-config
find_package(PkgConfig REQUIRED)
pkg_check_modules(TSDUCK REQUIRED IMPORTED_TARGET tsduck)

# Build shared library
add_library(tsduck_interop SHARED
    src/tsduck_interop.cpp
)

# Include directories
target_include_directories(tsduck_interop
    PUBLIC
        $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
        $<INSTALL_INTERFACE:include>
    PRIVATE
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

# Print configuration
message(STATUS "TSDuck interop library configuration:")
message(STATUS "  TSDuck version: ${TSDUCK_VERSION}")
message(STATUS "  TSDuck include dirs: ${TSDUCK_INCLUDE_DIRS}")
message(STATUS "  TSDuck libraries: ${TSDUCK_LIBRARIES}")
message(STATUS "  Build type: ${CMAKE_BUILD_TYPE}")
message(STATUS "  Install prefix: ${CMAKE_INSTALL_PREFIX}")
