#!/usr/bin/env bash
# Scans a local container image for credentials (tools/scan-artifacts.ps1): EVERY layer separately, plus the image configuration.
#   tools/scan-image.sh <image-ref> [report-path]
# The image is exported with `docker save`, whose tarball holds each layer as its own tar (and the config JSON with the baked
# environment variables). The scanner opens each layer tar, so a file that one layer adds and a later layer deletes (a whiteout)
# is still scanned: it is in the pushed layer blob even though a container would not see it. `docker export` of a container
# only shows the final merged filesystem, which is why it is not used.
# Exit codes follow scan-artifacts.ps1: 0 clean, 1 credential found, 2 nothing scanned or the save failed.
# The image must exist locally (docker build --load, or docker pull). Nothing is pushed or changed.
set -euo pipefail

image="${1:?usage: scan-image.sh <image-ref> [report-path]}"
report="${2:-}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
work="$(mktemp -d)"
cleanup() { rm -rf "$work"; }
trap cleanup EXIT

docker image inspect "$image" >/dev/null || { echo "ERROR: image $image not found locally" >&2; exit 2; }

mkdir -p "$work/scan"
# Image configuration (baked environment variables, labels, history) is part of the artifact too.
docker image inspect "$image" > "$work/scan/_image-inspect.json"
docker history --no-trunc "$image" > "$work/scan/_image-history.txt"
docker save -o "$work/scan/image.tar" "$image" || { echo "ERROR: docker save $image failed" >&2; exit 2; }

args=(-NoProfile -File "$here/scan-artifacts.ps1" -Path "$work/scan")
[ -n "$report" ] && args+=(-ReportPath "$report")
pwsh "${args[@]}"
