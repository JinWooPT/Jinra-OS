#!/usr/bin/env bash
set -euo pipefail

[[ $# == 1 ]]
source_dir=$(dirname "$(realpath "$0")")
source="$source_dir/session/jinra-bluetooth.cpp"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
/usr/lib/qt6/libexec/moc "$source" -o "$work/jinra-bluetooth.moc"
clang++ -std=c++20 -O2 -fPIC -I"$work" -I"$source_dir/../jinra-111-release/publish-src/src" $(pkg-config --cflags Qt6Widgets Qt6DBus) \
    "$source" -o "$work/jinra-bluetooth" $(pkg-config --libs Qt6Widgets Qt6DBus)
install -Dm755 "$work/jinra-bluetooth" "$1/jinra-bluetooth"
ldd "$1/jinra-bluetooth" | grep -E 'Qt6(DBus|Widgets)'
