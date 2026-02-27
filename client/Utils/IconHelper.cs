using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;

namespace client.Utils
{
    public static class IconHelper
    {
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(
            string pszPath, uint dwFileAttributes,
            ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

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

        /// <summary>
        /// 根据进程名获取应用图标的 PNG 字节数组
        /// </summary>
        public static byte[]? GetIconPngByProcessName(string processName)
        {
            try
            {
                var exePath = GetExePathByProcessName(processName);
                if (exePath == null)
                {
                    Log.Debug("无法获取进程路径: {ProcessName}", processName);
                    return null;
                }

                Log.Debug("获取到进程路径: {ProcessName} -> {ExePath}", processName, exePath);
                var data = ExtractIconAsPng(exePath);
                if (data != null)
                    Log.Debug("图标提取成功: {ProcessName}，大小 {Size} bytes", processName, data.Length);
                else
                    Log.Debug("图标提取失败（SHGetFileInfo 返回空）: {ProcessName}", processName);

                return data;
            }
            catch (Exception ex)
            {
                Log.Warning("获取图标异常 [{ProcessName}]: {Error}", processName, ex.Message);
                return null;
            }
        }

        private static string? GetExePathByProcessName(string processName)
        {
            try
            {
                var processes = Process.GetProcessesByName(processName);
                foreach (var proc in processes)
                {
                    try
                    {
                        var path = proc.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(path))
                            return path;
                    }
                    catch
                    {
                        // 某些进程（系统/UAC保护）无法获取路径，跳过
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

        private static byte[]? ExtractIconAsPng(string filePath)
        {
            var shInfo = new SHFILEINFO();
            var result = SHGetFileInfo(filePath, 0, ref shInfo,
                (uint)Marshal.SizeOf(typeof(SHFILEINFO)),
                SHGFI_ICON | SHGFI_LARGEICON);

            if (result == IntPtr.Zero || shInfo.hIcon == IntPtr.Zero)
                return null;

            try
            {
                using var bitmap = System.Drawing.Icon.FromHandle(shInfo.hIcon).ToBitmap();
                using var ms = new MemoryStream();
                bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                return ms.ToArray();
            }
            finally
            {
                DestroyIcon(shInfo.hIcon);
            }
        }
    }
}
