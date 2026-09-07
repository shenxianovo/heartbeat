using Heartbeat.Desktop.Windows.Configuration;
using Heartbeat.Desktop.Windows.Services;

namespace Heartbeat.Desktop.Windows.Tests.Configuration;

public sealed class WindowsHubRuntimeHooksTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-windows-hooks-{Guid.NewGuid():N}");

    [Fact]
    public void LegacyCacheProbe_UsesTheInjectedConfigurationDataDirectory()
    {
        var config = new ConfigManager(Path.Combine(_root, "isolated", "config.json"));
        var hooks = new WindowsHubRuntimeHooks(new NoopIconUploadService(), config);

        Assert.Equal(
            Path.Combine(config.DataDirectory, "cache.json"),
            hooks.LegacyCachePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class NoopIconUploadService : IIconUploadService
    {
        public Task EnsureIconUploadedAsync(string appIdentityKey, string? appDisplayName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
