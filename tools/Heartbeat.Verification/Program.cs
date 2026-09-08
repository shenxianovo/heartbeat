using Heartbeat.Verification;

if (args.FirstOrDefault() == "__profile-probe")
    return ProfileProbe.Run(args[1..]);

if (args.FirstOrDefault() == "__supervise")
    return await ServiceSupervisor.RunAsync(args[1..]);

if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("""
        Heartbeat real-process verification
        Prepare runner (builds its source dependencies once):
          dotnet publish tools/Heartbeat.Verification -c Release -o .local/verification-runner
        Execute prepared runner (no MSBuild):
          dotnet .local/verification-runner/Heartbeat.Verification.dll run <headless-main|desktop-main> [options]

          --config PATH             Existing Headless config (default: .local/heartbeat-headless.json).
                                    Only apiKey and management settings are read; its data is never used.
          --artifact SERVICE=PATH   Use existing analytics/headless/desktop/reference binary (Desktop also accepts .app).
                                    Skips that service's publish. Use the prepared runner to avoid source builds.
                                    Report includes version and tree hash; omitted services are published from source.
          --timeout-seconds N       Per-stage runtime deadline (default: 120; builds: 600).
          --keep                    Keep the lane alive after the result until Ctrl+C, then clean up.
          --fault disconnect-upload Deliberately disconnect Hub upload; this run MUST fail.

        Requires .NET 10, Docker, and access to the existing online Auth service.
        Reports: .local/verification/<run-id>/report.json
        Exit codes: 0 passed, 1 failed/cleanup failed, 2 dependency blocked, 130 cancelled.
        """);
    return args.Length == 0 ? 2 : 0;
}

VerificationOptions options;
try { options = VerificationOptions.Parse(args); }
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
using var termination = OperatingSystem.IsWindows() ? null :
    System.Runtime.InteropServices.PosixSignalRegistration.Create(
        System.Runtime.InteropServices.PosixSignal.SIGTERM,
        context => { context.Cancel = true; cancellation.Cancel(); });
return await VerificationCommand.RunAsync(options, cancellation.Token);
