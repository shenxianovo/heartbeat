using Heartbeat.Core;
using Heartbeat.Core.DTOs.Segments;

namespace Heartbeat.Core.Tests;

/// <summary>
/// 唯一摄入校验策略的行为契约（ADR-020 后 UsageValidationPolicy 退役，
/// 时间阈值用例自其测试移植）。
/// </summary>
public class SegmentValidationPolicyTests
{
    private static readonly DateTimeOffset Now = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static ActivitySegmentItem Segment(
        DateTimeOffset start,
        DateTimeOffset end,
        string source = "browser",
        string identityKey = "https://example.com") => new()
    {
        Id = Guid.CreateVersion7(),
        Source = source,
        IdentityKey = identityKey,
        StartTime = start,
        EndTime = end
    };

    [Fact]
    public void OfflineBacklog_WithValidDuration_PassesAfterSeveralDays()
    {
        var segment = Segment(Now.AddDays(-4), Now.AddDays(-4).AddMinutes(5));

        Assert.True(SegmentValidationPolicy.IsValid(segment, Now));
    }

    [Fact]
    public void ValidSegment_Passes()
    {
        var result = SegmentValidationPolicy.Filter(
            [Segment(Now.AddMinutes(-10), Now.AddMinutes(-5))], Now);

        Assert.Single(result);
    }

    [Fact]
    public void ZeroLengthSegment_Passes()
    {
        // 点事件 = 零长度段（ADR-017 §3）
        var t = Now.AddMinutes(-5);
        var result = SegmentValidationPolicy.Filter([Segment(t, t)], Now);

        Assert.Single(result);
    }

    [Fact]
    public void EmptyId_Rejected()
    {
        var seg = Segment(Now.AddMinutes(-10), Now.AddMinutes(-5));
        seg.Id = Guid.Empty;

        Assert.Empty(SegmentValidationPolicy.Filter([seg], Now));
    }

    [Fact]
    public void MissingSource_Rejected()
    {
        var result = SegmentValidationPolicy.Filter(
            [Segment(Now.AddMinutes(-10), Now.AddMinutes(-5), source: " ")], Now);

        Assert.Empty(result);
    }

    [Fact]
    public void MissingIdentityKey_Rejected()
    {
        var result = SegmentValidationPolicy.Filter(
            [Segment(Now.AddMinutes(-10), Now.AddMinutes(-5), identityKey: "")], Now);

        Assert.Empty(result);
    }

    [Fact]
    public void EndBeforeStart_Rejected()
    {
        var result = SegmentValidationPolicy.Filter(
            [Segment(Now.AddMinutes(-2), Now.AddMinutes(-5))], Now);

        Assert.Empty(result);
    }

    [Fact]
    public void YearBeforeMinYear_Rejected()
    {
        var result = SegmentValidationPolicy.Filter(
            [Segment(new DateTimeOffset(2019, 12, 31, 23, 0, 0, TimeSpan.Zero), Now)], Now);

        Assert.Empty(result);
    }

    [Fact]
    public void FutureBeyondSkewTolerance_Rejected()
    {
        var result = SegmentValidationPolicy.Filter(
            [Segment(Now.AddMinutes(5), Now.AddMinutes(15))], Now);

        Assert.Empty(result);
    }

    [Fact]
    public void FutureWithinSkewTolerance_Passes()
    {
        var result = SegmentValidationPolicy.Filter(
            [Segment(Now.AddMinutes(-5), Now.AddMinutes(9))], Now);

        Assert.Single(result);
    }

    [Fact]
    public void DurationExceedsMax_Rejected()
    {
        var result = SegmentValidationPolicy.Filter(
            [Segment(Now.AddHours(-25), Now.AddMinutes(-5))], Now);

        Assert.Empty(result);
    }

    [Fact]
    public void DurationExactlyMax_Passes()
    {
        var start = Now.AddHours(-24);
        var result = SegmentValidationPolicy.Filter(
            [Segment(start, start.AddHours(24))], Now);

        Assert.Single(result);
    }

    [Fact]
    public void DefaultStartTime_Rejected()
    {
        var result = SegmentValidationPolicy.Filter([Segment(default, Now)], Now);

        Assert.Empty(result);
    }

    [Fact]
    public void ResultIsSortedByStartTime()
    {
        var later = Segment(Now.AddMinutes(-3), Now.AddMinutes(-1), identityKey: "B");
        var earlier = Segment(Now.AddMinutes(-10), Now.AddMinutes(-8), identityKey: "A");

        var result = SegmentValidationPolicy.Filter([later, earlier], Now);

        Assert.Equal(2, result.Count);
        Assert.Equal("A", result[0].IdentityKey);
        Assert.Equal("B", result[1].IdentityKey);
    }

    [Fact]
    public void RotationContractLeavesPositiveToleranceBelowIngestMaximum()
    {
        Assert.True(SegmentRotationPolicy.RotateAfter < SegmentValidationPolicy.MaxDuration);
        Assert.True(SegmentRotationPolicy.UploadAndClockTolerance > TimeSpan.Zero);
        Assert.Equal(
            SegmentValidationPolicy.MaxDuration,
            SegmentRotationPolicy.RotateAfter + SegmentRotationPolicy.UploadAndClockTolerance);
    }
}
