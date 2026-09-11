#!/bin/bash
set -euo pipefail
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/base-test.sh"
assert_eq() {
  [[ $1 == "$2" ]] || fail "$3"
  pass "$3"
}

runtime=$(mktemp -d)
trap 'rm -rf "$runtime"' EXIT
export OMARCHY_WINDOWS_DIR="$runtime"
set -- help
source "$ROOT/bin/omarchy-windows-vm" >/dev/null
CALLER_UID=$(id -u)

cat >"$DISPLAY_OVERRIDE" <<'EOF'
services:
  windows:
    image: omarchy-windows-dmabuf
    environment:
      OMARCHY_RENDERER: "dmabuf"
      DISPLAY: "dbus,gl=on"
EOF
chmod 600 "$DISPLAY_OVERRIDE"
write_display_override() { fail "DMA-BUF overlay was overwritten with GTK"; }
ensure_gtk_image() { fail "DMA-BUF startup unexpectedly built GTK"; }
write_oem_override() { printf 'updated' >"$runtime/oem-result"; }

before=$(sha256sum "$DISPLAY_OVERRIDE")
prepare_display_and_oem wayland-1
assert_eq "$before" "$(sha256sum "$DISPLAY_OVERRIDE")" "Enhanced startup preserves the display overlay"
assert_eq "$DISPLAY_OVERRIDE" "$(display_override_file)" "Enhanced startup selects the persistent overlay"
assert_eq updated "$(cat "$runtime/oem-result")" "Enhanced startup still refreshes OEM arguments"
chmod 666 "$DISPLAY_OVERRIDE"
if enhanced_display_configured; then fail "Writable overlay must not enable enhanced startup"; fi
pass "Untrusted overlay cannot enable enhanced startup"
