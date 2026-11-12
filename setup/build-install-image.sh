#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C
[[ $# == 3 && $EUID == 0 ]]
payload=$1
request=$2
output=$3
for tool in python3 zstd tar sgdisk mkfs.ext4 e2fsck mkfs.vfat mcopy mkfs.exfat fsck.exfat gzip; do
    if ! command -v "$tool" >/dev/null; then
        printf 'Missing WSL tool: %s. Install python3 zstd gdisk e2fsprogs dosfstools mtools exfatprogs.\n' "$tool" >&2
        exit 1
    fi
done

# The root image and the staged system can exceed a WSL /tmp mounted in RAM.
work=$(mktemp -d /var/tmp/jinra-setup.XXXXXX)
root="$work/system"
cleanup() {
    if mountpoint -q "$root/proc"; then umount "$root/proc"; fi
    if mountpoint -q "$root/dev"; then umount "$root/dev"; fi
    rm -rf -- "$work"
}
trap cleanup EXIT
python3 - "$request" > "$work/parameters" <<'PY'
import json, re, sys
with open(sys.argv[1], encoding="utf-8-sig") as stream:
    plan = json.load(stream)
profile, scale = plan["profile"], plan["upscaler"]
capacity, share = int(plan["capacity"]), int(plan["sharedMiB"])
partition, fs, folder = (plan.get(key, "") for key in ("libraryPartition", "libraryFilesystem", "libraryFolder"))
assert profile in {"nvidia", "amd"}, "Unknown graphics profile"
assert scale in ({"game", "dlss", "dlaa", "fsr31"} if profile == "nvidia" else {"game", "fsr31"}), "Unsupported upscaler for this profile"
assert capacity % 512 == 0 and 26 * 10**9 <= capacity <= 16 * 10**12, "Unsupported target capacity or sector size"
assert share == 0 or 1024 <= share <= 131072, "Shared partition must be between 1 and 128 GiB"
assert (24834 + share) * 1048576 <= capacity, "The selected disk is too small for this layout"
assert partition or share, "Select a separate library or create a shared partition"
if partition:
    assert re.fullmatch(r"[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}", partition), "The game library needs a GPT partition"
    assert fs in {"ntfs3", "exfat"}, "The game library needs NTFS or exFAT"
    assert not re.search(r"[\x00-\x1f\\]", folder), "Invalid game library path"
    assert not folder or all(part not in ("", ".", "..") for part in folder.split("/")), "Invalid game library path"
else:
    assert not (fs or folder)
print(profile, scale, capacity, share, partition, fs, folder, sep="\n")
PY
mapfile -t params < "$work/parameters"
profile=${params[0]}
scale=${params[1]}
capacity=${params[2]}
share=${params[3]}
partition=${params[4]}
filesystem=${params[5]}
folder=${params[6]}
printf 'Preparing the %s graphics profile.\n' "$profile"
zstd -dc -- "$payload" | tar -C "$work" -xf -
test -x "$root/usr/libexec/jinra/jinra"
test -x "$root/usr/local/libexec/jinra-capture"
test -x "$root/usr/local/libexec/jinra-supervisor"
test -x "$root/usr/local/bin/jinra-bluetooth"
test -x "$root/usr/local/bin/jinra-record-gameplay"
test -x "$root/usr/local/libexec/jinra-os-update"
test -x "$root/usr/local/bin/jinra-os-launch"
test -s "$root/usr/libexec/jinra/os-version"
test -s "$work/bootloader/BOOTX64.EFI"
test -s "$work/bootloader/grubx64.efi"

if [[ $profile == amd ]]; then
    chroot "$root" dpkg-query -W -f='${db:Status-Status} ${binary:Package}\n' > "$work/packages"
    mapfile -t packages < <(awk '$1 == "installed" && $2 ~ /^(libnvidia-|nvidia-|xserver-xorg-video-nvidia-|linux-(modules|objects|signatures)-nvidia-)/ {print $2}' "$work/packages")
    printf '#!/bin/sh\nexit 101\n' > "$root/usr/sbin/policy-rc.d"
    chmod 755 "$root/usr/sbin/policy-rc.d"
    mount -t proc proc "$root/proc"
    mount --bind /dev "$root/dev"
    if (( ${#packages[@]} )); then
        chroot "$root" env DEBIAN_FRONTEND=noninteractive apt-get -y --no-download purge "${packages[@]}"
    fi
    chroot "$root" update-initramfs -u -k all
    umount "$root/proc"
    umount "$root/dev"
    rm -f "$root/usr/sbin/policy-rc.d" "$root/etc/modprobe.d/jinra-nvidia.conf" \
        "$root/etc/modules-load.d/jinra-nvenc.conf" \
        "$root/usr/libexec/jinra/libnvidia-ngx-dlss.so.310.9.16"
    chroot "$root" dpkg-query -W -f='${db:Status-Status}\n' mesa-vulkan-drivers | grep -qx installed
    find "$root/lib/modules" -type f -name 'amdgpu.ko*' -print -quit | grep -q amdgpu
else
    chroot "$root" dpkg-query -W -f='${db:Status-Status}\n' nvidia-driver-595-open | grep -qx installed
    test -x "$root/usr/libexec/jinra/libnvidia-ngx-dlss.so.310.9.16"
fi

uid=$(chroot "$root" id -u jinra)
gid=$(chroot "$root" id -g jinra)
home="$root/home/jinra"
install -d -m755 -o "$uid" -g "$gid" "$home/Games" "$home/user" "$home/user/config"
install -d "$root/boot/efi" "$root/boot/grub" "$root/etc/systemd/system/greetd.service.d"
root_uuid=$(cat /proc/sys/kernel/random/uuid)
efi_id=$(python3 -c 'import secrets; print(secrets.token_hex(4).upper())')
efi_uuid="${efi_id:0:4}-${efi_id:4:4}"
printf 'UUID=%s / ext4 defaults 0 1\nUUID=%s /boot/efi vfat umask=0077 0 2\n' \
    "$root_uuid" "$efi_uuid" > "$root/etc/fstab"
mount_units=()
if (( share )); then
    truncate -s "${share}M" "$work/games"
    mkfs.exfat -L JINRA_GAMES "$work/games"
    fsck.exfat -n "$work/games"
    games_uuid=$(blkid -p -s UUID -o value "$work/games")
    printf 'UUID=%s /home/jinra/Games exfat uid=%s,gid=%s,umask=0077,nofail,x-systemd.device-timeout=8s 0 0\n' \
        "$games_uuid" "$uid" "$gid" >> "$root/etc/fstab"
    mount_units+=(home-jinra-Games.mount)
fi
if [[ -n $partition ]]; then
    install -d "$root/mnt/jinra-library"
    printf 'PARTUUID=%s /mnt/jinra-library %s ro,uid=%s,gid=%s,umask=0077,nofail,x-systemd.device-timeout=8s 0 0\n' \
        "$partition" "$filesystem" "$uid" "$gid" >> "$root/etc/fstab"
    ln -s "/mnt/jinra-library${folder:+/$folder}" "$home/Library"
    mount_units+=(mnt-jinra\\x2dlibrary.mount)
fi
printf '[Unit]\nWants=%s\nAfter=%s\n' "${mount_units[*]}" "${mount_units[*]}" \
    > "$root/etc/systemd/system/greetd.service.d/games-volume.conf"

dlss=false
quality=0
fsr=0
case "$scale" in
    dlss) dlss=true; quality=2 ;;
    dlaa) dlss=true; quality=1 ;;
    fsr31) fsr=2 ;;
esac
{
    # Store settings using the same enum indices and default flags as Jinra.
    cat <<'INI'
[Core]
memory_layout_mode\default=false
memory_layout_mode=4

[System]
use_docked_mode\default=false
use_docked_mode=1

[Renderer]
backend\default=false
backend=1
resolution_setup\default=false
resolution_setup=3
use_vsync\default=false
use_vsync=2
gpu_accuracy\default=false
gpu_accuracy=0
scaling_filter\default=false
scaling_filter=5
anti_aliasing\default=false
anti_aliasing=2
astc_recompression\default=false
astc_recompression=0
max_anisotropy\default=false
max_anisotropy=5
vram_usage_mode\default=false
vram_usage_mode=1
game_dlss_network\default=false
game_dlss_network=true
fsr_sharpening_slider\default=false
fsr_sharpening_slider=0
post_shader_enabled\default=false
post_shader_enabled=true
frame_gen\default=false
frame_gen=false
pin_gpu_threads\default=false
pin_gpu_threads=true
use_asynchronous_shaders\default=false
use_asynchronous_shaders=true
async_presentation\default=false
async_presentation=true
use_video_framerate\default=false
use_video_framerate=true
use_disk_shader_cache\default=false
use_disk_shader_cache=true
use_vulkan_driver_pipeline_cache\default=false
use_vulkan_driver_pipeline_cache=true
native_async_draws\default=false
native_async_draws=2
native_pipeline_wait_ms\default=false
native_pipeline_wait_ms=8
oot_high_refresh\default=false
oot_high_refresh=true
INI
    printf 'nvidia_dlss\\default=false\nnvidia_dlss=%s\n' "$dlss"
    printf 'nvidia_dlss_quality\\default=false\nnvidia_dlss_quality=%s\n' "$quality"
    printf 'oot_anti_aliasing\\default=false\noot_anti_aliasing=%s\n' "$fsr"
    if [[ -n $partition ]]; then
        printf '\n[Paths]\ngamedirs\\size=1\ngamedirs\\1\\path=/home/jinra/Library\n'
        printf 'gamedirs\\1\\deep_scan\\default=false\ngamedirs\\1\\deep_scan=true\n'
    fi
} > "$home/user/config/qt-config.ini"
chown "$uid:$gid" "$home/user/config/qt-config.ini"
chmod 600 "$home/user/config/qt-config.ini"

python3 - "$request" "$root" "$home/user" "$uid" "$gid" <<'PY'
import json, os, re, shutil, sys
from pathlib import Path

request, root, user = map(Path, sys.argv[1:4])
uid, gid = map(int, sys.argv[4:])
plan = json.loads(request.read_text(encoding='utf-8-sig'))
width, height, hz = (int(plan.get(key, 0)) for key in ('width', 'height', 'refresh'))
if width or height or hz:
    assert 320 <= width <= 16384 and 240 <= height <= 16384 and 24 <= hz <= 1000
    mode = root / 'etc/jinra/display-mode'
    mode.parent.mkdir(parents=True, exist_ok=True)
    mode.write_text(f'{width} {height} {hz}\n')
    monitor = plan.get('monitor', '')
    if monitor:
        assert re.fullmatch(r'[0-9a-f]{8}', monitor), 'Invalid monitor EDID code'
        (mode.parent / 'primary-monitor').write_text(f'{monitor}\n')

if plan.get('modsPath'):
    source = Path(plan['modsPath'])
    assert source.is_absolute() and source.is_dir() and not source.is_symlink()
    target = user / 'load'
    target.mkdir()
    for game in sorted(source.iterdir()):
        if not re.fullmatch(r'[0-9a-fA-F]{16}', game.name):
            continue
        if game.is_symlink() or not game.is_dir():
            raise ValueError(f'Invalid mod directory: {game.name}')
        for item in game.rglob('*'):
            if item.is_symlink() or not (item.is_dir() or item.is_file()):
                raise ValueError(f'Invalid file in {game.name} mod directory: {item.name}')
        shutil.copytree(game, target / game.name.upper())
        print(f'Imported local mods for {game.name.upper()}.')
    for path in [target, *target.rglob('*')]:
        os.chown(path, uid, gid)
PY

options=
if [[ $profile == nvidia ]]; then options=nvidia_drm.modeset=1; fi
mkdir -p "$work/efi/EFI/BOOT" "$work/efi/EFI/ubuntu"
cp "$work/bootloader/"* "$work/efi/EFI/BOOT/"
cat > "$work/efi/EFI/BOOT/grub.cfg" <<EOF
set timeout=0
search --no-floppy --fs-uuid --set=root $root_uuid
menuentry 'Jinra OS' {
    linux /boot/vmlinuz root=UUID=$root_uuid ro quiet loglevel=3 systemd.show_status=false vt.global_cursor_default=0 $options console=ttyS0,115200
    initrd /boot/initrd.img
}
EOF
cp "$work/efi/EFI/BOOT/grub.cfg" "$work/efi/EFI/ubuntu/grub.cfg"
cp "$work/efi/EFI/BOOT/grub.cfg" "$root/boot/grub/grub.cfg"
test -s "$root/boot/vmlinuz"
test -s "$root/boot/initrd.img"

printf 'Creating the EFI, system and library partitions.\n'
truncate -s 256M "$work/esp"
mkfs.vfat -F32 -i "$efi_id" -n JINRAEFI "$work/esp"
mcopy -si "$work/esp" "$work/efi/EFI" ::/
truncate -s 24576M "$work/rootfs"
mkfs.ext4 -q -U "$root_uuid" -L JINRAOS -d "$root" "$work/rootfs"
e2fsck -fn "$work/rootfs"

disk="$work/disk"
truncate -s "$capacity" "$disk"
gpt=(--clear --new=1:2048:+256M --typecode=1:ef00 --change-name=1:Jinra-EFI
    --new=2:526336:+24576M --typecode=2:8300 --change-name=2:Jinra-OS)
if (( share )); then
    gpt+=(--new="3:50857984:+${share}M" --typecode=3:0700 --change-name=3:Jinra-Games)
fi
sgdisk "${gpt[@]}" "$disk"
dd if="$work/esp" of="$disk" bs=1M seek=1 conv=sparse,notrunc status=none
dd if="$work/rootfs" of="$disk" bs=1M seek=257 conv=sparse,notrunc status=none
if (( share )); then
    dd if="$work/games" of="$disk" bs=1M seek=24833 conv=sparse,notrunc status=none
fi
sgdisk --verify "$disk"

printf 'Packaging the verified disk image for Windows.\n'
front=$(( (24833 + share) * 1048576 ))
backup_offset=$(( capacity - 33 * 512 ))
head -c "$front" "$disk" | gzip -1 > "$output/front.img.gz"
dd if="$disk" of="$output/backup.gpt" bs=512 skip=$(( backup_offset / 512 )) count=33 status=none
(cd "$output" && sha256sum front.img.gz backup.gpt > image.sha256)
printf '{"capacity":%s,"frontBytes":%s,"backupOffset":%s}\n' "$capacity" "$front" "$backup_offset" > "$output/image.json"
printf 'Image prepared and its partition table verified.\n'
