using client.Services;
using client.Storage;
using client.Utils;

var configService = new ConfigService();
var config = configService.LoadConfig();

var cache = new LocalCache("cache.json");
var monitorService = new AppMonitorService();
var uploadService = new UsageUploadService(config.ApiUrl, config.ApiKey, config.DeviceName, cache);

TimerHelper.RunEvery(TimeSpan.FromSeconds(config.SampleIntervalSeconds), () => monitorService.RecordActiveApp());
TimerHelper.RunEvery(TimeSpan.FromMinutes(config.UploadIntervalMinutes), async () =>
{
    var usages = monitorService.GetAndClearUsages();
    if (usages.Count > 0)
        await uploadService.UploadAsync(usages);
});

await uploadService.UploadCachedAsync();

Console.WriteLine("Running... Ctrl+C to exit");
await Task.Delay(-1);