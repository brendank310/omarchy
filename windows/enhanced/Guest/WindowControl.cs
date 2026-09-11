using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Omarchy.Enhanced;

internal static class WindowControl
{
  internal const int MaxWidth = 2048, MaxHeight = 1536;

  internal static nint Resolve(JsonElement request)
  {
    string id = request.GetProperty("windowId").GetString() ?? "";
    uint pid = request.GetProperty("processId").GetUInt32();
    if (!long.TryParse(id, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long value) ||
        !DesktopAgent.Snapshot().Any(w => w.Id == id && w.ProcessId == pid))
      throw new InvalidDataException("Window is no longer available");
    return (nint)value;
  }

  internal static object Resize(nint window, JsonElement request)
  {
    int width = request.GetProperty("width").GetInt32(), height = request.GetProperty("height").GetInt32();
    int x = request.TryGetProperty("x", out var px) ? px.GetInt32() : 0;
    int y = request.TryGetProperty("y", out var py) ? py.GetInt32() : 0;
    if (width < 200 || height < 120 || width > MaxWidth || height > MaxHeight)
      throw new InvalidDataException("Unsupported window size");
    if (x < 0 || y < 0 || x + width > 8192 || y + height > 8192)
      throw new InvalidDataException("Unsupported window position");
    if (Native.IsIconic(window) || Native.IsZoomed(window)) ShowWindow(window, 4);
    if (!Native.GetWindowRect(window, out var outer)) throw new Win32Exception();
    var frame = Native.VisibleFrame(window, outer);
    int left = frame.Left - outer.Left, top = frame.Top - outer.Top;
    int extraWidth = outer.Right - outer.Left - (frame.Right - frame.Left);
    int extraHeight = outer.Bottom - outer.Top - (frame.Bottom - frame.Top);
    uint flags = request.TryGetProperty("raise", out var raise) && raise.GetBoolean() ? 0x10u : 0x14u;
    if (!SetWindowPos(window, 0, x - left, y - top, width + extraWidth, height + extraHeight, flags)) throw new Win32Exception();
    Native.DwmFlush();
    Native.GetWindowRect(window, out var rect);
    rect = Native.VisibleFrame(window, rect);
    return new { width = rect.Right - rect.Left, height = rect.Bottom - rect.Top };
  }

  internal static void Focus(nint window)
  {
    if (Native.IsIconic(window)) ShowWindow(window, 9);
    if (Native.GetForegroundWindow() == window) return;
    // Requests execute on pool threads, which may not have an input queue yet.
    PeekMessage(out _, 0, 0, 0, 0);
    uint foreground = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
    uint current = GetCurrentThreadId();
    uint target = Native.GetWindowThreadProcessId(window, out _);
    bool attached = foreground != current && AttachThreadInput(current, foreground, true);
    if (foreground != current && !attached) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot attach foreground input queue");
    bool targetAttached = false;
    try
    {
      targetAttached = target != current && target != foreground && AttachThreadInput(current, target, true);
      SetForegroundWindow(window);
      BringWindowToTop(window);
      SetFocus(window);
    }
    finally
    {
      if (targetAttached) AttachThreadInput(current, target, false);
      if (attached) AttachThreadInput(current, foreground, false);
    }
    // Activation of another input queue completes asynchronously. Wait for
    // that queue to process the activation before acknowledging host focus.
    if (SendMessageTimeout(window, 0, 0, 0, 2, 2000, out _) == 0)
      throw new InvalidDataException("Application did not respond to focus");
    if (Native.GetForegroundWindow() != window) throw new InvalidDataException("Windows refused application focus");
  }

  [DllImport("user32.dll", SetLastError=true)] static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
  [DllImport("user32.dll")] static extern bool ShowWindow(nint window, int command);
  [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint window);
  [DllImport("user32.dll")] static extern bool BringWindowToTop(nint window);
  [DllImport("user32.dll")] static extern nint SetFocus(nint window);
  [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
  [StructLayout(LayoutKind.Sequential)]
  struct Message
  {
    public nint Window;
    public uint Id;
    public nuint WParam;
    public nint LParam;
    public uint Time;
    public int X, Y;
    public uint Private;
  }
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern bool PeekMessage(out Message message, nint window, uint minimum, uint maximum, uint remove);
  [DllImport("user32.dll", SetLastError=true)] static extern bool AttachThreadInput(uint current, uint other, bool attach);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern nint SendMessageTimeout(nint window, uint message, nuint wparam, nint lparam, uint flags, uint timeout, out nuint result);
}
