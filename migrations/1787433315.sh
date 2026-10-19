echo "Early-load the Apple HID drivers so the trackpad survives the boot race"

# install/hardware/apple/fix-asahi-hid-race.sh runs on new installs only, so
# existing ones keep losing the trackpad on unlucky boots until this runs it.
# See docs/apple-silicon-trackpad.md.
OMARCHY_PATH="${OMARCHY_PATH:-/usr/share/omarchy}"
hid_race_script="$OMARCHY_PATH/install/hardware/apple/fix-asahi-hid-race.sh"
conf="${OMARCHY_APPLE_HID_CONF:-/etc/mkinitcpio.conf.d/apple_hid_modules.conf}"

[[ -f $hid_race_script ]] || exit 0
[[ -f $conf ]] && exit 0 # already configured, by the installer or another user

# The leaf gates on the hardware itself and writes the drop-in, so running it
# keeps one copy of both. Anything that is not an Apple Silicon Mac writes
# nothing and falls out below.
bash -euo pipefail "$hid_race_script"
[[ -f $conf ]] || exit 0

# MODULES reaches the boot only through a rebuilt initramfs, so the drop-in on
# its own would look applied and change nothing.
echo "Rebuilding the initramfs so the Apple HID drivers load early"
if ! sudo mkinitcpio -P; then
  echo "mkinitcpio failed. Run 'sudo mkinitcpio -P' to early-load the Apple HID drivers." >&2
  exit 0
fi

omarchy-state set reboot-required
