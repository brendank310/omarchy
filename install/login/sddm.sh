# Prevent password-based SDDM logins from creating an encrypted login keyring
# that conflicts with Omarchy's passwordless default keyring behavior. The ISO
# owns autologin/session state because it knows whether the target is encrypted.
pam_file="${OMARCHY_SDDM_PAM_FILE:-/etc/pam.d/sddm}"
state_file="${OMARCHY_SDDM_STATE_FILE:-/var/lib/sddm/state.conf}"

if [[ -f $pam_file ]]; then
  sed -i '/-auth.*pam_gnome_keyring\.so/d' "$pam_file"
  sed -i '/-password.*pam_gnome_keyring\.so/d' "$pam_file"
fi

# The by-hand Mac install has no ISO provisioning step to seed Last/User. The
# theme submits userModel.lastUser, so a missing state file logs in as an empty
# username. Preserve state supplied by the ISO or a previous SDDM login.
if (( ${OMARCHY_FIRST_INSTALL:-0} )) &&
  [[ -n ${OMARCHY_INSTALL_USER:-} && ! -e $state_file ]]; then
  mkdir -p "$(dirname "$state_file")"
  printf '[Last]\nUser=%s\n' "$OMARCHY_INSTALL_USER" >"$state_file"
fi
