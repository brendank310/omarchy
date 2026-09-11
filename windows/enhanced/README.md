# Enhanced Windows experience

Windows 11 x64 guest integration for Omarchy's KVM VM. This development package has been tested in a Windows KVM guest; see [verification results](VERIFICATION.md). The standalone host viewer imports QEMU DMA-BUFs into a native Wayland window, crops to the selected application’s visible frame, and redirects input through QEMU. See [host setup](Host/README.md) and the [rendering contract](Host/DMA-BUF.md).

The guest service owns a dedicated VirtIO serial port and starts a desktop agent with the active console user's token. The agent enumerates windows and launches traditional desktop applications through their machine App Paths registrations, with friendly names and IDs for Word, Excel, and classic Outlook. Launches run as that user, never as SYSTEM. The agent's private named pipe permits only SYSTEM and that user; the service also checks the connecting process ID against the process it launched.

## Build and install

Install the .NET 10 SDK on a build machine and run `./Build.ps1`. The self-contained `artifacts/win-x64` output requires no preinstalled .NET runtime in the guest. From an elevated Windows PowerShell session, run:

```powershell
./Install.ps1 -Payload ./artifacts/win-x64 -VirtioIso C:\Downloads\virtio-win.iso -VirtioSha256 <verified-sha256>
```

When using the extracted ZIP package, omit `-Payload` to use its adjacent `win-x64` directory. Obtain the ISO and verify its digest through a trusted distribution channel. The installer checks the supplied digest and driver catalog Authenticode signatures, installs the upstream Windows 11 x64 driver packages (excluding the unrelated SMBus package) with `pnputil /add-driver /install` without forcing a reboot, and installs the service under Program Files. Upgrades stage the executable and keep a rollback copy until service startup succeeds; failures restore the previous binary and its running state. VirtIO driver installation is not rolled back, because existing storage and network devices may depend on those drivers. Reboot when the driver installer requests it. `Uninstall.ps1` removes the integration service and binaries while retaining boot-critical VirtIO drivers.

## Host channel contract

Reserve a dedicated port named `org.omarchy.enhanced.0`. The host configuration helper exposes a protected Unix socket through the container to QEMU; the underlying device arguments are:

```text
-device virtio-serial-pci
-chardev socket,id=omarchy-enhanced,path=/run/omarchy/enhanced.sock,server=on,wait=off
-device virtserialport,chardev=omarchy-enhanced,name=org.omarchy.enhanced.0
```

The socket grants authority to launch user applications and observe window titles. Restrict its parent directory and socket to the intended host user. Do not publish it over TCP. This is a separate channel from `org.qemu.guest_agent.0`; the service opens `\\.\Global\org.omarchy.enhanced.0` inside Windows. VirtIO serial was selected for its existing Windows support; vsock can be added behind the same framing contract later.

Frames contain a four-byte unsigned little-endian length followed by UTF-8 JSON, bounded to 1 MiB and JSON depth 24. Each request includes integer `v: 1`, a nonempty string `id` of at most 128 characters, and `method`. After `session.ready`, include its opaque `session` token in every request. This token changes each time the desktop agent is replaced; old requests receive `stale_session` rather than reaching another signed-in user. Replies echo `id` and contain `result` or `error`. Events have a `type` instead of a request ID. Connections begin with `hello`, including capabilities and `rendering: false`. Reconnects must discard previous window state and await a new snapshot; never replay launch requests automatically after an uncertain disconnect.

```json
{"v":1,"id":"catalog-1","session":"<session.ready token>","method":"apps.list"}
{"v":1,"id":"launch-1","session":"<session.ready token>","method":"app.launch","appId":"word"}
```

`apps.list` returns up to 128 installed catalog entries with stable IDs (`word`, `excel`, `outlook`, or a hash of another registered executable path), display names and executable paths. Store/UWP applications and Start Menu shortcuts with custom arguments are not yet cataloged. `app.launch` only accepts a catalog ID; executable paths, arguments and shell commands are rejected. A launch reply means Windows accepted process creation, not that the app has finished opening. If Windows has no signed-in console user, requests receive `no_interactive_session`. The integration does not enable automatic sign-in or unlock Windows. If the service starts before sign-in, it detects the new console session automatically while retaining any partial host frame until the desktop relay takes over.

