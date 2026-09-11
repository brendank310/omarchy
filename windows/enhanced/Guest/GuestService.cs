using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Omarchy.Enhanced;

internal static class GuestService
{
  const string Name = "OmarchyEnhancedWindows";
  const string Device = @"\\.\Global\org.omarchy.enhanced.0";
  static readonly CancellationTokenSource Stop = new();
  static readonly Native.ServiceMain MainCallback = ServiceMain;
  static readonly Native.ServiceHandler HandlerCallback = Control;
  static nint statusHandle;

  public static void Run()
  {
    if (!Native.StartServiceCtrlDispatcher([
      new() { Name = Name, Main = MainCallback }, new() ]))
      throw new Win32Exception(Marshal.GetLastWin32Error());
  }

  static void Status(uint state, uint error = 0)
  {
    var status = new Native.ServiceStatus { Type = 0x10, State = state,
      Accepted = state == 4 ? 5u : 0u, Win32Exit = error,
      WaitHint = state is 2 or 3 ? 10000u : 0u };
    Native.SetServiceStatus(statusHandle, ref status);
  }

  static uint Control(uint control, uint eventType, nint data, nint context)
  {
    if (control is 1 or 5) { Status(3); Stop.Cancel(); }
    return 0;
  }

  static void ServiceMain(uint count, nint args)
  {
    statusHandle = Native.RegisterServiceCtrlHandlerEx(Name, HandlerCallback, 0);
    if (statusHandle == 0) return;
    Status(4);
    try { RunConnections(Stop.Token).GetAwaiter().GetResult(); Status(1); }
    catch (OperationCanceledException) { Status(1); }
    catch (Exception error) { LogFailure(error); Status(1, 1); }
  }

  static async Task RunConnections(CancellationToken cancel)
  {
    while (!cancel.IsCancellationRequested)
    {
      try
      {
        using var handle = Native.CreateFile(Device, 0xc0000000, 0, 0, 3, 0x40000000, 0);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        // Buffered FileStream serializes reads and writes behind one semaphore.
        // This full-duplex device must publish events while a host read is pending.
        await using var transport = new FileStream(handle, FileAccess.ReadWrite, 1, true);
        await Serve(new Wire(transport), cancel);
      }
      catch (Exception e) when (e is IOException or Win32Exception or InvalidDataException or System.Text.Json.JsonException or ObjectDisposedException)
      {
        // The port can disappear during host shutdown or driver installation.
        // Reconnect without retaining requests from the previous connection.
        LogFailure(e);
      }
      catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { }
      await Task.Delay(2000, cancel);
    }
  }

