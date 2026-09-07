using Heartbeat.Desktop.Windows.Models;
using Heartbeat.Desktop.Windows.Services;
using Heartbeat.Desktop.Windows.Utils;
using Heartbeat.Collection.Hub.Collectors;
using Heartbeat.Collection.Hub.Configuration;
using Heartbeat.Collection.Hub.Runtime;

namespace Heartbeat.Desktop.Windows.Configuration;

public sealed class HubConfigurationAdapter : IHubConfiguration, ICollectorRegistry, IDisposable
{
    private readonly ConfigManager _config;

    public HubConfigurationAdapter(ConfigManager config)
    {
        _config = config;
        _config.ConfigChanged += OnConfigChanged;
    }

    public HubRuntimeSettings Current
    {
        get
        {
            var config = _config.Current;
            return new HubRuntimeSettings(
                config.ApiKey,
                TimeSpan.FromMinutes(config.UploadIntervalMinutes),
                config.IngestPort);
        }
    }

    public event Action? Changed;

    public IReadOnlyDictionary<string, CollectorRegistration> Snapshot
        => _config.Current.Collectors.ToDictionary(
            pair => pair.Key,
            pair => ToRegistration(pair.Value));

    public CollectorRegistration Touch(string source, int? flushPeriodMs = null)
    {
        CollectorEntry? result = null;
        _config.Update(config =>
        {
            if (!config.Collectors.TryGetValue(source, out var entry))
            {
                entry = new CollectorEntry();
                config.Collectors[source] = entry;
            }
            if (flushPeriodMs is > 0)
                entry.FlushPeriodMs = flushPeriodMs;
            result = entry;
        });
        return ToRegistration(result!);
    }

    public void Discover(IEnumerable<string> sources)
    {
        var missing = sources.Where(source => !_config.Current.Collectors.ContainsKey(source)).ToList();
        if (missing.Count == 0) return;
        _config.Update(config =>
        {
            foreach (var source in missing)
                config.Collectors.TryAdd(source, new CollectorEntry());
        });
    }

    public void StoreDeclaration(string source, string declarationJson, int version)
    {
        _config.Update(config =>
        {
            if (!config.Collectors.TryGetValue(source, out var entry))
            {
                entry = new CollectorEntry();
                config.Collectors[source] = entry;
            }
            if (entry.DeclarationVersion != version || entry.DeclarationJson == null)
            {
                entry.DeclarationJson = declarationJson;
                entry.DeclarationVersion = version;
            }
        });
    }

    public void StoreVerifiedPackageDeclaration(string source, string declarationJson, int version)
    {
        _config.Update(config =>
        {
            if (!config.Collectors.TryGetValue(source, out var entry))
            {
                entry = new CollectorEntry();
                config.Collectors[source] = entry;
            }
            if (entry.DeclarationVersion != version || entry.DeclarationJson != declarationJson)
            {
                entry.DeclarationJson = declarationJson;
                entry.DeclarationVersion = version;
            }
        });
    }

    private void OnConfigChanged(AgentConfig _) => Changed?.Invoke();

    private static CollectorRegistration ToRegistration(CollectorEntry entry)
        => new(entry.Enabled, entry.FlushPeriodMs, entry.DeclarationJson, entry.DeclarationVersion);

    public void Dispose()
    {
        _config.ConfigChanged -= OnConfigChanged;
        GC.SuppressFinalize(this);
    }
}

public sealed class WindowsDeviceIdentity(
    ConfigManager config,
    IMachineIdentity machineIdentity) : IDeviceIdentity
{
    public string HardwareId => machineIdentity.HardwareId;
    public string DeviceName => string.IsNullOrEmpty(config.Current.DeviceName)
        ? Environment.MachineName
        : config.Current.DeviceName;
}

public sealed class WindowsHubRuntimeHooks(
    IIconUploadService icons,
    ConfigManager config) : IHubRuntimeHooks
{
    internal string LegacyCachePath => Path.Combine(config.DataDirectory, "cache.json");

    public void OnStarting()
    {
        var legacyPath = LegacyCachePath;
        if (File.Exists(legacyPath))
            Serilog.Log.Information("检测到已退役的 usage 离线缓存 {Path}（ADR-020），文件保留但不再读取", legacyPath);
    }

    public async Task SegmentsDrainedAsync(IReadOnlyCollection<Heartbeat.Core.DTOs.Segments.ActivitySegmentItem> segments, CancellationToken cancellationToken = default)
    {
        foreach (var app in segments
                     .Where(segment => !string.IsNullOrEmpty(segment.AppIdentityKey))
                     .Select(segment => (IdentityKey: segment.AppIdentityKey!, segment.AppDisplayName))
                     .DistinctBy(item => item.IdentityKey, StringComparer.OrdinalIgnoreCase))
        {
            if (cancellationToken.IsCancellationRequested) break;
            try { await icons.EnsureIconUploadedAsync(app.IdentityKey, app.AppDisplayName, cancellationToken); }
            catch (Exception ex) { Serilog.Log.Warning(ex, "图标上传失败: {App}", app.IdentityKey); }
        }
    }
}
