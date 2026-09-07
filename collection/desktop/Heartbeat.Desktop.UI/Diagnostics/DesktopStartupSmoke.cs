using System.Text.Json;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Heartbeat.Desktop.UI.Diagnostics;

/// <summary>
/// 打包产物的启动 smoke：证明 Desktop 宿主能走完 <see cref="IHost.StartAsync"/> 并干净停止。
/// 它只认识宿主自身，不解析也不断言任何具名可选 Collector 的状态；
/// Request 提供隔离数据目录，Lifecycle 在宿主与日志都释放后清理自建目录。
/// 只给 release 流水线用——它不拉起任何 UI，跑完就退出，并把结果写成一行 JSON 便于日志留痕。
/// </summary>
public static class DesktopStartupSmoke
{
    public const string ArgumentName = "--verify-startup";
    public const string DataDirectoryArgumentName = "--verify-startup-data-directory";

    /// <param name="ReportPath">可选的报告落盘路径；Windows 的 GUI 子系统进程没有 stdout，靠它取回结果。</param>
    /// <param name="DataDirectoryOverride">
    /// 可选的数据目录；不给就自己在临时目录下开一个一次性目录。smoke 永远不落在真实用户数据目录里。
    /// </param>
    public sealed record Request(string? ReportPath = null, string? DataDirectoryOverride = null)
    {
        /// <summary>本次 smoke 实际使用的数据目录，宿主组合要照它重定向整棵本机数据树。</summary>
        public string DataDirectory { get; } = string.IsNullOrWhiteSpace(DataDirectoryOverride)
            ? Path.Combine(Path.GetTempPath(), "heartbeat-startup-smoke", Guid.NewGuid().ToString("N"))
            : Path.GetFullPath(DataDirectoryOverride);

        /// <summary>目录是 smoke 自己开的，跑完就该删掉；调用方指定的目录一律不动。</summary>
        public bool OwnsDataDirectory { get; } = string.IsNullOrWhiteSpace(DataDirectoryOverride);
    }

    /// <summary>
    /// Owns only the one-shot data directory. Platform heads create this before composing the host and
    /// dispose it after host disposal and log flushing, when no Windows file handle can still pin the tree.
    /// </summary>
    public sealed class Lifecycle : IDisposable
    {
        private readonly Request _request;
        private bool _disposed;

        internal Lifecycle(Request request) => _request = request;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_request.OwnsDataDirectory)
                TryDeleteDirectory(_request.DataDirectory);
        }
    }

    public static Lifecycle BeginLifecycle(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new Lifecycle(request);
    }

    public static bool TryGetRequest(IEnumerable<string>? arguments, out Request request)
    {
        request = new Request();
        if (arguments is null)
            return false;

        var requested = false;
        string? reportPath = null;
        string? dataDirectory = null;
        foreach (var argument in arguments)
        {
            if (string.Equals(argument, ArgumentName, StringComparison.Ordinal))
            {
                requested = true;
                continue;
            }
            if (argument.StartsWith(ArgumentName + "=", StringComparison.Ordinal))
            {
                requested = true;
                reportPath = Blank(argument[(ArgumentName.Length + 1)..]);
                continue;
            }
            if (argument.StartsWith(DataDirectoryArgumentName + "=", StringComparison.Ordinal))
                dataDirectory = Blank(argument[(DataDirectoryArgumentName.Length + 1)..]);
        }

        if (!requested)
            return false;
        request = new Request(reportPath, dataDirectory);
        return true;

        static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// smoke 无法得出结论时的出口（例如单实例守卫已被别的进程占住）。宁可报失败，
    /// 也不要让「进程提前退出」被误读成「宿主启动正常」。
    /// </summary>
    public static int Inconclusive(Request request, string reason, TextWriter? output = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["hostStarted"] = false,
            ["systemCollector"] = "unknown",
            ["failure"] = reason
        });
        (output ?? Console.Out).WriteLine($"startup-smoke inconclusive {json}");
        WriteReport(request.ReportPath, json);
        return 1;
    }

    /// <returns>进程退出码：0 表示宿主起停成功。宿主释放与数据目录清理由外层 Lifecycle 排序。</returns>
    public static int Run(
        IHost host,
        Request request,
        TimeSpan? timeout = null,
        TextWriter? output = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(request);
        var budget = timeout ?? TimeSpan.FromSeconds(60);
        var report = new Dictionary<string, object?>
        {
            ["hostStarted"] = false,
            ["systemCollector"] = "unknown",
            ["dataDirectory"] = request.DataDirectory,
            ["failure"] = null
        };

        try
        {
            using var cancellation = new CancellationTokenSource(budget);
            host.StartAsync(cancellation.Token).GetAwaiter().GetResult();
            report["hostStarted"] = true;
            report["hardwareId"] = host.Services.GetService<Heartbeat.Collection.Hub.Configuration.IDeviceIdentity>()?.HardwareId;

            // System 是宿主唯一写死的 BuiltIn Collector，它的 runtime 缺席就说明组合坏了。
            var systemComposed = host.Services.GetService<CollectorRuntime>() is not null;
            report["systemCollector"] = systemComposed ? "registered" : "missing";
            if (!systemComposed)
                report["failure"] = "system built-in collector runtime is not registered";

            host.StopAsync(cancellation.Token).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            report["failure"] = $"{exception.GetType().Name}: {exception.Message}";
        }
        var succeeded = report["failure"] is null;
        var json = JsonSerializer.Serialize(report);
        (output ?? Console.Out).WriteLine($"startup-smoke {(succeeded ? "ok" : "failed")} {json}");
        WriteReport(request.ReportPath, json);
        return succeeded ? 0 : 1;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 清理失败只留下临时目录，不改变 smoke 结论。
        }
    }

    private static void WriteReport(string? reportPath, string json)
    {
        if (string.IsNullOrWhiteSpace(reportPath))
            return;
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(reportPath));
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(reportPath, json);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 报告只是可观测性，写不下去不改变 smoke 结论。
        }
    }
}
