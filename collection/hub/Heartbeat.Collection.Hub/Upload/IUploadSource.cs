namespace Heartbeat.Collection.Hub.Upload;

/// <summary>
/// Owns pending data until confirmation. Reading never transfers custody; confirming a snapshot
/// must preserve newer arrivals. A failed confirmation may replay data, never lose it.
/// </summary>
public interface IUploadSource<T>
{
    DeliveryRemainder Remainder => DeliveryRemainder.Unknown;
    UploadStreamStatus StorageStatus => UploadStreamStatus.Ready;
    List<T> ReadBatch();
    void Confirm(IReadOnlyList<T> items);
}
