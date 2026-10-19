#!/bin/bash

set -euo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/base-test.sh"

# Static checks first: these run on any machine (and in CI on aarch64) and
# assert the repo's aarch64 surface without needing Asahi hardware. The dynamic
# checks at the bottom simulate aarch64 with stubbed commands.

# x86 channel templates stay Architecture=auto. Apple Silicon and other aarch64
# machines install from default/pacman/<platform>/, not from these files.
for config in "$ROOT"/default/pacman/pacman-*.conf; do
  grep -qF 'Architecture = auto' "$config" ||
    fail "x86 channel templates keep Architecture=auto" "missing in: $(basename "$config")"
  ! grep -qF 'Architecture = aarch64' "$config" ||
    fail "x86 channel templates do not pin aarch64" "$(basename "$config")"
done
pass "x86 channel templates keep Architecture=auto"

apple_edge="$ROOT/default/pacman/aarch64-apple/pacman-edge.conf"
[[ -f $apple_edge ]] || fail "Apple Silicon has an edge pacman template"
grep -qF 'https://pkgs.omarchy.org/edge/$arch' "$apple_edge" ||
  fail "Apple Silicon edge uses the Omarchy edge repository"
grep -qF 'https://github.com/asahi-alarm/asahi-alarm/releases/download/aarch64' "$apple_edge" ||
  fail "Apple Silicon edge includes the Asahi ALARM repository"
grep -qF 'Include = /etc/pacman.d/mirrorlist' "$apple_edge" ||
  fail "Apple Silicon edge reads core and alarm from the platform mirrorlist"
! grep -qF 'SigLevel = Optional TrustAll' "$apple_edge" ||
  fail "Apple Silicon edge does not trust unsigned packages"
apple_mirror="$ROOT/default/pacman/aarch64-apple/mirrorlist-edge"
grep -qF 'https://arm-mirror.omarchy.org/' "$apple_mirror" ||
  fail "Apple Silicon edge uses the ARM mirror"
pass "Apple Silicon edge template is signed and uses the ARM repositories"

# The x86 mirrorlists are the upstream Omarchy mirrors. They must not be the
# old Arch Linux ARM lists, which would send an x86 install at the wrong archive.
for mirror in stable rc edge; do
  mirrorlist="$ROOT/default/pacman/mirrorlist-$mirror"
  grep -qF 'mirror.omarchy.org' "$mirrorlist" ||
    fail "x86 mirrorlist-$mirror points at the Omarchy mirror"
  ! grep -qF 'archlinuxarm.org' "$mirrorlist" ||
    fail "x86 mirrorlist-$mirror does not point at Arch Linux ARM"
done
pass "x86 mirrorlists point at the Omarchy mirror"

# Paths must derive from OMARCHY_PATH (AGENTS.md); a hardcoded HOME breaks once
# the checkout is wired to /usr/share/omarchy.
if grep -qF '$HOME/.local/share/omarchy' "$ROOT/bin/omarchy-refresh-pacman-mirrorlist"; then
  fail "the mirrorlist refresh derives its source from OMARCHY_PATH, not HOME"
fi
pass "the mirrorlist refresh derives its source from OMARCHY_PATH"

# The upstream x86 upgrade rewrites /etc/pacman.d/mirrorlist to x86 mirrors and
# must refuse to run on Apple Silicon (the inverse of the guard in
# omarchy-upgrade-to-quattro-mac). It is not safe to execute here — it writes
# /etc through sudo — so assert the guard statically instead.
grep -qF 'uname -m' "$ROOT/bin/omarchy-upgrade-to-quattro" &&
  grep -qF 'aarch64' "$ROOT/bin/omarchy-upgrade-to-quattro" ||
  fail "the upstream x86 upgrade fences itself off on Apple Silicon"
pass "the upstream x86 upgrade fences itself off on Apple Silicon"

# --- Simulated aarch64 environment ------------------------------------------

# The Mac fork's safety net: omarchy-pkg-add must skip a package the Arch ARM
# repos do not serve, instead of passing it to pacman and failing the whole
# transaction. Simulate pacman -Q/-Si/-S with a stub; only the ARM package
# "exists" in the fake repos.
test_tmp=$(mktemp -d)
trap 'rm -rf "$test_tmp"' EXIT

stub_bin="$test_tmp/bin"
mkdir -p "$stub_bin"

write_stub() {
  local name="$1"
  local body="$2"

  cat >"$stub_bin/$name" <<<"$body"
  chmod +x "$stub_bin/$name"
}

# -Q answers "installed" only after a -S has installed the package (the helper
# re-checks with -Q after installing), and only for the one package that exists.
write_stub omarchy-hw-x86 '#!/bin/bash
exit 1
'

write_stub pacman '#!/bin/bash
name=$2
[[ $name == "--" ]] && name=$3
case "$1" in
  -Q)
    if [[ $name == "$ARM_ONLY_PKG" ]]; then
      [[ -f $PKG_TEST_STATE ]] && exit 0
      touch "$PKG_TEST_STATE"
    fi
    exit 1
    ;;
  -Si)
    if [[ $name == "$ARM_ONLY_PKG" ]]; then
      echo "Repository : extra"
      echo "Name        : $name"
      exit 0
    fi
    exit 1
    ;;
  -S)
    printf "pacman -S %s\n" "$*" >>"$PKG_TEST_LOG"
    ;;
  *) exit 1 ;;
esac
'

write_stub omarchy-pkg-missing '#!/bin/bash
exit 0
'

write_stub sudo '#!/bin/bash
"$@"
'

ARM_ONLY_PKG="ripgrep" \
  PKG_TEST_STATE="$test_tmp/installed" \
  PKG_TEST_LOG="$test_tmp/install.log" \
  PATH="$stub_bin:$PATH" \
  "$ROOT/bin/omarchy-pkg-add" ripgrep lib32-nvidia-utils >/dev/null 2>"$test_tmp/skip.log"

grep -qF "Skipping 'lib32-nvidia-utils'" "$test_tmp/skip.log" ||
  fail "the package helper skips packages the repos do not serve" "$(cat "$test_tmp/skip.log")"
grep -qF 'ripgrep' "$test_tmp/install.log" ||
  fail "the package helper installs the available package" "$(cat "$test_tmp/install.log")"
! grep -qF 'lib32-nvidia-utils' "$test_tmp/install.log" ||
  fail "the package helper never passes an unavailable package to pacman" "$(cat "$test_tmp/install.log")"
pass "the package helper skips x86-only packages instead of failing the install"
