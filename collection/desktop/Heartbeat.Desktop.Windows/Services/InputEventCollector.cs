using Heartbeat.Desktop.Windows.Utils;
using Heartbeat.Collector.System.Input;
using Heartbeat.Collector.System.Observations;
using Heartbeat.Collection.Hub.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Heartbeat.Desktop.Windows.Services
{
    /// <summary>
    /// 输入事件采集服务：钩子生命周期 + 将原始钩子事件翻译为 InputEventBuffer 的语义调用。
    /// 钩子线程由 WindowsLowLevelInputHook 自持（消息泵统一形态），详见 ADR-012。
    /// buffer 为共享单例：本服务只写入；buffer 经 system Collector Protocol 发布 Event Fact，
    /// Hub 投影回同一实例后再由 IUploadSource drain。
    /// </summary>
    public sealed class InputEventCollector : IHostedService, IDisposable, IAsyncDisposable
    {
        private readonly ILowLevelInputHook _hook;
        private readonly IInputActivitySignal _inputActivity;
        private readonly IInteractionSignalPolicy _interactionSettings;
        private readonly IInputEventRecordingPolicy _recordingSettings;
        private readonly InputEventBuffer _buffer;
        private readonly ObservationReconciler _transitions;
        private readonly object _gate = new();
        private readonly InputObservationStatus _status;
        private bool _started;
        private bool _hookRunning;
        private Task? _stopTask;

        public InputEventCollector(ILowLevelInputHook hook, IInputActivitySignal inputActivity,
            IInteractionSignalPolicy interactionSettings, IInputEventRecordingPolicy recordingSettings,
            InputEventBuffer buffer, InputObservationStatus? status = null)
        {
            _status = status ?? new InputObservationStatus();
            _hook = hook;
            _inputActivity = inputActivity;
            _interactionSettings = interactionSettings;
            _recordingSettings = recordingSettings;
            _buffer = buffer;
            _transitions = new ObservationReconciler(ReconcileHook);
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_stopTask is not null) throw new InvalidOperationException("Input collection is stopping.");
                if (_started) return Task.CompletedTask;
                _hook.Failed += OnNativeFailure;
                _hook.KeyDown += OnKeyDown;
                _hook.KeyUp += OnKeyUp;
                _hook.MouseButton += OnMouseButton;
                _hook.Scroll += OnScroll;
                _interactionSettings.Changed += OnInteractionChanged;
                _recordingSettings.Changed += OnRecordingChanged;
                _started = true;
            }
            _ = ObserveTransitionAsync(_transitions.Reconcile());
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            TaskCompletionSource completion;
            lock (_gate)
            {
                if (_stopTask is not null) return _stopTask.WaitAsync(cancellationToken);
                _started = false;
                Unsubscribe();
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _stopTask = completion.Task;
            }
            _ = CompleteStopAsync(completion);
            return completion.Task.WaitAsync(cancellationToken);
        }

        private async Task CompleteStopAsync(TaskCompletionSource completion)
        {
            try { await _transitions.Reconcile().ConfigureAwait(false); completion.SetResult(); }
            catch (Exception exception) { completion.SetException(exception); }
        }

        private async Task ObserveTransitionAsync(Task transition)
        {
            try { await transition; }
            catch (Exception exception) { Log.Warning(exception, "Windows input capability transition failed"); }
        }

        private void OnNativeFailure(Exception error)
        {
            _status.SetAvailable(false);
            Log.Warning(error, "Windows input observation failed");
            _ = ObserveTransitionAsync(_transitions.Reconcile());
        }

        // 回调保持最小工作：仅转发给 buffer（buffer 内部为并发安全的轻量操作）
        private void OnKeyDown(WindowsNativeKeyObservation observation)
        {
            lock (_gate) { if (!_started || !_status.IsAvailable) return; }
            if (!_recordingSettings.Enabled ||
                !WindowsKeyPositionMapper.TryMap(observation, out var position))
                return;

            _buffer.OnKeyDown(position);
        }

        private void OnKeyUp(WindowsNativeKeyObservation observation)
        {
            // 即使录制刚被关闭也要清 held state，避免再次启用后把下一次真实按下误判为 repeat。
            if (WindowsKeyPositionMapper.TryMap(observation, out var position))
                _buffer.OnKeyUp(position);
        }
        private void OnMouseButton(short code)
        {
            lock (_gate) { if (!_started || !_status.IsAvailable) return; }
            // 点击（含触摸板点击）标记输入活动，供标题变化门控使用（ADR-016）。
            if (_interactionSettings.Enabled)
                _inputActivity.MarkClick();
            if (_recordingSettings.Enabled)
                _buffer.OnMouseButton(code);
        }
        private void OnScroll(int delta)
        {
            lock (_gate) { if (!_started || !_status.IsAvailable) return; }
            if (_recordingSettings.Enabled)
                _buffer.OnScroll(delta);
        }

        private void OnRecordingChanged(bool enabled)
        {
            _status.SetAvailable(true);
            if (!enabled)
                _buffer.ResetTransientState();
            _ = ObserveTransitionAsync(_transitions.Reconcile());
        }

        private void OnInteractionChanged(bool enabled)
        {
            _status.SetAvailable(true);
            _ = ObserveTransitionAsync(_transitions.Reconcile());
        }

        private void ReconcileHook()
        {
            try { ApplyHookState(); }
            catch
            {
                _status.SetAvailable(false);
                throw;
            }
        }

        private void ApplyHookState()
        {
            bool started;
            lock (_gate) started = _started;
            var shouldRun = started && _status.IsAvailable && (_interactionSettings.Enabled || _recordingSettings.Enabled);
            if (shouldRun && !_hookRunning)
            {
                _hook.StartHook();
                _hookRunning = true;
            }
            else if (!shouldRun)
            {
                StopHook();
            }
        }

        private void StopHook()
        {
            if (!_hookRunning) return;
            _hook.StopHook();
            _hookRunning = false;
        }

        private void Unsubscribe()
        {
            _hook.Failed -= OnNativeFailure;
            _hook.KeyDown -= OnKeyDown;
            _hook.KeyUp -= OnKeyUp;
            _hook.MouseButton -= OnMouseButton;
            _hook.Scroll -= OnScroll;
            _interactionSettings.Changed -= OnInteractionChanged;
            _recordingSettings.Changed -= OnRecordingChanged;
        }

        public ValueTask DisposeAsync() => new(StopAsync(CancellationToken.None));

        public void Dispose()
        {
            StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            GC.SuppressFinalize(this);
        }
    }
}
