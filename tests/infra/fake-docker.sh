#!/usr/bin/env bash
# Test double for the docker CLI, used by ScanImage.Tests.ps1 so tools/scan-image.sh runs for real without a Docker daemon.
# It serves ONE image from a docker-archive tarball ($FAKE_DOCKER_ARCHIVE, as written by `docker save`): config.json (the image
# configuration) and layer*.tar (layers, applied in name order). Only the sub-commands scan-image.sh uses exist:
#   image inspect <ref>   prints [ <image config> ]         history --no-trunc <ref>   prints one line
#   create <ref>          extracts the layers, prints an id  export <id>               tars the extracted root filesystem
#   rm -f <id>            no-op
set -euo pipefail
archive="${FAKE_DOCKER_ARCHIVE:?FAKE_DOCKER_ARCHIVE not set}"
state="${FAKE_DOCKER_STATE:?FAKE_DOCKER_STATE not set}"
# Windows paths contain a colon: without --force-local GNU tar reads "C:" as a remote host.
tar() { command tar --force-local "$@"; }
mkdir -p "$state"

case "$1" in
  image)
    [ "$2" = "inspect" ] || { echo "fake-docker: unsupported: $*" >&2; exit 1; }
    printf '['; tar -xOf "$archive" config.json; printf ']\n'
    ;;
  history)
    echo "fake-docker history for $archive"
    ;;
  create)
    rm -rf "$state/rootfs"; mkdir -p "$state/rootfs" "$state/layers"
    tar -xf "$archive" -C "$state/layers"
    for layer in $(cd "$state/layers" && ls layer*.tar | sort); do
      tar -xf "$state/layers/$layer" --no-same-owner --no-same-permissions -C "$state/rootfs"
    done
    echo "fake-container-id"
    ;;
  export)
    tar -cf - -C "$state/rootfs" .
    ;;
  rm)
    :
    ;;
  *)
    echo "fake-docker: unsupported: $*" >&2; exit 1
    ;;
esac
