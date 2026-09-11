#!/usr/bin/env sh
set -eu

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
PLATFORM=${1:-darwin_arm64}
if [ "$#" -gt 0 ]; then
  shift
fi

if [ "$PLATFORM" != "darwin_arm64" ]; then
  echo "Unsupported Xenon build platform: $PLATFORM" >&2
  exit 1
fi

cmake -S "$ROOT" -B "$ROOT/build/$PLATFORM/cmake" \
  -DXENON_PLATFORM="$PLATFORM" "$@"
cmake --build "$ROOT/build/$PLATFORM/cmake" --config Release --target xenon-native
