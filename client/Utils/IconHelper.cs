using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Serilog;

namespace client.Utils
{
    [SupportedOSPlatform("windows")]
    public static class IconHelper
    {
        #region P/Invoke

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(
            string pszPath, uint dwFileAttributes,
            ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, System.Text.StringBuilder lpExeName, ref uint lpdwSize);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetClassLongPtrW")]
        private static extern IntPtr GetClassLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetClassLongW")]
        private static extern uint GetClassLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_LARGEICON = 0x000000000;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint WM_GETICON = 0x007F;
        private const IntPtr ICON_BIG = 1;
        private const IntPtr ICON_SMALL2 = 2;
        private const int GCL_HICON = -14;
        private const int GCL_HICONSM = -34;

        #endregion

        /// <summary>
        /// 根据进程名获取应用图标的 PNG 字节数组（多策略依次尝试）
        /// </summary>
        public static byte[]? GetIconPngByProcessName(string processName)
        {
            try
            {
                byte[]? data;

                // ── 策略 1: 通过进程获取 exe 路径 ──
                var exePath = GetExePathByProcessName(processName);
                if (exePath != null)
                {
                    Log.Debug("获取到进程路径: {ProcessName} -> {ExePath}", processName, exePath);

                    // 1a: SHGetFileInfo
                    data = ExtractIconBySHGetFileInfo(exePath);
                    if (data != null)
                    {
                        Log.Debug("图标提取成功 [SHGetFileInfo]: {ProcessName}", processName);
                        return data;
                    }

                    // 1b: Icon.ExtractAssociatedIcon（.NET 内置方法）
                    data = ExtractIconByAssociatedIcon(exePath);
                    if (data != null)
                    {
                        Log.Debug("图标提取成功 [ExtractAssociatedIcon]: {ProcessName}", processName);
                        return data;
                    }
                }
                else
                {
                    Log.Debug("无法获取进程路径: {ProcessName}", processName);
                }

                // ── 策略 2: 从窗口句柄直接获取图标 ──
                data = ExtractIconFromWindow(processName);
                if (data != null)
                {
                    Log.Debug("图标提取成功 [窗口句柄]: {ProcessName}", processName);
                    return data;
                }

                // ── 策略 3: 从注册表查找 exe 路径 ──
                var regPath = FindExePathFromRegistry(processName);
                if (regPath != null && regPath != exePath)
                {
                    Log.Debug("注册表查到路径: {ProcessName} -> {RegPath}", processName, regPath);

                    data = ExtractIconBySHGetFileInfo(regPath);
                    if (data != null)
                    {
                        Log.Debug("图标提取成功 [注册表+SHGetFileInfo]: {ProcessName}", processName);
                        return data;
                    }

                    data = ExtractIconByAssociatedIcon(regPath);
                    if (data != null)
                    {
                        Log.Debug("图标提取成功 [注册表+ExtractAssociatedIcon]: {ProcessName}", processName);
                        return data;
                    }
                }

                Log.Debug("所有策略均未能提取图标: {ProcessName}", processName);
                return null;
            }
            catch (Exception ex)
            {
                Log.Warning("获取图标异常 [{ProcessName}]: {Error}", processName, ex.Message);
                return null;
            }
        }

        #region 获取 exe 路径

        /// <summary>
        /// 通过进程名获取 exe 路径，依次尝试 QueryFullProcessImageName 和 MainModule
        /// </summary>
        private static string? GetExePathByProcessName(string processName)
        {
            try
            {
                var processes = Process.GetProcessesByName(processName);
                foreach (var proc in processes)
                {
                    try
                    {
                        // 优先使用 QueryFullProcessImageName（只需 PROCESS_QUERY_LIMITED_INFORMATION 权限）
                        var path = GetProcessImagePath(proc.Id);
                        if (!string.IsNullOrEmpty(path))
                            return path;

                        // 回退到 MainModule
                        path = proc.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(path))
                            return path;
                    }
                    catch (Exception ex)
                    {
                        Log.Verbose("无法获取进程 {Pid} 的路径: {Error}", proc.Id, ex.Message);
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                }
            }
            catch
            {
                // ignored
            }
            return null;
        }

