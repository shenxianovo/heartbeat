using Heartbeat.Core.DTOs.Segments;

namespace Heartbeat.Core;

/// <summary>
/// 段的通用完整性校验（ADR-017/020，唯一摄入校验策略）：
/// 允许零长度段（点事件），要求 Source/IdentityKey/Id 齐全，时间阈值拒收畸形数据。
/// 不限制 Source 取值——'system' 冒充的拒收是 Agent 枢纽 loopback 层的职责（ADR-020），策略本身 source 无关。
/// </summary>
public static class SegmentValidationPolicy
{
    public static readonly TimeSpan TimeSkewTolerance = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(24);
    public static readonly int MinYear = 2020;

    public static bool IsValid(ActivitySegmentItem segment, DateTimeOffset now) =>
        segment.Id != Guid.Empty
        && !string.IsNullOrWhiteSpace(segment.Source)
        && !string.IsNullOrWhiteSpace(segment.IdentityKey)
        && segment.StartTime != default
        && segment.EndTime >= segment.StartTime
        && segment.StartTime.Year >= MinYear
        && segment.EndTime <= now + TimeSkewTolerance
        // Offline delivery may be days late. MaxDuration bounds the observed interval,
        // not its age at upload; historical facts must remain replayable.
        && (segment.EndTime - segment.StartTime) <= MaxDuration;

    public static List<ActivitySegmentItem> Filter(List<ActivitySegmentItem> segments, DateTimeOffset now)
    {
        return segments
            .Where(segment => IsValid(segment, now))
            .OrderBy(s => s.StartTime)
            .ToList();
    }
}
