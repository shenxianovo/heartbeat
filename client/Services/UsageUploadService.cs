using client.DTOs;
using client.Models;
using client.Storage;
using Serilog;
using System.Net.Http.Json;

namespace client.Services
{
    public class UsageUploadService(Config config, HttpClient httpClient, LocalCache cache)
    {
        private readonly string _uploadUrl = $"{config.ApiBaseUrl}/usage";

        private UsageUploadRequest MapToDto(List<AppUsageItem> items)
        {
            return new UsageUploadRequest
            {
                DeviceName = config.DeviceName,
                ApiKey = config.ApiKey,
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
            Log.Information("正在上传 {Count} 条使用记录...", usages.Count);
            try
            {
                var res = await httpClient.PostAsJsonAsync(_uploadUrl, dto);
                if (!res.IsSuccessStatusCode)
                {
                    var body = await res.Content.ReadAsStringAsync();
                    Log.Warning("上传失败 [{StatusCode}]: {Body}，{Count} 条记录已缓存到本地",
                        (int)res.StatusCode, body, usages.Count);
                    cache.Add(usages);
                    return;
                }
                Log.Information("上传成功，共 {Count} 条记录", usages.Count);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "上传失败（网络异常），{Count} 条记录已缓存到本地", usages.Count);
                cache.Add(usages);
            }
        }

        public async Task UploadCachedAsync()
        {
            var cached = cache.Load();
            if (cached.Count == 0) return;

            Log.Information("发现 {Count} 条缓存记录，尝试上传...", cached.Count);
            var dto = MapToDto(cached);
            try
            {
                var res = await httpClient.PostAsJsonAsync(_uploadUrl, dto);
                if (!res.IsSuccessStatusCode)
                {
                    var body = await res.Content.ReadAsStringAsync();
                    Log.Warning("缓存记录上传失败 [{StatusCode}]: {Body}", (int)res.StatusCode, body);
                    return;
                }
                cache.Clear();
                Log.Information("缓存记录上传成功，已清除本地缓存");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "缓存记录上传失败（网络异常），保留本地缓存待下次重试");
            }
        }
    }
}
