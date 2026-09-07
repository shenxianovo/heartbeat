using Heartbeat.Desktop.UI.Hosting;

namespace Heartbeat.Desktop.Updater.Velopack;

public static class DesktopInstallationBinding
{
    public static DesktopInstallation Resolve(bool allowsBinding, string channel,
        Func<IDesktopLoginStart> loginStart)
    {
        if (!allowsBinding) return DesktopInstallation.Detached;
        var client = new VelopackReleaseUpdateClient(channel);
        if (!client.IsInstalled) return DesktopInstallation.Detached;
        return new DesktopInstallation(loginStart(), restart => new VelopackUpdateController(channel, restart));
    }
}
