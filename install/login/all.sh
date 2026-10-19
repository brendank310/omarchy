run_logged "$OMARCHY_INSTALL/login/sddm.sh"
# x86 installs limine from the ISO, which already carries the Plymouth theme
# and kernel command line. Apple Silicon has no limine and no ISO step, so the
# boot splash and the non-limine bootloader entries are configured here.
if omarchy-hw-aarch64-apple; then
  run_logged "$OMARCHY_INSTALL/login/plymouth.sh"
  run_logged "$OMARCHY_INSTALL/login/alt-bootloaders.sh"
fi
