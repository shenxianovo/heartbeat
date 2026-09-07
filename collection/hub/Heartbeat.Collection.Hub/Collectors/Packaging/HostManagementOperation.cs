namespace Heartbeat.Collection.Hub.Collectors.Packages;

public enum HostManagementOperationKind
{
    Install,
    Retry,
    Uninstall,
    SubmitAuthorization
}

public enum HostManagementOperationPhase
{
    Pending,
    Running,
    Committing,
    Succeeded,
    Cancelled,
    Failed
}

public enum HostManagementOperationCancellation
{
    Accepted,
    NotCancellable,
    AlreadyTerminal,
    NotFound
}

public sealed record HostManagementOperation(
    Guid OperationId,
    HostManagementOperationKind Kind,
    string PackageId,
    HostManagementOperationPhase Phase,
    string? Failure = null)
{
    public bool IsTerminal => Phase is
        HostManagementOperationPhase.Succeeded or
        HostManagementOperationPhase.Cancelled or
        HostManagementOperationPhase.Failed;
}

public static class HostManagementOperationExtensions
{
    public static async ValueTask<HostManagementOperation> WaitForSuccessAsync(
        this ICollectorMarketplace marketplace,
        HostManagementOperation accepted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(marketplace);
        ArgumentNullException.ThrowIfNull(accepted);
        var completed = await marketplace.WaitForOperationAsync(
            accepted.OperationId,
            cancellationToken);
        if (completed.Phase == HostManagementOperationPhase.Succeeded)
            return completed;
        throw new InvalidOperationException(
            completed.Failure ?? $"Collector operation ended as {completed.Phase}.");
    }
}

