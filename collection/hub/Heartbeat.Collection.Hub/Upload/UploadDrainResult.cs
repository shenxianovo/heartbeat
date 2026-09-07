namespace Heartbeat.Collection.Hub.Upload;

/// <summary>
/// Evidence from the owner of pending data. A null unconfirmed count means the remainder could
/// not be enumerated. Memory custody alone is unconfirmed; it is not proof of persistence.
/// Counts describe retained copies, not globally unique Facts across storage owners.
/// </summary>
public sealed record DeliveryRemainder(int RetainedLocally, int? Unconfirmed)
{
    public static DeliveryRemainder Unknown { get; } = new(0, null);
    public bool IsDurable => Unconfirmed == 0;
    public bool IsEmpty => IsDurable && RetainedLocally == 0;

    public static DeliveryRemainder operator +(DeliveryRemainder left, DeliveryRemainder right) =>
        new(left.RetainedLocally + right.RetainedLocally, left.Unconfirmed + right.Unconfirmed);
}

/// <summary>Items are snapshots read this round; the remainder also includes untouched backlog.</summary>
public sealed record UploadDrainResult<T>(
    IReadOnlyList<T> Items,
    int DeliveredCount,
    DeliveryRemainder Remainder,
    Exception? Error = null);
