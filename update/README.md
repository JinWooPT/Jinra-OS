# Jinra OS updates

Open the power menu in Jinra Home and select **System updates**. The dialog shows the installed and available versions. Select **Install update**, then restart Jinra OS to activate it. The Setup application prepares disks for first-time installation.

This update format covers the Jinra emulator, Home session, bundled shader preparation recipes, Bluetooth pairing tool and gameplay recorder. The Ubuntu kernel, bootloader and graphics drivers belong to the base image. Saves, mods, shaders, user settings, firmware and game libraries live outside the update directory. Compatible shader caches are reused. A change to the shader translator or driver can still require new pipelines to be built.

The update service stages a complete runtime in `/var/lib/jinra-os/versions`, verifies it, then switches the active version. The running session keeps using its own runtime until restart. If the updated session repeatedly fails to start, the launcher falls back to the runtime shipped on the disk for that boot. Inspect failures with `journalctl -u 'jinra-os-update@*.service'`.

## Release a runtime update

Build and stage the Linux emulator and helpers. With the official signing key available locally, run:

```bash
bash update/package-update.sh BUILD_DIRECTORY/stage/usr RELEASE_DIRECTORY X.Y.Z PRIVATE_SIGNING_KEY
python3 update/test-updater.py RELEASE_DIRECTORY
```

Create a stable GitHub Release tagged `vX.Y.Z` with the following assets:

- `jinra-os-update.json`
- `jinra-os-update.sig`
- `jinra-os-update-linux-x64.tar.gz`

Keep the private key off GitHub. The trusted public key is installed with Jinra OS. The updater checks the Ed25519 signature on the manifest and the SHA-256 of the runtime archive. Release versions increase numerically. Upload the complete set of assets before making the release public.

For a fresh-install release, also run `sudo bash setup/package-payload.sh BUILD_DIRECTORY RELEASE_DIRECTORY/jinra-os-payload.tar.zst X.Y.Z` and include the Windows Setup with the matching `.sha256` file. Keep the release download names unchanged so the Setup and Home updater can find their files.
