using Heartbeat.Desktop.UI.Hosting;
using Heartbeat.Desktop.UI.Presentation;

namespace Heartbeat.Desktop.UI.Tests.Hosting;

public sealed class DesktopInstallationTests
{
    [Fact]
    public async Task DetachedRunCannotMutateInstallationAndReportsUnsupported()
    {
        var installation = DesktopInstallation.Detached;
        installation.ReconcileLoginStart();
        Assert.False(installation.LoginStart.IsSupported);
        Assert.Throws<InvalidOperationException>(() => installation.LoginStart.Enable("test"));
        Assert.Throws<InvalidOperationException>(installation.LoginStart.Disable);
        using var updates = installation.CreateUpdates(() => throw new Exception("must not restart"));
        updates.Start();
        Assert.False(updates.IsSupported);
        Assert.Equal(UpdateCheckResult.Skipped, await updates.CheckAsync());
        Assert.False(await updates.ApplyAsync());
        Assert.False(updates.ScheduleOnExitIfReady());
    }

    [Fact]
    public void BoundInstallationReconcilesOnlyExplicitlyAndOnlyWhenEnabled()
    {
        var login = new LoginStart();
        var installation = new DesktopInstallation(login, _ => throw new NotSupportedException());
        Assert.Equal(0, login.Writes);
        installation.ReconcileLoginStart();
        Assert.Equal(0, login.Writes);
        login.IsEnabled = true;
        installation.ReconcileLoginStart();
        Assert.Equal(1, login.Writes);
    }

    private sealed class LoginStart : IDesktopLoginStart
    {
        public bool IsEnabled { get; set; }
        public int Writes { get; private set; }
        public void Enable(string executablePath) => Writes++;
        public void Disable() => Writes++;
    }
}
