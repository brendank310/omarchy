#!/bin/bash

set -euo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/base-test.sh"

post_install="$ROOT/install/post-install/pacman.sh"

# post-install/pacman.sh overwrites /etc/pacman.conf with one of these variants.
# pacman refuses to parse a config whose Include is missing, so every included
# file has to be installed alongside it or the system loses pacman entirely.
missing=()
while read -r included; do
  base=${included##*/}
  grep -qF "$base" "$post_install" || missing+=("$included")
done < <(grep -h '^Include = /etc/pacman.d/' "$ROOT"/default/pacman/pacman-*.conf |
  awk '{ print $3 }' | sort -u)

if (( ${#missing[@]} )); then
  fail "pacman config restore installs every mirrorlist it includes" \
    "not installed by post-install/pacman.sh:$(printf '\n  %s' "${missing[@]}")"
fi
pass "pacman config restore installs every mirrorlist it includes"

# The source has to ship too, or the copy above silently does nothing.
while read -r included; do
  base=${included##*/}
  [[ $base == "mirrorlist" ]] && continue
  [[ -f "$ROOT/default/pacman/$base" ]] ||
    fail "the included mirrorlist ships in the repo" "missing: default/pacman/$base"
done < <(grep -h '^Include = /etc/pacman.d/' "$ROOT"/default/pacman/pacman-*.conf |
  awk '{ print $3 }' | sort -u)
pass "every included mirrorlist ships in the repo"

# Finalization copies the platform's channel template and mirrorlist whole.
# Apple Silicon's mirrorlist is Omarchy's ARM mirror, not the mirrorlist the
# base image happened to have. A source install that already prepared its
# config sets OMARCHY_PRESERVE_PACMAN_CONFIG and skips that copy.
grep -qF 'cp -f "$templates/pacman-$channel.conf" /etc/pacman.conf' "$post_install" ||
  fail "pacman config restore installs the platform pacman.conf"
grep -qF 'cp -f "$templates/mirrorlist-$channel" /etc/pacman.d/mirrorlist' "$post_install" ||
  fail "pacman config restore installs the platform mirrorlist"
grep -qF 'OMARCHY_PRESERVE_PACMAN_CONFIG' "$post_install" ||
  fail "a source install can keep the pacman config it already prepared"
pass "pacman config restore installs the platform template and mirrorlist"
