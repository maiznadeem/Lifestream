#!/usr/bin/env bash
# Scans the filesystem and configuration of a local container image for credentials (tools/scan-artifacts.ps1).
#   tools/scan-image.sh <image-ref> [report-path]
# Exit codes follow scan-artifacts.ps1: 0 clean, 1 credential found, 2 nothing scanned or the export failed.
# The image must exist locally (docker build --load, or docker pull). Nothing is pushed or changed.
set -euo pipefail

image="${1:?usage: scan-image.sh <image-ref> [report-path]}"
report="${2:-}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
work="$(mktemp -d)"
container=""
cleanup() {
  if [ -n "$container" ]; then docker rm -f "$container" >/dev/null 2>&1 || true; fi
  rm -rf "$work"
}
trap cleanup EXIT

docker image inspect "$image" >/dev/null || { echo "ERROR: image $image not found locally" >&2; exit 2; }

mkdir -p "$work/fs"
# Image configuration (baked environment variables, labels, history) is part of the artifact too.
docker image inspect "$image" > "$work/fs/_image-inspect.json"
docker history --no-trunc "$image" > "$work/fs/_image-history.txt"

container="$(docker create "$image")"
# Special files (devices) cannot be created by an unprivileged tar and hold no credentials.
docker export "$container" | tar -x --no-same-owner --no-same-permissions --exclude='dev/*' --exclude='./dev/*' -C "$work/fs"

args=(-NoProfile -File "$here/scan-artifacts.ps1" -Path "$work/fs")
[ -n "$report" ] && args+=(-ReportPath "$report")
pwsh "${args[@]}"
