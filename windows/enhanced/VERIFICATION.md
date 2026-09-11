# Windows KVM verification

Development verification on 2026-09-12 and 2026-09-13 used the authorized active Omarchy Windows 11 x64 VM (build 26200). This is a record of observed results, not a release certification.

## Completed

- The self-contained guest and Windows test executables build with .NET SDK 10.0.401; protocol tests, PowerShell syntax checks and mocked installer tests pass.
- The official stable VirtIO ISO 0.1.302 was downloaded over HTTPS. Its locally computed SHA256 is `303f7ae40dad495d6ae474fdc571df58958a4dbc5c37a522d80f9a203867949d`; this is not an independently published digest. Windows validated the selected driver catalogs against its certificate trust store.
- The upstream MSI/EXE wrappers are unsigned. The installer uses signed INF/catalog packages through PnPUtil. The unrelated SMBus package is excluded because its catalog chains to an untrusted development root. Signature validation remains mandatory for selected drivers.
- Actual PnPUtil output showed network driver exit 259 means the existing device driver is up to date. The installer accepts 0, 259 and 3010, and rejects other failures. Mocked regression coverage exercises these outcomes.
- Driver installation completed, requested a reboot, and the VM was restarted. The service starts automatically in session 0 and launches its desktop agent in session 1.
- All 16 Windows assertions passed: layout packing and DEVMODEW ABI, enumeration, physical geometry, process/DPI, visible-frame geometry, Unicode title change, move/resize, non-overlap, foreground transfer between two test windows, minimize, hide and destruction.
- The host received `hello` and `session.ready` on the dedicated VirtIO serial channel.

## Live channel and application checks

Disabling FileStream buffering resolved a live full-duplex stall: a pending request read no longer blocks outgoing events. Catalog and window snapshots now arrive while the host is idle. The service upgrade stopped and replaced the old executable successfully.

Independent host connections launched Word (PID 5200), Excel (PID 6264) and Outlook (PID 944). Windows process ownership inspection confirmed all three belong to the signed-in user in session 1, while the service belongs to SYSTEM in session 0. Metadata included Word's `OpusApp`, Excel's `XLMAIN`, and Outlook's startup dialog/splash windows. Word's home screen was visually inspected. Outlook was left at its startup dialog; account setup and mail operations were not tested.

The live service rejected an obsolete session token with `stale_session` and rejected a launch request carrying an extra arguments field. Repeated host disconnect/reconnect cycles produced fresh handshakes, catalogs and snapshots.

A window geometry assertion failed after reboot changed the desktop to 621×303: the requested move extended outside that desktop. After using geometry that fits the small desktop, all eight assertions passed again.

A reversible alternate-desktop test confirmed empty snapshots and `desktop_locked` launch rejection while the normal desktop was inaccessible, followed by resumed metadata after restoration. This tests the desktop privacy gate, not Windows credential unlock.

## Further release testing

Full sign-out/sign-in, actual workstation lock/unlock, prolonged backpressure, multiple users, and destructive rollback/uninstall scenarios still need broader testing on disposable guests. These are release-hardening gaps; the current evidence covers the Windows-side driver installation, service, metadata and command-channel implementation.

## Test VM provisioning

The test VM has a dedicated `org.omarchy.enhanced.0` VirtIO serial port backed by `/storage/omarchy-enhanced/enhanced.sock` inside its container. The host storage directory restricts access to the VM owner, with the container supervisor setting the metadata socket owner and mode 0600 after QEMU creates it. This socket is not published over TCP. The main and OEM Compose definitions have `.before-enhanced` backups next to them. The source now includes `Host/configure.py` for backed-up, validated configuration and a container entrypoint that starts the descriptor relay. The normal VM launcher preserves an explicitly configured DMA-BUF overlay.

## Host renderer verification

