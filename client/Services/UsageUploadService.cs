using client.DTOs;
using client.Storage;
using Serilog;
using System.Net.Http.Json;

namespace client.Services
{
    public class UsageUploadService
    {
        private readonly string _apiUrl;
        private readonly string _apiKey;
        private readonly string _deviceName;
        private readonly LocalCache _cache;
        private readonly HttpClient _client = new();

        public UsageUploadService(string apiUrl, string apiKey, string deviceName, LocalCache cache)
        {
            _apiUrl = apiUrl;
            _apiKey = apiKey;
            _deviceName = deviceName;
            _cache = cache;
        }

        private UsageUploadRequest MapToDto(List<AppUsageItem> items)
        {
            return new UsageUploadRequest
            {
                DeviceName = _deviceName,
                ApiKey = _apiKey,
                Usages = items.ConvertAll(i => new AppUsageItem
                {
                    AppName = i.AppName,
                    StartTime = i.StartTime,
                    EndTime = i.EndTime
                })
            };
        }

        public async Task UploadAsync(List<AppUsageItem> usages)
        {
            var dto = MapToDto(usages);
            Log.Information("正在上传 {Count} 条使用记录到 {Url}...", usages.Count, _apiUrl);
            try
            {
                var res = await _client.PostAsJsonAsync(_apiUrl, dto);
                if (!res.IsSuccessStatusCode)
                {
                    var body = await res.Content.ReadAsStringAsync();
                    Log.Warning("上传失败 [{StatusCode}]: {Body}，{Count} 条记录已缓存到本地",
                        (int)res.StatusCode, body, usages.Count);
                    _cache.Add(usages);
                    return;
                }
                Log.Information("上传成功，共 {Count} 条记录", usages.Count);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "上传失败（网络异常），{Count} 条记录已缓存到本地", usages.Count);
                _cache.Add(usages);
            }
        }

        public async Task UploadCachedAsync()
        {
            var cached = _cache.Load();
            if (cached.Count == 0) return;

            Log.Information("发现 {Count} 条缓存记录，尝试上传...", cached.Count);
            var dto = MapToDto(cached);
            try
            {
                var res = await _client.PostAsJsonAsync(_apiUrl, dto);
                if (!res.IsSuccessStatusCode)
                {
                    var body = await res.Content.ReadAsStringAsync();
                    Log.Warning("缓存记录上传失败 [{StatusCode}]: {Body}", (int)res.StatusCode, body);
                    return;
                }
                _cache.Clear();
                Log.Information("缓存记录上传成功，已清除本地缓存");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "缓存记录上传失败（网络异常），保留本地缓存待下次重试");
            }
        }
    }
}
