#!/usr/bin/env bash
# Test double for the docker CLI, used by ScanImage.Tests.ps1 so tools/scan-image.sh runs for real without a Docker daemon.
# It serves ONE image from a docker-archive tarball ($FAKE_DOCKER_ARCHIVE, as written by `docker save`): config.json (the image
# configuration), layer*.tar (layers, applied in name order) and manifest.json. Sub-commands:
#   image inspect <ref>   prints [ <image config> ]         history --no-trunc <ref>   prints one line
#   save -o <file> <ref>  writes the archive (what docker save writes: every layer as its own tar)
#   create <ref>          applies the layers like a container runtime, INCLUDING whiteouts (.wh.<name> deletes <name>,
#                         .wh..wh..opq empties the directory), prints an id
#   export <id>           tars that merged root filesystem (what docker export writes)
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
  save)
    [ "$2" = "-o" ] || { echo "fake-docker: unsupported: $*" >&2; exit 1; }
    cp "$archive" "$3"
    ;;
  create)
    rm -rf "$state/rootfs" "$state/layers"; mkdir -p "$state/rootfs" "$state/layers"
    tar -xf "$archive" -C "$state/layers"
    for layer in $(cd "$state/layers" && ls layer*.tar | sort); do
      tar -xf "$state/layers/$layer" --no-same-owner --no-same-permissions -C "$state/rootfs"
      while IFS= read -r wh; do
        dir="$(dirname "$wh")"; base="$(basename "$wh")"
        if [ "$base" = ".wh..wh..opq" ]; then find "$dir" -mindepth 1 -maxdepth 1 ! -name '.wh..wh..opq' -exec rm -rf {} +
        else rm -rf "$dir/${base#.wh.}"; fi
        rm -f "$wh"
      done < <(find "$state/rootfs" -name '.wh.*')
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
