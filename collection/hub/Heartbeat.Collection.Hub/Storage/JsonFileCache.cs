using System.Text.Json;

namespace Heartbeat.Collection.Hub.Storage;

/// <summary>
/// Versioned, atomically persisted JSON cache. Legacy formats are handled only by explicit
/// IJsonCacheMigration implementations. A migration failure leaves the source file and backup
/// recoverable and makes normal cache operations unavailable.
/// </summary>
public sealed class JsonFileCache<T> : ICache<T>, IDisposable
{
    private readonly string _filePath;
    private readonly int _maxItems;
    private readonly bool _indented;
    private readonly IJsonCacheFileFormat<T> _currentFormat;
    private readonly IReadOnlyList<IJsonCacheMigration<T>> _migrations;
    private readonly ReaderWriterLockSlim _lock = new();
    private List<T> _cache = [];
    private Exception? _startupFailure;

    public JsonFileCache(
        string filePath,
        int maxItems,
        IJsonCacheFileFormat<T> currentFormat,
        IReadOnlyList<IJsonCacheMigration<T>>? migrations = null,
        bool indented = false)
    {
        if (currentFormat.Version <= 0)
            throw new ArgumentOutOfRangeException(nameof(currentFormat), "Cache schema version must be positive.");

        _filePath = filePath;
        _maxItems = maxItems;
        _currentFormat = currentFormat;
        _migrations = migrations ?? [];
        _indented = indented;
        Initialize();
    }

    public CacheFileStatus Status { get; private set; } = CacheFileStatus.Ready;

    public void Add(List<T> items)
    {
        EnsureAvailable();
        if (items is not { Count: > 0 }) return;

        _lock.EnterWriteLock();
        try
        {
            var replacement = new List<T>(_cache.Count + items.Count);
            replacement.AddRange(_cache);
            replacement.AddRange(items);
            EnsureCapacity(replacement);
            CommitReplacement(replacement);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public List<T> Load()
    {
        EnsureAvailable();
        _lock.EnterReadLock();
        try
        {
            return new List<T>(_cache);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public void Replace(List<T> items)
    {
        EnsureAvailable();
        _lock.EnterWriteLock();
        try
        {
            var replacement = new List<T>(items ?? []);
            EnsureCapacity(replacement);
            CommitReplacement(replacement);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public PreparedReplacement PrepareReplacement(List<T> items)
    {
        EnsureAvailable();
        var replacement = new List<T>(items ?? []);
        EnsureCapacity(replacement);
        var temporaryPath = WriteAndValidateTemporary(replacement);
        return new PreparedReplacement(this, temporaryPath, _filePath, replacement);
    }

    public void AcceptPublishedReplacement(PreparedReplacement replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (!ReferenceEquals(replacement.Owner, this))
            throw new InvalidOperationException("The prepared cache replacement belongs to another cache.");
        if (File.Exists(replacement.TemporaryPath))
            throw new InvalidOperationException("The prepared cache replacement has not been published.");
        _lock.EnterWriteLock();
        try
        {
            _cache = replacement.Items;
            replacement.MarkAccepted();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void Clear() => Replace([]);

    private void Initialize()
    {
        if (!File.Exists(_filePath)) return;

        string? backupPath = null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_filePath));
            var root = document.RootElement;
            var sourceVersion = ReadVersion(root);

            if (sourceVersion == _currentFormat.Version)
            {
                _cache = ReadCurrent(root);
                return;
            }

            var migration = _migrations.SingleOrDefault(item =>
                item.SourceVersion == sourceVersion && item.TargetVersion == _currentFormat.Version)
                ?? throw new JsonException(
                    $"No cache migration is registered from schema {DescribeVersion(sourceVersion)} " +
                    $"to {_currentFormat.Version}.");

            var backupCandidate = CreateBackupPath(sourceVersion);
            File.Copy(_filePath, backupCandidate, overwrite: false);
            backupPath = backupCandidate;

            var migrated = migration.Migrate(root);
            WriteAndValidateReplacement(migrated);
            _cache = migrated;
            Status = new CacheFileStatus(
                CacheFileState.Migrated,
                $"Cache schema {DescribeVersion(sourceVersion)} was migrated to {_currentFormat.Version}.",
                "Keep the backup until the migrated activity has uploaded successfully.",
                backupPath);
        }
        catch (Exception ex)
        {
            _startupFailure = ex;
            Status = new CacheFileStatus(
                CacheFileState.MigrationFailed,
                ex.Message,
                "Inspect or restore the cache backup, then restart Heartbeat. Upload draining is paused.",
                backupPath);
        }
    }

    private static int? ReadVersion(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return null;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("schemaVersion", out var version) ||
            !version.TryGetInt32(out var value))
            throw new JsonException("Cache has neither an unversioned array nor a schemaVersion envelope.");
        return value;
    }

    private List<T> ReadCurrent(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("items", out var items))
            throw new JsonException("Versioned cache does not contain items.");
        return _currentFormat.DeserializeItems(items);
    }

    private void CommitReplacement(List<T> replacement)
    {
        WriteAndValidateReplacement(replacement);
        _cache = replacement;
    }

    private void WriteAndValidateReplacement(IReadOnlyList<T> items)
    {
        var tempPath = WriteAndValidateTemporary(items);
        try
        {
            File.Move(tempPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private string WriteAndValidateTemporary(IReadOnlyList<T> items)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var tempPath = _filePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var envelope = new CacheEnvelope(
                _currentFormat.Version,
                _currentFormat.SerializeItems(items));
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = _indented
            };
            File.WriteAllText(tempPath, JsonSerializer.Serialize(envelope, options));

            using (var validationDocument = JsonDocument.Parse(File.ReadAllText(tempPath)))
            {
                var validationRoot = validationDocument.RootElement;
                if (ReadVersion(validationRoot) != _currentFormat.Version)
                    throw new JsonException("Replacement cache has the wrong schemaVersion.");
                _ = ReadCurrent(validationRoot);
            }

            return tempPath;
        }
        catch
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }
    }

    private string CreateBackupPath(int? sourceVersion)
    {
        var basePath = $"{_filePath}.legacy-{DescribeVersion(sourceVersion)}.bak";
        if (!File.Exists(basePath)) return basePath;
        return $"{basePath}.{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
    }

    private void EnsureCapacity(List<T> items)
    {
        if (items.Count <= _maxItems) return;
        throw new IOException($"Cache capacity {_maxItems} exceeded; existing data was retained.");
    }

    private void EnsureAvailable()
    {
        if (Status.State != CacheFileState.MigrationFailed) return;
        throw new CacheUnavailableException(Status.Action, _startupFailure);
    }

    private static string DescribeVersion(int? version) => version?.ToString() ?? "unversioned";

    public void Dispose()
    {
        _lock.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed record CacheEnvelope(int SchemaVersion, JsonElement Items);

    public sealed class PreparedReplacement : IDisposable
    {
        private bool _accepted;

        internal PreparedReplacement(
            JsonFileCache<T> owner,
            string temporaryPath,
            string destinationPath,
            List<T> items)
        {
            Owner = owner;
            TemporaryPath = temporaryPath;
            DestinationPath = destinationPath;
            Items = items;
        }

        internal JsonFileCache<T> Owner { get; }
        internal List<T> Items { get; }
        public string TemporaryPath { get; }
        public string DestinationPath { get; }

        internal void MarkAccepted() => _accepted = true;

        public void Dispose()
        {
            if (!_accepted && File.Exists(TemporaryPath))
                File.Delete(TemporaryPath);
        }
    }
}
