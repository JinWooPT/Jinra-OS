#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
import hashlib
import importlib.machinery
import importlib.util
import io
import json
from pathlib import Path
import sys
import tarfile
import tempfile


source = Path(__file__).with_name("jinra-os-update")
loader = importlib.machinery.SourceFileLoader("jinra_update", str(source))
spec = importlib.util.spec_from_loader(loader.name, loader)
updater = importlib.util.module_from_spec(spec)
loader.exec_module(updater)


def fails(action):
    try:
        action()
    except ValueError:
        return
    raise AssertionError("Unexpectedly accepted an invalid release")


release = Path(sys.argv[1])
manifest = (release / "jinra-os-update.json").read_bytes()
signature = (release / "jinra-os-update.sig").read_bytes()
key = source.with_name("releases.pub.pem")
verify = updater.verify_manifest
updater.verify_manifest = lambda raw, proof: verify(raw, proof, key)
metadata = updater.verify_manifest(manifest, signature)
fails(lambda: updater.verify_manifest(manifest + b" ", signature))
version = metadata["version"]

with tempfile.TemporaryDirectory(prefix="jinra-test-update-") as work:
    work = Path(work)
    base, state, saves = (work / name for name in ("base", "state", "home"))
    for folder in (base, state, saves):
        folder.mkdir()
    (base / "os-version").write_text("0.9.0\n")
    for name in ("saves", "mods", "shader-cache"):
        (saves / name).write_bytes(name.encode())
    before = {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in saves.iterdir()}
    updater.open_release = lambda url: (release / url.rsplit("/", 1)[-1]).open("rb")

    with (release / updater.ASSET).open("rb") as stream:
        altered = bytearray(stream.read())
    altered[-1] ^= 1
    healthy_open = updater.open_release
    updater.open_release = lambda url: io.BytesIO(altered) if url.endswith(updater.ASSET) else healthy_open(url)
    fails(lambda: updater.install(version, state, base))
    assert updater.selected_runtime(state, base) == base
    assert not list(state.glob(".download-*"))
    updater.open_release = healthy_open

    updater.install(version, state, base)
    active = updater.selected_runtime(state, base)
    assert active == state / "versions" / version
    assert updater.installed_version(active) == version
    assert all((active / name).is_file() for name in updater.EXECUTABLES)
    assert before == {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in saves.iterdir()}
    fails(lambda: updater.install(version, state, base))

    bad = work / "invalid.tar.gz"
    with tarfile.open(bad, "w:gz") as archive:
        entry = tarfile.TarInfo("../home/saves")
        entry.size = 1
        archive.addfile(entry, io.BytesIO(b"x"))
    fails(lambda: updater.unpack(bad, work / "staged"))
    assert before == {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in saves.iterdir()}
    assert not list(state.glob(".download-*"))

print("Verified the signed update, checksum failure, archive path validation, version switch and data preservation.")
