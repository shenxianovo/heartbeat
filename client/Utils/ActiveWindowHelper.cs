using client.Models;
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

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd); // 是否最小化

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        /// <summary>
        /// 获取最上层（最前台焦点）应用的进程名
        /// </summary>
        public static string? GetTopMostProcessName()
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;
            return GetProcessNameFromHwnd(hwnd);
        }

        /// <summary>
        /// 获取所有前台可见（未最小化且可见）应用的进程名
        /// </summary>
        public static HashSet<string> GetForegroundProcessNames()
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            EnumWindows((hWnd, _) =>
            {
                if (IsWindowVisible(hWnd) && !IsIconic(hWnd) && GetWindowTextLength(hWnd) > 0)
                {
                    var name = GetProcessNameFromHwnd(hWnd);
                    if (name != null)
                        result.Add(name);
                }
                return true;
            }, IntPtr.Zero);
            return result;
        }

        /// <summary>
        /// 获取所有拥有主窗口的进程名（包括最小化的）
        /// </summary>
        public static HashSet<string> GetAllWindowedProcessNames()
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            EnumWindows((hWnd, _) =>
            {
                if (IsWindowVisible(hWnd) && GetWindowTextLength(hWnd) > 0)
                {
                    var name = GetProcessNameFromHwnd(hWnd);
                    if (name != null)
                        result.Add(name);
                }
                return true;
            }, IntPtr.Zero);
            return result;
        }

        /// <summary>
        /// 根据监测模式获取当前活跃的应用进程名集合
        /// </summary>
        public static HashSet<string> GetActiveProcessNames(MonitorMode mode)
        {
            return mode switch
            {
                MonitorMode.TopMost => GetTopMostAsSet(),
                MonitorMode.Foreground => GetForegroundProcessNames(),
                MonitorMode.All => GetAllWindowedProcessNames(),
                _ => GetTopMostAsSet()
            };
        }

        private static HashSet<string> GetTopMostAsSet()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var name = GetTopMostProcessName();
            if (name != null)
                set.Add(name);
            return set;
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
