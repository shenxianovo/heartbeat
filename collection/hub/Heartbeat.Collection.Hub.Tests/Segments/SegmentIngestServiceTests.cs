using Heartbeat.Core;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Collection.Hub.Presence;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Collection.Hub.Time;
using Heartbeat.Collection.Hub.Upload;

namespace Heartbeat.Collection.Hub.Tests.Segments;

public class SegmentIngestServiceTests
{
    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
    }

    private static ActivitySegmentItem Segment(
        string source = "browser",
        string identityKey = "https://example.com",
        DateTimeOffset? start = null,
        DateTimeOffset? end = null)
    {
        var s = start ?? DateTimeOffset.UtcNow.AddMinutes(-5);
        return new ActivitySegmentItem
        {
            Id = Guid.CreateVersion7(),
            Source = source,
            IdentityKey = identityKey,
            StartTime = s,
            EndTime = end ?? s.AddMinutes(2)
        };
    }

    [Fact]
    public void Accept_ValidSegments_Buffered()
    {
        var svc = new SegmentIngestService(new FakeClock());

        var accepted = svc.Accept([Segment(), Segment(identityKey: "https://other.com")]);

        Assert.Equal(2, accepted);
        Assert.Equal(2, svc.GetAndClearSegments().Count);
    }

    [Fact]
    public void Accept_SystemSource_Buffered()
    {
        // source 无关（ADR-020）：冒充守卫在 loopback 协议层，
        // 内置采集器进程内直调 Accept，system 段照常入缓冲。
        var svc = new SegmentIngestService(new FakeClock());

        var accepted = svc.Accept([Segment(source: "system", identityKey: "code|main.cs")]);

        Assert.Equal(1, accepted);
        Assert.Single(svc.GetAndClearSegments());
    }

    [Fact]
    public void Accept_MissingId_AssignsUuid()
    {
        var svc = new SegmentIngestService(new FakeClock());
        var seg = Segment();
        seg.Id = Guid.Empty;

        var accepted = svc.Accept([seg]);

        Assert.Equal(1, accepted);
        Assert.NotEqual(Guid.Empty, svc.GetAndClearSegments()[0].Id);
    }

    [Fact]
    public void Accept_ZeroLengthSegment_Accepted()
    {
        // 点事件 = 零长度段(ADR-017 §3)
        var svc = new SegmentIngestService(new FakeClock());
        var t = DateTimeOffset.UtcNow.AddMinutes(-1);

        var accepted = svc.Accept([Segment(start: t, end: t)]);

        Assert.Equal(1, accepted);
    }

    [Fact]
    public void Accept_InvalidSegments_Filtered()
    {
        var svc = new SegmentIngestService(new FakeClock());
        var missingKey = Segment();
        missingKey.IdentityKey = "";
        var reversed = Segment(start: DateTimeOffset.UtcNow, end: DateTimeOffset.UtcNow.AddMinutes(-5));

        var accepted = svc.Accept([missingKey, reversed]);

        Assert.Equal(0, accepted);
        Assert.Empty(svc.GetAndClearSegments());
    }

    [Fact]
    public void Accept_SameIdSnapshot_ReplacesEarlier()
    {
        // 缓冲按 Id 键控（ADR-018）：同段后到快照覆盖先到，攒批自动压缩
        var svc = new SegmentIngestService(new FakeClock());
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-5);
        var first = Segment(start: t0, end: t0.AddMinutes(1));
        var second = Segment(start: t0, end: t0.AddMinutes(3));
        second.Id = first.Id;

        svc.Accept([first]);
        svc.Accept([second]);

        var drained = svc.GetAndClearSegments();
        var single = Assert.Single(drained);
        Assert.Equal(first.Id, single.Id);
        Assert.Equal(t0.AddMinutes(3), single.EndTime);
    }

    [Fact]
    public void GetAndClearSegments_DrainsBuffer()
    {
        var svc = new SegmentIngestService(new FakeClock());
        svc.Accept([Segment()]);

        Assert.Single(svc.GetAndClearSegments());
        Assert.Empty(svc.GetAndClearSegments());
    }

    [Fact]
    public void DurableProjection_EvictionDoesNotEraseUndeliveredCustodyEvidence()
    {
        var svc = new SegmentIngestService(new FakeClock());
        IUploadSource<ActivitySegmentItem> source = svc;
        var evicted = Segment(start: DateTimeOffset.UtcNow.AddHours(-1));
        svc.UpsertDurable(evicted, 1);
        for (var index = 0; index < 20_000; index++)
            svc.UpsertDurable(Segment(), 1);

        source.Drain();

        Assert.Equal(new DeliveryRemainder(1, 0), source.Remainder);
        svc.UpsertDurable(evicted, 2);
        source.Drain();
        Assert.True(source.Remainder.IsEmpty);
    }

    [Fact]
    public void Reinject_Absent_Inserts()
    {
        var svc = new SegmentIngestService(new FakeClock());
        IUploadSource<ActivitySegmentItem> source = svc;
        var seg = Segment();

        source.Reinject([seg]);

        Assert.Equal(new DeliveryRemainder(0, 1), source.Remainder);
        Assert.Same(seg, Assert.Single(svc.GetAndClearSegments()));
    }

    [Fact]
    public void Reinject_DoesNotRollBackNewerSnapshot()
    {
        // 不回滚（ADR-022）：批次在外期间 hub 已收到同 Id 更新快照，
        // 退回的旧快照不得覆盖——与服务端单调生长门同一条规则（ADR-018）。
        var svc = new SegmentIngestService(new FakeClock());
        IUploadSource<ActivitySegmentItem> source = svc;
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-5);
        var stale = Segment(start: t0, end: t0.AddMinutes(1));
        var newer = Segment(start: t0, end: t0.AddMinutes(3));
        newer.Id = stale.Id;

        svc.Accept([newer]);
        source.Reinject([stale]);

        var single = Assert.Single(svc.GetAndClearSegments());
        Assert.Equal(t0.AddMinutes(3), single.EndTime);
    }

    [Fact]
    public void Reinject_RetractedSnapshotDoesNotResurrectAfterDrainFailure()
    {
        var svc = new SegmentIngestService(new FakeClock());
        IUploadSource<ActivitySegmentItem> source = svc;
        ISegmentRetractionSink retractionSink = svc;
        var segment = Segment();
        svc.Accept([segment]);
        var drained = svc.GetAndClearSegments();

        retractionSink.Retract(segment.Id);
        source.Reinject(drained);

        Assert.Empty(svc.GetAndClearSegments());
    }

    // ---- 集面读模型（ADR-021） ----

    [Fact]
    public void Report_UpdatesCurrentActivity_AndRaisesEvent()
    {
        var svc = new SegmentIngestService(new FakeClock());
        var events = new List<CurrentActivity?>();
        svc.CurrentActivityChanged += events.Add;

        svc.Report(new CurrentActivity("win:code", "Code"));
        svc.Report(new CurrentActivity("sys:away", "离开"));

        Assert.Equal("sys:away", svc.CurrentActivity!.AppIdentityKey);
        Assert.Equal(["win:code", "sys:away"], events.Select(x => x?.AppIdentityKey));
    }

    [Fact]
    public void Report_SameValue_Silent()
    {
        // 去重：下游（心跳的变了就推）依赖"值实际变化才广播"
        var svc = new SegmentIngestService(new FakeClock());
        var events = new List<CurrentActivity?>();
        svc.CurrentActivityChanged += events.Add;

        svc.Report(new CurrentActivity("win:code", "Code"));
        svc.Report(new CurrentActivity("win:code", "Code"));

        Assert.Single(events);
    }

    [Fact]
    public void Accept_StampsSourceLastSeen_PerSource()
    {
        // Active 的机制（ADR-021）：last-seen 从流量派生，Accept 时刻戳
        var baseTime = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock { UtcNow = baseTime };
        var svc = new SegmentIngestService(clock);
        var t0 = baseTime.AddMinutes(-5);

        svc.Accept([
            Segment(source: "browser", start: t0, end: t0.AddMinutes(1)),
            Segment(source: "system", identityKey: "code|t", start: t0, end: t0.AddMinutes(1))]);
        clock.UtcNow = baseTime.AddMinutes(10);
        svc.Accept([Segment(source: "browser", start: t0, end: t0.AddMinutes(2))]);

        Assert.Equal(baseTime, svc.SourceLastSeen["system"]);
        Assert.Equal(baseTime.AddMinutes(10), svc.SourceLastSeen["browser"]);
    }

    [Fact]
    public void ReadModel_SurvivesDrain()
    {
        // 读模型与出网 buffer 分离（ADR-021）：drain 不清空集面状态
        var svc = new SegmentIngestService(new FakeClock());
        svc.Report(new CurrentActivity("win:code", "Code"));
        svc.Accept([Segment()]);

        svc.GetAndClearSegments();

        Assert.Equal("win:code", svc.CurrentActivity!.AppIdentityKey);
        Assert.True(svc.SourceLastSeen.ContainsKey("browser"));
    }
}