  static void LogFailure(Exception error)
  {
    // Keep only exception type/code, never host request bodies or document titles.
    // Program Files is writable by this SYSTEM service, not by desktop users.
    try
    {
      string path = Path.Combine(AppContext.BaseDirectory, "service.log");
      if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
        File.Move(path, path + ".previous", true);
      File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {error.GetType().Name} 0x{error.HResult:x8}\n");
    }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }
  }

  static async Task Serve(Wire host, CancellationToken cancel)
  {
    await using var inbox = new RequestInbox(host, cancel);
    bool unavailableSent = false;
    await host.Write(new { v = 1, type = "hello", name = "Enhanced Windows experience",
      capabilities = new[] { "apps.list", "app.launch", "windows.snapshot", "window.resize", "window.focus", "windows.layout" }, rendering = false }, cancel);
    // Exactly one reader owns the host transport. When no desktop is available,
    // requests receive a useful error rather than accumulating for a later user.
    while (!cancel.IsCancellationRequested)
    {
      uint session = Native.WTSGetActiveConsoleSessionId();
      if (session == uint.MaxValue || !Native.WTSQueryUserToken(session, out nint token))
      {
        if (!unavailableSent)
        {
          await host.Write(new { v = 1, type = "session.unavailable" }, cancel);
          unavailableSent = true;
        }
        if (!await inbox.AvailableWithin(TimeSpan.FromMilliseconds(500))) continue;
        using var request = await inbox.Read();
        if (request == null) return;
        var (id, _) = Requests.Parse(request.RootElement);
        await host.Write(new { v = 1, id, error = "no_interactive_session" }, cancel);
        continue;
      }
      try
      {
        // Preserve UAC: never launch the desktop agent with a split token's
        // elevated half, even if WTS hands that half to this SYSTEM service.
        if (!Native.GetTokenInformation(token, 18, out int elevation, sizeof(int), out _))
          throw new Win32Exception(Marshal.GetLastWin32Error());
        if (elevation == 2)
        {
          if (!Native.GetTokenInformation(token, 19, out nint limited, nint.Size, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
          Native.CloseHandle(token);
          token = limited;
        }
        await ServeSession(host, inbox, session, token, cancel);
      }
      finally { Native.CloseHandle(token); }
      return; // Reopen transport and issue a fresh hello after session changes.
    }
  }

  static async Task ServeSession(Wire host, RequestInbox inbox, uint session, nint token, CancellationToken cancel)
  {
    string epoch = Guid.NewGuid().ToString("N");
    await host.Write(new { v = 1, type = "session.ready", session = epoch, windowsSession = session }, cancel);
    var name = "Omarchy.Enhanced." + Guid.NewGuid().ToString("N");
    using var identity = new WindowsIdentity(token);
    var security = new PipeSecurity();
    security.SetAccessRuleProtection(true, false);
    security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
      PipeAccessRights.FullControl, AccessControlType.Allow));
    security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.ReadWrite, AccessControlType.Allow));
    using var pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1,
      PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
    var process = StartAgent(token, name);
    using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancel);
    try
    {
      using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
      timeout.CancelAfter(TimeSpan.FromSeconds(15));
      await pipe.WaitForConnectionAsync(timeout.Token);
      if (!Native.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) || pid != process.ProcessId)
        throw new IOException("Unexpected desktop agent process");
      var agent = new Wire(pipe);
      Task outgoing = RelayAgent(agent, host, lifetime.Token);
      Task incoming = RelayHost(host, inbox, agent, epoch, lifetime.Token);
      Task monitor = MonitorSession(session, lifetime.Token);
      Task completed = await Task.WhenAny(outgoing, incoming, monitor);
      lifetime.Cancel();
      pipe.Dispose();
      inbox.Cancel();
      // Observe every task; cancellation interrupts pending overlapped reads.
      try { await Task.WhenAll(outgoing, incoming, monitor); }
      catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { }
      await completed;
    }
    finally
    {
      lifetime.Cancel();
      Native.TerminateProcess(process.Process, 0);
      Native.CloseHandle(process.Thread);
      Native.CloseHandle(process.Process);
    }
  }

  static Native.ProcessInfo StartAgent(nint token, string pipe)
  {
    string executable = Environment.ProcessPath!;
    var startup = new Native.StartupInfo { Size = Marshal.SizeOf<Native.StartupInfo>(), Desktop = @"winsta0\default" };
    if (!Native.CreateEnvironmentBlock(out nint environment, token, false))
      throw new Win32Exception(Marshal.GetLastWin32Error());
    try
    {
      if (!Native.CreateProcessAsUser(token, executable,
          new StringBuilder($"\"{executable}\" agent {pipe}"), 0, 0, false,
          0x08000400, environment, AppContext.BaseDirectory, ref startup, out var process))
        throw new Win32Exception(Marshal.GetLastWin32Error());
      return process;
    }
    finally { Native.DestroyEnvironmentBlock(environment); }
  }

  static async Task MonitorSession(uint session, CancellationToken cancel)
  {
    while (Native.WTSGetActiveConsoleSessionId() == session)
      await Task.Delay(500, cancel);
  }

  static async Task RelayAgent(Wire agent, Wire host, CancellationToken cancel)
  {
    while (await agent.Read(cancel) is { } message)
      using (message) { await host.Write(message.RootElement, cancel); }
  }

  static async Task RelayHost(Wire host, RequestInbox inbox, Wire agent, string epoch, CancellationToken cancel)
  {
    while (await inbox.Read() is { } request)
    {
      using (request)
      {
        var (id, method) = Requests.Parse(request.RootElement);
        if (!Requests.MatchesSession(request.RootElement, epoch))
          await host.Write(new { v = 1, id, error = "stale_session" }, cancel);
        else if (method is "apps.list" or "app.launch" or "window.resize" or "window.focus" or "windows.layout")
          await agent.Write(request.RootElement, cancel);
        else
          await host.Write(new { v = 1, id, error = "unsupported_method" }, cancel);
      }
    }
  }
}
