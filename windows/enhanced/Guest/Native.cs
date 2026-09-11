using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Omarchy.Enhanced;

internal static class Native
{
  [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
  internal delegate bool EnumWindow(nint window, nint data);
  [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindow callback, nint data);
  [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint window);
  [DllImport("user32.dll")] internal static extern bool IsIconic(nint window);
  [DllImport("user32.dll")] internal static extern bool IsZoomed(nint window);
  [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
  [DllImport("user32.dll")] internal static extern nint GetWindow(nint window, uint command);
  [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint window, out Rect rect);
  [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint process);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint window, StringBuilder text, int count);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(nint window, StringBuilder text, int count);
  [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint window);
  [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(nint context);
  [DllImport("user32.dll", SetLastError = true)] internal static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
  [DllImport("user32.dll")] internal static extern bool CloseDesktop(nint desktop);
  [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  internal static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder value, uint length, out uint needed);
  [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint window, uint attribute, out int value, int size);
  [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint window, uint attribute, out Rect value, int size);
  [DllImport("dwmapi.dll")] internal static extern int DwmFlush();

  internal static Rect VisibleFrame(nint window, Rect outer)
  {
    return DwmGetWindowAttribute(window, 9, out Rect frame, Marshal.SizeOf<Rect>()) == 0 &&
      frame.Right > frame.Left && frame.Bottom > frame.Top ? frame : outer;
  }
  [DllImport("kernel32.dll")] internal static extern uint WTSGetActiveConsoleSessionId();
  [DllImport("wtsapi32.dll", SetLastError = true)] internal static extern bool WTSQueryUserToken(uint session, out nint token);
  [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool GetTokenInformation(nint token, int information, out int value, int length, out int needed);
  [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool GetTokenInformation(nint token, int information, out nint value, int length, out int needed);
  [DllImport("userenv.dll", SetLastError = true)] internal static extern bool CreateEnvironmentBlock(out nint environment, nint token, bool inherit);
  [DllImport("userenv.dll")] internal static extern bool DestroyEnvironmentBlock(nint environment);
  [DllImport("kernel32.dll")] internal static extern bool CloseHandle(nint handle);
  [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
  [DllImport("kernel32.dll")] internal static extern bool TerminateProcess(nint process, uint exitCode);
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
  [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  internal static extern bool CreateProcessAsUser(nint token, string application, StringBuilder command,
    nint processSecurity, nint threadSecurity, bool inherit, uint flags, nint environment, string directory,
    ref StartupInfo startup, out ProcessInfo process);
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  internal struct StartupInfo
  {
    public int Size;
    public string? Reserved, Desktop, Title;
    public uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
    public ushort Show, ReservedSize;
    public nint ReservedData, Input, Output, Error;
  }
  [StructLayout(LayoutKind.Sequential)]
  internal struct ProcessInfo { public nint Process, Thread; public uint ProcessId, ThreadId; }

  [StructLayout(LayoutKind.Sequential)]
  internal struct ServiceStatus
  {
    public uint Type, State, Accepted, Win32Exit, SpecificExit, Checkpoint, WaitHint;
  }
  internal delegate void ServiceMain(uint count, nint args);
  internal delegate uint ServiceHandler(uint control, uint eventType, nint data, nint context);
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  internal struct ServiceEntry { public string? Name; public ServiceMain? Main; }
  [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  internal static extern bool StartServiceCtrlDispatcher([In] ServiceEntry[] entries);
  [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  internal static extern nint RegisterServiceCtrlHandlerEx(string name, ServiceHandler handler, nint context);
  [DllImport("advapi32.dll", SetLastError = true)]
  internal static extern bool SetServiceStatus(nint handle, ref ServiceStatus status);
}
