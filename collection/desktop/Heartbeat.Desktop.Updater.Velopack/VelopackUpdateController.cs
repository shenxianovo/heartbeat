using Heartbeat.Desktop.UI.Presentation;
using Heartbeat.Desktop.UI.Hosting;
using Serilog;
using Velopack;
using Velopack.Sources;

namespace Heartbeat.Desktop.Updater.Velopack;

public interface IReleaseUpdate
{
    string Version { get; }
}

public interface IReleaseUpdateClient
{
    bool IsInstalled { get; }
    Task<IReleaseUpdate?> CheckForUpdatesAsync();
    Task DownloadUpdatesAsync(IReleaseUpdate release, Action<int> progress);
    void ScheduleUpdateAndRestart(IReleaseUpdate release);
    void ScheduleUpdateAndExit(IReleaseUpdate release);
}

public sealed class VelopackUpdateController : IDesktopUpdates
{
    public const string RepositoryUrl = "https://github.com/shenxianovo/Heartbeat";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(4);
    private static readonly TimeSpan[] DefaultRetryDelays =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(8),
    ];

    private readonly IReleaseUpdateClient _client;
    private readonly Func<Task> _prepareForRestart;
    private readonly IReadOnlyList<TimeSpan> _retryDelays;
    private readonly object _gate = new();
    private IReleaseUpdate? _pending;
    private Task<UpdateCheckResult>? _inflightCheck;
    private bool _isDownloading;
    private bool _exiting;
    private Timer? _timer;
    private UpdateSnapshot _current = UpdateSnapshot.Idle;

    public VelopackUpdateController(string channel, Func<Task> prepareForRestart)
        : this(new VelopackReleaseUpdateClient(channel), prepareForRestart, DefaultRetryDelays)
    {
    }

    public VelopackUpdateController(
        IReleaseUpdateClient client,
        Func<Task> prepareForRestart,
        IReadOnlyList<TimeSpan> retryDelays)
    {
        _client = client;
        _prepareForRestart = prepareForRestart;
        _retryDelays = retryDelays;
    }

    public bool IsSupported => true;
    public UpdateSnapshot Current
    {
        get { lock (_gate) return _current; }
    }
    public event Action<UpdateSnapshot>? Changed;

    public void Start()
    {
        _ = CheckAsync();
        _timer = new Timer(_ => _ = CheckAsync(), null, CheckInterval, CheckInterval);
    }

    public Task<UpdateCheckResult> CheckAsync()
    {
        if (!_client.IsInstalled) return Task.FromResult(UpdateCheckResult.Skipped);

        TaskCompletionSource<UpdateCheckResult> completion;
        lock (_gate)
        {
            if (_exiting || _current.State is UpdateState.Downloading or UpdateState.ReadyToApply)
                return Task.FromResult(UpdateCheckResult.Skipped);
            if (_inflightCheck != null) return _inflightCheck;

            completion = new TaskCompletionSource<UpdateCheckResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _inflightCheck = completion.Task;
        }

        _ = CompleteCheckAsync(completion);
        return completion.Task;
    }

    private async Task CompleteCheckAsync(TaskCompletionSource<UpdateCheckResult> completion)
    {
        try
        {
            try
            {
                var update = await _client.CheckForUpdatesAsync();
                if (update == null)
                {
                    Publish(UpdateSnapshot.Idle);
                    completion.TrySetResult(UpdateCheckResult.UpToDate);
                    return;
                }

                lock (_gate) _pending = update;
                Publish(new UpdateSnapshot(UpdateState.UpdateAvailable, update.Version));
                _ = DownloadAsync();
                completion.TrySetResult(UpdateCheckResult.UpdateFound);
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "检查更新失败");
                Publish(Current with { Error = "检查更新失败，请检查网络后重试。" });
                completion.TrySetResult(UpdateCheckResult.CheckFailed);
            }
        }
        finally
        {
            lock (_gate) _inflightCheck = null;
        }
    }

    private async Task DownloadAsync()
    {
        IReleaseUpdate release;
        lock (_gate)
        {
            if (_exiting || _current.State != UpdateState.UpdateAvailable || _isDownloading || _pending == null) return;
            _isDownloading = true;
            release = _pending;
        }

        Publish(Current with { State = UpdateState.Downloading, DownloadProgress = 0, Error = null });
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await _client.DownloadUpdatesAsync(
                        release,
                        progress => Publish(Current with { DownloadProgress = progress }));
                    Publish(Current with { State = UpdateState.ReadyToApply, DownloadProgress = 100 });
                    return;
                }
                catch (Exception exception) when (
                    attempt < _retryDelays.Count && IsTransient(exception))
                {
                    var delay = _retryDelays[attempt];
                    Log.Warning(
                        exception,
                        "下载更新失败（第 {Attempt} 次），{Delay} 后重试",
                        attempt + 1,
                        delay);
                    await Task.Delay(delay);
                }
                catch (Exception exception)
                {
                    Log.Error(exception, "下载更新失败，已放弃重试");
                    Publish(Current with
                    {
                        State = UpdateState.UpdateAvailable,
                        DownloadProgress = null,
                        Error = "更新下载失败，将在下次检查时重试。",
                    });
                    return;
                }
            }
        }
        finally
        {
            lock (_gate) _isDownloading = false;
        }
    }

    public async Task<bool> ApplyAsync()
    {
        lock (_gate)
            if (_current.State != UpdateState.ReadyToApply || _pending is null) return false;
        // The application owns acceptance and the one final action; a button cannot launch an installer.
        await _prepareForRestart();
        return true;
    }

    public Action PrepareExit(DesktopExitReason reason)
    {
        IReleaseUpdate? pending;
        lock (_gate)
        {
            _exiting = true;
            pending = _current.State == UpdateState.ReadyToApply ? _pending : null;
        }
        _timer?.Dispose();
        return () =>
        {
            if (pending is null) return;
            if (reason == DesktopExitReason.UpdateRestart) _client.ScheduleUpdateAndRestart(pending);
            else _client.ScheduleUpdateAndExit(pending);
        };
    }

    private void Publish(UpdateSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_exiting) return;
            _current = snapshot;
        }
        Changed?.Invoke(snapshot);
    }

    private static bool IsTransient(Exception exception) =>
        exception is HttpRequestException or TimeoutException or TaskCanceledException or IOException
        || exception.InnerException is { } inner && IsTransient(inner);

    public void Dispose() => _timer?.Dispose();
}

