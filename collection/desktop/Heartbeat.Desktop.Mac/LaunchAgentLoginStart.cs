using Heartbeat.Desktop.UI.Hosting;
using System.Runtime.InteropServices;
using System.Security;
using Heartbeat.Desktop.Mac.Native;
using Serilog;

namespace Heartbeat.Desktop.Mac;

/// <summary>每用户 LaunchAgent adapter；管理 ~/Library 与当前 GUI launchd domain，不需要管理员权限。</summary>
public sealed class LaunchAgentLoginStart : IDesktopLoginStart
{
    private const string Label = "com.shenxianovo.heartbeat";
    private const string LaunchCtlPath = "/bin/launchctl";
    private readonly IMacCommandRunner _commands;
    private readonly string _plistPath;
    private readonly Func<uint> _effectiveUserId;

    public LaunchAgentLoginStart(IMacCommandRunner commands)
        : this(commands, DefaultPlistPath(), geteuid)
    {
    }

    internal LaunchAgentLoginStart(
        IMacCommandRunner commands,
        string plistPath,
        Func<uint> effectiveUserId)
    {
        _commands = commands;
        _plistPath = plistPath;
        _effectiveUserId = effectiveUserId;
    }

    public bool IsEnabled => File.Exists(_plistPath);

    public void Enable(string executablePath)
    {
        var escaped = SecurityElement.Escape(executablePath)
            ?? throw new ArgumentException("Executable path is invalid.", nameof(executablePath));
        var plist = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>{{Label}}</string>
              <key>ProgramArguments</key>
              <array><string>{{escaped}}</string></array>
              <key>RunAtLoad</key><true/>
              <key>KeepAlive</key><false/>
            </dict>
            </plist>
            """;

        Directory.CreateDirectory(Path.GetDirectoryName(_plistPath)!);
        var temporary = _plistPath + ".tmp";
        File.WriteAllText(temporary, plist);
        File.Move(temporary, _plistPath, overwrite: true);
        ReloadIfStale(executablePath);
    }

    public void Disable()
    {
        if (File.Exists(_plistPath))
            File.Delete(_plistPath);
    }

    private void ReloadIfStale(string executablePath)
    {
        var userDomain = $"gui/{_effectiveUserId()}";
        var serviceTarget = $"{userDomain}/{Label}";
        var loadedJob = _commands.Run(LaunchCtlPath, ["print", serviceTarget]);
        if (loadedJob.ExitCode == 0)
        {
            var loadedExecutable = ReadLoadedExecutable(loadedJob.StandardOutput);
            if (string.Equals(loadedExecutable, executablePath, StringComparison.Ordinal))
                return;

            var unload = _commands.Run(LaunchCtlPath, ["bootout", serviceTarget]);
            if (unload.ExitCode != 0)
            {
                Log.Warning(
                    "无法卸载陈旧的登录启动任务 {Label}: {Error}",
                    Label,
                    unload.StandardError.Trim());
                return;
            }
        }

        var load = _commands.Run(LaunchCtlPath, ["bootstrap", userDomain, _plistPath]);
        if (load.ExitCode != 0)
        {
            Log.Error(
                "无法加载登录启动任务 {Label}: {Error}",
                Label,
                load.StandardError.Trim());
        }
    }

    private static string? ReadLoadedExecutable(string output)
    {
        const string prefix = "program = ";
        foreach (var line in output.Split('\n'))
        {
            var value = line.Trim();
            if (value.StartsWith(prefix, StringComparison.Ordinal))
                return value[prefix.Length..];
        }

        return null;
    }

    private static string DefaultPlistPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", $"{Label}.plist");

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern uint geteuid();
}
