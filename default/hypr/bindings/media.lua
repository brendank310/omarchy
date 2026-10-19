-- Volume, brightness, keyboard backlight, and touchpad controls.
o.bind("XF86AudioRaiseVolume", "Volume up", { audio = "raise" }, { locked = true, repeating = true })
o.bind("XF86AudioLowerVolume", "Volume down", { audio = "lower" }, { locked = true, repeating = true })
o.bind("XF86AudioMute", "Mute", { audio = "mute-toggle" }, { locked = true })
o.bind("XF86AudioMicMute", "Mute microphone", "omarchy-audio-input-mute", { locked = true })
o.bind("XF86MonBrightnessUp", "Brightness up", { brightness = "raise" }, { locked = true, repeating = true })
o.bind("XF86MonBrightnessDown", "Brightness down", { brightness = "lower" }, { locked = true, repeating = true })
-- Apple Silicon has no XF86KbdBrightness key. With hid_apple fnmode=1 the top
-- row emits XF86MonBrightness*, and Shift drives the keyboard backlight.
o.bind("SHIFT + XF86MonBrightnessUp", "Keyboard brightness up", "omarchy-brightness-keyboard up", { locked = true, repeating = true })
o.bind("SHIFT + XF86MonBrightnessDown", "Keyboard brightness down", "omarchy-brightness-keyboard down", { locked = true, repeating = true })
o.bind("XF86KbdBrightnessUp", "Keyboard brightness up", "omarchy-brightness-keyboard up", { locked = true, repeating = true })
o.bind("XF86KbdBrightnessDown", "Keyboard brightness down", "omarchy-brightness-keyboard down", { locked = true, repeating = true })
o.bind("XF86KbdLightOnOff", "Keyboard backlight cycle", "omarchy-brightness-keyboard cycle", { locked = true })
o.bind_toggle("XF86TouchpadToggle", "Toggle touchpad", "touchpad", { locked = true })
o.bind("XF86TouchpadOn", "Enable touchpad", "omarchy-toggle-touchpad on", { locked = true })
o.bind("XF86TouchpadOff", "Disable touchpad", "omarchy-toggle-touchpad off", { locked = true })

-- Precise volume and brightness controls.
o.bind("ALT + XF86AudioRaiseVolume", "Volume up precise", "omarchy-audio-output-volume +1", { locked = true, repeating = true })
o.bind("ALT + XF86AudioLowerVolume", "Volume down precise", "omarchy-audio-output-volume -1", { locked = true, repeating = true })
o.bind("ALT + XF86MonBrightnessUp", "Brightness up precise", "omarchy-brightness-display +1%", { locked = true, repeating = true })
o.bind("ALT + XF86MonBrightnessDown", "Brightness down precise", "omarchy-brightness-display 1%-", { locked = true, repeating = true })

-- Media controls.
o.bind("XF86AudioNext", "Next track", { ipc = "media.next" }, { locked = true })
o.bind("ALT + XF86AudioPlay", "Next track", { ipc = "media.next" }, { locked = true })
o.bind("XF86AudioPause", "Pause", { ipc = "media.playPause" }, { locked = true })
o.bind("XF86AudioPlay", "Play", { ipc = "media.playPause" }, { locked = true })
o.bind("XF86AudioPrev", "Previous track", { ipc = "media.previous" }, { locked = true })
o.bind("ALT + SHIFT + XF86AudioPlay", "Previous track", { ipc = "media.previous" }, { locked = true })
o.bind("XF86Eject", "Eject media", "eject", { locked = true })

o.bind("SHIFT + XF86AudioMute", "Switch audio output", "omarchy-audio-output-switch", { locked = true })
o.bind("SHIFT + XF86AudioPause", "Switch media source", "omarchy-audio-source-switch", { locked = true })
o.bind("SHIFT + XF86AudioPlay", "Switch media source", "omarchy-audio-source-switch", { locked = true })

-- Apple keyboards have no Print Screen key. fnmode=1 makes the top row emit
-- media keycodes, so bind both the F-keys and those keycodes.
o.bind("SUPER + F12", "Screenshot Display", "omarchy-capture-screenshot fullscreen")
o.bind("SUPER + F11", "Screenshot Region", "omarchy-capture-screenshot region")
o.bind("SUPER + F10", "Screenshot Window", "omarchy-capture-screenshot windows")
o.bind("SUPER + ALT + F12", "Screen recording Display", "omarchy-capture-screenrecording --fullscreen")
o.bind("SUPER + XF86AudioMute", "Screenshot Window (Apple top row)", "omarchy-capture-screenshot windows")
o.bind("SUPER + XF86AudioLowerVolume", "Screenshot Region (Apple top row)", "omarchy-capture-screenshot region")
o.bind("SUPER + XF86AudioRaiseVolume", "Screenshot Display (Apple top row)", "omarchy-capture-screenshot fullscreen")
o.bind("SUPER + ALT + XF86AudioRaiseVolume", "Screen recording Display (Apple top row)", "omarchy-capture-screenrecording --fullscreen")
