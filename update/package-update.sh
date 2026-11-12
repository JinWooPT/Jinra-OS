#!/usr/bin/env bash
set -euo pipefail

[[ $# == 4 ]]
stage=$(realpath "$1")
output=$(realpath "$2")
version=$3
key=$(realpath "$4")
[[ $version =~ ^(0|[1-9][0-9]{0,5})\.(0|[1-9][0-9]{0,5})\.(0|[1-9][0-9]{0,5})$ ]]
source=$(dirname "$(realpath "$0")")/..
build=${stage%/stage/usr}
cmp "$source/update/releases.pub.pem" <(openssl pkey -in "$key" -pubout)
scratch=$(mktemp -d)
trap 'rm -rf -- "$scratch"' EXIT
install -m755 "$stage/bin/jinra" "$scratch/jinra"
install -m755 "$stage/local/libexec/jinra-capture" "$scratch/jinra-capture"
install -m755 "$stage/local/bin/jinra-bluetooth" "$scratch/jinra-bluetooth"
for name in jinra-session jinra-supervisor jinra-record-gameplay; do
    install -m755 "$source/session/$name" "$scratch/$name"
done
mkdir "$scratch/recipes"
install -m644 "$build/source/dist/recipes/"*.nx2recipe "$scratch/recipes/"
asset=jinra-os-update-linux-x64.tar.gz
(
    cd "$scratch"
    tar -czf "$output/$asset" jinra jinra-capture jinra-bluetooth jinra-session jinra-supervisor \
        jinra-record-gameplay recipes/*.nx2recipe
)
python3 - "$output" "$version" "$asset" <<'PY'
import hashlib, json, sys
from pathlib import Path

output = Path(sys.argv[1])
asset = output / sys.argv[3]
with asset.open("rb") as stream:
    digest = hashlib.file_digest(stream, "sha256").hexdigest()
manifest = {"schema": 1, "version": sys.argv[2], "asset": asset.name,
            "size": asset.stat().st_size, "sha256": digest}
(output / "jinra-os-update.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
PY
openssl pkeyutl -sign -inkey "$key" -rawin -in "$output/jinra-os-update.json" -out "$output/jinra-os-update.sig"
