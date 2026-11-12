#!/usr/bin/env bash
set -euo pipefail

[[ $# == 3 && $EUID == 0 ]]
root=$(realpath "$1")
stage=$(realpath "$2")
version=$3
[[ $root != / && -f $root/etc/os-release && $version =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]
update=$(dirname "$(realpath "$0")")
session="$update/../session"
runtime="$root/usr/libexec/jinra"
install -Dm755 "$stage/bin/jinra" "$runtime/jinra"
install -Dm755 "$stage/local/libexec/jinra-capture" "$runtime/jinra-capture"
install -Dm755 "$stage/local/bin/jinra-bluetooth" "$runtime/jinra-bluetooth"
for name in jinra-session jinra-supervisor jinra-record-gameplay; do
    install -Dm755 "$session/$name" "$runtime/$name"
done
printf '%s\n' "$version" > "$runtime/os-version"
install -Dm755 "$update/jinra-os-update" "$root/usr/local/libexec/jinra-os-update"
install -Dm755 "$update/jinra-os-launch" "$root/usr/local/bin/jinra-os-launch"
install -Dm644 "$update/jinra-os-update@.service" "$root/etc/systemd/system/jinra-os-update@.service"
install -Dm644 "$update/60-jinra-os-update.rules" "$root/etc/polkit-1/rules.d/60-jinra-os-update.rules"
install -Dm644 "$update/releases.pub.pem" "$root/etc/jinra/releases.pub.pem"
install -d -m755 "$root/var/lib/jinra-os"
sed 's|command = "/usr/local/bin/jinra-session"|command = "/usr/local/bin/jinra-os-launch"|' \
    "$session/greetd.toml" > "$root/etc/greetd/config.toml"
