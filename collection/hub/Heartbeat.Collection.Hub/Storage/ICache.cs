namespace Heartbeat.Collection.Hub.Storage
{
    /// <summary>
    /// 离线缓存 seam（ADR-020）：数据 owner 的持久化侧。
    /// 生产 adapter 是 <see cref="JsonFileCache{T}"/>；测试用 fake 驱动保管契约。
    /// </summary>
    public interface ICache<T>
    {
        CacheFileStatus Status { get; }

        /// <summary>追加一批。写盘失败时原缓存不变，调用方仍保管未提交项。</summary>
        void Add(List<T> items);
        List<T> Load();
        /// <summary>原子替换缓存内容。数据 owner 用它提交确认后的剩余项。</summary>
        void Replace(List<T> items);
        void Clear();
    }
}
