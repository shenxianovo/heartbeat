using System.Diagnostics;
using System.Runtime.InteropServices;

namespace client.Utils
{
    public static class ActiveWindowHelper
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        // WinEventHook 相关
        private delegate void WinEventDelegate(
            IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(
            uint eventMin, uint eventMax,
            IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc,
            uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

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
            public POINT pt;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
        private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
        private const uint WM_QUIT = 0x0012;

        private static WinEventDelegate? _winEventDelegate;
        private static IntPtr _foregroundHook;
        private static IntPtr _minimizeStartHook;
        private static IntPtr _minimizeEndHook;
        private static uint _messageLoopThreadId;

        /// <summary>
        /// 前台窗口切换时触发，参数为新的前台进程名（可能为 null）
        /// </summary>
        public static event Action<string?>? ForegroundWindowChanged;

        /// <summary>
        /// 获取当前前台窗口的进程名
        /// </summary>
        public static string? GetForegroundProcessName()
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;
            return GetProcessNameFromHwnd(hwnd);
        }

        /// <summary>
        /// 启动事件钩子，监听前台窗口切换。必须在专用线程上调用（内部运行消息循环）。
        /// </summary>
        public static void StartHook()
        {
            _messageLoopThreadId = GetCurrentThreadId();

            // 必须保持委托引用，防止 GC 回收
            _winEventDelegate = new WinEventDelegate(OnWinEvent);

            _foregroundHook = SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _winEventDelegate,
                0, 0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

            _minimizeStartHook = SetWinEventHook(
                EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZESTART,
                IntPtr.Zero, _winEventDelegate,
                0, 0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

            _minimizeEndHook = SetWinEventHook(
                EVENT_SYSTEM_MINIMIZEEND, EVENT_SYSTEM_MINIMIZEEND,
                IntPtr.Zero, _winEventDelegate,
                0, 0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

            // 运行消息循环（阻塞当前线程）
            while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            // 清理
            if (_foregroundHook != IntPtr.Zero) UnhookWinEvent(_foregroundHook);
            if (_minimizeStartHook != IntPtr.Zero) UnhookWinEvent(_minimizeStartHook);
            if (_minimizeEndHook != IntPtr.Zero) UnhookWinEvent(_minimizeEndHook);
        }

        /// <summary>
        /// 停止事件钩子，退出消息循环
        /// </summary>
        public static void StopHook()
        {
            if (_messageLoopThreadId != 0)
            {
                PostThreadMessage(_messageLoopThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }
        }

        private static void OnWinEvent(
            IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            // 对于所有事件，都重新获取当前前台窗口，确保准确
            var processName = GetForegroundProcessName();
            ForegroundWindowChanged?.Invoke(processName);
        }

        private static string? GetProcessNameFromHwnd(IntPtr hWnd)
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            try
            {
                var process = Process.GetProcessById((int)pid);
                return process.ProcessName;
            }
            catch
            {
                return null;
            }
        }
    }
}
