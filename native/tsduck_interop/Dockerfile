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

# TSDuck version to use (update as needed)
ARG TSDUCK_VERSION=3.43-4549

# =============================================================================
# Stage 1: Build TsDuck from source with minimal dependencies
# =============================================================================
FROM ubuntu:24.04 AS tsduck-builder

ARG TSDUCK_VERSION

# Install only the essential build dependencies for minimal TsDuck
# We explicitly DO NOT install: libusb-1.0-0-dev, libpcsclite-dev, libsrt-openssl-dev,
# librist-dev, libedit-dev, libcurl4-openssl-dev
RUN apt-get update && apt-get install -y --no-install-recommends \
    build-essential \
    git \
    dos2unix \
    ca-certificates \
    # Python 3 is required by TsDuck build system (even with NOPYTHON=1)
    python3 \
    # Locale support for build scripts
    locales \
    # Minimal TsDuck build dependencies (from their docs)
    libssl-dev \
    zlib1g-dev \
    && locale-gen en_US.UTF-8 \
    && rm -rf /var/lib/apt/lists/*

# Set locale to avoid warnings in build scripts
ENV LANG=en_US.UTF-8
ENV LC_ALL=en_US.UTF-8

# Clone TsDuck source at specific version
WORKDIR /tsduck-src
RUN git clone --depth 1 --branch v${TSDUCK_VERSION} https://github.com/tsduck/tsduck.git .

# Build TsDuck with all unnecessary features disabled
# This produces a minimal libtsduck.so without external dependencies
# See: https://tsduck.io/docs/tsduck-dev.html section 1.1.2.6
ENV MAKEFLAGS="-j$(nproc)"

# Common build flags to disable unnecessary features
ENV TSDUCK_BUILD_FLAGS="NODEKTEC=1 NOHIDES=1 NOVATEK=1 NOCURL=1 NOPCSC=1 NOSRT=1 NORIST=1 NOEDITLINE=1 NOUSB=1 NOJAVA=1 NOPYTHON=1 NOTEST=1"

RUN make -j$(nproc) $TSDUCK_BUILD_FLAGS

# Install TsDuck to /opt/tsduck for isolation
# NODOC=1 skips documentation which requires asciidoctor
RUN make install $TSDUCK_BUILD_FLAGS NODOC=1 SYSPREFIX=/opt/tsduck

# Verify the minimal build has no unwanted dependencies
# TsDuck is split into libtscore.so (basic utils) and libtsduck.so (TS processing)
RUN echo "=== Checking library files ===" && \
    ls -la /opt/tsduck/lib/*.so* && \
    echo "=== Checking libtscore.so dependencies ===" && \
    ldd /opt/tsduck/lib/libtscore.so && \
    echo "=== Checking libtsduck.so dependencies ===" && \
    ldd /opt/tsduck/lib/libtsduck.so && \
    echo "=== Verifying no unwanted dependencies ===" && \
    ! ldd /opt/tsduck/lib/libtsduck.so | grep -E 'libusb|libpcsclite|libsrt|librist|libedit' && \
    ! ldd /opt/tsduck/lib/libtscore.so | grep -E 'libusb|libpcsclite|libsrt|librist|libedit' && \
    echo "SUCCESS: No unwanted dependencies found"

# =============================================================================
# Stage 2: Build the native interop library against minimal TsDuck
# =============================================================================
FROM ubuntu:24.04 AS builder

# Install build dependencies for the interop library
RUN apt-get update && apt-get install -y --no-install-recommends \
    build-essential \
    cmake \
    pkg-config \
    # Minimal runtime deps that TsDuck still needs
    libssl-dev \
    zlib1g-dev \
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
    && rm -rf /var/lib/apt/lists/*

# Copy the built libraries (interop + libtscore + libtsduck)
COPY --from=builder /install/lib/*.so* /usr/local/lib/
COPY --from=builder /opt/tsduck/lib/libtscore.so* /usr/local/lib/
COPY --from=builder /opt/tsduck/lib/libtsduck.so* /usr/local/lib/
COPY --from=builder /install/include/ /usr/local/include/

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
