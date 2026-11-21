#!/usr/bin/env bash
#
# NitroLite — builds the .deb and .rpm from a finished publish. Used by both the release and the CI workflow, so the
# package CI installs is built exactly like the one that ships.
#
#   packaging/linux/build-packages.sh VERSION OUTDIR
#
# Expects staging/NitroLite-linux-x64/ (the self-contained publish) and runs from the repository root. Prints the
# two package paths as deb=… and rpm=… lines for $GITHUB_OUTPUT.

set -euo pipefail

VERSION="${1:?usage: build-packages.sh VERSION OUTDIR}"
OUTDIR="${2:?usage: build-packages.sh VERSION OUTDIR}"

# nfpm, pinned and checked against the digest its release publishes, so the packager cannot change under us.
NFPM_VERSION=2.48.0
NFPM_SHA256=6d7f9a28b2f397934403950fcbba83f598b08d02edbe79a0815b3b4897341750

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
curl -fsSL -o "$work/nfpm.tar.gz" \
    "https://github.com/goreleaser/nfpm/releases/download/v${NFPM_VERSION}/nfpm_${NFPM_VERSION}_Linux_x86_64.tar.gz"
echo "${NFPM_SHA256}  $work/nfpm.tar.gz" | sha256sum -c - >&2
tar -xzf "$work/nfpm.tar.gz" -C "$work" nfpm

# The packages install to /opt/nitrolite, so that is the path the helper's unit runs.
sed -e 's|@APP_DIR@|/opt/nitrolite|g' packaging/linux/nitrolite-helper.service.in > staging/nitrolite-helper.service
if grep -q '@APP_DIR@' staging/nitrolite-helper.service; then
    echo "error: the helper unit still has a placeholder" >&2
    exit 1
fi
rm -f staging/NitroLite-linux-x64/*.pdb

mkdir -p "$OUTDIR"
DEB="$OUTDIR/NitroLite-v$VERSION-amd64.deb"
RPM="$OUTDIR/NitroLite-v$VERSION-x86_64.rpm"
export VERSION
"$work/nfpm" package --config packaging/linux/nfpm.yaml --packager deb --target "$DEB" >&2
"$work/nfpm" package --config packaging/linux/nfpm.yaml --packager rpm --target "$RPM" >&2

# A package missing its binary, its helper or its polkit rule installs cleanly and does nothing.
if command -v dpkg-deb >/dev/null 2>&1; then
    dpkg-deb --contents "$DEB" > "$work/contents.txt"
    for f in ./opt/nitrolite/NitroLite.Desktop ./opt/nitrolite/libSkiaSharp.so \
             ./usr/lib/systemd/system/nitrolite-helper.socket ./usr/lib/systemd/system/nitrolite-helper.service \
             ./usr/share/polkit-1/actions/io.github.canerztk8.nitrolite.policy \
             ./usr/lib/udev/rules.d/99-nitrolite.rules ./usr/share/applications/nitrolite.desktop; do
        grep -q " $f" "$work/contents.txt" || { echo "error: $f is missing from the .deb" >&2; exit 1; }
    done
fi

( cd "$OUTDIR" && sha256sum "$(basename "$DEB")" > "$(basename "$DEB").sha256" \
               && sha256sum "$(basename "$RPM")" > "$(basename "$RPM").sha256" )

echo "deb=$DEB"
echo "rpm=$RPM"
