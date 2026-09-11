using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Omarchy.Enhanced;

internal static class DesktopAgent
{
  static bool DesktopAvailable()
  {
    nint desktop = Native.OpenInputDesktop(0, false, 1);
    if (desktop == 0) return false;
    try
    {
      var name = new StringBuilder(256);
      return Native.GetUserObjectInformation(desktop, 2, name, 512, out _) &&
        name.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase);
    }
    finally { Native.CloseDesktop(desktop); }
  }

  static ApplicationEntry[] Catalog()
  {
    List<ApplicationEntry> apps = [];
    // App Paths covers both Click-to-Run and MSI Office installations. Only
    // machine registrations are accepted; the host cannot create new entries.
    foreach (var (id, name, executable) in new[] {
      ("word", "Microsoft Word", "WINWORD.EXE"),
      ("excel", "Microsoft Excel", "EXCEL.EXE"),
      ("powerpoint", "Microsoft PowerPoint", "POWERPNT.EXE"),
      ("outlook", "Microsoft Outlook", "OUTLOOK.EXE") })
    {
      foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
      {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var key = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + executable);
        if (key?.GetValue(null) is string path)
        {
          path = Environment.ExpandEnvironmentVariables(path).Trim('"');
          if (Path.IsPathFullyQualified(path) && File.Exists(path) &&
              Path.GetFileName(path).Equals(executable, StringComparison.OrdinalIgnoreCase))
          { apps.Add(new(id, name, path)); break; }
        }
      }
    }
    // Discover other traditional desktop apps from machine App Paths. The
    // registered executable is the authority, never a path supplied by the host.
    foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
    {
      using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
      using var paths = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
      if (paths == null) continue;
      foreach (string entry in paths.GetSubKeyNames().Order(StringComparer.OrdinalIgnoreCase))
      {
        if (apps.Count >= 128) break;
        using var key = paths.OpenSubKey(entry);
        if (key?.GetValue(null) is not string registered) continue;
        string path = Environment.ExpandEnvironmentVariables(registered).Trim('"');
        if (path.Length > 512 || !Path.IsPathFullyQualified(path) || !File.Exists(path) ||
            !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            apps.Any(app => app.Executable.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
        string name = Path.GetFileNameWithoutExtension(path);
        if (name.Length > 128) continue;
        string id = "app." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
          Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..24].ToLowerInvariant();
        apps.Add(new(id, name, path));
      }
    }
    return apps.ToArray();
  }

  internal static WindowInfo[] Snapshot()
  {
    List<WindowInfo> windows = [];
    if (!DesktopAvailable()) return [];
    var foreground = Native.GetForegroundWindow();
    Native.EnumWindows((window, _) => {
      if (!Native.IsWindowVisible(window)) return true;
      if (Native.DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
      var title = new StringBuilder(1024);
      if (Native.GetWindowText(window, title, title.Capacity) == 0) return true;
      if (!Native.GetWindowRect(window, out var rect)) return true;
      var frame = Native.VisibleFrame(window, rect);
      Native.GetWindowThreadProcessId(window, out uint pid);
      var className = new StringBuilder(256);
      Native.GetClassName(window, className, className.Capacity);
      nint owner = Native.GetWindow(window, 4);
      windows.Add(new(window.ToInt64().ToString("x"), pid, title.ToString(), className.ToString(),
        rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top,
        Native.IsIconic(window), Native.IsZoomed(window), window == foreground, Native.GetDpiForWindow(window),
        owner == 0 ? null : owner.ToInt64().ToString("x"), windows.Count) {
          Frame = new(frame.Left, frame.Top, frame.Right - frame.Left, frame.Bottom - frame.Top)
        });
      return windows.Count < 128;
    }, 0);
    return windows.ToArray();
  }

  public static async Task Run(string pipeName, CancellationToken cancel)
  {
    Native.SetProcessDpiAwarenessContext(-4); // Per-monitor v2; report physical pixels.
    using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
      PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
    await pipe.ConnectAsync(15000, cancel);
    var wire = new Wire(pipe);
    var catalog = Catalog();
    await wire.Write(new { v = 1, type = "agent.ready", apps = catalog }, cancel);
    using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
    var snapshots = PublishSnapshots(wire, stop.Token);
    var commands = ProcessCommands(wire, catalog, stop.Token);
    try
    {
      await Task.WhenAny(snapshots, commands);
    }
    finally
    {
      stop.Cancel();
      pipe.Dispose();
      try { await Task.WhenAll(snapshots, commands); } catch (OperationCanceledException) { }
    }
  }

  static async Task ProcessCommands(Wire wire, ApplicationEntry[] catalog, CancellationToken cancel)
  {
    while (await wire.Read(cancel) is { } request)
    {
      using (request)
      {
        var root = request.RootElement;
        var (id, method) = Requests.Parse(root);
        try
        {
          switch (method)
          {
            case "windows.layout":
              if (!DesktopAvailable())
              {
                await wire.Write(new { v = 1, id, error = "desktop_locked" }, cancel);
                break;
              }
              await wire.Write(new { v = 1, id, result = WindowLayout.Apply(root) }, cancel);
              break;
            case "window.resize":
            case "window.focus":
              if (!DesktopAvailable())
              {
                await wire.Write(new { v = 1, id, error = "desktop_locked" }, cancel);
                break;
              }
              nint target = WindowControl.Resolve(root);
              object result;
              if (method == "window.resize") result = WindowControl.Resize(target, root);
              else { WindowControl.Focus(target); result = new { accepted = true }; }
              await wire.Write(new { v = 1, id, result }, cancel);
              break;
            case "apps.list":
              catalog = Catalog();
              await wire.Write(new { v = 1, id, result = catalog }, cancel);
              break;
            case "app.launch":
              if (!DesktopAvailable())
              {
                await wire.Write(new { v = 1, id, error = "desktop_locked" }, cancel);
                break;
              }
              var app = Requests.ResolveLaunch(root, catalog);
              using (var process = Process.Start(new ProcessStartInfo(app.Executable) {
                UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(app.Executable)!
              }))
                await wire.Write(new { v = 1, id, result = new { processId = process?.Id } }, cancel);
              break;
            default:
              await wire.Write(new { v = 1, id, error = "unsupported_method" }, cancel);
              break;
          }
        }
        catch (Exception e) when (e is InvalidDataException or System.ComponentModel.Win32Exception or IOException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or JsonException)
        { await wire.Write(new { v = 1, id, error = "launch_failed", message = e.Message }, cancel); }
      }
    }
  }

  static async Task PublishSnapshots(Wire wire, CancellationToken cancel)
  {
    long sequence = 0;
    // Full snapshots reconcile creates, closes, moves, focus and title changes
    // without depending on event hooks that can overflow or miss startup state.
    while (!cancel.IsCancellationRequested)
    {
      var windows = Snapshot();
      await wire.Write(new { v = 1, type = "windows.snapshot", sequence = ++sequence,
        session = Process.GetCurrentProcess().SessionId, capacityReached = windows.Length == 128, windows }, cancel);
      await Task.Delay(250, cancel);
    }
  }
}
