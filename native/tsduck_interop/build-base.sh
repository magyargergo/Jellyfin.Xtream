#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

IMAGE_NAME="jellyfin-xtream-tsduck-base"
TAG="latest"

echo "Building TSDuck base image..."
docker build -f Dockerfile.base -t "$IMAGE_NAME:$TAG" .

echo ""
echo "✓ Base image built successfully: $IMAGE_NAME:$TAG"
echo ""
echo "To build the native library, run:"
echo "  cd native/tsduck_interop"
echo "  docker build --target artifacts -o type=local,dest=output ."
