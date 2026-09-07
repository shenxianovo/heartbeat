using Heartbeat.Desktop.UI.Hosting;

namespace Heartbeat.Verification;

internal static class ProfileProbe
{
    // Internal cross-process regression helper. Does not compose a Desktop Host.
    public static int Run(string[] args)
    {
        using var bootstrap = new DesktopBootstrap(["--data-directory", args[0]], args[1]);
        if (!bootstrap.TryAcquire(args[2])) { Console.WriteLine("occupied"); return 3; }
        Console.WriteLine("acquired");
        Console.ReadLine();
        return 0;
    }
}
