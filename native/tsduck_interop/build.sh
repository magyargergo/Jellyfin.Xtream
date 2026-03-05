#!/bin/bash
# Build script for libtsduck_interop native library

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BUILD_DIR="${SCRIPT_DIR}/build"
INSTALL_DIR="${SCRIPT_DIR}/install"

# Parse arguments
BUILD_TYPE="Release"
CLEAN=0
DOCKER=0

while [[ $# -gt 0 ]]; do
    case $1 in
        --debug)
            BUILD_TYPE="Debug"
            shift
            ;;
        --clean)
            CLEAN=1
            shift
            ;;
        --docker)
            DOCKER=1
            shift
            ;;
        *)
            echo "Unknown option: $1"
            exit 1
            ;;
    esac
done

if [[ $DOCKER -eq 1 ]]; then
    echo "Building with Docker..."
    docker build --target artifacts -o type=local,dest="${SCRIPT_DIR}/output" "${SCRIPT_DIR}"
    echo "Built library copied to ${SCRIPT_DIR}/output/"
    exit 0
fi

# Clean if requested
if [[ $CLEAN -eq 1 ]]; then
    echo "Cleaning build directory..."
    rm -rf "${BUILD_DIR}" "${INSTALL_DIR}"
fi

# Check for TSDuck
if ! pkg-config --exists tsduck 2>/dev/null; then
    echo "Error: TSDuck development package not found."
    echo "Please install tsduck-dev package or build TSDuck from source."
    echo ""
    echo "On Debian/Ubuntu:"
    echo "  curl -fsSL https://tsduck.io/download/tsduck.gpg | sudo gpg --dearmor -o /etc/apt/keyrings/tsduck.gpg"
    echo "  echo 'deb [signed-by=/etc/apt/keyrings/tsduck.gpg] https://tsduck.io/download/debian bookworm main' | sudo tee /etc/apt/sources.list.d/tsduck.list"
    echo "  sudo apt-get update && sudo apt-get install tsduck-dev"
    exit 1
fi

echo "TSDuck version: $(pkg-config --modversion tsduck)"
echo "Build type: ${BUILD_TYPE}"

# Configure
echo "Configuring..."
cmake -B "${BUILD_DIR}" \
    -DCMAKE_BUILD_TYPE="${BUILD_TYPE}" \
    -DCMAKE_INSTALL_PREFIX="${INSTALL_DIR}" \
    "${SCRIPT_DIR}"

# Build
echo "Building..."
cmake --build "${BUILD_DIR}" --parallel "$(nproc)"

# Install locally
echo "Installing to ${INSTALL_DIR}..."
cmake --install "${BUILD_DIR}" --prefix "${INSTALL_DIR}"

echo ""
echo "Build complete!"
echo "Library: ${INSTALL_DIR}/lib/libtsduck_interop.so"
echo ""
echo "To use with .NET, copy all libraries to your runtime directory:"
echo "  cp ${INSTALL_DIR}/lib/libtsduck_interop.so* runtimes/linux-x64/native/"
echo "  cp /path/to/libtscore.so* runtimes/linux-x64/native/"
echo "  cp /path/to/libtsduck.so* runtimes/linux-x64/native/"
echo ""
echo "Or use --docker to build with minimal dependencies (recommended):"
echo "  ./build.sh --docker"
