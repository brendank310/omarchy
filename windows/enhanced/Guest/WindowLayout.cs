using System.Runtime.InteropServices;
using System.Text.Json;

namespace Omarchy.Enhanced;

// Arrange applications in disjoint regions of the primary guest scanout. Only
// geometry crosses the metadata channel; pixels stay in QEMU's framebuffer.
internal static class WindowLayout
{
  static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
  internal sealed record Entry(string WindowId, uint ProcessId, int Width, int Height);
  internal sealed record Placement(Entry Window, int X, int Y);

  internal static Placement[]? Pack(IReadOnlyList<Entry> windows, int width, int height)
  {
    List<Placement> result = [];
    int x = 0, y = 0, rowHeight = 0;
    foreach (var window in windows)
    {
      if (window.Width > width || window.Height > height) return null;
      if (x + window.Width > width) { x = 0; y += rowHeight; rowHeight = 0; }
      if (y + window.Height > height) return null;
      result.Add(new(window, x, y));
      x += window.Width;
      rowHeight = Math.Max(rowHeight, window.Height);
    }
    return result.ToArray();
  }

  internal static object Apply(JsonElement request)
  {
    var entries = request.GetProperty("windows").Deserialize<Entry[]>(Json) ?? [];
    if (entries.Length is < 1 or > 16 || entries.Select(e => (e.WindowId, e.ProcessId)).Distinct().Count() != entries.Length)
      throw new InvalidDataException("Expected 1–16 distinct application windows");
    foreach (var entry in entries)
    {
      if (entry.Width is < 200 or > WindowControl.MaxWidth || entry.Height is < 120 or > WindowControl.MaxHeight)
        throw new InvalidDataException("Unsupported application size");
      using var identity = JsonDocument.Parse(JsonSerializer.Serialize(entry, Json));
      WindowControl.Resolve(identity.RootElement);
    }
    for (int attempt = 0; attempt < 3; attempt++)
    {
      Mode current = Current();
      var placements = Pack(entries, (int)current.Width, (int)current.Height);
      if (placements is null)
      {
        var modes = Modes().Where(m => m.Width <= 8192 && m.Height <= 8192 && m.Bits == 32)
          .OrderBy(m => (long)m.Width * m.Height).ThenByDescending(m => m.Frequency);
        bool changed = false;
        foreach (Mode candidate in modes)
        {
          placements = Pack(entries, (int)candidate.Width, (int)candidate.Height);
          if (placements is null) continue;
          Mode mode = candidate;
          if (ChangeDisplaySettingsEx(null, ref mode, 0, 2, 0) != 0) continue;
          if (ChangeDisplaySettingsEx(null, ref mode, 0, 0, 0) != 0) continue;
          changed = true;
          break;
        }
        if (!changed) throw new InvalidDataException("No supported guest display mode can fit these application windows");
      }
      List<object> actual = [];
      bool fits = true;
      for (int i = 0; i < placements!.Length; i++)
      {
        var placement = placements[i];
        using var resize = JsonDocument.Parse(JsonSerializer.Serialize(new {
          windowId = placement.Window.WindowId, processId = placement.Window.ProcessId,
          width = entries[i].Width, height = entries[i].Height, x = placement.X, y = placement.Y, raise = true
        }, Json));
        nint window = WindowControl.Resolve(resize.RootElement);
        WindowControl.Resize(window, resize.RootElement);
        Native.GetWindowRect(window, out var outer);
        var frame = Native.VisibleFrame(window, outer);
        int width = frame.Right - frame.Left, height = frame.Bottom - frame.Top;
        actual.Add(new { windowId = entries[i].WindowId, processId = entries[i].ProcessId,
          x = frame.Left, y = frame.Top, width, height });
        if (width > entries[i].Width || height > entries[i].Height)
        {
          entries[i] = entries[i] with { Width = Math.Max(width, entries[i].Width), Height = Math.Max(height, entries[i].Height) };
          fits = false;
        }
      }
      if (fits)
      {
        Mode display = Current();
        return new { width = display.Width, height = display.Height, windows = actual };
      }
    }
    throw new InvalidDataException("Applications did not settle into non-overlapping display regions");
  }

  internal static Mode Current()
  {
    Mode mode = new() { Size = (ushort)Marshal.SizeOf<Mode>() };
    if (!EnumDisplaySettings(null, -1, ref mode)) throw new InvalidDataException("Cannot query guest display mode");
    return mode;
  }

  internal static IEnumerable<Mode> Modes()
  {
    for (int index = 0; index < 1024; index++)
    {
      Mode mode = new() { Size = (ushort)Marshal.SizeOf<Mode>() };
      if (!EnumDisplaySettings(null, index, ref mode)) yield break;
      yield return mode;
    }
  }

  // Blittable DEVMODEW, retaining all driver-returned fields for mode testing.
  [StructLayout(LayoutKind.Explicit, Size = 220)]
  internal struct Mode
  {
    [FieldOffset(68)] public ushort Size;
    [FieldOffset(168)] public uint Bits;
    [FieldOffset(172)] public uint Width;
    [FieldOffset(176)] public uint Height;
    [FieldOffset(184)] public uint Frequency;
  }
  [DllImport("user32.dll", CharSet = CharSet.Unicode)]
  static extern bool EnumDisplaySettings(string? device, int index, ref Mode mode);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)]
  static extern int ChangeDisplaySettingsEx(string? device, ref Mode mode, nint window, uint flags, nint parameter);
}
