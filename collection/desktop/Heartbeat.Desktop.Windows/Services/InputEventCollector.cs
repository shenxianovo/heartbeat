using Heartbeat.Desktop.Windows.Utils;
using Heartbeat.Collector.System.Input;
using Heartbeat.Collector.System.Observations;
using Heartbeat.Collection.Hub.Configuration;
using Microsoft.Extensions.Hosting;

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
        private readonly ObservationCapability<bool, bool> _capability;
        private readonly object _gate = new();
        private readonly InputObservationStatus _status;
        private bool _started;

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
            _capability = new(true, false,
                () => new(true, _interactionSettings.Enabled || _recordingSettings.Enabled ? true : null),
                _ => _hook.StartHook(), _hook.StopHook);
            _capability.Changed += _status.SetAvailable;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_started) return _capability.Start().WaitAsync(cancellationToken);
                _hook.Failed += _capability.ReportFailure;
                _hook.KeyDown += OnKeyDown;
                _hook.KeyUp += OnKeyUp;
                _hook.MouseButton += OnMouseButton;
                _hook.Scroll += OnScroll;
                _interactionSettings.Changed += OnInteractionChanged;
                _recordingSettings.Changed += OnRecordingChanged;
                _started = true;
                return _capability.Start().WaitAsync(cancellationToken);
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _started = false;
                Unsubscribe();
                return _capability.Stop().WaitAsync(cancellationToken);
            }
        }

        public Task Refresh() => _capability.Refresh();

        // 回调保持最小工作：仅转发给 buffer（buffer 内部为并发安全的轻量操作）
        private void OnKeyDown(WindowsNativeKeyObservation observation)
        {
            if (!_capability.AcceptsObservations) return;
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
            if (!_capability.AcceptsObservations) return;
            // 点击（含触摸板点击）标记输入活动，供标题变化门控使用（ADR-016）。
            if (_interactionSettings.Enabled)
                _inputActivity.MarkClick();
            if (_recordingSettings.Enabled)
                _buffer.OnMouseButton(code);
        }
        private void OnScroll(int delta)
        {
            if (!_capability.AcceptsObservations) return;
            if (_recordingSettings.Enabled)
                _buffer.OnScroll(delta);
        }

        private void OnRecordingChanged(bool enabled)
        {
            if (!enabled)
                _buffer.ResetTransientState();
            _capability.Refresh(retry: true);
        }

        private void OnInteractionChanged(bool enabled)
        {
            _capability.Refresh(retry: true);
        }

        private void Unsubscribe()
        {
            _hook.Failed -= _capability.ReportFailure;
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
