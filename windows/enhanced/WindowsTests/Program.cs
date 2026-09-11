using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Omarchy.Enhanced;

[assembly: SupportedOSPlatform("windows")]

var packed = WindowLayout.Pack(new[] {
  new WindowLayout.Entry("1", 1, 621, 670), new WindowLayout.Entry("2", 2, 621, 670)
}, 1280, 800);
Check(packed is { Length: 2 } && packed[0].X == 0 && packed[1].X == 621 && packed[1].Y == 0,
  "Two tiled host applications fit disjoint guest regions");
Check(WindowLayout.Pack(new[] { new WindowLayout.Entry("1", 1, 1300, 800) }, 1280, 800) is null,
  "Oversized application requires a larger guest mode");
var rows = WindowLayout.Pack(Enumerable.Range(1, 3).Select(i => new WindowLayout.Entry(i.ToString(), (uint)i, 600, 300)).ToArray(), 1280, 800);
Check(rows is { Length: 3 } && rows[2].X == 0 && rows[2].Y == 300, "Guest layout wraps without overlap");
Check(Marshal.SizeOf<WindowLayout.Mode>() == 220, "Display mode uses the DEVMODEW ABI size");
if (args.Contains("--layout-only")) return;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Run in an unlocked Windows test desktop");
Native.SetProcessDpiAwarenessContext(-4);
nint window = TestWindow.CreateWindowEx(0, "STATIC", "Omarchy integration test", 0x10cf0000,
  10, 10, 250, 150, 0, 0, 0, 0);
if (window == 0) throw new Exception("Could not create test window");
string id = window.ToInt64().ToString("x");
try
{
  var initial = DesktopAgent.Snapshot().Single(w => w.Id == id);
  Check(initial.Title == "Omarchy integration test", "Real window is enumerated");
  Check(initial.Width == 250 && initial.Height == 150, "Physical window geometry is reported");
  Check(initial.ProcessId == Environment.ProcessId && initial.Dpi > 0, "Process and DPI are reported");
  Check(initial.Frame is { Width: > 0, Height: > 0 }, "Visible frame geometry is reported");
  TestWindow.SetWindowText(window, "日本語 — changed title");
  TestWindow.MoveWindow(window, 20, 20, 300, 180, true);
  var moved = DesktopAgent.Snapshot().Single(w => w.Id == id);
  Check(moved.Title == "日本語 — changed title", "Unicode title changes are observed");
  Check(moved.X == 20 && moved.Y == 20 && moved.Width == 300 && moved.Height == 180, "Move and resize are observed");
  nint second = TestWindow.CreateWindowEx(0, "STATIC", "Omarchy second application", 0x10cf0000,
    10, 10, 250, 150, 0, 0, 0, 0);
  if (second == 0) throw new Exception("Could not create second test window");
  try
  {
    string secondId = second.ToInt64().ToString("x");
    using var request = JsonDocument.Parse(JsonSerializer.Serialize(new { windows = new[] {
      new { windowId = id, processId = Environment.ProcessId, width = 250, height = 150 },
      new { windowId = secondId, processId = Environment.ProcessId, width = 250, height = 150 }
    }}));
    WindowLayout.Apply(request.RootElement);
    var snapshot = DesktopAgent.Snapshot();
    var a = snapshot.Single(w => w.Id == id).Frame!;
    var b = snapshot.Single(w => w.Id == secondId).Frame!;
    Check(a.X + a.Width <= b.X || b.X + b.Width <= a.X || a.Y + a.Height <= b.Y || b.Y + b.Height <= a.Y,
      "Two real application frames do not overlap");
    WindowControl.Focus(window);
    Check(Native.GetForegroundWindow() == window, "First application receives foreground focus");
    WindowControl.Focus(second);
    Check(Native.GetForegroundWindow() == second, "Focus transfers to the second application");
  }
  finally { TestWindow.DestroyWindow(second); }
  TestWindow.ShowWindow(window, 6);
  Check(DesktopAgent.Snapshot().Single(w => w.Id == id).Minimized, "Minimize is observed");
  TestWindow.ShowWindow(window, 0);
  Check(!DesktopAgent.Snapshot().Any(w => w.Id == id), "Hidden window is removed from snapshot");
}
finally { TestWindow.DestroyWindow(window); }
Check(!DesktopAgent.Snapshot().Any(w => w.Id == id), "Destroyed window is absent");

static void Check(bool condition, string description)
{
  if (!condition) throw new Exception(description);
  Console.WriteLine("ok - " + description);
}

static class TestWindow
{
  [DllImport("user32.dll", CharSet = CharSet.Unicode)]
  internal static extern nint CreateWindowEx(uint extended, string className, string title, uint style,
    int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool SetWindowText(nint window, string text);
  [DllImport("user32.dll")] internal static extern bool MoveWindow(nint window, int x, int y, int width, int height, bool repaint);
  [DllImport("user32.dll")] internal static extern bool ShowWindow(nint window, int command);
  [DllImport("user32.dll")] internal static extern bool DestroyWindow(nint window);
}