`agent.ready` includes the initial catalog. `windows.snapshot` publishes the current visible, titled, uncloaked top-level windows every 250 ms, with a sequence number, Windows session ID and window records. Records include HWND as a hexadecimal string, process ID, title, class, physical-pixel outer geometry, a `frame` rectangle excluding invisible resize borders, DPI, minimized/maximized state, foreground state, owner HWND and enumeration order. Each full snapshot replaces the prior one; removed records represent closed or hidden windows. HWND values can be reused and must not be treated as permanent identities. Owned dialogs are included. Snapshot size is capped at 128 windows to keep worst-case escaped titles within the frame limit; `capacityReached` indicates when that cap is reached, and larger desktops are not fully represented yet. Locked or secure desktops produce empty snapshots and reject launches with `desktop_locked`.

## Tests

Run `dotnet run --project Tests/Tests.csproj` on Linux or Windows for protocol and partial-frame/session handoff tests. `Tests/Installer.ps1` exercises install, upgrade, startup-failure rollback and driver-digest rejection using real temporary files with mocked Windows driver/service/ACL operations. `Tests/PowerShellSyntax.ps1` parses every package script. The build script runs these checks before publishing. In an unlocked Windows desktop, run `dotnet run --project WindowsTests/WindowsTests.csproj` for real Win32 window lifecycle tests. The ZIP also includes a self-contained `windows-tests/WindowsTests.exe` so guest tests do not require the SDK. These tests create and destroy a temporary test window, checking titles, geometry, DPI, minimize, hide and close. With the installed service running in a signed-in guest and the dedicated host socket configured, run `python3 Smoke.py /path/to/broker.sock` when viewers are running (or use `enhanced.sock` when no broker owns it) on the host. Add `--launch word` to test process creation for an installed Office app. This probe checks catalog and metadata exchange; it does not prove that the launched app finished opening.

## Remaining work and verification

- Verify and harden service lifecycle, reconnects, session changes, lock/unlock privacy, stop cancellation, and bounded backpressure in a Windows KVM guest.
- Driver installation, reboot startup and service upgrade have been exercised in the test VM. Exercise destructive rollback/uninstall scenarios on a disposable guest before release.
- The Windows window lifecycle tests and live host catalog/metadata/Office launch checks have passed in the test VM. Expand automated coverage for session transitions and transport failures; Linux protocol tests alone cannot prove these behaviors.
- The host implementation supports multiple application views through a shared metadata broker and non-overlapping guest window layout. It shares QEMU’s existing framebuffer through DMA-BUF; no guest pixel capture or guest pixel transport is implemented. Clipboard, accessibility integration, independent surfaces for every guest window, and general popup composition are outside the current renderer.
- The viewer follows foreground owned dialogs in its existing host window. Live dialog, mouse, enlarged-scanout and disconnect verification is still in progress; see `Host/DMA-BUF.md` for the current evidence.
- TODO: research a Windows VirtIO graphics driver for future 3D acceleration, including virgl/Vulkan feasibility, synchronization, buffer sharing and Windows driver signing. No new graphics driver is included or implemented.

## References

- [Microsoft: PnPUtil](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax) documents signed driver package staging and installation.
- [Microsoft: service recovery flags](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-service_failure_actions_flag) describes recovery after a service reports an error stop.
- [Microsoft: CreateProcessAsUser](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessasuserw) documents session tokens and environment handling for interactive agent creation.
- [VirtIO Windows drivers](https://github.com/virtio-win/kvm-guest-drivers-windows) provides the existing guest driver stack.
- [VirtIO Windows guest-agent setup](https://virtio-win.github.io/Knowledge-Base/Qemu-ga-win.html) describes dedicated VirtIO serial channels.
