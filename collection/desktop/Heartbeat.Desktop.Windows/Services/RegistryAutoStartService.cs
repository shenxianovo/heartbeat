using Heartbeat.Desktop.UI.Hosting;
using Microsoft.Win32;
using Serilog;
using System.Runtime.Versioning;

namespace Heartbeat.Desktop.Windows.Services
{
    [SupportedOSPlatform("windows")]
    public class RegistryAutoStartService : IDesktopLoginStart
    {
        private const string RegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "Heartbeat";

        public bool IsEnabled
        {
            get
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, false);
                    return key?.GetValue(AppName) != null;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "读取自启动状态失败");
                    return false;
                }
            }
        }

        public void Enable(string executablePath)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true);
                key?.SetValue(AppName, $"\"{executablePath}\"");
                Log.Information("已启用开机自启动: {Path}", executablePath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "启用自启动失败");
            }
        }

        public void Disable()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true);
                key?.DeleteValue(AppName, false);
                Log.Information("已禁用开机自启动");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "禁用自启动失败");
            }
        }
    }
}
