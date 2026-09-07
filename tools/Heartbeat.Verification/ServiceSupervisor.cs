using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Heartbeat.Verification;

/// <summary>
/// Keeps a process-group owner alive even if a service exits before its children.
/// Closing the runner's stdin pipe also triggers cleanup if the runner dies.
/// </summary>
internal static class ServiceSupervisor
{
    [DllImport("libc", EntryPoint = "setsid", SetLastError = true)]
    private static extern int SetSession();

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Signal(int pid, int signal);

    internal static void SignalGroup(int pid, int signal)
    {
        if (!OperatingSystem.IsWindows()) Signal(-pid, signal);
    }

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 2) return 2;
        if (!OperatingSystem.IsWindows() && SetSession() < 0) return 1;
        using var termination = OperatingSystem.IsWindows() ? null :
            PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => context.Cancel = true);
        var statePath = args[0];
        var start = new ProcessStartInfo(args[1]) { UseShellExecute = false };
        foreach (var arg in args[2..]) start.ArgumentList.Add(arg);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Service could not start.");
        await File.WriteAllTextAsync(statePath + ".started", child.Id.ToString());
        var observeExit = ObserveExitAsync();
        // The parent sends "stop", or EOF arrives when it disappears.
        await Console.In.ReadLineAsync();
        if (OperatingSystem.IsWindows())
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
        }
        else SignalGroup(Environment.ProcessId, 15);
        try { await observeExit.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException) { }
        if (!OperatingSystem.IsWindows()) SignalGroup(Environment.ProcessId, 9);
        return 0;

        async Task ObserveExitAsync()
        {
            await child.WaitForExitAsync();
            var temporary = statePath + ".exit.tmp";
            await File.WriteAllTextAsync(temporary, child.ExitCode.ToString());
            File.Move(temporary, statePath + ".exit", overwrite: true);
        }
    }
}
