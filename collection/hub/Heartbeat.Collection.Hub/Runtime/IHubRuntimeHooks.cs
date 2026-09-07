using Heartbeat.Core.DTOs.Segments;

namespace Heartbeat.Collection.Hub.Runtime;

/// <summary>
/// 平台 host 对 upload runtime 的可选挂点。Windows 用它保留旧缓存提示与图标提取；
/// 无头 host 使用默认空实现，不必携带 System.Drawing 或平台文件约定。
/// </summary>
public interface IHubRuntimeHooks
{
    void OnStarting();
    Task SegmentsDrainedAsync(IReadOnlyCollection<ActivitySegmentItem> segments, CancellationToken cancellationToken = default);
}

public sealed class NullHubRuntimeHooks : IHubRuntimeHooks
{
    public void OnStarting() { }
    public Task SegmentsDrainedAsync(IReadOnlyCollection<ActivitySegmentItem> segments, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
