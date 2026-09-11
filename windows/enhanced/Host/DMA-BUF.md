# DMA-BUF viewer implementation

The active rendering design uses QEMU's D-Bus display with OpenGL and a native Wayland window managed by Hyprland. SPICE/QXL and guest-side pixel capture are superseded. The standalone viewer now uses a GTK GLArea and EGL import through `dmabuf.c`; SPICE code and its build dependency have been removed.

## Verified host capabilities

On this host, `/dev/dri/renderD129` is Intel Iris Xe through i915; `/dev/dri/renderD128` is NVIDIA Quadro T500 Mobile. The Intel EGL probe successfully created a GL texture and exported a one-plane DMA-BUF with a DRM format modifier. EGL reports DMA-BUF import, modifier import and export support. The VM's QEMU build includes the `dbus` display backend. The container maps the Intel render node and exports a live D-Bus console at 1280×800. The guest focus and 900×600 resize commands pass live tests. The viewer creates an Intel GL context and successfully imports the live QEMU DMA-BUF with format `34324258` and modifier `100000000000001`. Screenshots verify the upright visible-frame crop, host-driven resizing at 621×670 and 471×670, and Ctrl+A plus normal keyboard input. A native pointer event opened Word’s blank document. Text-only multi-viewer testing verified both viewers reimporting a larger 1680×1050 scanout. Four alternating host activations successfully focused the corresponding Word/Excel guest HWNDs, with both native viewers connected and their 621×328 guest frames disjoint. Modal-dialog and disconnect checks remain in progress.

The stable host path for the Intel render node is `/dev/dri/by-path/pci-0000:00:02.0-render`; NVIDIA is `/dev/dri/by-path/pci-0000:01:00.0-render`. Resolve the selected PCI path when configuring the container instead of assuming that `renderD129` will always identify Intel after a reboot. QEMU export and viewer import have both been tested on Intel; cross-GPU import and presentation on an NVIDIA-connected monitor have not been verified.

The listener authentication failure was caused by Unix peer credentials crossing the container PID namespace, rather than the second GPU. `listener-relay.py` creates the listener socket pair inside the container and forwards control traffic and file descriptors. It does not map or copy pixel storage. The container entrypoint now supervises this relay and fixes metadata socket ownership after QEMU creates it. A VM restart verified socket recreation and a successful subsequent import.

Run the capability probe with:

```bash
cc egl-probe.c -o /tmp/enhanced-egl-probe $(pkg-config --cflags --libs epoxy gbm)
/tmp/enhanced-egl-probe /dev/dri/renderD129
```

## Rendering contract

- Give QEMU access to the selected render node and enable its D-Bus display with `gl=on` and `rendernode=/dev/dri/renderD129`.
- Use a private D-Bus connection, not the desktop session bus. Register an `org.qemu.Display1.Listener` on a passed Unix socket.
- Receive `ScanoutDMABUF` (or the multi-plane `ScanoutDMABUF2` interface), import its file descriptors, format, stride, offsets and modifier through EGL, and sample that imported texture in a GTK GLArea inside a native Wayland toplevel.
- Apply the guest metadata rectangle as texture coordinates. Do not map the pixel buffer, read pixels back to CPU memory, compress frames or send pixels through the guest metadata channel.
- Retain the imported resource for the scanout lifetime. Respect QEMU's `UpdateDMABUF` acknowledgement synchronization; do not acknowledge while the GPU is still reading the old content.
- Route pointer and keyboard events through QEMU's D-Bus Mouse and Keyboard interfaces. Translate local pointer positions through scaling and the crop origin.
- Send Hyprland window allocation changes to the broker’s `window.resize` command. The broker combines application sizes into `windows.layout`; the guest packs disjoint frames and selects a tested driver-supported display mode when more space is needed.
- Blank the view and release input when the selected guest window disappears, the desktop becomes inaccessible, or either connection fails.

The metadata broker multiplexes one guest channel across independent viewers and routes each reply to its originating client. Host activation explicitly focuses that viewer’s guest application. The shared scanout is the composed guest desktop. A crop cannot recover pixels of an obscured or minimized application; the selected window must be visible. Owned dialogs and popup handling need explicit testing.

## Meaning of zero-copy

The intended zero-copy boundary is QEMU's exported DMA-BUF to the viewer's imported GPU texture. QEMU's software display path can still upload its guest display surface into a GL texture before exporting it; this does not establish end-to-end zero-copy guest rendering. Hyprland will normally composite the viewer surface. Direct scanout, guest 3D acceleration and eliminating QEMU's own software-to-GPU upload are separate capabilities and must not be claimed from a successful EGL import alone.

## References

- [QEMU D-Bus display protocol](https://www.qemu.org/docs/master/interop/dbus-display)
- [QEMU display listener implementation](https://gitlab.com/qemu-project/qemu/-/blob/master/ui/dbus-listener.c)
