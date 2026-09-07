namespace Heartbeat.Collection.Hub.Upload
{
    /// <summary>
    /// 出网源：Upload Stream 的上游 buffer 形状。segments 的 hub 缓冲与
    /// input-events 缓冲是两个生产 adapter——出网侧从此只认识这一套词汇。
    /// Drain 是破坏性取走下一个 adapter-defined 出网批；Reinject 接收既没送达也没缓存住的退回批
    /// （ADR-020 上传通道契约），保证 drain 后的批不静默蒸发。
    /// </summary>
    public interface IUploadSource<T>
    {
        /// <summary>Owner evidence for all remaining data, including any undrained backlog.</summary>
        DeliveryRemainder Remainder => DeliveryRemainder.Unknown;

        /// <summary>取走下一个出网批；adapter 可以为请求体或延迟约束保留其余 backlog。</summary>
        List<T> Drain();

        /// <summary>退回重注入：双失败（送不达且缓存不住）的批回到源，下轮重试。</summary>
        void Reinject(List<T> items);
    }

    /// <summary>
    /// A durable source keeps a drained snapshot until the upload stream atomically commits which
    /// records still require retry. A crash before completion therefore replays stable IDs instead
    /// of losing the in-flight batch.
    /// </summary>
    public interface IDurableUploadSource<T> : IUploadSource<T>
    {
        void CompleteDrain(IReadOnlyList<T> drained, IReadOnlyList<T> retryItems);
    }
}
