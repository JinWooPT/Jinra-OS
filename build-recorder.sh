#!/usr/bin/env bash
set -euo pipefail

[[ $# == 1 ]]
build=$(realpath "$1")
source_dir=$(dirname "$(realpath "$0")")
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT
cd "$work"
apt-get download libpipewire-0.3-dev libspa-0.2-dev libgstreamer1.0-dev libgstreamer-plugins-base1.0-dev
for package in ./*.deb; do dpkg-deb -x "$package" .; done
libs="$build/system/usr/lib/x86_64-linux-gnu"
cc -std=gnu17 -O2 -Wall -Wextra -Werror \
    -isystem usr/include/pipewire-0.3 -isystem usr/include/spa-0.2 \
    -isystem usr/include/gstreamer-1.0 $(pkg-config --cflags glib-2.0 gobject-2.0) \
    -I"$source_dir/../jinra-111-release/publish-src/src/video_core" \
    "$source_dir/session/jinra-capture.c" -o jinra-capture -L"$libs" -Wl,-rpath-link,"$libs" \
    -l:libpipewire-0.3.so.0 -l:libgstapp-1.0.so.0 -l:libgstreamer-1.0.so.0 \
    $(pkg-config --libs glib-2.0 gobject-2.0)
install -Dm755 jinra-capture "$build/stage/usr/local/libexec/jinra-capture"
