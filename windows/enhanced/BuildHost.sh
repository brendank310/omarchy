#!/bin/bash
set -euo pipefail

# Build an Arch package from this checkout and the previously built guest ZIP.
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
[[ -f $root/windows/enhanced/artifacts/EnhancedWindows-win-x64.zip ]] || {
  echo "Build the guest package with windows/enhanced/Build.ps1 first." >&2
  exit 1
}
mkdir -p "$work/integration/windows/enhanced/Host" "$work/integration/windows/enhanced/artifacts" "$work/integration/bin"
cp "$root/LICENSE" "$work/integration/"
cp "$root/bin/omarchy-windows-app" "$root/bin/omarchy-default-office" "$root/bin/omarchy-launch-office" "$root/bin/omarchy-setup-windows-office" "$work/integration/bin/"
cp "$root/windows/enhanced/Host/"*.{c,h,py,md} "$root/windows/enhanced/Host/Makefile" "$root/windows/enhanced/Host/Dockerfile" "$root/windows/enhanced/Host/display-bus.conf" "$work/integration/windows/enhanced/Host/"
cp "$root/windows/enhanced/Smoke.py" "$work/integration/windows/enhanced/"
cp -r "$root/windows/enhanced/Packaging" "$work/integration/windows/enhanced/"
cp "$root/windows/enhanced/artifacts/EnhancedWindows-win-x64.zip" "$work/integration/windows/enhanced/artifacts/"
tar -C "$work" -czf "$work/integration.tar.gz" integration
cp "$root/windows/enhanced/Packaging/PKGBUILD" "$work/PKGBUILD"
hash=$(sha256sum "$work/integration.tar.gz")
sed -i "s/GENERATED_BY_BUILD_HOST/${hash%% *}/" "$work/PKGBUILD"
cd "$work"
makepkg --force --cleanbuild
cp "$work/"*.pkg.tar.zst "$root/windows/enhanced/artifacts/"
for package in "$root/windows/enhanced/artifacts/"*.pkg.tar.zst; do
  sha256sum "$package" >"$package.sha256"
done
