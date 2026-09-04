using System.Diagnostics;
using System.Text.Json;
using Heartbeat.Collector.System.Input;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Time;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Core.DTOs.Input;

namespace Heartbeat.Collector.System.Tests.Input;

public class InputEventBufferTests
{
    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UnixEpoch;
    }

    private sealed class RejectingCommitFence : ICollectorProjectionCommitFence
    {
        public bool IsFenced => true;

        public bool TryPublishFile(string preparedPath, string authoritativePath) => false;
    }

    private static InputEventBuffer NewBuffer(int capacity = 100_000)
        => new(new FakeClock(), capacity);

    [Fact]
    public void OnKeyDown_RecordsEvent()
    {
        var buf = NewBuffer();

        Assert.True(buf.OnKeyDown(InputKeyPosition.KeyA));

        var items = buf.DrainAll();
        Assert.Single(items);
        Assert.Equal(InputEventType.KeyDown, items[0].EventType);
        Assert.Equal((short)InputKeyPosition.KeyA, items[0].Code);
        Assert.Equal(InputCodeSets.HeartbeatKeyPositionV1, items[0].CodeSet);
    }

    [Fact]
    public void Requeue_PreservesIds_ForIdempotentReupload()
    {
        // 上传通道退回契约（ADR-020）：保 Id 回队，服务端按 Id 幂等去重
        var buf = NewBuffer();
        buf.OnKeyDown(InputKeyPosition.KeyA);
        buf.OnMouseButton(1);
        var drained = buf.DrainAll();

        buf.Requeue(drained);

        var requeued = buf.DrainAll();
        Assert.Equal(drained.Select(i => i.Id), requeued.Select(i => i.Id));
    }

    [Fact]
    public void OnKeyDown_FiltersAutoRepeat_UntilKeyUp()
    {
        var buf = NewBuffer();

        Assert.True(buf.OnKeyDown(InputKeyPosition.KeyA));   // 首次记录
        Assert.False(buf.OnKeyDown(InputKeyPosition.KeyA));  // 自动重复，丢弃
        Assert.False(buf.OnKeyDown(InputKeyPosition.KeyA));  // 仍丢弃

        buf.OnKeyUp(InputKeyPosition.KeyA);
        Assert.True(buf.OnKeyDown(InputKeyPosition.KeyA));   // 抬起后再按，重新记录

        Assert.Equal(2, buf.DrainAll().Count);
    }

    [Fact]
    public void OnKeyDown_DifferentKeys_NotFiltered()
    {
        var buf = NewBuffer();

        Assert.True(buf.OnKeyDown(InputKeyPosition.KeyA));
        Assert.True(buf.OnKeyDown(InputKeyPosition.KeyB));
        Assert.True(buf.OnKeyDown(InputKeyPosition.KeyC));

        Assert.Equal(3, buf.DrainAll().Count);
    }

    [Fact]
    public void OnMouseButton_RecordsEvent()
    {
        var buf = NewBuffer();

        buf.OnMouseButton(1);
        buf.OnMouseButton(2);
        buf.OnMouseButton(3);

        var items = buf.DrainAll();
        Assert.Equal(3, items.Count);
        Assert.All(items, i => Assert.Equal(InputEventType.MouseButton, i.EventType));
    }

    [Fact]
    public void OnScroll_OneNotch_RecordsOneEvent()
    {
        var buf = NewBuffer();

        buf.OnScroll(InputEventBuffer.WheelDelta);  // 上滚一档

        var items = buf.DrainAll();
        Assert.Single(items);
        Assert.Equal(InputEventType.MouseScroll, items[0].EventType);
        Assert.Equal((short)1, items[0].Code);  // 上
    }

    [Fact]
    public void OnScroll_NegativeDelta_RecordsScrollDown()
    {
        var buf = NewBuffer();

        buf.OnScroll(-InputEventBuffer.WheelDelta);

        var items = buf.DrainAll();
        Assert.Single(items);
        Assert.Equal((short)2, items[0].Code);  // 下
    }

    [Fact]
    public void OnScroll_FractionalDeltas_AccumulateToWholeNotch()
    {
        var buf = NewBuffer();

        // 三次 40 凑成一档（120），第三次才记录
        buf.OnScroll(40);
        Assert.Empty(buf.DrainAll());
        buf.OnScroll(40);
        Assert.Empty(buf.DrainAll());
        buf.OnScroll(40);

        var items = buf.DrainAll();
        Assert.Single(items);
        Assert.Equal((short)1, items[0].Code);
    }

    [Fact]
    public void OnScroll_MultipleNotchesAtOnce_RecordsMultipleEvents()
    {
        var buf = NewBuffer();

        buf.OnScroll(InputEventBuffer.WheelDelta * 3);  // 一次滚三档

        var items = buf.DrainAll();
        Assert.Equal(3, items.Count);
        Assert.All(items, i => Assert.Equal((short)1, i.Code));
    }

    [Fact]
    public void OnScroll_RemainderCarriesOver()
    {
        var buf = NewBuffer();

        buf.OnScroll(200);  // 一档(120) + 余 80
        Assert.Single(buf.DrainAll());

        buf.OnScroll(40);   // 80 + 40 = 120 → 再一档
        Assert.Single(buf.DrainAll());
    }

    [Fact]
    public void Enqueue_AtCapacity_ReturnsBackpressureAndPreservesExistingEvents()
    {
        var buf = NewBuffer(capacity: 3);

        buf.OnMouseButton(1);
        buf.OnMouseButton(2);
        buf.OnMouseButton(3);
        var error = Assert.Throws<InputEventCapacityExceededException>(() => buf.OnMouseButton(1));

        Assert.Equal(3, error.Capacity);
        Assert.Equal(3, buf.Count);
        Assert.Equal([1, 2, 3], buf.DrainAll().Select(item => item.Code));
    }

    [Fact]
    public void DrainAll_EmptiesBuffer()
    {
        var buf = NewBuffer();

        buf.OnKeyDown(InputKeyPosition.KeyA);
        Assert.Single(buf.DrainAll());
        Assert.Empty(buf.DrainAll());
        Assert.Equal(0, buf.Count);
    }

    [Fact]
    public void ResetTransientState_AllowsHeldKeyAgain_AndDropsScrollRemainder()
    {
        var buf = NewBuffer();
        Assert.True(buf.OnKeyDown(InputKeyPosition.KeyA));
        buf.OnScroll(80);

        buf.ResetTransientState();

        Assert.True(buf.OnKeyDown(InputKeyPosition.KeyA));
        buf.OnScroll(40);
        var items = buf.DrainAll();
        Assert.Equal(2, items.Count(i => i.EventType == InputEventType.KeyDown));
        Assert.DoesNotContain(items, i => i.EventType == InputEventType.MouseScroll);
    }

    [Fact]
    public void Enqueue_GeneratesUniqueIds()
    {
        var buf = NewBuffer();

        buf.OnMouseButton(1);
        buf.OnMouseButton(1);

        var items = buf.DrainAll();
        Assert.NotEqual(items[0].Id, items[1].Id);
        Assert.NotEqual(Guid.Empty, items[0].Id);
    }

    [Fact]
    public void DurableProjection_DeduplicatesFactId_SurvivesRestart_AndCommitsDrain()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-input-buffer-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "input-event-facts-buffer.json");
        var item = new InputEventItem
        {
            Id = Guid.CreateVersion7(),
            EventType = InputEventType.KeyDown,
            CodeSet = InputCodeSets.HeartbeatKeyPositionV1,
            Code = (short)InputKeyPosition.KeyA,
            Timestamp = DateTimeOffset.UnixEpoch
        };
        try
        {
            var first = new InputEventBuffer(new FakeClock(), durableProjectionPath: path);
            ((IInputEventFactSink)first).Accept(item, isReplay: false);
            ((IInputEventFactSink)first).Accept(item, isReplay: true);

            var restarted = new InputEventBuffer(new FakeClock(), durableProjectionPath: path);
            var drained = ((IUploadSource<InputEventItem>)restarted).Drain();

            Assert.Equal(item.Id, Assert.Single(drained).Id);
            ((IDurableUploadSource<InputEventItem>)restarted).CompleteDrain(drained, []);
            var completed = new InputEventBuffer(new FakeClock(), durableProjectionPath: path);
            Assert.Empty(((IUploadSource<InputEventItem>)completed).Drain());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DurableProjection_ConfirmedUploadIsNotRequeuedByLaterRuntimeReplay()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-input-buffer-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "input-event-facts-buffer.json");
        var item = new InputEventItem
        {
            Id = Guid.CreateVersion7(),
            EventType = InputEventType.KeyDown,
            CodeSet = InputCodeSets.HeartbeatKeyPositionV1,
            Code = (short)InputKeyPosition.KeyA,
            Timestamp = DateTimeOffset.UnixEpoch
        };
        try
        {
            var first = new InputEventBuffer(new FakeClock(), durableProjectionPath: path);
            ((IInputEventFactSink)first).Accept(item, isReplay: false);
            var source = (IDurableUploadSource<InputEventItem>)first;
            var delivered = source.Drain();
            source.CompleteDrain(delivered, []);

            var restarted = new InputEventBuffer(new FakeClock(), durableProjectionPath: path);
            ((IInputEventFactReplaySink)restarted).Replay([item]);

            Assert.Empty(((IUploadSource<InputEventItem>)restarted).Drain());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DurableProjection_ReplaysLargeFactBatchWithinStartupBudget()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-input-buffer-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "input-event-facts-buffer.json");
        var items = Enumerable.Range(0, 20_000).Select(index => new InputEventItem
        {
            Id = Guid.CreateVersion7(),
            EventType = InputEventType.KeyDown,
            CodeSet = InputCodeSets.HeartbeatKeyPositionV1,
            Code = (short)InputKeyPosition.KeyA,
            Timestamp = DateTimeOffset.UnixEpoch.AddTicks(index)
        }).ToArray();
        try
        {
            var buffer = new InputEventBuffer(new FakeClock(), durableProjectionPath: path);

            var stopwatch = Stopwatch.StartNew();
            ((IInputEventFactReplaySink)buffer).Replay(items);
            stopwatch.Stop();

            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                $"20,000 durable InputEvent Facts took {stopwatch.Elapsed} to replay.");
            var restarted = new InputEventBuffer(new FakeClock(), durableProjectionPath: path);
            var retained = restarted.DrainAll();
            Assert.Equal(items.Select(item => item.Id), retained.Select(item => item.Id));
            Assert.Equal(items.Length, retained.Select(item => item.Id).Distinct().Count());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DurableUploadSource_DrainsAtMostFiveThousandItemsAndPreservesRemainder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-input-buffer-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "input-event-facts-buffer.json");
        var items = Enumerable.Range(0, 5_003).Select(index => new InputEventItem
        {
            Id = Guid.CreateVersion7(),
            EventType = InputEventType.KeyDown,
            CodeSet = InputCodeSets.HeartbeatKeyPositionV1,
            Code = (short)InputKeyPosition.KeyA,
            Timestamp = DateTimeOffset.UnixEpoch.AddTicks(index)
        }).ToArray();
        try
        {
            var buffer = new InputEventBuffer(new FakeClock(), durableProjectionPath: path);
            ((IInputEventFactReplaySink)buffer).Replay(items);
            var source = (IDurableUploadSource<InputEventItem>)buffer;

            var first = source.Drain();

            Assert.Equal(5_000, first.Count);
            Assert.Equal(items.Take(5_000).Select(item => item.Id), first.Select(item => item.Id));
            Assert.True(
                JsonSerializer.SerializeToUtf8Bytes(
                    new InputEventUploadRequest { Events = first }).Length < 1_048_576,
                "The bounded InputEvent upload batch must stay below the default reverse-proxy body limit.");
            source.CompleteDrain(first, []);
            Assert.Equal(items.Skip(5_000).Select(item => item.Id), source.Drain().Select(item => item.Id));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DurableProjection_DeadlineFenceRejectsFinalCacheMutation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-input-buffer-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "input-event-facts-buffer.json");
        var item = new InputEventItem
        {
            Id = Guid.CreateVersion7(),
            EventType = InputEventType.MouseButton,
            CodeSet = InputCodeSets.HeartbeatKeyPositionV1,
            Code = 1,
            Timestamp = DateTimeOffset.UnixEpoch
        };
        try
        {
            var buffer = new InputEventBuffer(new FakeClock(), durableProjectionPath: path);

            var accepted = ((IInputEventFactSink)buffer).TryAccept(
                item,
                isReplay: false,
                new RejectingCommitFence());

            Assert.False(accepted);
            var restarted = new InputEventBuffer(new FakeClock(), durableProjectionPath: path);
            Assert.Empty(restarted.DrainAll());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DurableProjection_AtCapacityBackpressuresWithoutTrimmingAcrossRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-input-buffer-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "input-event-facts-buffer.json");
        var items = Enumerable.Range(1, 3).Select(index => new InputEventItem
        {
            Id = Guid.CreateVersion7(),
            EventType = InputEventType.MouseButton,
            CodeSet = InputCodeSets.HeartbeatKeyPositionV1,
            Code = (short)index,
            Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(index)
        }).ToArray();
        try
        {
            var first = new InputEventBuffer(new FakeClock(), capacity: 2, durableProjectionPath: path);
            ((IInputEventFactSink)first).Accept(items[0], isReplay: false);
            ((IInputEventFactSink)first).Accept(items[1], isReplay: false);

            Assert.Throws<InputEventCapacityExceededException>(() =>
                ((IInputEventFactSink)first).Accept(items[2], isReplay: false));

            var restarted = new InputEventBuffer(new FakeClock(), capacity: 2, durableProjectionPath: path);
            var retained = ((IUploadSource<InputEventItem>)restarted).Drain();
            Assert.Equal(items.Take(2).Select(item => item.Id), retained.Select(item => item.Id));

            ((IDurableUploadSource<InputEventItem>)restarted).CompleteDrain(retained, [retained[1]]);
            ((IInputEventFactSink)restarted).Accept(items[2], isReplay: false);
            var completedRestart = new InputEventBuffer(
                new FakeClock(), capacity: 2, durableProjectionPath: path);
            Assert.Equal(
                [items[1].Id, items[2].Id],
                ((IUploadSource<InputEventItem>)completedRestart).Drain().Select(item => item.Id));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CurrentCacheVersion_OverNewCapacityIsPreservedAndBackpressuresUntilDrained()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-input-buffer-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "input-event-facts-buffer.json");
        try
        {
            var writer = new InputEventBuffer(new FakeClock(), capacity: 4, durableProjectionPath: path);
            for (short code = 1; code <= 4; code++)
                writer.OnMouseButton(code);

            var reopened = new InputEventBuffer(new FakeClock(), capacity: 3, durableProjectionPath: path);
            Assert.Equal(4, reopened.Count);
            Assert.Throws<InputEventCapacityExceededException>(() => reopened.OnMouseButton(5));
            Assert.Equal([1, 2, 3, 4], reopened.DrainAll().Select(item => item.Code));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DiagnosticsDistinguishIdleBacklogAndBackpressure()
    {
        var registry = new UploadStatusRegistry();
        var buffer = new InputEventBuffer(new FakeClock(), capacity: 2, statusRegistry: registry);
        Assert.Equal(
            UploadStreamState.Ready,
            registry.Snapshot[InputEventBuffer.StatusStreamName].State);

        buffer.OnMouseButton(1);
        Assert.Equal(
            UploadStreamState.Backlog,
            registry.Snapshot[InputEventBuffer.StatusStreamName].State);

        buffer.OnMouseButton(2);
        Assert.Equal(
            UploadStreamState.Backpressure,
            registry.Snapshot[InputEventBuffer.StatusStreamName].State);

        buffer.DrainAll();
        Assert.Equal(
            UploadStreamState.Ready,
            registry.Snapshot[InputEventBuffer.StatusStreamName].State);
    }

    [Fact]
    public async Task DurableProjection_ConcurrentConfirmedDrainAndEnqueuePreservesExactIds()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-input-buffer-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "input-event-facts-buffer.json");
        try
        {
            var buffer = new InputEventBuffer(new FakeClock(), capacity: 10, durableProjectionPath: path);
            var initial = Enumerable.Range(1, 10).Select(index => new InputEventItem
            {
                Id = Guid.CreateVersion7(),
                EventType = InputEventType.MouseButton,
                CodeSet = InputCodeSets.HeartbeatKeyPositionV1,
                Code = (short)index,
                Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(index)
            }).ToArray();
            var next = Enumerable.Range(11, 5).Select(index => new InputEventItem
            {
                Id = Guid.CreateVersion7(),
                EventType = InputEventType.MouseButton,
                CodeSet = InputCodeSets.HeartbeatKeyPositionV1,
                Code = (short)index,
                Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(index)
            }).ToArray();
            foreach (var item in initial)
                ((IInputEventFactSink)buffer).Accept(item, isReplay: false);
            var drained = ((IUploadSource<InputEventItem>)buffer).Drain();
            using var start = new ManualResetEventSlim();

            var complete = Task.Run(() =>
            {
                start.Wait();
                ((IDurableUploadSource<InputEventItem>)buffer).CompleteDrain(
                    drained,
                    drained.Skip(5).ToArray());
            });
            var enqueue = Task.Run(async () =>
            {
                start.Wait();
                foreach (var item in next)
                {
                    while (true)
                    {
                        try
                        {
                            ((IInputEventFactSink)buffer).Accept(item, isReplay: false);
                            break;
                        }
                        catch (InputEventCapacityExceededException)
                        {
                            await Task.Yield();
                        }
                    }
                }
            });
            start.Set();
            await Task.WhenAll(complete, enqueue);

            var restarted = new InputEventBuffer(new FakeClock(), capacity: 10, durableProjectionPath: path);
            var retained = restarted.DrainAll();
            Assert.Equal(10, retained.Count);
            Assert.Equal(10, retained.Select(item => item.Id).Distinct().Count());
            Assert.Equal(
                initial.Skip(5).Select(item => item.Id).Concat(next.Select(item => item.Id)),
                retained.Select(item => item.Id));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
