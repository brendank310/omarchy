#!/bin/bash

# Asahi Alarm ships LANG=C and Omarchy Mac has no ISO step to replace it, so a
# by-hand install runs non-UTF-8: byte-wise sorting, ASCII-only \u escapes, and
# any tool that reads the locale for its encoding.
locale_conf="${OMARCHY_LOCALE_CONF:-/etc/locale.conf}"
locale_gen="${OMARCHY_LOCALE_GEN:-/etc/locale.gen}"

# Repair only the stock state -- an unset LANG, or the bare C/POSIX the image
# ships. Any named locale is somebody's choice, C.UTF-8 included, so leave it.
# A machine with no locale.conf at all reads as unset, not as a failure:
# under pipefail the missing file would otherwise abort the installer.
current=$(sed -n 's/^LANG=//p' "$locale_conf" 2>/dev/null | tail -1 | tr -d '"') || current=""

case ${current:-C} in
  C | POSIX) ;;
  *)
    echo "Leaving the locale as $current"
    return 0 2>/dev/null || exit 0
    ;;
esac

if (( ${EUID:-$(id -u)} == 0 )); then
  as_root=()
else
  as_root=(sudo)
fi

echo "Setting up locale (en_US.UTF-8)..."

if ! locale -a 2>/dev/null | grep -qi "en_US.utf-\?8"; then
  if grep -q '^#en_US.UTF-8' "$locale_gen" 2>/dev/null; then
    "${as_root[@]}" sed -i 's/^#en_US.UTF-8/en_US.UTF-8/' "$locale_gen"
  elif ! grep -q '^en_US.UTF-8' "$locale_gen" 2>/dev/null; then
    echo "en_US.UTF-8 UTF-8" | "${as_root[@]}" tee -a "$locale_gen" >/dev/null
  fi

  "${as_root[@]}" locale-gen >/dev/null 2>&1
fi

echo "LANG=en_US.UTF-8" | "${as_root[@]}" tee "$locale_conf" >/dev/null

# The session that ran this keeps its inherited LANG; everything after it here
# should see the new one.
export LANG=en_US.UTF-8

echo "Locale set to en_US.UTF-8"
