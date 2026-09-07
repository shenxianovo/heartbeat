using Heartbeat.Desktop.Windows.Services;
using Heartbeat.Desktop.Windows.Utils;
using Heartbeat.Core.DTOs.Input;
using Heartbeat.Collector.System.Collection;
using Heartbeat.Collector.System.Input;
using Heartbeat.Collection.Hub.Configuration;
using Heartbeat.Collection.Hub.Time;

namespace Heartbeat.Desktop.Windows.Tests.Services;

public sealed class InputEventCollectorTests
{
    private sealed class FakeHook : ILowLevelInputHook
    {
        public event Action<WindowsNativeKeyObservation>? KeyDown;
        public event Action<Exception>? Failed;
        public void RaiseFailure(Exception error) => Failed?.Invoke(error);
        public event Action<WindowsNativeKeyObservation>? KeyUp;
        public event Action<short>? MouseButton;
        public event Action<int>? Scroll;
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public Action? BeforeStart { get; init; }
        public bool Running { get; private set; }
        public void StartHook() { BeforeStart?.Invoke(); StartCount++; Running = true; }
        public void StopHook() { StopCount++; Running = false; }
        public void RaiseKeyDown(WindowsNativeKeyObservation value) => KeyDown?.Invoke(value);
        public void RaiseKeyUp(WindowsNativeKeyObservation value) => KeyUp?.Invoke(value);
        public void RaiseMouse(short value) => MouseButton?.Invoke(value);
        public void RaiseScroll(int value) => Scroll?.Invoke(value);
    }

    private sealed class MutableInteractionPolicy(bool enabled) : IInteractionSignalPolicy
    {
        public bool Enabled { get; private set; } = enabled;
        public event Action<bool>? Changed;
        public void Set(bool value)
        {
            Enabled = value;
            Changed?.Invoke(value);
        }
    }

    private sealed class FakeSignal : IInputActivitySignal
    {
        public int Clicks { get; private set; }
        public void MarkClick() => Clicks++;
        public bool ClickedWithin(TimeSpan window) => Clicks > 0;
    }

    private sealed class MutablePolicy(bool enabled) : IInputEventRecordingPolicy
    {
        public bool Enabled { get; private set; } = enabled;
        public event Action<bool>? Changed;
        public void Set(bool value)
        {
            Enabled = value;
            Changed?.Invoke(value);
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-08-11T00:00:00Z");
    }

