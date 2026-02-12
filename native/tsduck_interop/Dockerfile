# Multi-stage Dockerfile for building libtsduck_interop.so with MINIMAL dependencies
# Builds TsDuck from source to eliminate unnecessary runtime dependencies
# Target runtime: Ubuntu 24.04-based systems (Jellyfin Docker)
#
# Key optimization: Builds TsDuck with NOxxx flags to remove dependencies on:
#   - libusb (USB device support)
#   - libpcsclite (smartcard support)
#   - libsrt (SRT protocol)
#   - librist (RIST protocol)
#   - libedit (line editing)
#   - libcurl (HTTP support)
#   - Dektec/HiDes/VATek hardware support
#
# References:
#   - https://tsduck.io/docs/tsduck-dev.html
#   - https://github.com/tsduck/tsduck/issues/1488

ARG TSDUCK_BASE_IMAGE=tsduck-base:local
FROM ${TSDUCK_BASE_IMAGE} AS tsduck-builder

# =============================================================================
# Stage 2: Build the native interop library against minimal TsDuck
# =============================================================================
FROM ubuntu:24.04 AS builder

# Install build dependencies for the interop library
RUN mkdir -p /usr/share/man/man1 \
    && apt-get update && apt-get install -y --no-install-recommends \
    build-essential \
    cmake \
    pkg-config \
    # Minimal runtime deps that TsDuck still needs
    libssl-dev \
    zlib1g-dev \
    # libcurl is needed by our interop library (stream_source.cpp HTTP streaming)
    # Note: TsDuck itself is built with NOCURL=1, but our code uses curl directly
    libcurl4-openssl-dev \
    && rm -rf /var/lib/apt/lists/*

# Copy the minimal TsDuck installation
COPY --from=tsduck-builder /opt/tsduck /opt/tsduck

# Set up pkg-config to find our minimal TsDuck
# TsDuck installs .pc files to share/pkgconfig
ENV PKG_CONFIG_PATH="/opt/tsduck/share/pkgconfig"
ENV LD_LIBRARY_PATH="/opt/tsduck/lib"

# Verify pkg-config finds TsDuck
RUN pkg-config --modversion tsduck && \
    echo "TsDuck include: $(pkg-config --cflags tsduck)" && \
    echo "TsDuck libs: $(pkg-config --libs tsduck)"

# Copy interop library source code
WORKDIR /build
COPY include/ include/
COPY src/ src/
COPY CMakeLists.txt .

# Build the interop library linked against minimal TsDuck
RUN cmake -B build \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_PREFIX_PATH=/opt/tsduck \
    && cmake --build build --parallel $(nproc) \
    && cmake --install build --prefix /install

# Prepare bundled libraries directory
# Copy our interop library AND both TsDuck libraries (libtscore + libtsduck)
RUN mkdir -p /bundle && \
    cp /install/lib/libtsduck_interop.so* /bundle/ && \
    cp /opt/tsduck/lib/libtscore.so* /bundle/ && \
    cp /opt/tsduck/lib/libtsduck.so* /bundle/ && \
    echo "=== Bundle contents ===" && \
    ls -la /bundle/ && \
    echo "=== libtsduck_interop.so dependencies ===" && \
    ldd /bundle/libtsduck_interop.so && \
    echo "=== libtscore.so dependencies ===" && \
    ldd /bundle/libtscore.so && \
    echo "=== libtsduck.so dependencies ===" && \
    ldd /bundle/libtsduck.so

# =============================================================================
# Stage 3: Runtime image with native library (for testing)
# =============================================================================
FROM ubuntu:24.04 AS runtime

# Install only the minimal runtime dependencies
RUN apt-get update && apt-get install -y --no-install-recommends \
    libssl3 \
    zlib1g \
    libcurl4 \
    && rm -rf /var/lib/apt/lists/*

# Copy the built libraries (interop + libtscore + libtsduck)
COPY --from=builder /install/lib/*.so* /usr/local/lib/
COPY --from=builder /opt/tsduck/lib/libtscore.so* /usr/local/lib/
COPY --from=builder /opt/tsduck/lib/libtsduck.so* /usr/local/lib/
COPY --from=builder /install/include/ /usr/local/include/

# Deploy TsDuck data files (required by DuckContext / Names singleton)
COPY --from=tsduck-builder /opt/tsduck/share/tsduck /usr/local/share/tsduck

# Update library cache
RUN ldconfig

# Set library path
ENV LD_LIBRARY_PATH="/usr/local/lib:${LD_LIBRARY_PATH}"

# Verify libraries load correctly
RUN ldd /usr/local/lib/libtsduck_interop.so && \
    ldd /usr/local/lib/libtsduck.so

# =============================================================================
# Stage 4: Extract artifacts only (for CI/CD)
# =============================================================================
FROM scratch AS artifacts

# Copy the interop library and bundled minimal TsDuck library
COPY --from=builder /bundle/ /

# Usage:
# docker build --target artifacts -o type=local,dest=./output .
#
# This produces:
#   - libtsduck_interop.so (our P/Invoke bridge)
#   - libtscore.so (TsDuck core utilities - strings, args, system)
#   - libtsduck.so (TsDuck TS processing - depends on libtscore)
#
# All libraries are built with minimal dependencies (no USB/SRT/RIST/smartcard/curl)