internal sealed class HostManagementOperationOwner : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _operations = [];
    private readonly Dictionary<string, Guid> _latestByPackage = new(StringComparer.Ordinal);
    private bool _stopping;
    private bool _disposed;

    public HostManagementOperation Start(
        HostManagementOperationKind kind,
        string packageId,
        string targetKey,
        Func<HostManagementOperationExecution, Task> execute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);
        ArgumentNullException.ThrowIfNull(execute);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stopping)
                throw new InvalidOperationException("Host Management Operations are stopping.");
            if (_latestByPackage.TryGetValue(packageId, out var previousId))
            {
                var previous = _operations[previousId];
                if (!previous.Snapshot().IsTerminal)
                {
                    if (previous.Kind == kind && previous.TargetKey == targetKey)
                        return previous.Snapshot();
                    throw new InvalidOperationException(
                        $"Collector Package '{packageId}' already has a {previous.Kind} operation in progress.");
                }
                _operations.Remove(previousId);
                previous.Cancellation.Dispose();
            }
            var entry = new Entry(Guid.CreateVersion7(), kind, packageId, targetKey);
            _operations.Add(entry.OperationId, entry);
            _latestByPackage[packageId] = entry.OperationId;
            _ = RunAsync(entry, execute);
            return entry.Snapshot();
        }
    }

    public IReadOnlyList<HostManagementOperation> Snapshot()
    {
        lock (_gate)
            return _latestByPackage.Values
                .Select(operationId => _operations[operationId].Snapshot())
                .OrderBy(operation => operation.PackageId, StringComparer.Ordinal)
                .ToArray();
    }

    public HostManagementOperation Get(Guid operationId)
    {
        lock (_gate)
        {
            if (!_operations.TryGetValue(operationId, out var entry))
                throw new KeyNotFoundException($"Host Management Operation '{operationId:D}' was not found.");
            return entry.Snapshot();
        }
    }

    public async ValueTask<HostManagementOperation> WaitAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_operations.TryGetValue(operationId, out entry!))
                throw new KeyNotFoundException($"Host Management Operation '{operationId:D}' was not found.");
        }
        return await entry.Completion.Task.WaitAsync(cancellationToken);
    }

    public HostManagementOperationCancellation Cancel(Guid operationId)
    {
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            if (!_operations.TryGetValue(operationId, out var entry))
                return HostManagementOperationCancellation.NotFound;
            if (entry.Snapshot().IsTerminal)
                return HostManagementOperationCancellation.AlreadyTerminal;
            if (entry.Phase == HostManagementOperationPhase.Committing)
                return HostManagementOperationCancellation.NotCancellable;
            entry.CancellationRequested = true;
            cancellation = entry.Cancellation;
        }
        try { cancellation.Cancel(); }
        catch (AggregateException) { }
        return HostManagementOperationCancellation.Accepted;
    }

    public async ValueTask StopAsync()
    {
        Entry[] entries;
        var cancellations = new List<CancellationTokenSource>();
        lock (_gate)
        {
            _stopping = true;
            entries = _operations.Values.ToArray();
            foreach (var entry in entries.Where(entry =>
                         entry.Phase is HostManagementOperationPhase.Pending or
                             HostManagementOperationPhase.Running))
            {
                entry.CancellationRequested = true;
                cancellations.Add(entry.Cancellation);
            }
        }
        List<Exception>? failures = null;
        foreach (var cancellation in cancellations)
        {
            try { cancellation.Cancel(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        await Task.WhenAll(entries.Select(entry => entry.Completion.Task));
        if (failures is { Count: 1 }) throw failures[0];
        if (failures is { Count: > 1 }) throw new AggregateException(failures);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var entry in _operations.Values)
                entry.Cancellation.Dispose();
            _operations.Clear();
            _latestByPackage.Clear();
        }
    }

    private async Task RunAsync(
        Entry entry,
        Func<HostManagementOperationExecution, Task> execute)
    {
        await Task.Yield();
        SetResult(entry, HostManagementOperationPhase.Running);
        try
        {
            await execute(new HostManagementOperationExecution(this, entry));
            lock (_gate)
                entry.Phase = entry.CancellationRequested &&
                              entry.Phase != HostManagementOperationPhase.Committing
                    ? HostManagementOperationPhase.Cancelled
                    : HostManagementOperationPhase.Succeeded;
        }
        catch (OperationCanceledException) when (entry.CancellationRequested)
        {
            SetResult(entry, HostManagementOperationPhase.Cancelled);
        }
        catch (Exception exception)
        {
            SetResult(entry, HostManagementOperationPhase.Failed, exception.Message);
        }
        finally
        {
            entry.Completion.TrySetResult(entry.Snapshot());
        }
    }

    private void SetResult(
        Entry entry,
        HostManagementOperationPhase phase,
        string? failure = null)
    {
        lock (_gate)
        {
            entry.Phase = phase;
            entry.Failure = failure;
        }
    }

    internal void BeginCommit(Entry entry)
    {
        lock (_gate)
        {
            if (entry.CancellationRequested)
                throw new OperationCanceledException(entry.Cancellation.Token);
            entry.Phase = HostManagementOperationPhase.Committing;
        }
    }

    internal sealed class Entry(
        Guid operationId,
        HostManagementOperationKind kind,
        string packageId,
        string targetKey)
    {
        public Guid OperationId { get; } = operationId;
        public HostManagementOperationKind Kind { get; } = kind;
        public string PackageId { get; } = packageId;
        public string TargetKey { get; } = targetKey;
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<HostManagementOperation> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile HostManagementOperationPhase Phase = HostManagementOperationPhase.Pending;
        public volatile bool CancellationRequested;
        public string? Failure;

        public HostManagementOperation Snapshot() => new(
            OperationId,
            Kind,
            PackageId,
            Phase,
            Failure);
    }
}

internal sealed class HostManagementOperationExecution(
    HostManagementOperationOwner owner,
    HostManagementOperationOwner.Entry entry)
{
    public CancellationToken SafeCancellation => entry.Cancellation.Token;
    public void BeginCommit() => owner.BeginCommit(entry);
}
