# Native Windows application viewer

The viewer runs as the desktop user in a native Wayland window. QEMU exports its existing framebuffer over a private D-Bus connection; EGL imports the DMA-BUF and the shader samples the application rectangle supplied by the guest agent. Input goes through QEMU's keyboard and mouse interfaces. The guest channel carries metadata and window controls only.

Each application gets its own native host window. A shared metadata broker supports simultaneous viewers, while the guest arranges their application frames in disjoint regions of one display. It requires an installed Enhanced Windows guest service, an unlocked Windows console, a QEMU build with D-Bus/OpenGL support, and a compatible host render device. See [verified behavior and remaining checks](../VERIFICATION.md).

## Build

Host dependencies are a C compiler, pkg-config, GTK 3, libepoxy, GLib/GIO Unix support, json-glib, Python 3 and dbus-daemon. On Omarchy, install missing optional development dependencies through `omarchy-pkg-add`. From the repository root:

```bash
make -C windows/enhanced/Host
sudo docker build -t omarchy-windows-dmabuf windows/enhanced/Host
```

The image currently extends `omarchy-windows-gtk`, which the existing Omarchy Windows launcher builds. It uses QEMU's upstream D-Bus backend without a source patch. The added entrypoint supervises the container-local descriptor relay and sets the metadata socket's owner and mode after QEMU creates it.

## Configure an existing VM

Use the updated `bin/omarchy-windows-vm` from this branch in the installed Omarchy command location before normal launcher startup. Its enhanced-display check preserves the configured DMA-BUF overlay instead of regenerating GTK settings. A source checkout alone does not update the root-owned privileged launcher.

Start the private display bus as the desktop user, then configure the existing VM as root. Choose the PCI render path for the GPU that should export and import the buffers. The example selects this laptop's Intel GPU:

```bash
python3 windows/enhanced/Host/start-bus.py "$HOME/.windows/omarchy-enhanced"
sudo python3 windows/enhanced/Host/configure.py --render-node /dev/dri/by-path/pci-0000:00:02.0-render
```

The configuration helper verifies root-owned Compose files and the authorizing account's protected storage mount, makes timestamped backups, and validates the resulting Compose configuration. It restores the original files if validation fails. It does not restart the VM. Save Windows work and stop the VM before starting it with the new configuration:

```bash
omarchy windows vm stop
python3 windows/enhanced/Host/launch.py --start-vm --app word
```

The private bus must be started before QEMU. `launch.py` ensures it exists before invoking the VM launcher. The VM keeps running when the application viewer closes; use `omarchy windows vm stop` to shut it down. The launcher shares the guest channel through `broker.py`; use `broker.sock` for additional metadata clients while viewers are connected.

## Select an application

For an already running VM:

```bash
python3 windows/enhanced/Host/launch.py --app word &
python3 windows/enhanced/Host/launch.py --app excel &
python3 windows/enhanced/Host/launch.py --list
python3 windows/enhanced/Host/launch.py --window <window-id>
```

`--app` uses the guest's application catalog ID, not an arbitrary command line. Word, Excel and classic Outlook wait for their main window class, skip transient splash windows, and support selecting an existing process after Office redirects a launch. Other catalog applications are matched by the returned process ID. `--list` prints window IDs, process IDs and titles; titles can contain document names. With no selection argument, the launcher chooses a visible foreground application.

The default `--render-node` is the Intel PCI path above. When configuring another GPU, pass the same host path to the launcher; it sets Mesa's PCI selector. Cross-GPU and NVIDIA rendering have not been verified. Explicitly selecting the same device for QEMU and the viewer avoids relying on unstable `renderD128`/`renderD129` numbering.

The application follows the host window's logical allocation, bounded by the guest's supported scanout and window limits. The current guest accepts 200–2048 pixels wide and 120–1536 pixels high. The broker combines viewer resize requests into a guest layout. When the frames do not fit, the guest tests and selects a larger driver-supported display mode without persisting it to the registry. Extra guest monitors are not required. An application can impose its own minimum window size, in which case the viewer preserves aspect ratio with letterboxing.

Windows/Super keys and Super-modified key chords stay on the host. They are never forwarded to QEMU, so host shortcuts cannot open the guest Start menu. Ctrl, Alt and Shift remain available to the application.

Activating a Hyprland application window requests foreground focus in Windows and waits for acknowledgement before forwarding input. Explicit application selection also restores minimized windows.

Foreground owned dialogs use the same host window and return to the main application when dismissed. The framebuffer is still the composed Windows desktop: obscured or minimized windows have no recoverable independent pixels. General untitled popups, clipboard synchronization, accessibility, guest 3D acceleration, and direct scanout are not implemented. See [the zero-copy boundary](DMA-BUF.md#meaning-of-zero-copy).

## Checks

```bash
make -C windows/enhanced/Host check-input
python3 windows/enhanced/Host/test-configure.py
python3 windows/enhanced/Host/test-relay.py
python3 windows/enhanced/Host/test-broker.py
python3 windows/enhanced/Host/test-launch.py
bash test/shell.d/windows-vm-enhanced-display-test.sh
```

The relay test needs Unix credential passing (`SO_PASSCRED`), which restricted sandboxes may block. It checks the transport with memfds and control bytes, without inspecting a guest framebuffer. Current integration checks use text metadata, focus acknowledgements and DMA-BUF import logs. Further image/video tests are deferred at the user’s request.

## Whole desktop

Use `launch.py --desktop` (or `omarchy windows app desktop` after installing the host package) for the entire QEMU scanout. This includes the taskbar, Start menu and Windows sign-in screen. It does not depend on application-window metadata and does not move or resize guest windows. The current guest desktop is scaled to fit the host window. Windows/Super remains a host key; click the guest Start button to use its menu.

Desktop and application viewers can coexist. They share one Windows console, pointer and keyboard focus. Moving, minimizing or covering a guest application from the desktop view also affects its application crop.

## Host package and Super menu

Build the guest ZIP with `windows/enhanced/Build.ps1`, then run `windows/enhanced/BuildHost.sh` as a normal user. It produces `windows/enhanced/artifacts/omarchy-windows-integration-0.1.0-3-x86_64.pkg.tar.zst`, containing the renderer, broker, launchers, desktop entries, guest installer ZIP, container sources and menu extension. Install the local package with `sudo pacman -U <package>` and run `omarchy setup windows office` as the desktop user. That command backs up and merges the menu extension, preserving existing custom entry fields.

App search then lists Microsoft Word, Excel, PowerPoint, Outlook and Windows Desktop. The Office submenu launches a document, spreadsheet or presentation with the selected suite. Choose Setup → Defaults → Office Suite → Microsoft Office, or run `omarchy default office microsoft`; `libreoffice` selects LibreOffice again. Suite selection changes these launch actions only. It does not change document MIME associations or remove LibreOffice.

The package supplies integration software, not Microsoft Office or a Windows license. The existing Windows VM must already be configured for DMA-BUF and have the guest service and desired Office applications installed. The included guest ZIP is at `/usr/share/omarchy-windows/EnhancedWindows-win-x64.zip`; the container build and configuration helpers are under `/usr/lib/omarchy-windows/Host`. Use the matching updated Omarchy VM launcher from this branch so startup preserves the DMA-BUF configuration.

Application viewers close when a complete, nonempty guest snapshot no longer contains their selected window identity. Minimized windows remain open. Empty privacy snapshots, truncated lists, and transport outages do not count as a window close. Whole-desktop mode remains open independently of application windows.
