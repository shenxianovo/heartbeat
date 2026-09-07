using Heartbeat.Collection.Hub.Storage;

namespace Heartbeat.Collection.Hub.Upload;

/// <summary>Retained source for an existing retry file, including legacy InputEvent backlog.</summary>
public sealed class CachedUploadSource<T>(ICache<T> cache) : IUploadSource<T>
{
    public UploadStreamStatus StorageStatus => cache.Status.State == CacheFileState.MigrationFailed
        ? new(UploadStreamState.CacheMigrationFailed, cache.Status.Message, cache.Status.Action,
            RecoveryPath: cache.Status.BackupPath)
        : UploadStreamStatus.Ready;

    public DeliveryRemainder Remainder => new(cache.Load().Count, 0);
    public List<T> ReadBatch() => cache.Load();

    public void Confirm(IReadOnlyList<T> items)
    {
        var confirmed = items.ToHashSet();
        cache.Replace(cache.Load().Where(item => !confirmed.Contains(item)).ToList());
    }
}
