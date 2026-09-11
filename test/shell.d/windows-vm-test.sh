#!/bin/bash

set -euo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/base-test.sh"

windows_vm_command="$ROOT/bin/omarchy-windows-vm"

rg -q '^    restart: "no"$' "$windows_vm_command" ||
  fail "Windows VM uses manual startup by default"
pass "Windows VM uses manual startup by default"

if rg -q '^    restart: unless-stopped$' "$windows_vm_command"; then
  fail "Windows VM does not restart automatically at boot"
fi
pass "Windows VM does not restart automatically at boot"

rg -q 'DISPLAY: "gtk,zoom-to-fit=on,grab-on-hover=on"' "$windows_vm_command" ||
  fail "Windows VM uses the QEMU GTK display backend"
pass "Windows VM uses the QEMU GTK display backend"

rg -q 'GDK_BACKEND: "wayland"' "$windows_vm_command" ||
  fail "Windows VM forces GTK onto Wayland"
pass "Windows VM forces GTK onto Wayland"

if rg -q 'xfreerdp|3389|/tmp/.X11-unix|GDK_BACKEND: "x11"' "$windows_vm_command"; then
  fail "Windows VM does not use RDP or X11"
fi
pass "Windows VM does not use RDP or X11"
