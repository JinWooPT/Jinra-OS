#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C
build=$(realpath "$1")
out=$(realpath -m "$2")
source_dir=$(dirname "$(realpath "$0")")/..
source_dir=$(realpath "$source_dir")
[[ $# == 3 && $EUID == 0 && -f "$build/rootfs-ready" && -x "$build/stage/usr/bin/jinra" ]]
version=$3
work=$(mktemp -d /var/tmp/jinra-payload.XXXXXX)
root="$work/system"
cleanup() {
    if mountpoint -q "$root/proc"; then umount "$root/proc" || return; fi
    if mountpoint -q "$root/dev"; then umount "$root/dev" || return; fi
    rm -rf -- "$work"
    rm -f -- "$out.partial"
}
trap cleanup EXIT
mkdir "$work/system" "$work/bootloader"
tar -C "$build/system" --one-file-system \
    --exclude='./home/jinra/*' --exclude='./root/*' --exclude='./tmp/*' \
    --exclude='./var/log/*' --exclude='./var/cache/apt/archives/*.deb' \
    --exclude='./var/lib/bluetooth/*' --exclude='./etc/NetworkManager/system-connections/*' \
    --exclude='./run/*' --exclude='./etc/ssh/ssh_host_*' --exclude='./var/lib/systemd/random-seed' \
    -cf - . | tar -C "$work/system" -xf -
for package in "$build"/efi-packages/*.deb; do
    dpkg-deb -x "$package" "$work/efi"
done
install -m644 "$(find "$work/efi/usr/lib" -name shimx64.efi.signed.latest -print -quit)" "$work/bootloader/BOOTX64.EFI"
install -m644 "$(find "$work/efi/usr/lib" -name grubx64.efi.signed -print -quit)" "$work/bootloader/grubx64.efi"
install -m644 "$work/efi/usr/lib/shim/mmx64.efi" "$work/bootloader/mmx64.efi"
install -Dm755 "$build/stage/usr/bin/jinra" "$root/usr/libexec/jinra/jinra"
install -Dm755 "$build/stage/usr/local/libexec/jinra-capture" "$root/usr/local/libexec/jinra-capture"
install -Dm755 "$build/stage/usr/local/bin/jinra-bluetooth" "$root/usr/local/bin/jinra-bluetooth"
install -Dm755 "$build/nvidia-dlss/lib/Linux_x86_64/rel/libnvidia-ngx-dlss.so.310.9.16" \
    "$root/usr/libexec/jinra/libnvidia-ngx-dlss.so.310.9.16"
install -Dm644 "$build/nvidia-dlss/LICENSE.txt" "$root/usr/share/doc/jinra-os/NVIDIA-DLSS-LICENSE.txt"
install -d "$root/usr/libexec/jinra/recipes"
install -m644 "$build/source/dist/recipes/"*.nx2recipe "$root/usr/libexec/jinra/recipes/"
ln -sfn ../libexec/jinra/jinra "$root/usr/bin/jinra"
for file in jinra-session jinra-record-gameplay jinra-select-display; do
    install -Dm755 "$source_dir/session/$file" "$root/usr/local/bin/$file"
done
install -Dm755 "$source_dir/session/jinra-supervisor" "$root/usr/local/libexec/jinra-supervisor"
install -Dm644 "$source_dir/session/90-jinra-wired.conf" "$root/etc/NetworkManager/conf.d/90-jinra-wired.conf"
install -Dm644 "$source_dir/session/jinra-display.service" "$root/etc/systemd/system/jinra-display.service"
bash "$source_dir/update/install-updater.sh" "$root" "$build/stage/usr" "$version"
install -d "$root/etc/modules-load.d"
printf '%s\n' nvidia_uvm > "$root/etc/modules-load.d/jinra-nvenc.conf"
rm -f "$root/etc/jinra/primary-monitor" "$root/etc/jinra/display-mode" \
    "$root/etc/jinra/shader-translator-legacy" "$root/etc/fstab"
truncate -s 0 "$root/etc/machine-id"
if [[ ! -x "$root/usr/bin/pactl" ]]; then
    # Older build roots already have these packages in their apt cache.
    mkdir -m755 "$root/tmp/jinra-debs"
    for package in libpulsedsp pulseaudio-utils; do
        cp -- "$build/system/var/cache/apt/archives/${package}_"*.deb "$root/tmp/jinra-debs/"
    done
    mount -t proc proc "$root/proc"
    mount --bind /dev "$root/dev"
    printf '#!/bin/sh\nexit 101\n' > "$root/usr/sbin/policy-rc.d"
    chmod 755 "$root/usr/sbin/policy-rc.d"
    chroot "$root" env DEBIAN_FRONTEND=noninteractive apt-get -y --no-download \
        --no-install-recommends -o Dpkg::Use-Pty=0 -o Dir::Cache::archives=/tmp/jinra-debs \
        install pulseaudio-utils
    rm -f "$root/usr/sbin/policy-rc.d"
    rm -rf -- "$root/tmp/jinra-debs"
    umount "$root/proc"
    umount "$root/dev"
fi
chroot "$root" ldd /usr/bin/jinra /usr/local/libexec/jinra-capture /usr/local/bin/jinra-bluetooth > "$work/ldd"
if grep -q 'not found' "$work/ldd"; then cat "$work/ldd"; exit 1; fi
test -x "$root/usr/bin/pactl"
chroot "$root" ldconfig -p | grep -F 'libnvidia-encode.so.1' >/dev/null
systemctl --root="$root" enable greetd.service jinra-display.service NetworkManager.service bluetooth.service
tar -C "$work" -cf - system bootloader | zstd -q -T4 -3 -f -o "$out.partial"
zstd -tq "$out.partial"
mv -f "$out.partial" "$out"
(cd "$(dirname "$out")" && sha256sum "$(basename "$out")" > "$(basename "$out").sha256")