The native GTK Wayland viewer imported a 1280×800 QEMU DMA-BUF on Intel Iris Xe, with DRM format `34324258` and modifier `100000000000001`. The guest does not supply pixel data. Visual comparison with QEMU's display exposed and corrected the texture orientation and initial resize bugs.

Screenshots verified Word's visible frame without guest wallpaper or invisible resize borders. Hyprland tile changes resized Word from 621×670 to 471×670 logical pixels. Ctrl+A and normal keyboard input replaced the test document contents with the expected phrase; Word applied its normal initial capitalization. The host uses logical allocation sizes for the current 100% Windows desktop and scales presentation for the host's scale factor of 3.

The integrated container image was built and restarted. The relay and metadata sockets were recreated automatically with the VM owner's UID. The application launcher reopened Word and imported its DMA-BUF after that restart. The Intel PCI path is mapped to a fixed container render node, independently of host node numbering.

Relay tests passed descriptor identity checks for 16 descriptors, close-on-exec behavior, a large control stream, and peer-disconnect cleanup. Configuration tests passed repeated migration without duplicate VirtIO channels. The VM-launcher regression verifies preservation of the enhanced overlay and rejection of an untrusted marker. CLI tests passed; the headless theme test emitted sandbox lock-file warnings unrelated to the VM changes.

A native virtual-pointer click opened a blank Word document. Two simultaneous viewers imported the same scanout and both reimported it after guest layout enlarged the mode to 1680×1050. Further image/video tests are deferred at the user’s request; subsequent checks use metadata and process logs.

Text-only Word/Excel integration passed four alternating Hyprland activations, each confirmed by the matching guest foreground HWND and focus acknowledgement. Both native Wayland viewers remained connected and imported DMA-BUFs. Their frames were disjoint at (0,0) 621×328 and (621,0) 621×328. A text-only resize check changed both host allocations to 561×328, confirmed both guest frame sizes matched, and restored them to 621×328. Explicit launch selection restores an existing minimized Office window. The guest focus handler joins both foreground and target input queues and waits for activation before acknowledging.

The updated installed launcher successfully restarted the restored DMA-BUF configuration. A prior startup attempt used GTK settings; reapplying the validated configuration and invoking the updated launcher restored the correct backend.

A headless test exercises the actual GTK keyboard handler: left/right Super and Super chords produce no guest key events, entering a host chord releases held guest keys, orphan releases are suppressed, and Ctrl+A still forwards. Live left/right Super press-release checks also preserved Excel’s foreground HWND.

Post-restart Word/Excel checks passed repeated foreground transfer and disjoint application frames with both DMA-BUF viewers connected. The initial launch incorrectly selected Office splash HWNDs; the launcher now waits for the Office main window class, with regression coverage. The user confirmed the second run renders correctly.

Remaining renderer verification: foreground owned dialogs and privacy/disconnect blanking. The new graphics driver remains future work; successful host DMA-BUF import does not establish guest 3D acceleration.

## Packaged desktop and Office launchers

The `omarchy-windows-integration` Arch package built and installed successfully. Its package checks passed the actual GTK input handler (including full-desktop geometry and Super filtering), Office splash/minimized selection, and shared broker tests. Menu, menu-guard and CLI suites passed. Menu installation preserved custom entries and was idempotent with a backup of the original extension.

The packaged Windows Desktop viewer opened as a native Wayland window and imported a 1920×1080 Intel DMA-BUF. It samples the full scanout and does not send application resize or focus commands. PowerPoint opened through the packaged launcher as another native Wayland viewer, alongside Word, Excel and the desktop view. The updated guest package passed all 16 Windows assertions.

The installed command router recognizes `omarchy windows app`, `omarchy default office`, `omarchy launch office`, and `omarchy setup windows office`. Microsoft and LibreOffice suite selections were tested in a temporary configuration. The Office and Default Office Suite menu routes opened successfully. Document MIME associations were not changed. No screenshots or videos were used for these checks.