internal sealed class VelopackReleaseUpdateClient : IReleaseUpdateClient
{
    private readonly UpdateManager _manager;

    public VelopackReleaseUpdateClient(string channel)
    {
        _manager = new UpdateManager(
            new GithubSource(VelopackUpdateController.RepositoryUrl, null, false),
            new UpdateOptions { ExplicitChannel = channel });
    }

    public bool IsInstalled => _manager.IsInstalled;

    public async Task<IReleaseUpdate?> CheckForUpdatesAsync()
    {
        var update = await _manager.CheckForUpdatesAsync();
        return update == null
            ? null
            : new VelopackReleaseUpdate(update.TargetFullRelease.Version.ToString(), update);
    }

    public Task DownloadUpdatesAsync(IReleaseUpdate release, Action<int> progress) =>
        _manager.DownloadUpdatesAsync(Unwrap(release), progress);

    public void ScheduleUpdateAndRestart(IReleaseUpdate release) =>
        _manager.WaitExitThenApplyUpdates(Unwrap(release), restart: true);

    public void ScheduleUpdateAndExit(IReleaseUpdate release) =>
        _manager.WaitExitThenApplyUpdates(Unwrap(release), silent: true, restart: false);

    private static UpdateInfo Unwrap(IReleaseUpdate release) =>
        release is VelopackReleaseUpdate velopackRelease
            ? velopackRelease.Update
            : throw new ArgumentException(
                "The release was not created by the Velopack client.",
                nameof(release));

    private sealed record VelopackReleaseUpdate(string Version, UpdateInfo Update) : IReleaseUpdate;
}
