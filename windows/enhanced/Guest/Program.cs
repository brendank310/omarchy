using System.Runtime.Versioning;
using Omarchy.Enhanced;

[assembly: SupportedOSPlatform("windows")]

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows guest required");
if (args.Length == 2 && args[0] == "agent")
{
  await DesktopAgent.Run(args[1], default);
}
else if (args.Length == 1 && args[0] == "service")
{
  GuestService.Run();
}
else
{
  Console.Error.WriteLine("Usage: Omarchy.EnhancedWindows.exe service | agent <pipe>");
  Environment.ExitCode = 2;
}
