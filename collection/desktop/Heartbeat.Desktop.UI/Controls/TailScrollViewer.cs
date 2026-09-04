using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Heartbeat.Desktop.UI.Controls;

/// <summary>
/// Keeps a bounded stream view at its tail until the user deliberately scrolls away.
/// </summary>
public sealed class TailScrollViewer : ScrollViewer
{
    private readonly TailFollowState _state = new();
    private bool _scrollScheduled;
    private bool _isApplyingTailScroll;

    protected override Type StyleKeyOverride => typeof(ScrollViewer);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        QueueScrollToEnd();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && change.GetNewValue<bool>())
        {
            _state.Resume();
            QueueScrollToEnd();
        }
    }

    protected override void OnScrollChanged(ScrollChangedEventArgs e)
    {
        base.OnScrollChanged(e);

        if (!_isApplyingTailScroll && Math.Abs(e.OffsetDelta.Y) > double.Epsilon)
            _state.ObserveOffset(Offset.Y, Extent.Height, Viewport.Height);

        if (_state.IsFollowingLatest &&
            (Math.Abs(e.ExtentDelta.Y) > double.Epsilon ||
             Math.Abs(e.ViewportDelta.Y) > double.Epsilon))
            QueueScrollToEnd();
    }

    private void QueueScrollToEnd()
    {
        if (_scrollScheduled || !_state.IsFollowingLatest) return;
        _scrollScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollScheduled = false;
            if (!_state.IsFollowingLatest || !IsEffectivelyVisible) return;

            _isApplyingTailScroll = true;
            try
            {
                ScrollToEnd();
                _state.ObserveOffset(Offset.Y, Extent.Height, Viewport.Height);
            }
            finally
            {
                _isApplyingTailScroll = false;
            }
        }, DispatcherPriority.Loaded);
    }
}
