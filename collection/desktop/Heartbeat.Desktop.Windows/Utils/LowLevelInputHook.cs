using System.Runtime.InteropServices;
using Heartbeat.Collector.System.Observations;

namespace Heartbeat.Desktop.Windows.Utils
{
    /// <summary>Windows 低级键盘钩子的原生观察；仅 platform adapter 解释此形状。</summary>
    public readonly record struct WindowsNativeKeyObservation(
        uint VirtualKey,
        uint ScanCode,
        bool IsExtended);

    /// <summary>
    /// 低级键盘/鼠标钩子（WH_KEYBOARD_LL / WH_MOUSE_LL），详见 ADR-012。
    /// 生产实现自持专用钩子线程（内部消息泵）：StartHook 立即返回，StopHook 阻塞收尾
    /// （三个 Win32 消息泵组件的统一形态）。
    /// 回调保持最小工作（解析 + 转发），避免触发 LowLevelHooksTimeout 被系统摘钩。
    /// </summary>
    public interface ILowLevelInputHook
    {
        event Action<Exception>? Failed;
        event Action<WindowsNativeKeyObservation>? KeyDown;
        event Action<WindowsNativeKeyObservation>? KeyUp;
        event Action<short>? MouseButton;  // 1=左 2=右 3=中
        event Action<int>? Scroll;          // 原始 wheel delta
        void StartHook();
        void StopHook();
    }

    public sealed class WindowsLowLevelInputHook : ILowLevelInputHook, IDisposable
    {
        // ── 事件 ──
        public event Action<WindowsNativeKeyObservation>? KeyDown;
        public event Action<Exception>? Failed;
        public event Action<WindowsNativeKeyObservation>? KeyUp;
        public event Action<short>? MouseButton;
        public event Action<int>? Scroll;

        // ── Win32 常量 ──
        private const int WH_KEYBOARD_LL = 13;
        private const int WH_MOUSE_LL = 14;

        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const uint LLKHF_EXTENDED = 0x01;

        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_MBUTTONDOWN = 0x0207;
        private const int WM_MOUSEWHEEL = 0x020A;

        private const uint WM_QUIT = 0x0012;

        // ── P/Invoke ──
        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll")]
        private static extern bool PeekMessage(out MSG message, IntPtr window, uint min, uint max, uint remove);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int pt_x;
            public int pt_y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public int pt_x;
            public int pt_y;
            public uint mouseData;   // 高位字为滚轮 delta
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private readonly object _gate = new();
        private Session? _session;
        private bool _disposed;

        public void StartHook()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_session is { } current && !current.Thread.Completion.IsCompleted)
                {
                    if (current.Thread.IsStopping)
                        throw new InvalidOperationException("The previous input session is still stopping.");
                    return;
                }
                _session = new Session(this);
                _session.Thread.Start();
            }
        }

        public void StopHook()
        {
            Session? session;
            lock (_gate) session = _session;
            session?.Thread.RequestStop();
            session?.Thread.Completion.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            lock (_gate)
                if (ReferenceEquals(_session, session) && session?.Thread.Completion.IsCompleted == true)
                    _session = null;
        }

        public void Dispose()
        {
            lock (_gate) _disposed = true;
            StopHook();
        }

        private void PublishFailure(Session session, Exception error)
        { if (Accepts(session)) Failed?.Invoke(error); }

        private bool Accepts(Session session)
        {
            lock (_gate) return ReferenceEquals(_session, session) && !session.Thread.IsStopping;
        }
        private void PublishKeyDown(Session session, WindowsNativeKeyObservation value)
        { if (Accepts(session)) KeyDown?.Invoke(value); }
        private void PublishKeyUp(Session session, WindowsNativeKeyObservation value)
        { if (Accepts(session)) KeyUp?.Invoke(value); }
        private void PublishMouse(Session session, short value)
        { if (Accepts(session)) MouseButton?.Invoke(value); }
        private void PublishScroll(Session session, int value)
        { if (Accepts(session)) Scroll?.Invoke(value); }

        private sealed class Session
        {
            private readonly WindowsLowLevelInputHook _owner;
            // Delegates and handles belong to this exact session until its thread finishes.
            private HookProc? _keyboardProc;
            private HookProc? _mouseProc;
            private IntPtr _keyboardHook;
            private IntPtr _mouseHook;
            public ObservationThread Thread { get; }
            public Session(WindowsLowLevelInputHook owner)
            {
                _owner = owner;
                Thread = new ObservationThread("InputHookThread", RunMessageLoop, error =>
                    owner.PublishFailure(this, error));
            }

            /// <summary>安装钩子并阻塞运行消息循环（在自持线程上执行，低级钩子要求线程有消息泵）。</summary>
            private void RunMessageLoop(CancellationToken stop)
            {
                // Force creation of the queue before registering the stop signal. Register also
                // observes cancellation that arrived before this thread was ready.
                _ = PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
                var threadId = GetCurrentThreadId();
                using var registration = stop.Register(() =>
                    PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero));
                try
                {
                    stop.ThrowIfCancellationRequested();
                    _keyboardProc = KeyboardCallback;
                    _mouseProc = MouseCallback;
                    var module = GetModuleHandle(null);
                    _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);
                    _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
                    if (_keyboardHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    while (!stop.IsCancellationRequested)
                    {
                        var result = GetMessage(out var message, IntPtr.Zero, 0, 0);
                        if (result == 0) break;
                        if (result < 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                        TranslateMessage(ref message);
                        DispatchMessage(ref message);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Serilog.Log.Error(exception, "低级输入钩子线程异常");
                    throw;
                }
                finally
                {
                    if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
                    if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
                    _keyboardHook = IntPtr.Zero;
                    _mouseHook = IntPtr.Zero;
                }
            }

            private IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
            {
                // 回调内吞异常：异常若穿过 P/Invoke 边界，行为未定义且可能导致系统摘钩。
                // 无论如何都要走到 CallNextHookEx，不破坏钩子链。
                if (nCode >= 0)
                {
                    try
                    {
                        int msg = (int)wParam;
                        var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                        var observation = new WindowsNativeKeyObservation(
                            data.vkCode,
                            data.scanCode,
                            (data.flags & LLKHF_EXTENDED) != 0);

                        if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                            _owner.PublishKeyDown(this, observation);
                        else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                            _owner.PublishKeyUp(this, observation);
                    }
                    catch (Exception ex)
                    {
                        Serilog.Log.Error(ex, "键盘钩子回调异常");
                    }
                }
                return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
            }

            private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
            {
                if (nCode >= 0)
                {
                    try
                    {
                        int msg = (int)wParam;
                        switch (msg)
                        {
                            case WM_LBUTTONDOWN:
                                _owner.PublishMouse(this, 1);
                                break;
                            case WM_RBUTTONDOWN:
                                _owner.PublishMouse(this, 2);
                                break;
                            case WM_MBUTTONDOWN:
                                _owner.PublishMouse(this, 3);
                                break;
                            case WM_MOUSEWHEEL:
                                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                                // 高 16 位为有符号 delta
                                short delta = (short)((data.mouseData >> 16) & 0xFFFF);
                                _owner.PublishScroll(this, delta);
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Serilog.Log.Error(ex, "鼠标钩子回调异常");
                    }
                }
                return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            }
        }
    }
}