        /// <summary>
        /// 使用 QueryFullProcessImageName 获取进程路径（比 MainModule 更可靠，对 UAC/跨架构进程也有效）
        /// </summary>
        private static string? GetProcessImagePath(int processId)
        {
            var hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (hProcess == IntPtr.Zero)
                return null;

            try
            {
                var sb = new System.Text.StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                return QueryFullProcessImageName(hProcess, 0, sb, ref size)
                    ? sb.ToString()
                    : null;
            }
            finally
            {
                CloseHandle(hProcess);
            }
        }

        #endregion

        #region 图标提取策略

        /// <summary>
        /// 策略 A: 使用 SHGetFileInfo 从 exe 提取图标
        /// </summary>
        private static byte[]? ExtractIconBySHGetFileInfo(string filePath)
        {
            try
            {
                var shInfo = new SHFILEINFO();
                var result = SHGetFileInfo(filePath, 0, ref shInfo,
                    (uint)Marshal.SizeOf(typeof(SHFILEINFO)),
                    SHGFI_ICON | SHGFI_LARGEICON);

                if (result == IntPtr.Zero || shInfo.hIcon == IntPtr.Zero)
                    return null;

                try
                {
                    return IconHandleToPng(shInfo.hIcon);
                }
                finally
                {
                    DestroyIcon(shInfo.hIcon);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("SHGetFileInfo 失败: {Error}", ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 策略 B: 使用 .NET Icon.ExtractAssociatedIcon 提取图标
        /// </summary>
        private static byte[]? ExtractIconByAssociatedIcon(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                    return null;

                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(filePath);
                if (icon == null)
                    return null;

                using var bitmap = icon.ToBitmap();
                using var ms = new MemoryStream();
                bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                return ms.ToArray();
            }
            catch (Exception ex)
            {
                Log.Debug("ExtractAssociatedIcon 失败: {Error}", ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 策略 C: 通过窗口句柄提取图标（WM_GETICON / GetClassLongPtr）
        /// </summary>
        private static byte[]? ExtractIconFromWindow(string processName)
        {
            try
            {
                var hwnd = FindMainWindowByProcessName(processName);
                if (hwnd == IntPtr.Zero)
                    return null;

                // 依次尝试: WM_GETICON(ICON_BIG) → WM_GETICON(ICON_SMALL2) → GetClassLongPtr(GCL_HICON)
                IntPtr hIcon = SendMessage(hwnd, WM_GETICON, ICON_BIG, IntPtr.Zero);

                if (hIcon == IntPtr.Zero)
                    hIcon = SendMessage(hwnd, WM_GETICON, ICON_SMALL2, IntPtr.Zero);

                if (hIcon == IntPtr.Zero)
                    hIcon = GetClassLongPtrCross(hwnd, GCL_HICON);

                if (hIcon == IntPtr.Zero)
                    hIcon = GetClassLongPtrCross(hwnd, GCL_HICONSM);

                if (hIcon == IntPtr.Zero)
                    return null;

                // 通过窗口获取的图标句柄由系统管理，不需要手动 DestroyIcon
                return IconHandleToPng(hIcon);
            }
            catch (Exception ex)
            {
                Log.Debug("窗口句柄图标提取失败: {Error}", ex.Message);
                return null;
            }
        }

        #endregion

        #region 注册表查找

        /// <summary>
        /// 从注册表搜索 exe 路径（App Paths + Uninstall 卸载信息）
        /// </summary>
        private static string? FindExePathFromRegistry(string processName)
        {
            var exeName = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? processName
                : processName + ".exe";

            // 1. App Paths
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}");
                var path = key?.GetValue(null)?.ToString();
                if (!string.IsNullOrEmpty(path) && File.Exists(path.Trim('"')))
                    return path.Trim('"');
            }
            catch (Exception ex)
            {
                Log.Debug("注册表 App Paths 查找失败: {Error}", ex.Message);
            }

            // 2. Uninstall 注册表项中搜索 DisplayIcon / InstallLocation
            string[] uninstallPaths =
            [
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            ];

            foreach (var regPath in uninstallPaths)
            {
                try
                {
                    using var baseKey = Registry.LocalMachine.OpenSubKey(regPath);
                    if (baseKey == null) continue;

                    foreach (var subKeyName in baseKey.GetSubKeyNames())
                    {
                        try
                        {
                            using var subKey = baseKey.OpenSubKey(subKeyName);
                            if (subKey == null) continue;

                            var displayName = subKey.GetValue("DisplayName")?.ToString() ?? "";
                            if (!displayName.Contains(processName, StringComparison.OrdinalIgnoreCase))
                                continue;

                            // 尝试 DisplayIcon
                            var iconPath = subKey.GetValue("DisplayIcon")?.ToString();
                            if (!string.IsNullOrEmpty(iconPath))
                            {
                                // DisplayIcon 可能带逗号(图标索引)，如 "C:\app.exe,0"
                                var cleanPath = iconPath.Split(',')[0].Trim('"').Trim();
                                if (File.Exists(cleanPath))
                                    return cleanPath;
                            }

                            // 尝试 InstallLocation + exe 名
                            var installDir = subKey.GetValue("InstallLocation")?.ToString();
                            if (!string.IsNullOrEmpty(installDir))
                            {
                                var candidate = Path.Combine(installDir, exeName);
                                if (File.Exists(candidate))
                                    return candidate;
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Verbose("读取注册表子项 {SubKey} 失败: {Error}", subKeyName, ex.Message);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug("扫描卸载列表 {RegPath} 失败: {Error}", regPath, ex.Message);
                }
            }

            return null;
        }

        #endregion

        #region 辅助方法

        /// <summary>
        /// 查找指定进程名的主窗口句柄
        /// </summary>
        private static IntPtr FindMainWindowByProcessName(string processName)
        {
            IntPtr foundHwnd = IntPtr.Zero;
            try
            {
                var processes = Process.GetProcessesByName(processName);
                foreach (var proc in processes)
                {
                    try
                    {
                        // 优先用进程自带的 MainWindowHandle
                        if (proc.MainWindowHandle != IntPtr.Zero)
                            return proc.MainWindowHandle;
                    }
                    catch { }
                    finally
                    {
                        proc.Dispose();
                    }
                }

                // 若 MainWindowHandle 为空，枚举所有窗口查找匹配进程的可见窗口
                var targetProcesses = Process.GetProcessesByName(processName);
                var pids = new HashSet<uint>();
                foreach (var p in targetProcesses)
                {
                    try { pids.Add((uint)p.Id); }
                    catch { }
                    finally { p.Dispose(); }
                }

                if (pids.Count == 0)
                    return IntPtr.Zero;

                EnumWindows((hWnd, _) =>
                {
                    if (!IsWindowVisible(hWnd))
                        return true;

                    GetWindowThreadProcessId(hWnd, out uint pid);
                    if (pids.Contains(pid))
                    {
                        foundHwnd = hWnd;
                        return false; // 找到即停止
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }

            return foundHwnd;
        }

        /// <summary>
        /// 跨 32/64 位兼容的 GetClassLongPtr
        /// </summary>
        private static IntPtr GetClassLongPtrCross(IntPtr hWnd, int nIndex)
        {
            if (IntPtr.Size == 8)
                return GetClassLongPtr64(hWnd, nIndex);
            else
                return new IntPtr(GetClassLong32(hWnd, nIndex));
        }

        /// <summary>
        /// 将图标句柄转为 PNG 字节数组
        /// </summary>
        private static byte[]? IconHandleToPng(IntPtr hIcon)
        {
            try
            {
                using var icon = System.Drawing.Icon.FromHandle(hIcon);
                using var bitmap = icon.ToBitmap();
                using var ms = new MemoryStream();
                bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                var result = ms.ToArray();
                return result.Length > 0 ? result : null;
            }
            catch (Exception ex)
            {
                Log.Debug("IconHandleToPng 转换失败: {Error}", ex.Message);
                return null;
            }
        }

        #endregion
    }
}
