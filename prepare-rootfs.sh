#!/usr/bin/env bash
set -euo pipefail

if [[ $# != 1 || $EUID != 0 ]]; then
    printf 'Usage: sudo bash %s BUILD_DIRECTORY\n' "$0" >&2
    exit 2
fi

source_dir=$(dirname "$(realpath "$0")")
build_dir=$(realpath -m "$1")
root="$build_dir/system"
mkdir -p "$root"
rm -f "$build_dir/rootfs-ready"

if [[ ! -f "$root/etc/os-release" ]]; then
    debootstrap --arch=amd64 --variant=minbase resolute "$root" \
        http://archive.ubuntu.com/ubuntu/
fi

cat > "$root/etc/apt/sources.list" <<'EOF'
deb http://archive.ubuntu.com/ubuntu resolute main restricted universe multiverse
deb http://archive.ubuntu.com/ubuntu resolute-updates main restricted universe multiverse
deb http://security.ubuntu.com/ubuntu resolute-security main restricted universe multiverse
EOF

printf '#!/bin/sh\nexit 101\n' > "$root/usr/sbin/policy-rc.d"
chmod 755 "$root/usr/sbin/policy-rc.d"
mount -t proc proc "$root/proc"
trap 'umount "$root/proc"' EXIT

chroot "$root" apt-get update
chroot "$root" env DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
    systemd-sysv systemd-resolved dbus dbus-user-session polkitd \
    linux-image-generic linux-firmware initramfs-tools intel-microcode \
    linux-modules-nvidia-595-open-generic nvidia-driver-595-open \
    gamescope greetd xwayland mesa-vulkan-drivers vulkan-tools \
    libqt6gui6 qt6-wayland fonts-dejavu-core \
    pipewire-audio wireplumber pulseaudio-utils network-manager bluez ca-certificates libarchive-tools \
    gstreamer1.0-tools gstreamer1.0-pipewire gstreamer1.0-plugins-good \
    gstreamer1.0-plugins-bad gstreamer1.0-plugins-ugly

if ! chroot "$root" id jinra >/dev/null 2>&1; then
    chroot "$root" useradd --create-home --shell /bin/bash jinra
fi
install -Dm755 "$source_dir/session/jinra-session" "$root/usr/local/bin/jinra-session"
install -Dm755 "$source_dir/session/jinra-supervisor" "$root/usr/local/libexec/jinra-supervisor"
install -Dm755 "$source_dir/session/jinra-record-gameplay" "$root/usr/local/bin/jinra-record-gameplay"
install -Dm755 "$source_dir/session/jinra-select-display" "$root/usr/local/bin/jinra-select-display"
install -Dm644 "$source_dir/session/jinra-display.service" "$root/etc/systemd/system/jinra-display.service"
install -Dm644 "$source_dir/session/greetd.toml" "$root/etc/greetd/config.toml"
install -Dm644 "$source_dir/session/90-jinra-wired.conf" "$root/etc/NetworkManager/conf.d/90-jinra-wired.conf"
printf 'jinra-os\n' > "$root/etc/hostname"
printf '127.0.0.1 localhost\n127.0.1.1 jinra-os\n' > "$root/etc/hosts"
sed -i 's/^PRETTY_NAME=.*/PRETTY_NAME="Jinra OS"/' "$root/usr/lib/os-release"
mkdir -p "$root/etc/modprobe.d"
printf 'options nvidia_drm modeset=1\n' > "$root/etc/modprobe.d/jinra-nvidia.conf"
install -d "$root/etc/modules-load.d"
printf '%s\n' nvidia_uvm > "$root/etc/modules-load.d/jinra-nvenc.conf"

systemctl --root="$root" mask getty@tty1.service
systemctl --root="$root" enable jinra-display.service greetd.service NetworkManager.service bluetooth.service
systemctl --root="$root" set-default graphical.target
kernel=$(basename "$(find "$root/boot" -maxdepth 1 -type f -name 'vmlinuz-*' | sort -V | tail -1)")
kernel_version=${kernel#vmlinuz-}
if [[ -f "$root/boot/initrd.img-$kernel_version" ]]; then
    chroot "$root" update-initramfs -u -k "$kernel_version"
else
    chroot "$root" update-initramfs -c -k "$kernel_version"
fi
test -s "$root/boot/initrd.img-$kernel_version"
chroot "$root" apt-get clean
rm -f "$root/usr/sbin/policy-rc.d"
truncate -s 0 "$root/etc/machine-id"

mkdir -p "$build_dir/efi-packages"
cd "$build_dir/efi-packages"
apt-get download shim-signed grub-efi-amd64-signed
touch "$build_dir/rootfs-ready"
