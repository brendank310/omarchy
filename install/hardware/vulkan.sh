# Install Vulkan drivers matching detected GPU hardware
# (NVIDIA Vulkan is handled by nvidia.sh via nvidia-utils)

declare -A VULKAN_DRIVERS=(
  [Intel]=vulkan-intel
  [AMD]=vulkan-radeon
  [Apple]=vulkan-asahi
)

PACKAGES=()

# Apple Silicon's display controller does not show up as a VGA device in lspci,
# so the vendor scan below misses it. M1 and later need vulkan-asahi anyway.
if omarchy-hw-aarch64-apple; then
  PACKAGES+=(vulkan-asahi)
fi

for vendor in "${!VULKAN_DRIVERS[@]}"; do
  if lspci | grep -iE "(VGA|Display).*$vendor" > /dev/null; then
    PACKAGES+=("${VULKAN_DRIVERS[$vendor]}")
  fi
done

# Adreno GPUs are platform devices, so lspci never sees them.
if omarchy-hw-aarch64-qualcomm; then
  PACKAGES+=(vulkan-freedreno)
fi

if (( ${#PACKAGES[@]} > 0 )); then
  declare -A seen_packages=()
  unique_packages=()
  for package in "${PACKAGES[@]}"; do
    if [[ -z ${seen_packages[$package]:-} ]]; then
      seen_packages[$package]=1
      unique_packages+=("$package")
    fi
  done
  omarchy-pkg-add "${unique_packages[@]}"
fi