    [Fact]
    public async Task DelayedFailureObserver_DoesNotOverwriteSuccessfulRecovery()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var recover = false;
        var hook = new FakeHook
        {
            BeforeStart = () =>
        {
            if (recover) return;
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            throw new IOException("Native start failed");
        }
        };
        var recording = new MutablePolicy(true);
        var status = new InputObservationStatus();
        using var collector = new InputEventCollector(hook, new FakeSignal(), new MutableInteractionPolicy(false),
            recording, new InputEventBuffer(new FixedClock()), status);
        var starting = Task.Run(() => collector.StartAsync(CancellationToken.None));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var context = new QueuedContext();
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            recording.Set(true);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); release.Set(); }
        await starting;
        Assert.True(SpinWait.SpinUntil(() => context.HasPending, TimeSpan.FromSeconds(5)));
        Assert.False(status.IsAvailable);
        recover = true;
        recording.Set(false);
        recording.Set(true);
        Assert.True(status.IsAvailable);
        context.Drain();
        Assert.True(status.IsAvailable);
        Assert.True(hook.Running);
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _callbacks = new();
        public bool HasPending => !_callbacks.IsEmpty;
        public override void Post(SendOrPostCallback callback, object? state) => _callbacks.Enqueue(() => callback(state));
        public void Drain() { while (_callbacks.TryDequeue(out var callback)) callback(); }
    }

    [Fact]
    public async Task NativeFailure_PausesInputAndAllowsExplicitRecoveryWithoutChangingIntent()
    {
        var hook = new FakeHook();
        var recording = new MutablePolicy(true);
        var status = new InputObservationStatus();
        var buffer = new InputEventBuffer(new FixedClock());
        using var collector = new InputEventCollector(hook, new FakeSignal(), new MutableInteractionPolicy(false),
            recording, buffer, status);
        await collector.StartAsync(CancellationToken.None);
        hook.RaiseFailure(new IOException("hook failed"));
        hook.RaiseMouse(1);
        Assert.False(status.IsAvailable);
        Assert.True(recording.Enabled);
        Assert.Empty(buffer.DrainAll());
        Assert.False(hook.Running);
        recording.Set(false);
        recording.Set(true);
        Assert.True(status.IsAvailable);
        Assert.True(hook.Running);
    }

    [Fact]
    public async Task StopDuringStart_JoinsTheTransitionBeforeReturning()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var hook = new FakeHook
        {
            BeforeStart = () =>
        {
            entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        }
        };
        using var collector = new InputEventCollector(hook, new FakeSignal(), new MutableInteractionPolicy(true),
            new MutablePolicy(false), new InputEventBuffer(new FixedClock()));
        var starting = Task.Run(() => collector.StartAsync(CancellationToken.None));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var stopping = collector.StopAsync(CancellationToken.None);
        try { Assert.False(stopping.IsCompleted); }
        finally { release.Set(); await starting; await stopping; }
        Assert.False(hook.Running);
        Assert.Equal(1, hook.StopCount);
    }

    [Fact]
    public async Task RecordingDisabled_KeepsInteractionSignalButProducesNoDurableEvents()
    {
        var hook = new FakeHook();
        var signal = new FakeSignal();
        var policy = new MutablePolicy(false);
        var buffer = new InputEventBuffer(new FixedClock());
        using var collector = new InputEventCollector(
            hook,
            signal,
            new MutableInteractionPolicy(true),
            policy,
            buffer);
        await collector.StartAsync(CancellationToken.None);

        hook.RaiseMouse(1);
        hook.RaiseScroll(120);
        hook.RaiseKeyDown(new WindowsNativeKeyObservation(0x41, 0x1E, false));

        Assert.Equal(1, signal.Clicks);
        Assert.Empty(buffer.DrainAll());
    }

    [Fact]
    public async Task DisablingRecording_ResetsHeldState_WithoutStoppingHook()
    {
        var hook = new FakeHook();
        var policy = new MutablePolicy(true);
        var buffer = new InputEventBuffer(new FixedClock());
        using var collector = new InputEventCollector(
            hook,
            new FakeSignal(),
            new MutableInteractionPolicy(true),
            policy,
            buffer);
        await collector.StartAsync(CancellationToken.None);
        var keyA = new WindowsNativeKeyObservation(0x41, 0x1E, false);

        hook.RaiseKeyDown(keyA);
        policy.Set(false);
        hook.RaiseKeyUp(keyA); // recording=false 时仍允许清理 held state
        policy.Set(true);
        hook.RaiseKeyDown(keyA);

        var events = buffer.DrainAll();
        Assert.Equal(2, events.Count);
        Assert.All(events, item => Assert.Equal(InputCodeSets.HeartbeatKeyPositionV1, item.CodeSet));
        Assert.All(events, item => Assert.Equal((short)InputKeyPosition.KeyA, item.Code));
    }

    [Fact]
    public async Task HookRunsOnlyWhileAtLeastOneInputCapabilityNeedsIt()
    {
        var hook = new FakeHook();
        var interaction = new MutableInteractionPolicy(false);
        var recording = new MutablePolicy(false);
        using var collector = new InputEventCollector(
            hook,
            new FakeSignal(),
            interaction,
            recording,
            new InputEventBuffer(new FixedClock()));

        await collector.StartAsync(CancellationToken.None);
        Assert.Equal(0, hook.StartCount);

        interaction.Set(true);
        Assert.Equal(1, hook.StartCount);

        interaction.Set(false);
        Assert.Equal(1, hook.StopCount);
    }
}
