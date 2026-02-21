#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

# This tag matches the default ARG TSDUCK_BASE_IMAGE in Dockerfile and Dockerfile.test
IMAGE_TAG="tsduck-base:local"

echo "Building TSDuck base image..."
docker build -f Dockerfile.tsduck-base -t "$IMAGE_TAG" .

echo ""
echo "Base image built successfully: $IMAGE_TAG"
echo ""
echo "To build the native library, run:"
echo "  cd native/tsduck_interop"
echo "  docker build --target artifacts -o type=local,dest=output ."
