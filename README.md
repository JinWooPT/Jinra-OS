# Jinra OS

Jinra OS is a dedicated x64 Linux system that boots directly into Jinra Home. It includes the controller interface, shader preparation, Shader Sync and 4K60 gameplay recording with the performance overlay.

## Downloads

| File | Use |
| --- | --- |
| [Jinra OS Setup for Windows](https://github.com/JinWooPT/Jinra-OS/releases/latest/download/Jinra-OS-Setup-Windows-x64.zip) | Extract the Setup and its support files. |
| [Jinra OS system image](https://github.com/JinWooPT/Jinra-OS/releases/latest/download/jinra-os-payload.tar.zst) | Keep this file compressed and place it beside the Setup executable. |

The Windows ZIP includes the SHA-256 file for the system image. Download both files from the [same release](https://github.com/JinWooPT/Jinra-OS/releases/latest).

## First installation

1. Use Windows 11 x64 with the .NET 10 Desktop Runtime and Ubuntu under WSL 2. In Ubuntu, install `python3 zstd gdisk e2fsprogs dosfstools mtools exfatprogs` with `sudo apt install`.
2. Run **Jinra OS Setup.exe** and select a dedicated disk. The Setup replaces the partitions on the selected target disk.
3. Choose **NVIDIA** or **AMD** and select the display mode. You can keep games in an existing folder on a separate NTFS or exFAT GPT disk. Jinra OS mounts that library read-only. A **JINRA_GAMES** partition gives Windows access to recordings and logs.
4. To import external mods, select **Copy mods from Jinra to Jinra OS** and choose the `user/load` folder from Jinra on Windows. Compatible built-in patches are already included.
5. Select **Prepare disk** and boot from it using the PC's UEFI boot menu. Open **Language** in the Setup for Portuguese.

See the [Setup guide in Portuguese](setup/README.md) for disk sizing, firmware installation and controller pairing. Games, console keys and console firmware are supplied by the user.

## Update an existing installation

Open **Power → System updates** in Jinra Home. Choose **Install update**, then **Restart** to activate it. The update downloads from this repository and preserves saves, mods, user settings and the shader cache. It uses the existing disk and partitions.

The current updater covers Jinra and the components of its Home session, including the recorder, Bluetooth pairing tool and bundled shader recipes. The Ubuntu kernel, graphics drivers and bootloader are part of the base image. See the [update format and release process](update/README.md).

## Source

The Setup, system scripts and updater live in this repository. The corresponding Linux emulator source is included with each release that ships a new emulator binary. The [Jinra emulator](https://github.com/JinWooPT/Jinra) is the underlying project. The NVIDIA DLSS runtime in the NVIDIA profile is distributed under the NVIDIA SDK terms included with the image. Ubuntu package licences and copyright files are included under `/usr/share/doc`.
Jinra OS Setup and updates for the dedicated Jinra Home system.
